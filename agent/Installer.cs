using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LlamaPaddock.Agent;

/// <summary>
/// What the setup runs, elevated: <c>paddock-agent.exe install | uninstall | detect | stop</c>. The
/// wizard only asks; these do the work, so a silent install runs the same steps. Each writes its
/// result to --result (INI, which the setup's script reads) or prints it as JSON.
///
/// ollama-network, run after the tray app has started, lets the dashboard reach Ollama: OLLAMA_HOST
/// on all addresses unless it's already set, Ollama restarted through the agent, and a firewall rule.
///
/// Install registers the agent as a LocalSystem service, which reads the Ollama user's files and
/// variables and leaves Ollama itself as it is, and the tray app to start at sign-in. It moves a
/// scheduled-task install of the Node agent over with its token, HTTPS key and settings, so the
/// dashboard needs no change.
/// </summary>
public static partial class Installer
{
    public const string ServiceName = "llama-paddock-agent";
    const string TaskName = "llama-paddock-agent";
    const string FirewallRule = "llama-paddock agent";
    const string OllamaFirewallRule = "llama-paddock Ollama";
    const string OllamaListenAll = "0.0.0.0:11434";
    const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "llama-paddock agent";
    const string TrayExe = "paddock-agent-tray.exe";
    static readonly string[] LegacyFiles = ["agent.json", "agent.key", "agent.crt", "agent-state.json"];

    static string ProgramDir => AppContext.BaseDirectory.TrimEnd('\\');
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "llama-paddock-agent");

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "install" or "uninstall" or "detect" or "stop" or "ollama-network";

    public static int Run(string[] args)
    {
        var opts = Options(args.Skip(1).ToArray());
        var result = new Dictionary<string, object?>();
        var warnings = new List<string>();
        int code = 0;
        try
        {
            if (args[0] != "detect" && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("run this as an administrator");
            switch (args[0])
            {
                case "install": Install(opts, result, warnings); break;
                case "uninstall": Uninstall(opts.ContainsKey("keep-data"), warnings); break;
                case "detect": Detect(result); break;
                // before the setup replaces the files the service and the tray apps have open
                case "stop": StopService(); StopTrays(); break;
                case "ollama-network": OllamaNetwork(opts, result, warnings); break;
            }
            result["ok"] = true;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            result["ok"] = false;
            result["error"] = e.Message;
            Console.Error.WriteLine(e.Message);
            code = 1;
        }
        result["warnings"] = warnings;
        foreach (var w in warnings) Console.Error.WriteLine("warning: " + w);
        if (!opts.TryGetValue("result", out var file)) Console.WriteLine(JsonSerializer.Serialize(result));
        else File.WriteAllText(file, Ini(result), new UTF8Encoding(false));
        return code;
    }

    /// <summary>--name value pairs, and --flag alone.</summary>
    static Dictionary<string, string> Options(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"unexpected {args[i]}");
            var name = args[i][2..];
            opts[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
        }
        return opts;
    }

    /// <summary>The result for the setup's script, which reads INI files but not JSON. Lists are joined with " | ".</summary>
    static string Ini(Dictionary<string, object?> result)
    {
        var sb = new StringBuilder("[result]\r\n");
        foreach (var (k, v) in result)
        {
            var text = v switch
            {
                bool b => b ? "1" : "0",
                IEnumerable<string> list => string.Join(" | ", list),
                _ => Convert.ToString(v) ?? "",
            };
            sb.Append(k).Append('=').Append(text.Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>A line the setup shows as its status while this runs.</summary>
    static void Step(string text) => Console.WriteLine($"step: {text}...");

    // ---------------------------------------------------------------- detect
    static void Detect(Dictionary<string, object?> result)
    {
        result["installed"] = ServiceExists();
        result["legacy"] = LegacyDirs();
        var users = OllamaUsers();
        // SID:DOMAIN\name, for the wizard's choice
        result["users"] = users.Select(u => $"{u.Sid}:{u.Name}").ToList();
        result["user"] = GuessUser(null, null) ?? "";
    }

    sealed record Profile(string Sid, string Dir)
    {
        public string LocalAppData => Path.Combine(Dir, "AppData", "Local");
        public string Name
        {
            get
            {
                try { return new SecurityIdentifier(Sid).Translate(typeof(NTAccount)).Value; }
                catch (Exception e) when (e is IdentityNotMappedException or SystemException) { return Sid; }
            }
        }
    }

    /// <summary>The PC's user profiles (domain and local accounts), from the registry's profile list.</summary>
    static List<Profile> Profiles()
    {
        var list = new List<Profile>();
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (key is null) return list;
        foreach (var sid in key.GetSubKeyNames())
        {
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith(".bak", StringComparison.Ordinal)) continue;
            using var sub = key.OpenSubKey(sid);
            var dir = Environment.ExpandEnvironmentVariables(sub?.GetValue("ProfileImagePath") as string ?? "");
            if (dir != "" && Directory.Exists(dir)) list.Add(new Profile(sid, dir));
        }
        return list;
    }

    static Profile? ProfileOf(string? path) => path is null ? null
        : Profiles().FirstOrDefault(p => path.StartsWith(p.Dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Scheduled-task installs of the Node agent: a profile's %LOCALAPPDATA%\llama-paddock-agent with an agent.json.</summary>
    static List<string> LegacyDirs() =>
        Profiles().Select(p => Path.Combine(p.LocalAppData, "llama-paddock-agent"))
            .Where(d => File.Exists(Path.Combine(d, "agent.json"))).ToList();

    /// <summary>Users with the Ollama desktop app (or ollama.exe) installed in their profile, or running it.</summary>
    static List<Profile> OllamaUsers()
    {
        var running = RunningOllamaOwners();
        return Profiles().Where(p => running.Contains(p.Sid)
            || File.Exists(Path.Combine(p.LocalAppData, "Programs", "Ollama", "ollama app.exe"))
            || File.Exists(Path.Combine(p.LocalAppData, "Programs", "Ollama", "ollama.exe"))).ToList();
    }

    static HashSet<string> RunningOllamaOwners()
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "ollama app", "ollama" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p) if (Native.ProcessUserSid(p.Id) is { } sid) owners.Add(sid);
            }
        }
        return owners;
    }

    /// <summary>
    /// The account that runs Ollama: the one the old agent ran as, else the one running Ollama now,
    /// else the only one with it installed. Null when that doesn't settle it.
    /// </summary>
    static string? GuessUser(string? legacy, string? current)
    {
        if (current is not null) return current;
        if (ProfileOf(legacy) is { } owner) return owner.Sid;
        var running = RunningOllamaOwners().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal)).ToList();
        if (running.Count == 1) return running[0];
        var installed = OllamaUsers();
        return installed.Count == 1 ? installed[0].Sid : null;
    }

    /// <summary>--ollama-user as a SID: given as one, or as DOMAIN\name or name.</summary>
    static string ResolveUser(string given)
    {
        if (given.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            try { return new SecurityIdentifier(given).Value; }
            catch (ArgumentException) { throw new InvalidOperationException($"{given} is not an account ID"); }
        }
        try { return ((SecurityIdentifier)new NTAccount(given).Translate(typeof(SecurityIdentifier))).Value; }
        catch (IdentityNotMappedException) { throw new InvalidOperationException($"there's no account {given} on this PC"); }
    }

    // ---------------------------------------------------------------- install
    static void Install(Dictionary<string, string> opts, Dictionary<string, object?> result, List<string> warnings)
    {
        Directory.CreateDirectory(DataDir);
        MakePrivate(DataDir);
        var agentJson = Path.Combine(DataDir, "agent.json");
        bool upgrade = File.Exists(agentJson);
        var existing = upgrade ? ReadJson(agentJson) : null;

        string? legacy = null;
        if (!upgrade)
        {
            if (opts.TryGetValue("migrate-from", out var from))
            {
                if (!File.Exists(Path.Combine(from, "agent.json"))) throw new InvalidOperationException($"no agent.json in {from}");
                legacy = from;
            }
            else
            {
                var found = LegacyDirs();
                if (found.Count > 1) throw new InvalidOperationException($"more than one Node agent is installed ({string.Join(", ", found)}): choose one with --migrate-from");
                legacy = found.FirstOrDefault();
            }
        }

        var sid = opts.TryGetValue("ollama-user", out var given) && given != "" ? ResolveUser(given)
            : GuessUser(legacy, existing?["PADDOCK_OLLAMA_USER_SID"]?.GetValue<string>() is { Length: > 0 } s ? s : null)
            ?? throw new InvalidOperationException(OllamaUsers().Count > 1
                ? "Ollama is installed for more than one user: choose the one that runs it (/OLLAMAUSER=<name>)"
                : "Ollama isn't installed for any user: install it from https://ollama.com first, or choose the user that will run it (/OLLAMAUSER=<name>)");
        if (Profiles().All(p => p.Sid != sid)) throw new InvalidOperationException($"{sid} has no profile on this PC: it has to have signed in once");

        Step("Stopping the running agent");
        StopService();
        StopTrays();

        // the Node agent holds the port: switch its task off (it would start it again), but keep it
        // until the service answers, so a failed install can put it back
        bool hadTask = TaskExists();
        if (hadTask || legacy is not null) Step("Stopping the Node agent");
        if (hadTask)
        {
            Run("schtasks.exe", "/End", "/TN", TaskName);
            Run("schtasks.exe", "/Change", "/TN", TaskName, "/DISABLE");
        }
        StopLegacyAgent();

        string? token = null;
        JsonObject cfg;
        if (existing is not null) cfg = existing;
        else if (legacy is not null)
        {
            Step("Moving the token, HTTPS key and settings over");
            foreach (var f in LegacyFiles)
            {
                var src = Path.Combine(legacy, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(DataDir, f), true);
            }
            cfg = ReadJson(agentJson);
            if (cfg.ContainsKey("PADDOCK_AGENT_TLS_KEY") || cfg.ContainsKey("PADDOCK_AGENT_TLS_CERT"))
                warnings.Add("agent.json points PADDOCK_AGENT_TLS_KEY/CERT at your own files: the service (LocalSystem) reads them from there");
            if (!File.Exists(Path.Combine(legacy, "agent.key")))
                warnings.Add("the old agent had no HTTPS key yet: accept the new one's fingerprint in the dashboard");
        }
        else
        {
            token = NewToken();
            cfg = new JsonObject
            {
                ["PADDOCK_AGENT_TOKEN"] = token,
                ["PADDOCK_AGENT_BIND"] = "0.0.0.0",
                ["PADDOCK_AGENT_PORT"] = opts.TryGetValue("port", out var port) && port != "" ? port : "11435",
                ["PADDOCK_DISK_PATHS"] = "",
                // HTTPS only; "on" also serves plain HTTP, for dashboards before 0.7.0
                ["PADDOCK_AGENT_HTTP"] = "off",
            };
        }
        // the service runs as another account: this is whose Ollama it reports on
        cfg["PADDOCK_OLLAMA_USER_SID"] = sid;
        Step("Writing the agent's settings");
        File.WriteAllText(agentJson, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        MakePrivate(DataDir);

        Step("Registering the Windows service");
        CreateOrUpdateService();
        // the tray app starts the user's Ollama for the service, from their session
        Step("Starting the tray app at sign-in");
        using (var run = Registry.LocalMachine.CreateSubKey(RunKey, true))
            run.SetValue(RunValue, $"\"{Path.Combine(ProgramDir, TrayExe)}\"");

        int agentPort = int.TryParse(cfg["PADDOCK_AGENT_PORT"]?.GetValue<string>(), out var n) ? n : 11435;
        if (!opts.ContainsKey("no-firewall"))
        {
            Step("Adding the firewall rule");
            Firewall(agentPort, opts.GetValueOrDefault("allow-from"), upgrade || legacy is not null, warnings);
        }

        try
        {
            Step("Starting the agent");
            StartService();
            Step("Waiting for the agent to answer");
            var health = WaitForHealth(agentPort);
            result["fingerprint"] = health["fingerprint"]?.GetValue<string>();
            result["version"] = health["version"]?.GetValue<string>();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            if (legacy is not null && hadTask)
            {
                StopService();
                Run("schtasks.exe", "/Change", "/TN", TaskName, "/ENABLE");
                Run("schtasks.exe", "/Run", "/TN", TaskName);
                throw new InvalidOperationException($"{e.Message}. The Node agent was started again. See {Path.Combine(DataDir, "logs", "agent.log")}");
            }
            throw new InvalidOperationException($"{e.Message}. See {Path.Combine(DataDir, "logs", "agent.log")}");
        }

        if (hadTask) Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
        if (legacy is not null)
        {
            // the dashboard pinned this key: the service presents the same one before the old copy goes
            if (SameFile(Path.Combine(legacy, "agent.key"), Path.Combine(DataDir, "agent.key")))
            {
                try { Directory.Delete(legacy, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"couldn't remove {legacy}: {e.Message}"); }
            }
            else warnings.Add($"kept {legacy}: its agent.key differs from the service's");
        }

        result["migrated"] = legacy is not null;
        result["upgraded"] = upgrade;
        result["port"] = agentPort;
        result["user"] = Profiles().First(p => p.Sid == sid).Name;
        var addresses = LanAddresses();
        result["addresses"] = addresses;
        result["hostname"] = Dns.GetHostName();
        result["url"] = $"https://{addresses.FirstOrDefault() ?? Dns.GetHostName()}:{agentPort}";
        // where the dashboard reaches Ollama itself: plain HTTP, Ollama has no HTTPS
        result["ollama_url"] = OllamaUrl(CurrentOllamaHost(OllamaUser.Find(AgentConfig.Load(DataDir))));
        if (token is not null) result["token"] = token;
    }

    /// <summary>
    /// The IPv4 addresses the dashboard can reach this PC on: connected adapters with a gateway, which
    /// leaves out loopback and the virtual switches of WSL and Hyper-V.
    /// </summary>
    static List<string> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .Where(ip => ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(ip => ip.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();

    static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static bool SameFile(string a, string b)
    {
        if (!File.Exists(a) || !File.Exists(b)) return !File.Exists(a);
        return SHA256.HashData(File.ReadAllBytes(a)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(b)));
    }

    static JsonObject ReadJson(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path).TrimStart('﻿')) as JsonObject ?? throw new InvalidOperationException($"{path} isn't a JSON object"); }
        catch (JsonException e) { throw new InvalidOperationException($"{path} is not valid JSON: {e.Message}"); }
    }

    /// <summary>The data folder holds the token and the HTTPS key: SYSTEM and administrators only.</summary>
    static void MakePrivate(string dir)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var who in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(who, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(acl);
        // files copied in keep their own lists: have them inherit the folder's
        foreach (var f in Directory.GetFiles(dir))
        {
            var fs = new FileSecurity();
            fs.SetAccessRuleProtection(false, false);
            new FileInfo(f).SetAccessControl(fs);
        }
    }

    // ---------------------------------------------------------------- Ollama on the network
    /// <summary>
    /// Ollama for Windows listens on localhost only, so the dashboard can't reach it until OLLAMA_HOST
    /// says otherwise and the firewall lets its port in. OLLAMA_HOST is set through the agent's own
    /// PUT /env, which restarts Ollama through the tray app, so it runs after the setup has started that.
    /// A value already set is the user's choice and stays.
    /// </summary>
    static void OllamaNetwork(Dictionary<string, string> opts, Dictionary<string, object?> result, List<string> warnings)
    {
        var cfg = AgentConfig.Load(DataDir);
        if (cfg.Token == "") throw new InvalidOperationException($"no agent.json in {DataDir}: install the agent first");
        var user = OllamaUser.Find(cfg) ?? throw new InvalidOperationException("the agent has no Ollama user: install the agent first");
        var current = CurrentOllamaHost(user);
        string host;
        if (!string.IsNullOrEmpty(current))
        {
            host = current;
            if (LoopbackOnly(current))
                warnings.Add($"OLLAMA_HOST is {current}, so Ollama only answers on this PC: set it to 0.0.0.0:{OllamaPort(current)} in the dashboard's Environment tab");
        }
        else
        {
            host = OllamaListenAll;
            Step("Letting Ollama listen on the network");
            SetOllamaHost(cfg, user, host, warnings);
        }
        if (!opts.ContainsKey("no-firewall"))
        {
            Step("Adding the firewall rule for Ollama");
            // the same addresses as the agent's rule, unless others are given
            FirewallRuleFor(OllamaFirewallRule, OllamaPort(host), opts.GetValueOrDefault("allow-from") is { Length: > 0 } a ? a : RuleAddresses(FirewallRule), warnings);
        }
        result["ollama_host"] = host;
        result["ollama_url"] = OllamaUrl(host);
    }

    /// <summary>
    /// Through the agent, which checks it like any change from the dashboard and restarts Ollama. The
    /// tray app may still be starting, so a 409 is tried again for a while; if it stays, the value is
    /// written directly and applies when Ollama next starts.
    /// </summary>
    static void SetOllamaHost(AgentConfig cfg, OllamaUser user, string host, List<string> warnings)
    {
        var env = CallAgent(cfg, HttpMethod.Get, "/env", null);
        if (env.Status != 200)
        {
            warnings.Add($"couldn't set OLLAMA_HOST: {Detail(env)}. Set it to {host} in the dashboard's Environment tab");
            return;
        }
        var vars = env.Body?["managed"]?.DeepClone() as JsonObject ?? new JsonObject();
        vars["OLLAMA_HOST"] = host;
        var body = new JsonObject { ["vars"] = vars, ["restart"] = true };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        (int Status, JsonNode? Body) put;
        while ((put = CallAgent(cfg, HttpMethod.Put, "/env", body)).Status == 409 && DateTime.UtcNow < deadline) Thread.Sleep(1000);
        if (put.Status == 200)
        {
            if (put.Body?["restart"]?["ok"]?.GetValue<bool>() == false)
                warnings.Add($"OLLAMA_HOST is set, but Ollama didn't answer after its restart: {put.Body["restart"]?["stderr"]}");
            return;
        }
        if (put.Status == 409 && user.SignedIn)
        {
            user.WriteUserVariables([KeyValuePair.Create<string, string?>("OLLAMA_HOST", host)]);
            warnings.Add($"OLLAMA_HOST is set to {host}, but the tray app isn't running in {user.Name}'s session to restart Ollama: restart Ollama, or sign out and in, for it to apply");
            return;
        }
        warnings.Add($"couldn't set OLLAMA_HOST: {Detail(put)}. Set it to {host} in the dashboard's Environment tab");
    }

    static string Detail((int Status, JsonNode? Body) r) => r.Body?["detail"]?.GetValue<string>() ?? $"the agent answered {r.Status}";

    /// <summary>The agent on the loopback interface, with its token: HTTPS, or plain HTTP if that's all it serves.</summary>
    static (int Status, JsonNode? Body) CallAgent(AgentConfig cfg, HttpMethod method, string path, JsonNode? body)
    {
        // only for this call to 127.0.0.1: the agent's certificate is self-signed
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
            { Timeout = TimeSpan.FromSeconds(90) };
        Exception? last = null;
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                using var req = new HttpRequestMessage(method, $"{scheme}://127.0.0.1:{cfg.Port}{path}");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.Token);
                if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var res = http.Send(req);
                var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                JsonNode? json = null;
                try { json = JsonNode.Parse(text); } catch (JsonException) { }
                return ((int)res.StatusCode, json);
            }
            catch (HttpRequestException e) { last = e; }
        }
        throw new InvalidOperationException($"the agent doesn't answer on port {cfg.Port}: {last?.Message}");
    }

    /// <summary>OLLAMA_HOST as Ollama sees it: the user's, else the PC's; null when neither sets it.</summary>
    static string? CurrentOllamaHost(OllamaUser? user)
    {
        if (user is null) return null;
        foreach (var scope in new[] { user.UserVariables(), user.MachineVariables() })
            if (scope?.GetValueOrDefault("OLLAMA_HOST") is { Length: > 0 } v) return v;
        return null;
    }

    [GeneratedRegex("^[a-z]+://", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();

    /// <summary>The port in OLLAMA_HOST ("0.0.0.0:11434", "http://host:8080", ":11434"), else Ollama's 11434.</summary>
    static int OllamaPort(string? host)
    {
        var m = Regex.Match(Scheme().Replace(host ?? "", ""), @":(\d{1,5})/?$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) && p is > 0 and < 65536 ? p : 11434;
    }

    /// <summary>Whether OLLAMA_HOST keeps Ollama to this PC: a loopback address or localhost.</summary>
    static bool LoopbackOnly(string host)
    {
        var h = Regex.Replace(Scheme().Replace(host, ""), @":\d{1,5}/?$", "").Trim('[', ']').ToLowerInvariant();
        return h == "localhost" || h == "::1" || h.StartsWith("127.", StringComparison.Ordinal);
    }

    static string OllamaUrl(string? host) => $"http://{LanAddresses().FirstOrDefault() ?? Dns.GetHostName()}:{OllamaPort(host)}";

    // ---------------------------------------------------------------- uninstall
    static void Uninstall(bool keepData, List<string> warnings)
    {
        StopService();
        if (ServiceExists()) Run("sc.exe", "delete", ServiceName);
        StopTrays();
        using (var run = Registry.LocalMachine.OpenSubKey(RunKey, true)) run?.DeleteValue(RunValue, false);
        if (TaskExists()) Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
        StopLegacyAgent();
        Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        // OLLAMA_HOST stays: it's one of the user's Ollama variables, which uninstalling leaves alone
        Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={OllamaFirewallRule}");
        if (!keepData)
        {
            try { Directory.Delete(DataDir, true); }
            catch (DirectoryNotFoundException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"couldn't remove {DataDir}: {e.Message}"); }
        }
    }

    // ---------------------------------------------------------------- service, processes, firewall
    static bool ServiceExists() => ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));

    static void CreateOrUpdateService()
    {
        var bin = $"\"{Path.Combine(ProgramDir, "paddock-agent.exe")}\"";
        var (code, output) = ServiceExists()
            ? Run("sc.exe", "config", ServiceName, "binPath=", bin, "obj=", "LocalSystem", "start=", "delayed-auto", "DisplayName=", "llama-paddock agent")
            : Run("sc.exe", "create", ServiceName, "binPath=", bin, "obj=", "LocalSystem", "start=", "delayed-auto", "DisplayName=", "llama-paddock agent");
        if (code != 0) throw new InvalidOperationException($"couldn't register the service: {output}");
        Run("sc.exe", "description", ServiceName, "Reports this PC and its Ollama to the llama-paddock dashboard, and changes Ollama's settings for it.");
        // restart after a crash, and when the agent exits with an error
        Run("sc.exe", "failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000");
        Run("sc.exe", "failureflag", ServiceName, "1");
    }

    static void StopService()
    {
        if (!ServiceExists()) return;
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    }

    static void StartService()
    {
        using var sc = new ServiceController(ServiceName);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    /// <summary>Every user's tray app: the setup replaces the program they run.</summary>
    static void StopTrays()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(TrayExe)))
        {
            using (p)
            {
                try { p.Kill(); p.WaitForExit(10_000); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* gone already */ }
            }
        }
    }

    static bool TaskExists() => Run("schtasks.exe", "/Query", "/TN", TaskName).Code == 0;

    /// <summary>The Node agent, run from a profile's %LOCALAPPDATA%\llama-paddock-agent by its scheduled task.</summary>
    static void StopLegacyAgent() =>
        Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
            @"Get-CimInstance Win32_Process -Filter ""Name = 'node.exe'"" | " +
            @"Where-Object { $_.CommandLine -like '*\AppData\Local\llama-paddock-agent\paddock-agent.mjs*' } | " +
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }");

    /// <summary>A fresh rule for the agent's port; on an upgrade the existing rule's addresses are kept unless new ones are given.</summary>
    static void Firewall(int port, string? allowFrom, bool keepExisting, List<string> warnings)
    {
        if (string.IsNullOrEmpty(allowFrom) && keepExisting) allowFrom = RuleAddresses(FirewallRule);
        FirewallRuleFor(FirewallRule, port, allowFrom, warnings);
    }

    /// <summary>The remote addresses an existing rule allows, or null.</summary>
    static string? RuleAddresses(string rule)
    {
        var show = Run("netsh.exe", "advfirewall", "firewall", "show", "rule", $"name={rule}");
        var m = Regex.Match(show.Output, @"RemoteIP:\s*(\S+)");
        return show.Code == 0 && m.Success ? m.Groups[1].Value : null;
    }

    static void FirewallRuleFor(string rule, int port, string? allowFrom, List<string> warnings)
    {
        if (string.IsNullOrEmpty(allowFrom)) allowFrom = "LocalSubnet";
        Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={rule}");
        var r = Run("netsh.exe", "advfirewall", "firewall", "add", "rule", $"name={rule}", "dir=in", "action=allow",
            "protocol=TCP", $"localport={port}", $"remoteip={allowFrom}", "profile=private,domain");
        if (r.Code != 0) warnings.Add($"couldn't add the firewall rule for TCP {port}: {r.Output.Trim()}");
    }

    /// <summary>/health on the loopback interface: HTTPS with the agent's own certificate, plain HTTP if that's all it serves.</summary>
    static JsonObject WaitForHealth(int port)
    {
        // only for this call to 127.0.0.1: the agent's certificate is self-signed
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
            { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var last = "no answer";
        while (DateTime.UtcNow < deadline)
        {
            foreach (var scheme in new[] { "https", "http" })
            {
                try
                {
                    if (JsonNode.Parse(http.GetStringAsync($"{scheme}://127.0.0.1:{port}/health").GetAwaiter().GetResult()) is JsonObject h
                        && h["ok"]?.GetValue<bool>() == true) return h;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { last = e.Message; }
            }
            Thread.Sleep(1000);
        }
        throw new InvalidOperationException($"the agent didn't answer on port {port} ({last})");
    }

    static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Result + errors);
    }
}
