using System.Diagnostics;
using System.Text.Json;

namespace LlamaPaddock.Agent;

/// <summary>
/// The tray apps an update closes. The setup stops them to replace their program, and an update
/// from the dashboard runs the setup as SYSTEM, which can't start them again in the users'
/// sessions. So the setup's stop step notes the sessions they ran in, and the service starts them
/// there again when it comes back.
/// </summary>
public static class TraySessions
{
    public const string TrayExe = "paddock-agent-tray.exe";
    static string File(string dataDir) => Path.Combine(dataDir, "tray-sessions.json");

    /// <summary>Stop every user's tray app, and note the sessions for <see cref="Restart"/>.</summary>
    public static void StopAndRemember(string dataDir)
    {
        var sessions = new SortedSet<int>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(TrayExe)))
        {
            using (p)
            {
                try
                {
                    sessions.Add(p.SessionId);
                    p.Kill();
                    p.WaitForExit(10_000);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* gone already */ }
            }
        }
        // a stop without tray apps keeps what an earlier one noted: the setup may run twice
        if (sessions.Count == 0) return;
        Directory.CreateDirectory(dataDir);
        System.IO.File.WriteAllText(File(dataDir), JsonSerializer.Serialize(sessions));
    }

    /// <summary>Start the tray app again in the sessions noted when it was stopped, once.</summary>
    public static void Restart(string programDir, string dataDir, ILogger log)
    {
        var file = File(dataDir);
        int[] sessions;
        try { sessions = JsonSerializer.Deserialize<int[]>(System.IO.File.ReadAllText(file)) ?? []; }
        catch (FileNotFoundException) { return; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning("can't read {File}: {Message}", file, e.Message);
            sessions = [];
        }
        System.IO.File.Delete(file);
        var exe = Path.Combine(programDir, TrayExe);
        foreach (var id in sessions)
        {
            try
            {
                // its one-per-session lock makes a second start harmless, after an interactive setup started it too
                if (Native.StartInSession(id, exe)) log.LogInformation("started the tray app again in session {Session}", id);
                else log.LogInformation("not starting the tray app in session {Session}: nobody is signed in to it", id);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                log.LogWarning("can't start the tray app in session {Session}: {Message}", id, e.Message);
            }
        }
    }
}
