using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

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
public static partial class Http
{
    /// <summary>The first value of a query parameter, or null: URLSearchParams.get().</summary>
    public static string? Query(this HttpRequest req, string name) =>
        req.Query.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;

    /// <summary>
    /// JavaScript's <c>Number(s)</c>: null and blank give 0, anything that isn't a number NaN. The routes
    /// keep the Node agent's fallbacks (<c>Number(x) || default</c>, <c>Number.isFinite</c>) on top.
    /// </summary>
    public static double JsNumber(string? s)
    {
        if (s is null) return 0;
        s = s.Trim().Trim('﻿').Trim();
        if (s == "") return 0;
        switch (s)
        {
            case "Infinity" or "+Infinity": return double.PositiveInfinity;
            case "-Infinity": return double.NegativeInfinity;
        }
        if (s.Length > 2 && s[0] == '0' && "xXoObB".Contains(s[1]))
        {
            int radix = char.ToLowerInvariant(s[1]) switch { 'x' => 16, 'o' => 8, _ => 2 };
            double n = 0;
            foreach (char c in s.AsSpan(2))
            {
                int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : 99;
                if (d >= radix) return double.NaN;
                n = n * radix + d;
            }
            return n;
        }
        return DecimalLiteral().IsMatch(s) ? double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) : double.NaN;
    }

    [GeneratedRegex(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")]
    private static partial Regex DecimalLiteral();

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
