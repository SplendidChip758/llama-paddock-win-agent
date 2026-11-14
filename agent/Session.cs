namespace LlamaPaddock.Agent;

/// <summary>
/// How the agent reaches the Ollama user's session. Reading their log, models and variables works
/// from any account that may open them; announcing new variables and starting Ollama only work from
/// inside the session, where Explorer and the tray app run.
/// </summary>
public interface IUserSession
{
    /// <summary>"in-process" or "helper", for the log.</summary>
    string Mode { get; }

    /// <summary>Who runs Ollama right now, or null when the agent can't tell.</summary>
    OllamaUser? User();

    /// <summary>
    /// Whether work in the user's session can be done now. Checked before anything changes, so a
    /// 409 never leaves Ollama stopped or variables half applied.
    /// </summary>
    Task<bool> Ready(CancellationToken ct);

    /// <summary>Tell the session the user's variables changed, so Ollama started afterwards sees them.</summary>
    Task NotifyEnvironmentChanged(CancellationToken ct);

    /// <summary>Start Ollama in the user's session. Returns once it's started, not once it answers.</summary>
    Task Launch(LaunchRequest request, CancellationToken ct);
}

public static class UserSessionExtensions
{
    /// <summary>The Ollama user, or the 503 that says the agent can't tell who that is.</summary>
    public static OllamaUser RequireUser(this IUserSession session) => session.User() ?? throw HttpError.UnknownUser();
}

/// <summary>The agent runs as the Ollama user: development, Visual Studio and the comparison tests.</summary>
public sealed class InProcessSession(AgentConfig cfg) : IUserSession
{
    readonly OllamaUser user = OllamaUser.Self(cfg);

    public string Mode => "in-process";
    public OllamaUser? User() => user;
    public Task<bool> Ready(CancellationToken ct) => Task.FromResult(true);

    public Task NotifyEnvironmentChanged(CancellationToken ct)
    {
        Native.BroadcastEnvironmentChange();
        return Task.CompletedTask;
    }

    public Task Launch(LaunchRequest request, CancellationToken ct)
    {
        Launcher.Start(request);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The service: the user is looked up on each call, since they can sign out and another can start
/// Ollama. Work inside their session goes to their tray app (HelperPipe); while it isn't running,
/// that work answers 409.
/// </summary>
public sealed class HelperSession(AgentConfig cfg) : IUserSession
{
    public string Mode => "helper";
    public OllamaUser? User() => OllamaUser.Find(cfg);

    public async Task<bool> Ready(CancellationToken ct) =>
        User() is { } user && await HelperPipe.Send(user.Sid, new(HelperPipe.Ping), TimeSpan.FromSeconds(2), ct) is { Ok: true };

    public Task NotifyEnvironmentChanged(CancellationToken ct) => Send(new(HelperPipe.NotifyEnvironment), ct);

    public Task Launch(LaunchRequest request, CancellationToken ct) => Send(new(HelperPipe.Launch, request), ct);

    async Task Send(HelperPipe.Request request, CancellationToken ct)
    {
        var user = this.RequireUser();
        // announcing variables waits on every window in the session, which can take a few seconds
        var answer = await HelperPipe.Send(user.Sid, request, TimeSpan.FromSeconds(30), ct) ?? throw HttpError.NoHelper();
        if (!answer.Ok) throw new HttpError(500, $"the tray app couldn't do it: {answer.Error}");
    }
}
