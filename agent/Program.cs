using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LlamaPaddock.Agent;
using Microsoft.Extensions.Hosting.WindowsServices;

var dataDir = Environment.GetEnvironmentVariable("PADDOCK_AGENT_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "llama-paddock-agent");
var cfg = AgentConfig.Load(dataDir);
if (cfg.Token == "")
{
    Console.Error.WriteLine("PADDOCK_AGENT_TOKEN must be set (agent.json or environment)");
    return 1;
}
if (cfg.OllamaUserSid != "")
{
    try { _ = new SecurityIdentifier(cfg.OllamaUserSid); }
    catch (ArgumentException)
    {
        Console.Error.WriteLine($"PADDOCK_OLLAMA_USER_SID is not an account ID (S-1-5-21-…): {cfg.OllamaUserSid}");
        return 1;
    }
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "llama-paddock-agent");
var bootLog = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("agent");

Tls.Identity? tls = null;
try { tls = Tls.Load(cfg.TlsKey, cfg.TlsCert, bootLog); }
catch (Exception e) when (cfg.HttpOn)
{
    bootLog.LogError("can't set up TLS ({Message}); serving plain HTTP only", e.Message);
}
var fingerprint = tls?.Fingerprint;

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 1_000_000;
    k.Listen(IPAddress.Parse(cfg.Bind), cfg.Port, listen => SharedPort.Configure(listen, tls));
});

var app = builder.Build();
// Node's JSON.stringify: names as written, "+" and the like unescaped
var json = new JsonSerializerOptions { PropertyNamingPolicy = null, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

// A service runs as another account, so it reaches the Ollama user's session through the tray app;
// started any other way, the agent is that user.
IUserSession session = WindowsServiceHelpers.IsWindowsService() ? new HelperSession(cfg) : new InProcessSession(cfg);
var routes = new Api(session, fingerprint).Routes;

Task Send(HttpContext ctx, int status, object body)
{
    ctx.Response.StatusCode = status;
    return ctx.Response.WriteAsJsonAsync(body, json);
}

bool Authorized(HttpRequest req)
{
    var h = req.Headers.Authorization.ToString();
    var supplied = Encoding.UTF8.GetBytes(h.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : "");
    return CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(cfg.Token));
}

app.Run(async ctx =>
{
    var req = ctx.Request;
    // the fingerprint lets the dashboard offer the switch to https://
    if (!req.IsHttps && !cfg.HttpOn)
    {
        await Send(ctx, 426, new { detail = "this agent only accepts HTTPS: use https:// in the agent URL", fingerprint });
        return;
    }
    if (!routes.TryGetValue($"{req.Method} {req.Path}", out var handler))
    {
        await Send(ctx, 404, new { detail = "not found" });
        return;
    }
    if (req.Path != "/health" && !Authorized(req))
    {
        await Send(ctx, 401, new { detail = "invalid agent token" });
        return;
    }
    try { await Send(ctx, 200, await handler(ctx)); }
    catch (HttpError e) { await Send(ctx, e.Status, new { detail = e.Message }); }
    catch (Exception e) { await Send(ctx, 500, new { detail = e.Message }); }
});

var modes = string.Join(" + ", new[] { tls is null ? null : "HTTPS", cfg.HttpOn ? "HTTP" : null }.Where(m => m is not null));
app.Logger.LogInformation("llama-paddock agent {Version} (windows) listening on {Bind}:{Port} ({Modes}), key {Fingerprint}",
    Api.Version, cfg.Bind, cfg.Port, modes, fingerprint ?? "none");
var ollamaUser = session.User();
app.Logger.LogInformation("Ollama user: {User} ({Mode}), log {Log}",
    ollamaUser?.Name ?? "unknown until Ollama runs", session.Mode, ollamaUser?.LogFile ?? "-");
app.Run();
return 0;
