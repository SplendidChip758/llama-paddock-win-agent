using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LlamaPaddock.Agent;

/// <summary>
/// Restarting Ollama with the current variables, as the Node agent does: stop the user's tray app,
/// server and model runners, start it again in their session, and wait for its API. The agent
/// stops Ollama itself; starting it goes through the session (IUserSession.Launch).
/// </summary>
public sealed partial class OllamaControl(AgentConfig cfg, IUserSession session, Env env)
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public async Task<object> Restart(CancellationToken ct)
    {
        if (cfg.DryRun) return new { ok = true, dry_run = true };
        var user = session.RequireUser();
        // never stop an Ollama that can't be started again
        if (!session.CanLaunch) throw HttpError.NoHelper();
        var (vars, customKeys) = env.OllamaVariables(user);

        Stop(user.Sid);
        for (int i = 0; i < 20 && Running(user.Sid); i++) await Task.Delay(500, ct);

        var host = ApiHost(vars.GetValueOrDefault("OLLAMA_HOST"));
        if (File.Exists(user.App))
        {
            await session.Launch(new(LaunchKind.Explorer, user.App, vars, customKeys), ct);
            if (await WaitForApi(host, 20_000, ct)) return Result(true, $"explorer.exe \"{user.App}\"", "");
            // Explorer didn't start it: start it directly with the variables read from the registry
            await session.Launch(new(LaunchKind.App, user.App, vars, customKeys), ct);
            bool ok = await WaitForApi(host, 20_000, ct);
            return Result(ok, user.App, ok ? "" : "Ollama did not answer on its API after restart");
        }
        // no desktop app (a standalone ollama.exe install): run the server directly
        var exe = Path.Combine(Path.GetDirectoryName(user.App) ?? "", "ollama.exe");
        var bin = File.Exists(exe) ? exe : "ollama.exe";
        await session.Launch(new(LaunchKind.Serve, bin, vars, customKeys), ct);
        bool served = await WaitForApi(host, 30_000, ct);
        return Result(served, $"{bin} serve", served ? "" : "Ollama did not answer on its API within 30s");
    }

    static object Result(bool ok, string launched, string stderr) => new { ok, launched, stderr };

    // ---------------------------------------------------------------- processes
    // Only the Ollama user's: as a service the agent could stop everyone's.
    static readonly string[] Names = ["ollama app", "ollama", "llama-server"];

    static IEnumerable<Process> Owned(string name, string sid)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            if (Native.ProcessUserSid(p.Id) == sid) yield return p;
            else p.Dispose();
        }
    }

    /// <summary>The tray app first, so it doesn't start the server again, then the server and its runners.</summary>
    static void Stop(string sid)
    {
        foreach (var name in Names)
        {
            foreach (var p in Owned(name, sid))
            {
                using (p)
                {
                    try { p.Kill(entireProcessTree: name == "ollama"); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* gone, or closed to us */ }
                }
            }
        }
    }

    static bool Running(string sid)
    {
        var found = Owned("ollama", sid).ToList();
        found.ForEach(p => p.Dispose());
        return found.Count > 0;
    }

    // ---------------------------------------------------------------- API
    [GeneratedRegex("^https?://")]
    private static partial Regex Scheme();

    /// <summary>Where Ollama will listen: OLLAMA_HOST as Ollama reads it, a wildcard address as localhost.</summary>
    public static string ApiHost(string? ollamaHost)
    {
        var host = string.IsNullOrEmpty(ollamaHost) ? "127.0.0.1:11434" : ollamaHost;
        host = Scheme().Replace(host, "", 1);
        if (host.StartsWith("0.0.0.0", StringComparison.Ordinal)) host = "127.0.0.1" + host[7..];
        else if (host.StartsWith("[::]", StringComparison.Ordinal)) host = "127.0.0.1" + host[4..];
        if (!host.Contains(':')) host += ":11434";
        return host;
    }

    static async Task<bool> WaitForApi(string host, int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var r = await http.GetAsync($"http://{host}/api/version", ct);
                if (r.IsSuccessStatusCode) return true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { /* not up yet */ }
            catch (UriFormatException) { return false; }
            await Task.Delay(1000, ct);
        }
        return false;
    }
}
