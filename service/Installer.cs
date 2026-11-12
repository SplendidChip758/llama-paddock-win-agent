using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;

namespace LlamaPaddock.Agent
{
    /// <summary>
    /// What the setup runs, elevated: `install`, `uninstall` and `detect`. Each writes a JSON result
    /// to --result for the wizard. Install moves a scheduled-task install (the legacy setup) over
    /// with its token, TLS key and Ollama variables, so the dashboard needs no change.
    /// </summary>
    static class Installer
    {
        const string TaskName = "llama-paddock-agent";
        const string FirewallRule = "llama-paddock agent";
        const string LegacyDirName = "llama-paddock-agent";
        // the same as the agent's MANAGED_RE
        static readonly Regex Managed = new Regex("^(OLLAMA_|CUDA_|HIP_|ROCR_|HSA_|GGML_|GPU_DEVICE_ORDINAL$)", RegexOptions.IgnoreCase);
        static readonly string[] LegacyFiles = { "agent.json", "agent.key", "agent.crt", "agent-state.json" };

        static string ProgramDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "llama-paddock-agent");
        static NTAccount ServiceAccount => new NTAccount("NT SERVICE", AgentService.Name);

        public static int Run(string command, Dictionary<string, string> opts)
        {
            var result = new Dictionary<string, object>();
            var warnings = new List<string>();
            result["warnings"] = warnings;
            int code = 0;
            try
            {
                if (command != "detect" && !Win.IsAdmin()) throw new InvalidOperationException("run this as an administrator");
                if (command == "install") Install(opts, result, warnings);
                else if (command == "uninstall") Uninstall(opts.ContainsKey("keep-data"), warnings);
                else if (command == "detect") Detect(result);
                // before the setup replaces the files the service has open
                else if (command == "stop") StopService();
                else throw new ArgumentException($"unknown command {command}");
                result["ok"] = true;
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.Message;
                Console.Error.WriteLine(ex.Message);
                code = 1;
            }
            foreach (string w in warnings) Console.Error.WriteLine("warning: " + w);
            if (!opts.TryGetValue("result", out string file)) Console.WriteLine(Win.ToJson(result));
            else if (file.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(file, Ini(result), Win.Utf8);
            else File.WriteAllText(file, Win.ToJson(result), Win.Utf8);
            return code;
        }

        /// <summary>The result for the setup's script, which reads INI files but not JSON. Lists are joined with " | ".</summary>
        static string Ini(Dictionary<string, object> result)
        {
            var sb = new System.Text.StringBuilder("[result]\r\n");
            foreach (var kv in result)
            {
                object v = kv.Value;
                string text = v is bool b ? (b ? "1" : "0")
                    : v is IEnumerable<string> list ? string.Join(" | ", list)
                    : Convert.ToString(v);
                sb.Append(kv.Key).Append('=').Append((text ?? "").Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ detect
        static void Detect(Dictionary<string, object> result)
        {
            result["installed"] = ServiceExists();
            result["legacy"] = LegacyDirs().ToArray();
            result["ollama"] = FindOllama(null);
        }

        /// <summary>Scheduled-task installs: each user profile's %LOCALAPPDATA%\llama-paddock-agent with an agent.json.</summary>
        static List<string> LegacyDirs() =>
            Win.Profiles().Select(p => Path.Combine(p.LocalAppData, LegacyDirName))
                .Where(d => File.Exists(Path.Combine(d, "agent.json"))).ToList();

        /// <summary>The Ollama desktop install: the given user's first, then anyone's, then a machine-wide one.</summary>
        static string FindOllama(Win.Profile preferred)
        {
            var candidates = new List<string>();
            var profiles = Win.Profiles();
            var first = preferred ?? Win.CurrentProfile();
            if (first != null) candidates.Add(Path.Combine(first.LocalAppData, "Programs", "Ollama", "ollama.exe"));
            candidates.AddRange(profiles.Select(p => Path.Combine(p.LocalAppData, "Programs", "Ollama", "ollama.exe")));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"));
            return candidates.FirstOrDefault(File.Exists);
        }

        // ------------------------------------------------------------------ install
        static void Install(Dictionary<string, string> opts, Dictionary<string, object> result, List<string> warnings)
        {
            string agentJson = Path.Combine(DataDir, "agent.json");
            bool upgrade = File.Exists(agentJson);

            // where the scheduled-task install is, if this moves one over
            string legacy = null;
            if (!upgrade)
            {
                if (opts.TryGetValue("migrate-from", out string from))
                {
                    if (!File.Exists(Path.Combine(from, "agent.json"))) throw new InvalidOperationException($"no agent.json in {from}");
                    legacy = from;
                }
                else
                {
                    var found = LegacyDirs();
                    if (found.Count > 1) throw new InvalidOperationException("more than one existing agent install: " + string.Join(", ", found) + ". Choose one with --migrate-from");
                    legacy = found.FirstOrDefault();
                }
            }
            // the account that ran Ollama: its models, variables and Startup folder are the ones to carry over
            Win.Profile user = Win.ProfileOf(legacy) ?? Win.ProfileOf(opts.TryGetValue("ollama-exe", out string oe) ? oe : null) ?? Win.CurrentProfile();

            string ollamaExe = opts.TryGetValue("ollama-exe", out oe) ? oe : null;
            var existing = upgrade ? Win.ReadJson(agentJson) : null;
            if (ollamaExe == null && existing != null && existing.TryGetValue("PADDOCK_OLLAMA_EXE", out object prev)) ollamaExe = prev as string;
            if (ollamaExe == null) ollamaExe = FindOllama(user);
            if (ollamaExe == null || !File.Exists(ollamaExe)) throw new InvalidOperationException("Ollama isn't installed: install it from https://ollama.com first, or choose its ollama.exe");

            StopService();
            CreateOrUpdateService();
            Win.MakePrivate(DataDir, ServiceAccount);
            Directory.CreateDirectory(Path.Combine(DataDir, "logs"));

            // the legacy agent holds the port: switch its task off (it would restart the agent), but keep
            // it until the service answers, so a failed install can put it back
            bool hadTask = TaskExists();
            if (hadTask) { Win.Run("schtasks.exe", "/End", "/TN", TaskName); Win.Run("schtasks.exe", "/Change", "/TN", TaskName, "/DISABLE"); }
            StopLegacyAgent();

            var state = Win.ReadJson(Path.Combine(DataDir, "install-state.json")) ?? new Dictionary<string, object>();
            string token = null;
            Dictionary<string, object> cfg;
            if (upgrade)
            {
                cfg = existing;
            }
            else if (legacy != null)
            {
                foreach (string f in LegacyFiles)
                {
                    string src = Path.Combine(legacy, f);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(DataDir, f), true);
                }
                cfg = Win.ReadJson(agentJson);
                if (cfg.ContainsKey("PADDOCK_AGENT_TLS_KEY") || cfg.ContainsKey("PADDOCK_AGENT_TLS_CERT"))
                    warnings.Add("agent.json points PADDOCK_AGENT_TLS_KEY/CERT at your own files: the service account (NT SERVICE\\llama-paddock-agent) must be able to read them");
                if (!File.Exists(Path.Combine(legacy, "agent.key")))
                    warnings.Add("the old agent had no HTTPS key yet: the new one's fingerprint has to be accepted in the dashboard");
            }
            else
            {
                token = NewToken();
                cfg = new Dictionary<string, object>
                {
                    ["PADDOCK_AGENT_TOKEN"] = token,
                    ["PADDOCK_AGENT_BIND"] = "0.0.0.0",
                    ["PADDOCK_AGENT_PORT"] = opts.TryGetValue("port", out string port) ? port : "11435",
                    ["PADDOCK_DISK_PATHS"] = "",
                    // HTTPS only; "on" also serves plain HTTP, for dashboards before 0.7.0
                    ["PADDOCK_AGENT_HTTP"] = "off",
                };
            }
            cfg["PADDOCK_OLLAMA_EXE"] = ollamaExe;
            Win.WriteJson(agentJson, cfg);

            string envFile = Path.Combine(DataDir, "ollama-env.json");
            var env = Win.ReadJson(envFile)?.ToDictionary(kv => kv.Key, kv => kv.Value as string, StringComparer.OrdinalIgnoreCase);
            if (env == null)
            {
                env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (user != null)
                {
                    var vars = Win.UserEnvironment(user.Sid);
                    if (vars == null) warnings.Add($"couldn't read the Ollama variables of {user.Dir}: that user isn't logged in. Set them in the dashboard's Environment tab");
                    else
                    {
                        var custom = CustomKeys(Path.Combine(DataDir, "agent-state.json"));
                        // the service doesn't expand %USERPROFILE% and the like, and its own profile is a different one
                        foreach (var kv in vars.Where(kv => Managed.IsMatch(kv.Key) || custom.Contains(kv.Key)))
                            env[kv.Key] = Environment.ExpandEnvironmentVariables(Regex.Replace(kv.Value, "%USERPROFILE%", user.Dir.Replace("$", "$$"), RegexOptions.IgnoreCase));
                    }
                }
                // keep using the models Ollama already has
                if (!env.ContainsKey("OLLAMA_MODELS") && user != null) env["OLLAMA_MODELS"] = Path.Combine(user.Dir, ".ollama", "models");
                Win.WriteJson(envFile, env);
            }

            // the desktop app would start its own Ollama at sign-in and take port 11434 from the service
            Kill("ollama app");
            Kill("ollama");
            if (user != null) DisableStartupShortcut(user, state);

            var grants = state.TryGetValue("grants", out object g) ? ((ArrayList)g).Cast<string>().ToList() : new List<string>();
            void grant(string dir, FileSystemRights rights)
            {
                Directory.CreateDirectory(dir);
                Win.Grant(dir, ServiceAccount, rights);
                if (!grants.Contains(dir, StringComparer.OrdinalIgnoreCase)) grants.Add(dir);
            }
            grant(Path.GetDirectoryName(ollamaExe), FileSystemRights.ReadAndExecute);
            if (env.TryGetValue("OLLAMA_MODELS", out string models) && models != "")
            {
                string modelsDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(models));
                grant(modelsDir, FileSystemRights.Modify);
                // Ollama resolves the full path of a model's files, which lists every folder on the way. A
                // user's profile is closed to the service, so for models inside one (C:\Users\me\.ollama\models
                // by default) it gets to list each folder above them, the profile folder included: the names
                // in those folders, not their files or subfolders. Without it every model load is denied.
                var owner = Win.ProfileOf(modelsDir);
                for (string dir = Path.GetDirectoryName(modelsDir);
                     owner != null && dir != null && dir.Length >= owner.Dir.TrimEnd('\\').Length;
                     dir = Path.GetDirectoryName(dir))
                {
                    Win.GrantList(dir, ServiceAccount);
                    if (!grants.Contains(dir, StringComparer.OrdinalIgnoreCase)) grants.Add(dir);
                }
            }
            state["grants"] = grants;
            Win.WriteJson(Path.Combine(DataDir, "install-state.json"), state);

            int agentPort = int.TryParse(cfg.TryGetValue("PADDOCK_AGENT_PORT", out object p) ? p as string : null, out int n) ? n : 11435;
            if (!opts.ContainsKey("no-firewall")) Firewall(agentPort, opts.TryGetValue("allow-from", out string allow) ? allow : null, upgrade || legacy != null, warnings);

            try
            {
                StartService();
                var health = WaitForHealth(agentPort);
                result["fingerprint"] = health.TryGetValue("fingerprint", out object fp) ? fp : null;
                result["version"] = health.TryGetValue("version", out object v) ? v : null;
            }
            catch (Exception ex)
            {
                if (legacy != null && hadTask)
                {
                    StopService();
                    Win.Run("schtasks.exe", "/Change", "/TN", TaskName, "/ENABLE");
                    Win.Run("schtasks.exe", "/Run", "/TN", TaskName);
                    throw new InvalidOperationException($"{ex.Message}. The previous agent was started again. See {Path.Combine(DataDir, "logs", "agent.log")}");
                }
                throw new InvalidOperationException($"{ex.Message}. See {Path.Combine(DataDir, "logs", "agent.log")}");
            }

            if (hadTask) Win.Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
            if (legacy != null)
            {
                // the dashboard pinned this key: the service must present the same one before the old copy goes
                if (SameFile(Path.Combine(legacy, "agent.key"), Path.Combine(DataDir, "agent.key")))
                {
                    try { Directory.Delete(legacy, true); } catch (Exception ex) { warnings.Add($"couldn't remove {legacy}: {ex.Message}"); }
                }
                else warnings.Add($"kept {legacy}: its agent.key differs from the service's");
            }

            result["migrated"] = legacy != null;
            result["upgraded"] = upgrade;
            result["port"] = agentPort;
            result["url"] = $"https://{Dns.GetHostName()}:{agentPort}";
            if (token != null) result["token"] = token;
        }

        static HashSet<string> CustomKeys(string stateFile)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var s = Win.ReadJson(stateFile);
            if (s != null && s.TryGetValue("customKeys", out object k) && k is ArrayList list) foreach (object o in list) keys.Add(o as string ?? "");
            return keys;
        }

        static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static bool SameFile(string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b)) return !File.Exists(a);
            using (var sha = SHA256.Create())
                return sha.ComputeHash(File.ReadAllBytes(a)).SequenceEqual(sha.ComputeHash(File.ReadAllBytes(b)));
        }

        static void DisableStartupShortcut(Win.Profile user, Dictionary<string, object> state)
        {
            string lnk = Path.Combine(user.Startup, "Ollama.lnk");
            if (!File.Exists(lnk)) return;
            string kept = Path.Combine(DataDir, "Ollama.lnk");
            File.Copy(lnk, kept, true);
            File.Delete(lnk);
            state["startup_shortcut"] = lnk;
        }

        // ------------------------------------------------------------------ uninstall
        static void Uninstall(bool keepData, List<string> warnings)
        {
            StopService();
            if (ServiceExists()) Win.Run("sc.exe", "delete", AgentService.Name);
            try { if (EventLog.SourceExists(AgentService.Name)) EventLog.DeleteEventSource(AgentService.Name); } catch { /* not there */ }
            if (TaskExists()) Win.Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
            StopLegacyAgent();
            Win.Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");

            var state = Win.ReadJson(Path.Combine(DataDir, "install-state.json")) ?? new Dictionary<string, object>();
            if (state.TryGetValue("startup_shortcut", out object lnk) && lnk is string dest)
            {
                string kept = Path.Combine(DataDir, "Ollama.lnk");
                try { if (File.Exists(kept) && !File.Exists(dest)) File.Copy(kept, dest); }
                catch (Exception ex) { warnings.Add($"couldn't put Ollama's Startup shortcut back: {ex.Message}"); }
            }
            if (state.TryGetValue("grants", out object g))
            {
                foreach (string dir in ((ArrayList)g).Cast<string>())
                {
                    try { Win.Revoke(dir, ServiceAccount); } catch (Exception ex) { warnings.Add($"couldn't remove the service's access to {dir}: {ex.Message}"); }
                }
            }
            if (!keepData)
            {
                try { Directory.Delete(DataDir, true); } catch (DirectoryNotFoundException) { } catch (Exception ex) { warnings.Add($"couldn't remove {DataDir}: {ex.Message}"); }
            }
        }

        // ------------------------------------------------------------------ service, firewall, processes
        static bool ServiceExists() => ServiceController.GetServices().Any(s => s.ServiceName.Equals(AgentService.Name, StringComparison.OrdinalIgnoreCase));

        static void CreateOrUpdateService()
        {
            string bin = $"\"{Path.Combine(ProgramDir, "paddock-agent-service.exe")}\"";
            string obj = $"NT SERVICE\\{AgentService.Name}";
            var (code, output) = ServiceExists()
                ? Win.Run("sc.exe", "config", AgentService.Name, "binPath=", bin, "obj=", obj, "start=", "delayed-auto", "DisplayName=", "llama-paddock agent")
                : Win.Run("sc.exe", "create", AgentService.Name, "binPath=", bin, "obj=", obj, "start=", "delayed-auto", "DisplayName=", "llama-paddock agent");
            if (code != 0) throw new InvalidOperationException($"couldn't register the service: {output}");
            Win.Run("sc.exe", "description", AgentService.Name, "Runs Ollama and the llama-paddock agent, which reports this PC to the llama-paddock dashboard.");
            Win.Run("sc.exe", "sidtype", AgentService.Name, "unrestricted");
            // restart after a crash, and when the agent exits with an error
            Win.Run("sc.exe", "failure", AgentService.Name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000");
            Win.Run("sc.exe", "failureflag", AgentService.Name, "1");
            try { if (!EventLog.SourceExists(AgentService.Name)) EventLog.CreateEventSource(AgentService.Name, "Application"); } catch { /* the event log is optional */ }
        }

        static void StopService()
        {
            if (!ServiceExists()) return;
            using (var sc = new ServiceController(AgentService.Name))
            {
                if (sc.Status == ServiceControllerStatus.Stopped) return;
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            }
        }

        static void StartService()
        {
            using (var sc = new ServiceController(AgentService.Name))
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }

        static bool TaskExists() => Win.Run("schtasks.exe", "/Query", "/TN", TaskName).code == 0;

        /// <summary>The legacy agent's Node, run from a user's %LOCALAPPDATA%\llama-paddock-agent.</summary>
        static void StopLegacyAgent()
        {
            using (var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'"))
            {
                foreach (ManagementObject p in search.Get())
                {
                    string cmd = p["CommandLine"] as string ?? "";
                    if (cmd.IndexOf(@"\AppData\Local\" + LegacyDirName + @"\paddock-agent.mjs", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { Process.GetProcessById(Convert.ToInt32(p["ProcessId"])).Kill(); } catch { /* gone already */ }
                }
            }
        }

        static void Kill(string name)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(10_000); } catch { /* gone already */ }
            }
        }

        /// <summary>A fresh rule for the agent's port; on an upgrade the existing rule's addresses are kept unless new ones are given.</summary>
        static void Firewall(int port, string allowFrom, bool keepExisting, List<string> warnings)
        {
            if (allowFrom == null && keepExisting)
            {
                var show = Win.Run("netsh.exe", "advfirewall", "firewall", "show", "rule", $"name={FirewallRule}");
                var m = Regex.Match(show.output, @"RemoteIP:\s*(\S+)");
                if (show.code == 0 && m.Success) allowFrom = m.Groups[1].Value;
            }
            if (string.IsNullOrEmpty(allowFrom)) allowFrom = "LocalSubnet";
            Win.Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
            var r = Win.Run("netsh.exe", "advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow",
                "protocol=TCP", $"localport={port}", $"remoteip={allowFrom}", "profile=private,domain");
            if (r.code != 0) warnings.Add($"couldn't add the firewall rule for TCP {port}: {r.output}");
        }

        /// <summary>/health on the loopback interface: HTTPS with the agent's own certificate, plain HTTP if that's all it serves.</summary>
        static Dictionary<string, object> WaitForHealth(int port)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            // only for this call to 127.0.0.1: the agent's certificate is self-signed
            ServicePointManager.ServerCertificateValidationCallback = (_, __, ___, ____) => true;
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            string last = "no answer";
            while (DateTime.UtcNow < deadline)
            {
                foreach (string scheme in new[] { "https", "http" })
                {
                    try
                    {
                        using (var wc = new WebClient())
                        {
                            var h = Win.ReadJsonText(wc.DownloadString($"{scheme}://127.0.0.1:{port}/health"));
                            if (h != null && h.TryGetValue("ok", out object ok) && ok is bool b && b) return h;
                        }
                    }
                    catch (Exception ex) { last = ex.Message; }
                }
                Thread.Sleep(1000);
            }
            throw new InvalidOperationException($"the agent didn't answer on port {port} ({last})");
        }
    }
}
