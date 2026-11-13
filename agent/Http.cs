using System.Text.Json;

namespace LlamaPaddock.Agent;

/// <summary>An error the client sees as its status and <c>{"detail": message}</c>, as in the Node agent.</summary>
public sealed class HttpError(int status, string detail) : Exception(detail)
{
    public int Status { get; } = status;

    // Windows loads a user's variables only while they're signed in: an empty list would be wrong
    public static HttpError NotSignedIn() => new(503, "the Ollama user isn't signed in");

    // starting Ollama and announcing new variables happen in the user's session, through the tray app
    public static HttpError NoHelper() => new(409, "sign in on the PC: Ollama runs in your session");

    public static HttpError UnknownUser() =>
        new(503, "can't tell which user runs Ollama: start Ollama, or run the agent's setup again");
}

/// <summary>Reading requests the way the Node agent does, so both answer a request the same.</summary>
public static class Http
{
    /// <summary>The first value of a query parameter, or null: URLSearchParams.get().</summary>
    public static string? Query(this HttpRequest req, string name) =>
        req.Query.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;

    /// <summary>The body as JSON, <c>{}</c> when empty; 400 when it isn't JSON, 413 over Kestrel's 1 MB limit.</summary>
    public static async Task<JsonElement> ReadJson(this HttpRequest req)
    {
        string text;
        try
        {
            using var reader = new StreamReader(req.Body);
            text = await reader.ReadToEndAsync(req.HttpContext.RequestAborted);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new HttpError(413, "body too large");
        }
        try
        {
            using var doc = JsonDocument.Parse(text == "" ? "{}" : text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new HttpError(400, "invalid JSON");
        }
    }
}
