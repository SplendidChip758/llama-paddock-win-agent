#!/usr/bin/env node
/**
 * llama-paddock agent — Windows
 * =============================
 * Runs in the user's session (Scheduled Task at logon) next to the Ollama
 * desktop app. Zero npm dependencies: needs Node.js >= 18.15.
 *
 * Same HTTP API as the Linux agent:
 *   GET  /health            no auth
 *   GET  /stats             system + nvidia-smi + Ollama process status
 *   GET  /env               Ollama-related USER environment variables
 *   PUT  /env               {vars, restart} — set/remove them, optionally restart Ollama
 *   POST /service/restart   restart the Ollama app with the current environment
 *   GET  /logs?lines=N      tail of %LOCALAPPDATA%\Ollama\server.log
 *
 * Why user variables: Ollama for Windows reads the account's environment
 * variables, and is a tray app running in the user's session.
 *
 * Only variables matching MANAGED_RE (OLLAMA_*, CUDA_*, ...) or ones added
 * through the dashboard are ever shown — never Path, TEMP, etc. — and only
 * those in ENV_ALLOW can be changed.
 *
 * Settings come from agent.json next to this file, overridden by environment
 * variables of the same name:
 *   PADDOCK_AGENT_TOKEN   required shared secret
 *   PADDOCK_AGENT_BIND    default 0.0.0.0
 *   PADDOCK_AGENT_PORT    default 11435
 *   PADDOCK_OLLAMA_APP    default %LOCALAPPDATA%\Programs\Ollama\ollama app.exe
 *   PADDOCK_DISK_PATHS    extra drives/folders to report, comma separated
 *   PADDOCK_AGENT_ENV_ALLOW extra variable names the dashboard may set, comma separated
 *                         (a trailing * allows a prefix: "HSA_*,MY_VAR"); see ENV_ALLOW
 *   PADDOCK_DRY_RUN       "1" = never kill/start Ollama (testing)
 */
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";
import { execFile, spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const VERSION = "0.7.0-dev.1";
const HERE = path.dirname(fileURLToPath(import.meta.url));
const fileCfg = (() => {
  const file = path.join(HERE, "agent.json");
  let text;
  try {
    text = fs.readFileSync(file, "utf8");
  } catch {
    return {};
  }
  try {
    // older installers wrote a UTF-8 BOM, which JSON.parse rejects
    return JSON.parse(text.replace(/^\uFEFF/, ""));
  } catch (e) {
    console.error(`${file} is not valid JSON: ${e.message}`);
    process.exit(1);
  }
})();
const cfg = (k, d = "") => process.env[k] ?? fileCfg[k] ?? d;

const LOCALAPPDATA = process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local");
const TOKEN = String(cfg("PADDOCK_AGENT_TOKEN"));
const OLLAMA_APP = String(cfg("PADDOCK_OLLAMA_APP", path.join(LOCALAPPDATA, "Programs", "Ollama", "ollama app.exe")));
const LOG_FILE = path.join(LOCALAPPDATA, "Ollama", "server.log");
const STATE_FILE = path.join(HERE, "agent-state.json");
const DRY_RUN = String(cfg("PADDOCK_DRY_RUN")) === "1";
const EXTRA_DISKS = String(cfg("PADDOCK_DISK_PATHS")).split(",").map((s) => s.trim()).filter(Boolean);
const KEY_RE = /^[A-Za-z_][A-Za-z0-9_]*$/;
const MANAGED_RE = /^(OLLAMA_|CUDA_|HIP_|ROCR_|HSA_|GGML_|GPU_DEVICE_ORDINAL$)/i;
// Variables the dashboard may set. Anything else (CUDA_INJECTION64_PATH, HSA_TOOLS_LIB, …) could
// load code into Ollama, so a dashboard login or agent token must not be enough to set it.
// The host can extend the list through PADDOCK_AGENT_ENV_ALLOW; variables already set that
// aren't listed are kept as they are and reported read-only. Keep in step with the Linux agent.
const ENV_ALLOW = [
  "OLLAMA_*",
  "CUDA_VISIBLE_DEVICES", "CUDA_DEVICE_ORDER", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "GPU_DEVICE_ORDINAL",
  "HSA_OVERRIDE_GFX_VERSION", "GGML_CUDA_ENABLE_UNIFIED_MEMORY",
  "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
  ...String(cfg("PADDOCK_AGENT_ENV_ALLOW")).split(",").map((s) => s.trim()).filter(Boolean),
];
const envAllowed = (name) => ENV_ALLOW.some((p) => p.endsWith("*")
  ? name.toUpperCase().startsWith(p.slice(0, -1).toUpperCase())
  : name.toUpperCase() === p.toUpperCase());

// ------------------------------------------------------------------ helpers
function run(cmd, args, timeoutMs = 15_000, opts = {}) {
  return new Promise((resolve) => {
    execFile(cmd, args, { timeout: timeoutMs, maxBuffer: 16 * 1024 * 1024, windowsHide: true, ...opts }, (err, stdout, stderr) => {
      resolve({ code: err ? (typeof err.code === "number" ? err.code : 1) : 0, stdout: String(stdout), stderr: String(stderr || (err && !stdout ? err.message : "")) });
    });
  });
}

/** Run a PowerShell script safely: the script is passed base64-encoded, data via a base64 JSON literal. */
async function powershell(script, data) {
  const payload = data === undefined ? "" : `$data = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${Buffer.from(JSON.stringify(data)).toString("base64")}')) | ConvertFrom-Json\n`;
  const full = `$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [Text.Encoding]::UTF8\n${payload}${script}`;
  const encoded = Buffer.from(full, "utf16le").toString("base64");
  const r = await run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], 30_000);
  if (r.code !== 0) throw httpError(500, `powershell: ${(r.stderr || r.stdout).trim().split("\n")[0]}`);
  return r.stdout;
}

const num = (v) => {
  v = String(v).trim();
  if (["", "[N/A]", "N/A", "[Not Supported]", "Not Supported"].includes(v)) return null;
  const f = Number(v);
  return Number.isNaN(f) ? v : f;
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function loadState() {
  try { return JSON.parse(fs.readFileSync(STATE_FILE, "utf8")); } catch { return { customKeys: [] }; }
}
function saveState(s) {
  fs.writeFileSync(STATE_FILE, JSON.stringify(s, null, 2));
}

// ------------------------------------------------------------------ CPU sampler
let cpuPrev = os.cpus().map((c) => ({ ...c.times }));
let cpuNow = { total: 0, perCore: cpuPrev.map(() => 0) };
setInterval(() => {
  const cur = os.cpus().map((c) => ({ ...c.times }));
  let busyAll = 0, totalAll = 0;
  const perCore = cur.map((t, i) => {
    const p = cpuPrev[i] || t;
    const total = Object.values(t).reduce((a, b) => a + b, 0) - Object.values(p).reduce((a, b) => a + b, 0);
    const idle = t.idle - p.idle;
    busyAll += total - idle;
    totalAll += total;
    return total > 0 ? +(((total - idle) / total) * 100).toFixed(1) : 0;
  });
  cpuNow = { total: totalAll > 0 ? +((busyAll / totalAll) * 100).toFixed(1) : 0, perCore };
  cpuPrev = cur;
}, 2000).unref();

// ------------------------------------------------------------------ system stats
let netPrev = null;
async function netRates() {
  // `netstat -e` prints interface totals: "Bytes   <received>   <sent>"
  const r = await run("netstat", ["-e"], 5000);
  const m = r.stdout.match(/^\s*Bytes\s+(\d+)\s+(\d+)/m);
  if (!m) return { rx_bps: null, tx_bps: null };
  const rx = Number(m[1]), tx = Number(m[2]), now = Date.now() / 1000;
  let rx_bps = null, tx_bps = null;
  if (netPrev && now > netPrev.t) {
    rx_bps = Math.max(0, (rx - netPrev.rx) / (now - netPrev.t));
    tx_bps = Math.max(0, (tx - netPrev.tx) / (now - netPrev.t));
  }
  netPrev = { t: now, rx, tx };
  return { rx_bps, tx_bps };
}

function diskUsage(p) {
  try {
    const s = fs.statfsSync(p);
    const total = s.blocks * s.bsize, free = s.bavail * s.bsize, used = (s.blocks - s.bfree) * s.bsize;
    return { path: p, total, used, free, pct: used + free > 0 ? +((used / (used + free)) * 100).toFixed(1) : 0 };
  } catch { return null; }
}

/** ollama.exe processes (server + runners) via tasklist CSV. */
async function ollamaProcesses() {
  const r = await run("tasklist", ["/FO", "CSV", "/NH", "/FI", "IMAGENAME eq ollama.exe"], 5000);
  const rows = r.stdout.split(/\r?\n/).filter((l) => l.startsWith('"ollama.exe"'));
  if (!rows.length) return null;
  let rss = 0;
  const pids = [];
  for (const row of rows) {
    const cols = row.match(/"([^"]*)"/g)?.map((c) => c.slice(1, -1)) ?? [];
    pids.push(Number(cols[1]));
    rss += Number((cols[4] || "0").replace(/[^\d]/g, "")) * 1024; // "123,456 K"
  }
  // the server is the oldest process; tasklist lists in creation order
  return { pid: pids[0], rss_total: rss, runners: Math.max(0, rows.length - 1) };
}

let modelsDirCache = null;
async function systemStats() {
  const total = os.totalmem(), avail = os.freemem();
  const proc = await ollamaProcesses();
  const disks = [];
  const seen = new Set();
  const modelsDir = modelsDirCache || path.join(os.homedir(), ".ollama", "models");
  for (const p of [path.parse(os.homedir()).root, ...EXTRA_DISKS, modelsDir]) {
    const d = diskUsage(p);
    if (!d) continue;
    const key = `${d.total}:${d.used}`;
    if (seen.has(key)) continue;
    seen.add(key);
    disks.push(d);
  }
  return {
    hostname: os.hostname(),
    uptime_s: Math.round(os.uptime()),
    cpu_count: os.cpus().length,
    cpu_pct: cpuNow.total,
    cpu_per_core: cpuNow.perCore,
    load: [0, 0, 0], // no load average on Windows
    mem: { total, used: total - avail, available: avail, pct: +(((total - avail) / total) * 100).toFixed(1) },
    swap: { total: 0, used: 0, pct: 0 },
    disks,
    net: await netRates(),
    ollama_process: proc,
  };
}

// ------------------------------------------------------------------ nvidia-smi
const GPU_FIELDS = [
  "index", "uuid", "name", "driver_version", "pstate", "temperature.gpu", "fan.speed",
  "utilization.gpu", "utilization.memory", "memory.used", "memory.total", "power.draw", "power.limit",
  "clocks.sm", "clocks.max.sm", "clocks.mem", "pcie.link.gen.current", "pcie.link.width.current",
];
const GPU_KEYS = [
  "index", "uuid", "name", "driver", "pstate", "temp_c", "fan_pct", "util_pct", "mem_util_pct",
  "mem_used_mib", "mem_total_mib", "power_w", "power_limit_w", "sm_clock_mhz", "sm_clock_max_mhz",
  "mem_clock_mhz", "pcie_gen", "pcie_width",
];
const TEXT_KEYS = new Set(["uuid", "name", "driver", "pstate"]);
const NVSMI_CANDIDATES = ["nvidia-smi", "C:\\Program Files\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe"];
let nvsmi = null;

async function nvidiaStats() {
  let q = null;
  for (const exe of nvsmi ? [nvsmi] : NVSMI_CANDIDATES) {
    q = await run(exe, [`--query-gpu=${GPU_FIELDS.join(",")}`, "--format=csv,noheader,nounits"]);
    if (q.code === 0) { nvsmi = exe; break; }
  }
  if (!q || q.code !== 0) {
    return { available: false, error: /ENOENT/.test(q?.stderr ?? "") ? "nvidia-smi not found" : (q?.stderr || q?.stdout || "").trim(), gpus: [], processes: [] };
  }
  const gpus = q.stdout.trim().split(/\r?\n/).filter(Boolean).map((line) => {
    const parts = line.split(",").map((s) => s.trim());
    return Object.fromEntries(GPU_KEYS.map((k, i) => [k, TEXT_KEYS.has(k) ? parts[i] : num(parts[i])]));
  }).filter((g) => g.uuid);
  const p = await run(nvsmi, ["--query-compute-apps=gpu_uuid,pid,process_name,used_memory", "--format=csv,noheader,nounits"]);
  // WDDM drivers report per-process memory as [N/A]; that's expected on consumer cards
  const processes = p.code !== 0 ? [] : p.stdout.trim().split(/\r?\n/).filter(Boolean).map((line) => {
    const [gpu_uuid, pid, name, mem] = line.split(",").map((s) => s.trim());
    return { gpu_uuid, pid: num(pid), name, mem_mib: num(mem) };
  });
  return { available: true, gpus, processes };
}

// ------------------------------------------------------------------ environment
/** Read user + machine scope variables in one PowerShell call. */
async function readScopes() {
  const out = await powershell(`
$u = [Environment]::GetEnvironmentVariables('User')
$m = [Environment]::GetEnvironmentVariables('Machine')
@{ user = $u; machine = $m } | ConvertTo-Json -Compress -Depth 3`);
  const parsed = JSON.parse(out || "{}");
  return { user: parsed.user || {}, machine: parsed.machine || {} };
}

function isManaged(key, customKeys) {
  return MANAGED_RE.test(key) || customKeys.some((k) => k.toLowerCase() === key.toLowerCase());
}

async function readEnvState() {
  const { user, machine } = await readScopes();
  const { customKeys } = loadState();
  const managed = Object.fromEntries(Object.entries(user).filter(([k]) => isManaged(k, customKeys)));
  const machineManaged = Object.fromEntries(Object.entries(machine).filter(([k]) => isManaged(k, customKeys)));
  const external = Object.fromEntries(Object.entries(machineManaged).filter(([k]) => !(k in managed)));
  if (managed.OLLAMA_MODELS || machineManaged.OLLAMA_MODELS) modelsDirCache = managed.OLLAMA_MODELS || machineManaged.OLLAMA_MODELS;
  return {
    platform: "windows",
    managed,
    override_path: "HKCU\\Environment",
    raw: Object.entries(managed).map(([k, v]) => `${k}=${v}`).join("\n"),
    effective: { ...machineManaged, ...managed },
    external,
    allowed: ENV_ALLOW,
  };
}

async function putEnv(body) {
  const vars = body?.vars;
  if (!vars || typeof vars !== "object" || Array.isArray(vars)) throw httpError(400, "body must be {vars: {...}, restart?: bool}");
  const before = await readEnvState();
  const existing = (k) => Object.entries(before.managed).find(([e]) => e.toLowerCase() === k.toLowerCase())?.[1];
  for (const [k, v] of Object.entries(vars)) {
    if (!KEY_RE.test(k)) throw httpError(400, `invalid variable name: ${JSON.stringify(k)}`);
    if (typeof v !== "string") throw httpError(400, `value of ${k} must be a string`);
    if (/[\r\n\0]/.test(v)) throw httpError(400, `newlines not allowed in value of ${k}`);
    if (/^(path|pathext|temp|tmp|userprofile|appdata|localappdata|systemroot|windir|comspec)$/i.test(k)) {
      throw httpError(400, `${k} is a system variable and can't be managed here`);
    }
    if (!envAllowed(k) && existing(k) !== v) {
      throw httpError(400, `${k} is not an Ollama variable; add it to PADDOCK_AGENT_ENV_ALLOW on the host to manage it here`);
    }
  }
  const state = loadState();
  // set the allowed vars given and remove the allowed ones that were dropped; anything outside
  // the allowlist stays exactly as it is, whether or not the request mentions it
  const changes = Object.fromEntries(Object.entries(vars).filter(([k]) => envAllowed(k)));
  for (const k of Object.keys(before.managed)) if (!(k in vars) && envAllowed(k)) changes[k] = null;
  state.customKeys = [...new Set([
    ...Object.keys(changes).filter((k) => changes[k] !== null && !MANAGED_RE.test(k)),
    ...Object.keys(before.managed).filter((k) => !envAllowed(k) && !MANAGED_RE.test(k)),
  ])];

  // SetEnvironmentVariable(User) writes HKCU\Environment and broadcasts WM_SETTINGCHANGE
  await powershell(`
foreach ($p in $data.PSObject.Properties) {
  [Environment]::SetEnvironmentVariable($p.Name, $p.Value, 'User')
}`, changes);
  saveState(state);

  const after = await readEnvState();
  const result = { written: after.override_path, content: after.raw };
  if (body.restart !== false) result.restart = await restartOllama();
  return result;
}

// ------------------------------------------------------------------ Ollama process control
async function ollamaRunning() {
  const r = await run("tasklist", ["/FO", "CSV", "/NH", "/FI", "IMAGENAME eq ollama.exe"], 5000);
  return r.stdout.includes('"ollama.exe"');
}

/** The environment a freshly logged-in Ollama would see: our env with Ollama vars replaced by current machine+user values. */
async function freshEnv() {
  const { user, machine } = await readScopes();
  const { customKeys } = loadState();
  const env = { ...process.env };
  for (const k of Object.keys(env)) if (isManaged(k, customKeys)) delete env[k];
  for (const scope of [machine, user]) {
    for (const [k, v] of Object.entries(scope)) if (isManaged(k, customKeys)) env[k] = v;
  }
  return env;
}

async function waitForApi(env, timeoutMs) {
  let host = env.OLLAMA_HOST || "127.0.0.1:11434";
  host = host.replace(/^https?:\/\//, "").replace(/^0\.0\.0\.0/, "127.0.0.1").replace(/^\[::\]/, "127.0.0.1");
  if (!host.includes(":")) host += ":11434";
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const r = await fetch(`http://${host}/api/version`, { signal: AbortSignal.timeout(2000) });
      if (r.ok) return true;
    } catch { /* not up yet */ }
    await sleep(1000);
  }
  return false;
}

async function restartOllama() {
  if (DRY_RUN) return { ok: true, dry_run: true };
  await run("taskkill", ["/IM", "ollama app.exe", "/F"], 10_000);
  await run("taskkill", ["/IM", "ollama.exe", "/F", "/T"], 10_000);
  for (let i = 0; i < 20 && (await ollamaRunning()); i++) await sleep(500);

  const env = await freshEnv();
  if (fs.existsSync(OLLAMA_APP)) {
    // Launch through Explorer, exactly like clicking it in the Start menu: Ollama
    // then isn't in the agent's process tree (Task Scheduler kills that tree when
    // the agent stops), and Explorer has already reloaded the user environment
    // from the WM_SETTINGCHANGE broadcast that SetEnvironmentVariable sends.
    spawn("explorer.exe", [OLLAMA_APP], { detached: true, stdio: "ignore" }).unref();
    if (await waitForApi(env, 20_000)) return { ok: true, launched: `explorer.exe "${OLLAMA_APP}"`, stderr: "" };
    // Fallback: start it ourselves with the environment rebuilt from the registry
    spawn(OLLAMA_APP, [], { detached: true, stdio: "ignore", env, cwd: path.dirname(OLLAMA_APP) }).unref();
    const ok = await waitForApi(env, 20_000);
    return { ok, launched: OLLAMA_APP, stderr: ok ? "" : "Ollama did not answer on its API after restart" };
  }
  // No desktop app (standalone ollama.exe install): run the server directly
  const exe = path.join(path.dirname(OLLAMA_APP), "ollama.exe");
  const bin = fs.existsSync(exe) ? exe : "ollama.exe";
  spawn(bin, ["serve"], { detached: true, stdio: "ignore", env, windowsHide: true }).unref();
  const ok = await waitForApi(env, 30_000);
  return { ok, launched: `${bin} serve`, stderr: ok ? "" : "Ollama did not answer on its API within 30s" };
}

async function serviceStatus() {
  const proc = await ollamaProcesses();
  return { active: proc ? "active" : "inactive", sub: proc ? "running" : "stopped", since: null, main_pid: proc?.pid ?? null };
}

function tailFile(file, lines) {
  let fd;
  try {
    fd = fs.openSync(file, "r");
    const size = fs.fstatSync(fd).size;
    const len = Math.min(size, 4 * 1024 * 1024);
    const buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, size - len);
    return buf.toString("utf8").split(/\r?\n/).filter((l) => l.length).slice(-lines);
  } catch (e) {
    return [`cannot read ${file}: ${e.message}`];
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
  }
}

// ------------------------------------------------------------------ HTTP
function httpError(status, detail) {
  return Object.assign(new Error(detail), { status });
}

function authorized(req) {
  const h = req.headers.authorization || "";
  const supplied = Buffer.from(h.toLowerCase().startsWith("bearer ") ? h.slice(7) : "");
  const expected = Buffer.from(TOKEN);
  return supplied.length === expected.length && crypto.timingSafeEqual(supplied, expected);
}

async function readJson(req) {
  let size = 0;
  const chunks = [];
  for await (const c of req) {
    size += c.length;
    if (size > 1_000_000) throw httpError(413, "body too large");
    chunks.push(c);
  }
  try { return JSON.parse(Buffer.concat(chunks).toString() || "{}"); } catch { throw httpError(400, "invalid JSON"); }
}

const routes = {
  "GET /health": async () => ({ ok: true, version: VERSION, platform: "windows", hostname: os.hostname() }),
  "GET /stats": async () => {
    const [system, nvidia, service] = await Promise.all([systemStats(), nvidiaStats(), serviceStatus()]);
    return { ts: Date.now() / 1000, version: VERSION, platform: "windows", system, nvidia, service };
  },
  "GET /env": async () => readEnvState(),
  "PUT /env": async (req) => putEnv(await readJson(req)),
  "POST /service/restart": async () => restartOllama(),
  "GET /logs": async (_req, url) => {
    const lines = Math.max(10, Math.min(Number(url.searchParams.get("lines")) || 200, 2000));
    return { lines: tailFile(LOG_FILE, lines) };
  },
};

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, "http://agent");
  const handler = routes[`${req.method} ${url.pathname}`];
  const send = (status, body) => {
    res.writeHead(status, { "Content-Type": "application/json" });
    res.end(JSON.stringify(body));
  };
  if (!handler) return send(404, { detail: "not found" });
  if (url.pathname !== "/health" && !authorized(req)) return send(401, { detail: "invalid agent token" });
  try {
    send(200, await handler(req, url));
  } catch (e) {
    send(e.status || 500, { detail: e.message });
  }
});

if (!TOKEN) {
  console.error("PADDOCK_AGENT_TOKEN must be set (agent.json or environment)");
  process.exit(1);
}
const port = Number(cfg("PADDOCK_AGENT_PORT", "11435"));
const bind = String(cfg("PADDOCK_AGENT_BIND", "0.0.0.0"));
server.listen(port, bind, () => console.log(`llama-paddock agent ${VERSION} (windows) listening on ${bind}:${port}`));
