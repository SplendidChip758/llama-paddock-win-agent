using System.Collections;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace LlamaPaddock.Agent;

/// <summary>
/// The user who runs Ollama, and where Ollama keeps its files for them. Ollama for Windows is a tray
/// app in that user's session, so its log, models and variables are theirs, not the agent's: the
/// service runs as another account.
/// </summary>
public sealed class OllamaUser
{
    // true: the agent runs as this user and reads everything the way the user's own programs do
    readonly bool self;

    public string Sid { get; }
    public string Profile { get; }
    public string LocalAppData { get; }
    /// <summary>The Ollama desktop app: PADDOCK_OLLAMA_APP, or where its installer puts it.</summary>
    public string App { get; }
    public string LogFile => Path.Combine(LocalAppData, "Ollama", "server.log");

    OllamaUser(bool self, string sid, string profile, string localAppData, string app)
    {
        this.self = self;
        Sid = sid;
        Profile = profile;
        LocalAppData = localAppData;
        App = app;
    }

    /// <summary>The agent's own account, for running it in-process (development, the comparison tests).</summary>
    public static OllamaUser Self(AgentConfig cfg)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Combine(profile, "AppData", "Local");
        return new OllamaUser(true, WindowsIdentity.GetCurrent().User!.Value, profile, local, AppPath(cfg, local));
    }

    /// <summary>
    /// For the service: the account the setup recorded, else the owner of the running Ollama. Null
    /// when there's neither, or the account has no profile on this PC.
    /// </summary>
    public static OllamaUser? Find(AgentConfig cfg)
    {
        var sid = cfg.OllamaUserSid is { Length: > 0 } s ? s : RunningOllamaOwner();
        if (sid is null) return null;
        using var entry = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        if (entry?.GetValue("ProfileImagePath") is not string dir || dir == "") return null;
        var profile = Environment.ExpandEnvironmentVariables(dir);
        // Volatile Environment exists while they're signed in and follows a redirected AppData
        var local = Volatile(sid).GetValueOrDefault("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Combine(profile, "AppData", "Local");
        return new OllamaUser(false, sid, profile, local, AppPath(cfg, local));
    }

    static string AppPath(AgentConfig cfg, string localAppData) =>
        cfg.OllamaApp is { Length: > 0 } app ? app : Path.Combine(localAppData, "Programs", "Ollama", "ollama app.exe");

    /// <summary>
    /// The owner of the tray app, else of the server (an install without the tray app). Only user
    /// accounts count: an ollama.exe running as a service has no session to work in.
    /// </summary>
    static string? RunningOllamaOwner()
    {
        foreach (var name in new[] { "ollama app", "ollama" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (Native.ProcessUserSid(p.Id) is { } sid && sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) return sid;
                }
            }
        }
        return null;
    }

    /// <summary>"DOMAIN\name" for logs, or the SID when Windows can't name it.</summary>
    public string Name
    {
        get
        {
            try { return new SecurityIdentifier(Sid).Translate(typeof(NTAccount)).Value; }
            catch (IdentityNotMappedException) { return Sid; }
            catch (SystemException) { return Sid; }
        }
    }

    /// <summary>Whether Windows has loaded their settings, which it does only while they're signed in.</summary>
    public bool SignedIn
    {
        get
        {
            if (self) return true;
            using var key = Registry.Users.OpenSubKey(Sid);
            return key is not null;
        }
    }

    /// <summary>The user's own variables (HKCU\Environment), expanded; null while they're signed out.</summary>
    public Dictionary<string, string>? UserVariables()
    {
        if (self) return FromProcess(EnvironmentVariableTarget.User);
        using var key = Registry.Users.OpenSubKey($@"{Sid}\Environment");
        if (key is null) return null;
        var lookup = ExpansionBase();
        return Read(key, lookup);
    }

    /// <summary>The PC's variables, expanded the way the user's programs see them.</summary>
    public Dictionary<string, string> MachineVariables()
    {
        if (self) return FromProcess(EnvironmentVariableTarget.Machine);
        return MachineRaw(ExpansionBase());
    }

    /// <summary>Where Ollama keeps models: OLLAMA_MODELS (the user's, then the PC's), else ~\.ollama\models.</summary>
    public string ModelsDir()
    {
        foreach (var scope in new[] { UserVariables(), MachineVariables() })
            if (scope?.GetValueOrDefault("OLLAMA_MODELS") is { Length: > 0 } dir) return dir;
        return Path.Combine(Profile, ".ollama", "models");
    }

    // The same as the Node agent's [Environment]::GetEnvironmentVariables(scope)
    static Dictionary<string, string> FromProcess(EnvironmentVariableTarget target)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables(target))
            vars[(string)e.Key] = e.Value as string ?? "";
        return vars;
    }

    /// <summary>
    /// What the user's %VARS% expand to, built like Windows builds a new session's environment: the
    /// service's own basics (SystemRoot, ProgramFiles, …), the PC's variables, then the user's profile
    /// and session values. The service's own USERPROFILE and the like are SYSTEM's, so they're replaced.
    /// </summary>
    Dictionary<string, string> ExpansionBase()
    {
        var lookup = FromProcess(EnvironmentVariableTarget.Process);
        foreach (var k in new[] { "USERPROFILE", "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP" })
            lookup.Remove(k);
        foreach (var (k, v) in MachineRaw(lookup)) lookup[k] = v;
        lookup["USERPROFILE"] = Profile;
        lookup["APPDATA"] = Path.Combine(Profile, "AppData", "Roaming");
        lookup["LOCALAPPDATA"] = LocalAppData;
        foreach (var (k, v) in Volatile(Sid)) lookup[k] = v;
        return lookup;
    }

    static Dictionary<string, string> MachineRaw(Dictionary<string, string> lookup)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        return key is null ? new(StringComparer.OrdinalIgnoreCase) : Read(key, lookup);
    }

    static Dictionary<string, string> Volatile(string sid)
    {
        using var key = Registry.Users.OpenSubKey($@"{sid}\Volatile Environment");
        return key is null ? new(StringComparer.OrdinalIgnoreCase) : Read(key, new(StringComparer.OrdinalIgnoreCase));
    }

    static Dictionary<string, string> Read(RegistryKey key, Dictionary<string, string> lookup)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetValueNames())
        {
            if (name == "") continue;
            var raw = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            vars[name] = key.GetValueKind(name) == RegistryValueKind.ExpandString ? Expand(raw, lookup) : raw;
        }
        return vars;
    }

    /// <summary>%NAME% from the lookup; unknown names stay as written, as in Windows.</summary>
    static string Expand(string text, Dictionary<string, string> lookup)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf('%', i);
            int end = start < 0 ? -1 : text.IndexOf('%', start + 1);
            if (end < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, start - i);
            var name = text[(start + 1)..end];
            if (name != "" && lookup.TryGetValue(name, out var value))
            {
                sb.Append(value);
                i = end + 1;
            }
            else
            {
                // keep the first % and look for a name from the second one on
                sb.Append('%').Append(name);
                i = end;
            }
        }
        return sb.ToString();
    }
}
