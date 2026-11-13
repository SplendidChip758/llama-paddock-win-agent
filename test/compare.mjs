#!/usr/bin/env node
/**
 * Runs the Node agent and the C# agent side by side on the same files and checks that they answer
 * the same: status and JSON body, apart from fields that change between calls (ts, version, and
 * /requests' until when it is "now"). Windows only; plain Node, no dependencies.
 *
 *   node test/compare.mjs <C# agent: paddock-agent.exe, or paddock-agent.dll to run with dotnet>
 *
 * Both agents run in-process as the current user, in dry run, with LOCALAPPDATA pointing at a
 * folder of log fixtures that the cases below rewrite.
 */
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { isDeepStrictEqual } from "node:util";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const TOKEN = "compare-token";
const NODE_PORT = 11541, CS_PORT = 11542;

const csharp = process.argv[2];
if (!csharp) {
  console.error("usage: node test/compare.mjs <paddock-agent.exe | paddock-agent.dll>");
  process.exit(2);
}

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "paddock-compare-"));
const local = path.join(tmp, "local");
const logDir = path.join(local, "Ollama");
const logFile = path.join(logDir, "server.log");
const data = path.join(tmp, "data");
fs.mkdirSync(data, { recursive: true });
const env = {
  ...process.env,
  LOCALAPPDATA: local,
  PADDOCK_AGENT_TOKEN: TOKEN,
  PADDOCK_DRY_RUN: "1",
  // one key for both, so the fingerprints match too
  PADDOCK_AGENT_TLS_KEY: path.join(tmp, "agent.key"),
  PADDOCK_AGENT_TLS_CERT: path.join(tmp, "agent.crt"),
  PADDOCK_AGENT_DATA: data,
};

const agents = [];
function start(name, cmd, args, port) {
  const out = fs.openSync(path.join(tmp, `${name}.log`), "w");
  const child = spawn(cmd, args, { env: { ...env, PADDOCK_AGENT_PORT: String(port) }, stdio: ["ignore", out, out], windowsHide: true });
  agents.push(child);
  return child;
}
async function waitHealthy(port, name) {
  for (let i = 0; i < 60; i++) {
    try {
      if ((await fetch(`http://127.0.0.1:${port}/health`)).ok) return;
    } catch { /* not up yet */ }
    await new Promise((r) => setTimeout(r, 500));
  }
  console.error(fs.readFileSync(path.join(tmp, `${name}.log`), "utf8"));
  throw new Error(`${name} agent didn't start`);
}
function stopAll() {
  for (const a of agents) a.kill();
}
process.on("exit", stopAll);

// ------------------------------------------------------------------ fixtures
const pad = (n, w = 2) => String(n).padStart(w, "0");
/** Go's slog time: local time with its offset, milliseconds. */
function slogTime(ms) {
  const d = new Date(ms);
  const off = -d.getTimezoneOffset();
  const sign = off >= 0 ? "+" : "-";
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}${sign}${pad(Math.floor(Math.abs(off) / 60))}:${pad(Math.abs(off) % 60)}`;
}
const gin = (when, status, latency, client, call) => `[GIN] ${when} | ${status} | ${latency} | ${client} | ${call}`;

function mixedLog() {
  const now = Date.now();
  const runner = (time) => `time=${time} level=INFO source=server.go:1 msg="llama runner started in 2.51 seconds"`;
  const lines = [
    "\uFEFFtime=2026-11-02T09:00:00.000-08:00 level=INFO msg=\"starting\"",
    "time=2026-11-02T09:00:01.000-08:00 level=INFO msg=\"inference compute\" id=GPU-1 library=cuda variant=v12",
    "looking for compatible GPUs",
    "CUDA error: out of memory",
    "a chip line that is not a GPU line",
    "hip is a word here",
    "HIP_VISIBLE_DEVICES=0",
    "NVML: unable to load",
    `offload ${"x".repeat(700)}`,
    `gpu with emoji at the cut ${"y".repeat(472)}\u{1F600}tail`,
    "[GIN] gpu in a request line is left out",
    "unicode \u2028 separator gpu",
    "lone carriage return\rgpu",
    // runner starts: an hour back is the default window
    runner(slogTime(now - 7200_000)),
    runner(slogTime(now - 1800_000)),
    runner(new Date(now - 600_000).toISOString()),
    runner(slogTime(now - 60_000).replace(/\.(\d{3})/, ".$1456789")),
    // a local time without a zone
    runner(slogTime(now - 120_000).slice(0, 23)),
    runner("not-a-time"),
    "llama runner started in 1 second, no time field",
    `time=  ${runner(slogTime(now - 30_000))}`,
    // requests
    gin("2026/11/02 - 09:14:07", "200", " 4.551040958s", "  192.168.30.20", 'POST     "/api/chat"'),
    gin("2026/11/02 - 09:14:08", "200", "1m2.5s", "192.168.30.21", 'POST     "/api/generate?stream=false"'),
    gin("2026/11/02 - 09:14:09", "404", "735.1µs", "::1", 'GET      "/api/tags"'),
    gin("2026/11/02 - 09:14:10", "500", "12us", "127.0.0.1", 'HEAD     "/"'),
    gin("2026/11/02 - 09:14:11", "200", "3ns", "127.0.0.1", 'GET      "/api/version"'),
    gin("2026/11/02 - 09:14:12", "200", "1h2m3.5s", "127.0.0.1", 'GET      "/api/ps"'),
    gin("2026/11/02 - 09:14:13", "200", "0.0004995ms", "127.0.0.1", 'GET      "/a"'),
    gin("2026/11/02 - 09:14:14", "200", "0.0005005ms", "127.0.0.1", 'GET      "/b"'),
    // clocks going forward and back (US dates; also tested wherever the runner's zone has them)
    gin("2026/03/08 - 02:30:00", "200", "1s", "127.0.0.1", 'GET      "/gap"'),
    gin("2026/11/01 - 01:30:00", "200", "1s", "127.0.0.1", 'GET      "/twice"'),
    gin("2026/03/29 - 02:30:00", "200", "1s", "127.0.0.1", 'GET      "/gap-eu"'),
    gin("2026/10/25 - 02:30:00", "200", "1s", "127.0.0.1", 'GET      "/twice-eu"'),
    // parts out of range roll over; two-digit years are 19xx
    gin("2026/13/45 - 25:61:61", "200", "1s", "127.0.0.1", 'GET      "/rolled"'),
    gin("2026/00/00 - 00:00:00", "200", "1s", "127.0.0.1", 'GET      "/zero"'),
    gin("0050/01/01 - 00:00:00", "200", "1s", "127.0.0.1", 'GET      "/old"'),
    "prefix text " + gin("2026/11/02 - 09:15:00", "201", "2s", "10.0.0.1", 'DELETE   "/api/delete"'),
    // look like requests but don't parse: skipped
    "[GIN] 2026/11/02 - 09:14:07 | 200 | 1s | 127.0.0.1",
    gin("2026/11/02 - 09:14:07", "2000", "1s", "127.0.0.1", 'GET "/"'),
    gin("2026/11/02 - 09:14:07", "\u0662\u0660\u0660", "1s", "127.0.0.1", 'GET "/"'),
    gin("2026/11/02 - 09:14:07", "200", "4.5x", "127.0.0.1", 'GET "/"'),
    gin("2026/11/02 - 09:14:07", "200", "", "127.0.0.1", 'GET "/"'),
    gin("2026/11/02 - 09:14:07", "200", "1s", "127.0.0.1 x", 'GET "/"'),
    gin("2026/11/02 - 09:14:07", "200", "1s", "127.0.0.1\u0085", 'GET "/"'),
    gin("2026/11/02 - 09:14:07", "200", "1s", "", 'GET "/"'),
    gin("2026/11/02 - 09:14:07", "200", "1s", "127.0.0.1", 'get "/"'),
    gin("2026/11/02 - 09:14:07", "200", "1s", "127.0.0.1", 'GET "/"\n'.trim() + " "),
    gin("2026-11-02 - 09:14:07", "200", "1s", "127.0.0.1", 'GET "/"'),
    gin("2026/11/02 - 09:14:07", "200", "1s", "127.0.0.1", 'GET "/a|b"'),
    "",
    "",
    "last line without a newline",
  ];
  const text = lines.join("\r\n");
  // invalid UTF-8 in the middle, decoded the same by both
  return Buffer.concat([Buffer.from(text.slice(0, 200)), Buffer.from([0xff, 0xc3, 0x28, 0xe2, 0x82, 0xf0, 0x9f, 0x98]), Buffer.from(text.slice(200))]);
}

/** Over 20 MB: GPU lines at both ends, more than 5000 requests, a cut first line for each tail read. */
function bigLog() {
  const parts = [];
  for (let i = 0; i < 200; i++) parts.push(`early gpu line ${i}`);
  const filler = `filler ${"z".repeat(90)}`;
  for (let i = 0; i < 230_000; i++) parts.push(i % 1000 === 0 ? `middle cuda line ${i}` : filler);
  for (let i = 0; i < 6000; i++) {
    const s = 1_780_000_000 + Math.floor(i / 3);
    const d = new Date(s * 1000);
    const when = `${d.getFullYear()}/${pad(d.getMonth() + 1)}/${pad(d.getDate())} - ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
    parts.push(gin(when, "200", `${i}.5ms`, "192.168.30.20", 'POST     "/api/chat"'));
  }
  for (let i = 0; i < 200; i++) parts.push(`late vram line ${i}`);
  return "é" + parts.join("\n") + "\n";
}

const writeLog = (content) => {
  fs.mkdirSync(logDir, { recursive: true });
  fs.writeFileSync(logFile, content);
};
const removeLogDir = () => fs.rmSync(logDir, { recursive: true, force: true });

// ------------------------------------------------------------------ cases
const auth = { Authorization: `Bearer ${TOKEN}` };
const LOG_ROUTES = [
  "/logs", "/logs?lines=5", "/logs?lines=15.7", "/logs?lines=abc", "/logs?lines=0", "/logs?lines=-3", "/logs?lines=0x20",
  "/logs?lines=%2012%20", "/logs?lines=1e9", "/logs?lines=12&lines=50", "/logs?lines=", "/logs?lines=Infinity",
  "/gpu-check",
  "/runner-starts", "/runner-starts?since=0", "/runner-starts?since=abc", `/runner-starts?since=1`, "/runner-starts?since=9999999999",
  "/requests", "/requests?since=1", "/requests?since=abc", "/requests?since=0", "/requests?since=-5", "/requests?since=Infinity",
  "/requests?since=1793639650", "/requests?since=1793639647", "/requests?since=1780000500",
];
const fixtures = [
  ["no log folder", removeLogDir],
  ["empty log", () => writeLog("")],
  ["mixed log", () => writeLog(mixedLog())],
  ["big log", () => writeLog(bigLog())],
];
const general = [
  { path: "/health", headers: {} },
  { path: "/health", headers: { Authorization: "Bearer wrong" } },
  { path: "/logs", headers: {} },
  { path: "/logs", headers: { Authorization: "Bearer wrong" } },
  { path: "/logs", headers: { Authorization: `bearer ${TOKEN}` } },
  { path: "/logs", headers: { Authorization: `Bearer  ${TOKEN}` } },
  { path: "/logs", headers: { Authorization: TOKEN } },
  { path: "/nothing-here", headers: auth },
  { path: "/logs/", headers: auth },
  { path: "/LOGS", headers: auth },
  { method: "POST", path: "/logs", headers: auth },
  { path: "/gpu/reload-uvm", headers: auth },
  { method: "POST", path: "/gpu/reload-uvm", headers: auth },
  { method: "POST", path: "/gpu/reload-uvm", headers: {} },
];

// fields that change between calls
function stable(route, body) {
  if (!body || typeof body !== "object") return body;
  const { ts: _ts, ...rest } = body;
  if (route === "/health") delete rest.version;
  if (route === "/requests" && rest.more === false) delete rest.until;
  return rest;
}

async function call(port, c) {
  const r = await fetch(`http://127.0.0.1:${port}${c.path}`, { method: c.method ?? "GET", headers: c.headers });
  const text = await r.text();
  let body;
  try { body = JSON.parse(text); } catch { body = { unparsed: text }; }
  return { status: r.status, body: stable(new URL(c.path, "http://x").pathname, body) };
}

function firstDifference(a, b, at = "") {
  if (isDeepStrictEqual(a, b)) return null;
  if (a && b && typeof a === "object" && typeof b === "object") {
    for (const k of new Set([...Object.keys(a), ...Object.keys(b)])) {
      const d = firstDifference(a[k], b[k], `${at}.${k}`);
      if (d) return d;
    }
  }
  const show = (v) => (v === undefined ? "missing" : JSON.stringify(v)?.slice(0, 300));
  return `${at || "body"}: node ${show(a)}, c# ${show(b)}`;
}

let failed = 0, passed = 0;
async function check(label, c) {
  const [n, s] = await Promise.all([call(NODE_PORT, c), call(CS_PORT, c)]);
  const diff = n.status !== s.status ? `status: node ${n.status}, c# ${s.status}` : firstDifference(n.body, s.body);
  if (diff) {
    failed++;
    console.log(`FAIL ${label}: ${c.method ?? "GET"} ${c.path}\n     ${diff}`);
  } else passed++;
}

try {
  const [csCmd, csArgs] = csharp.endsWith(".dll") ? ["dotnet", [csharp]] : [csharp, []];
  start("node", process.execPath, [path.join(ROOT, "paddock-agent.mjs")], NODE_PORT);
  // the Node agent makes the key first; the C# agent then loads the same one
  await waitHealthy(NODE_PORT, "node");
  start("csharp", csCmd, csArgs, CS_PORT);
  await waitHealthy(CS_PORT, "csharp");

  for (const c of general) await check("general", c);
  for (const [label, setup] of fixtures) {
    setup();
    for (const p of LOG_ROUTES) await check(label, { path: p, headers: auth });
  }
} finally {
  stopAll();
}
console.log(`${passed} passed, ${failed} failed (time zone ${Intl.DateTimeFormat().resolvedOptions().timeZone})`);
fs.rmSync(tmp, { recursive: true, force: true });
process.exit(failed ? 1 : 0);
