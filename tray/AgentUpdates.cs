using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace LlamaPaddock.Tray;

/// <summary>
/// Checking for and installing agent updates from the tray app. paddock-agent.exe does the work:
/// "check" as this user, "update" as an administrator after Windows' prompt, which is the same
/// verified update the dashboard starts. The update closes this app; the service starts it again,
/// and that one says how the update went (<see cref="Pending"/>).
/// </summary>
static class AgentUpdates
{
    public static readonly string Version = typeof(AgentUpdates).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";

    /// <summary>A prerelease follows prereleases, as the Linux installer's updater does.</summary>
    public static string Channel => Version.Contains('-') ? "dev" : "stable";

    static string AgentExe => Path.Combine(AppContext.BaseDirectory, "paddock-agent.exe");
    static string UpdateDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "llama-paddock-agent", "update");
    public static string UpdateLog => Path.Combine(UpdateDir, "update.log");

    public sealed record CheckResult(string Latest, bool Newer, string Page);

    /// <summary>The newest release on this install's channel. Throws why when it can't tell.</summary>
    public static async Task<CheckResult> Check()
    {
        var psi = new ProcessStartInfo(AgentExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("check");
        psi.ArgumentList.Add("--channel");
        psi.ArgumentList.Add(Channel);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var last = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "{}";
        using var doc = JsonDocument.Parse(last);
        var r = doc.RootElement;
        if (!r.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(r.TryGetProperty("error", out var e) ? e.GetString() : "the check failed");
        return new(r.GetProperty("latest").GetString() ?? "", r.GetProperty("newer").GetBoolean(), r.GetProperty("page").GetString() ?? "");
    }

    /// <summary>
    /// Install the newest release, after Windows asks for an administrator. Returns "updating" once
    /// the update has taken over, "current" when there's nothing newer, or null when the prompt was
    /// declined; throws why it failed.
    /// </summary>
    public static async Task<string?> Update()
    {
        var result = Path.Combine(Path.GetTempPath(), $"paddock-agent-update-{Guid.NewGuid():N}.ini");
        var psi = new ProcessStartInfo(AgentExe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in new[] { "update", "--channel", Channel, "--result", result }) psi.ArgumentList.Add(a);
        Remember();
        Process? p;
        try { p = Process.Start(psi); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: the prompt was declined
        {
            Forget();
            return null;
        }
        using (p) await p!.WaitForExitAsync();
        var values = ReadIni(result);
        File.Delete(result);
        if (values.GetValueOrDefault("ok") != "1")
        {
            Forget();
            throw new InvalidOperationException(values.GetValueOrDefault("error") is { Length: > 0 } error ? error : "the update didn't start");
        }
        if (values.GetValueOrDefault("state") != "updating") Forget();
        return values.GetValueOrDefault("state");
    }

    static Dictionary<string, string> ReadIni(string file)
    {
        try
        {
            return File.ReadAllLines(file).Where(l => l.Contains('='))
                .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
        }
        catch (IOException) { return []; }
    }

    // ---------------------------------------------------------------- after the update

    const string Key = @"Software\llama-paddock-agent";

    /// <summary>Note an update started here, so the tray app that comes back after it reports how it went.</summary>
    static void Remember()
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue("UpdatingFrom", Version);
        k.SetValue("UpdateStarted", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }

    static void Forget()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        k?.DeleteValue("UpdatingFrom", false);
        k?.DeleteValue("UpdateStarted", false);
    }

    /// <summary>
    /// How an update started from this app went, once it's over: "Updated to …", or why it didn't
    /// install. Null when no update was started here, or it took too long to tell. This app is closed
    /// and started again during an update (twice when it's rolled back), so it waits for the update's
    /// state, which users may read, rather than looking at its own version.
    /// </summary>
    public static async Task<(bool Ok, string Text)?> Pending(CancellationToken ct)
    {
        string? from;
        long started;
        using (var k = Registry.CurrentUser.OpenSubKey(Key))
        {
            from = k?.GetValue("UpdatingFrom") as string;
            if (from is null || !long.TryParse(k?.GetValue("UpdateStarted") as string, out started)) return null;
        }
        var deadline = DateTimeOffset.FromUnixTimeSeconds(started).AddMinutes(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = ReadState();
            // an older update's state until this one has written its own
            if (state is { } s && s.Started >= started - 60 && s.State is "done" or "failed")
            {
                Forget();
                return s.State == "done" && Version != from
                    ? (true, $"Updated to {Version}.")
                    : (false, $"The update didn't install{(s.Result is { Length: > 0 } r ? $": {r}" : "")}.");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        Forget();
        return null;
    }

    sealed record UpdateState(string State, double Started, string? Result);

    static UpdateState? ReadState()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(UpdateDir, "state.json")).TrimStart('﻿'));
            var r = doc.RootElement;
            return new(r.GetProperty("state").GetString() ?? "", r.GetProperty("started").GetDouble(),
                r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String ? res.GetString() : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
