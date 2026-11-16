namespace LlamaPaddock.Agent;

/// <summary>
/// The tray app's About window: check (as the user) whether a newer release is out, and update (as
/// an administrator, after Windows' prompt) the same way the dashboard does. Both take --channel
/// stable or dev; without it, the channel this version came from.
/// </summary>
public static partial class Installer
{
    static string Channel(Dictionary<string, string> opts)
    {
        var channel = opts.TryGetValue("channel", out var c) ? c : Releases.DefaultChannel;
        return channel is "stable" or "dev" ? channel : throw new InvalidOperationException("--channel must be stable or dev");
    }

    static void Check(Dictionary<string, string> opts, Dictionary<string, object?> result)
    {
        var channel = Channel(opts);
        // agent.json is for administrators only, so the release source comes from the environment (tests)
        var releases = new Releases(Environment.GetEnvironmentVariable("PADDOCK_UPDATE_URL") ?? "", _ => { });
        var tag = releases.Newest(channel).GetAwaiter().GetResult();
        result["current"] = Api.Version;
        result["channel"] = channel;
        result["latest"] = tag?.TrimStart('v') ?? "";
        result["newer"] = tag is not null && Releases.IsNewer(tag);
        result["page"] = tag is null ? "" : releases.Page(tag);
    }

    static void Update(Dictionary<string, string> opts, Dictionary<string, object?> result)
    {
        var channel = Channel(opts);
        Step($"Updating to the newest {channel} release");
        var updater = new Updater(AgentConfig.Load(DataDir), canUpdate: true, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        // "updating": the runner has the setup; the service, and the tray apps, restart in a moment
        result["state"] = updater.RunHere(channel).GetAwaiter().GetResult();
        result["log"] = Path.Combine(DataDir, "update", "update.log");
    }
}
