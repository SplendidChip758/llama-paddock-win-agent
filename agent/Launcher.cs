using System.Collections;

namespace LlamaPaddock.Agent;

public enum LaunchKind
{
    /// <summary>The desktop app through Explorer, as the Start menu starts it.</summary>
    Explorer,
    /// <summary>The desktop app started directly, with the environment rebuilt from the registry.</summary>
    App,
    /// <summary><c>ollama.exe serve</c>, for an install without the desktop app.</summary>
    Serve,
}

/// <summary>
/// What to start, and the Ollama variables it should see: the PC's and the user's, read just now.
/// The launcher replaces any Ollama variables in its own environment with them (OllamaNames.IsManaged
/// with CustomKeys), so a change made since it started counts.
/// </summary>
public sealed record LaunchRequest(LaunchKind Kind, string Exe, Dictionary<string, string> Vars, List<string> CustomKeys);

/// <summary>Starts Ollama from inside the user's session: the in-process agent, and the tray app for the service.</summary>
public static class Launcher
{
    public static void Start(LaunchRequest r)
    {
        switch (r.Kind)
        {
            // Explorer has already reloaded the user's variables when it was told they changed, and
            // Ollama started this way isn't in the agent's process tree
            case LaunchKind.Explorer:
                Native.StartDetached("explorer.exe", [r.Exe], null, null, hidden: false);
                break;
            case LaunchKind.App:
                Native.StartDetached(r.Exe, [], Environment(r), Path.GetDirectoryName(r.Exe), hidden: false);
                break;
            case LaunchKind.Serve:
                Native.StartDetached(r.Exe, ["serve"], Environment(r), null, hidden: true);
                break;
        }
    }

    /// <summary>
    /// The environment a freshly signed-in Ollama would see: the launcher's own, with Ollama's
    /// variables replaced by the current values.
    /// </summary>
    static Dictionary<string, string> Environment(LaunchRequest r)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
        {
            var k = (string)e.Key;
            if (!OllamaNames.IsManaged(k, r.CustomKeys)) env[k] = e.Value as string ?? "";
        }
        foreach (var (k, v) in r.Vars) env[k] = v;
        return env;
    }
}
