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

    /// <summary>Tell the session the user's variables changed, so Ollama started afterwards sees them.</summary>
    Task NotifyEnvironmentChanged(CancellationToken ct);
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

    public Task NotifyEnvironmentChanged(CancellationToken ct)
    {
        Native.BroadcastEnvironmentChange();
        return Task.CompletedTask;
    }
}

/// <summary>
/// The service: the user is looked up on each call, since they can sign out and another can start
/// Ollama. Work inside their session goes to the tray app over a named pipe that only they and
/// SYSTEM can open. Until the tray app answers there, that work answers 409.
/// </summary>
public sealed class HelperSession(AgentConfig cfg) : IUserSession
{
    public string Mode => "helper";
    public OllamaUser? User() => OllamaUser.Find(cfg);

    // the pipe to the tray app is built with service mode
    public Task NotifyEnvironmentChanged(CancellationToken ct) => throw HttpError.NoHelper();
}
