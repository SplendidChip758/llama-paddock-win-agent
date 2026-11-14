using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaPaddock.Agent;

/// <summary>
/// The named pipe between the service and the tray app, which runs in the Ollama user's session and
/// does there what the service can't: start Ollama and announce new variables. One request per
/// connection, as a line of JSON each way. Shared with the tray app.
///
/// Who's at the other end is checked both ways. The pipe's ACL lets in only its user and SYSTEM, and
/// the tray app serves only SYSTEM. The service only talks to a pipe served by the Ollama user's
/// own process, so another user can't stand in for their tray app by creating the pipe first.
/// </summary>
public static class HelperPipe
{
    public const string SystemSid = "S-1-5-18";

    /// <summary>One pipe per user: each signed-in user's tray app serves their own.</summary>
    public static string Name(string sid) => $"llama-paddock-agent-{sid}";

    public sealed record Request(string Op, LaunchRequest? Launch = null);
    public sealed record Response(bool Ok, string? Error = null);

    public const string Ping = "ping", NotifyEnvironment = "notify-env", Launch = "launch";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task WriteLine<T>(Stream pipe, T message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
    }

    /// <summary>A line, at most 1 MB, or null when the other end closed first.</summary>
    public static async Task<T?> ReadLine<T>(Stream pipe, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (await pipe.ReadAsync(one, ct) == 1)
        {
            if (one[0] == '\n') return JsonSerializer.Deserialize<T>(buffer.ToArray(), Json);
            if (buffer.Length >= 1_000_000) throw new InvalidDataException("message too long");
            buffer.WriteByte(one[0]);
        }
        return default;
    }

    /// <summary>
    /// Send a request to the tray app of the user with this SID. Null when it isn't there to answer:
    /// not running, not yet serving, or a pipe of that name served by anyone else.
    /// </summary>
    public static async Task<Response?> Send(string sid, Request request, TimeSpan answerWithin, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", Name(sid), PipeDirection.InOut, PipeOptions.Asynchronous,
            // the tray app runs as the user: it may learn who's calling, never act as SYSTEM
            TokenImpersonationLevel.Identification);
        try { await pipe.ConnectAsync(1000, ct); }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        if (Native.PipeServerProcessId(pipe.SafePipeHandle) is not { } pid || Native.ProcessUserSid(pid) != sid) return null;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(answerWithin);
        try
        {
            await WriteLine(pipe, request, limit.Token);
            return await ReadLine<Response>(pipe, limit.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidDataException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
