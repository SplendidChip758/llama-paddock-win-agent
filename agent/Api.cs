using System.Net;

namespace LlamaPaddock.Agent;

/// <summary>
/// The agent's routes, as <c>"METHOD /path"</c> handlers like the Node agent's <c>routes</c> table.
/// A handler returns the 200 body or throws <see cref="HttpError"/>; Program.cs answers with either.
/// </summary>
public sealed class Api
{
    public const string Version = "0.0.0-dev";
    // The agent API's protocol, shared with the Linux agent. Unlike Version it only goes up when the API
    // changes in a way older dashboards can't follow, never for fixes or new optional fields.
    public const int Protocol = 1;

    readonly IUserSession session;
    readonly string? fingerprint;
    // the DNS name keeps its case; MachineName is the upper-case NetBIOS name
    readonly string hostname = Dns.GetHostName();

    public IReadOnlyDictionary<string, Func<HttpContext, Task<object>>> Routes { get; }

    readonly Stats stats;

    public Api(AgentConfig cfg, IUserSession session, string? fingerprint)
    {
        this.session = session;
        this.fingerprint = fingerprint;
        stats = new Stats(cfg, session, hostname);
        Routes = new Dictionary<string, Func<HttpContext, Task<object>>>
        {
            ["GET /health"] = _ => Done(new { ok = true, version = Version, protocol = Protocol, platform = "windows", hostname, fingerprint }),
            ["GET /stats"] = _ => stats.Get(Version, Protocol, fingerprint),
            ["GET /logs"] = ctx =>
            {
                // slice() drops the fraction of a number like 15.5
                var lines = (int)Math.Max(10, Math.Min(Or(Js.Number(ctx.Request.Query("lines")), 200), 2000));
                return Done(new { lines = OllamaLog.Tail(LogFile(), lines) });
            },
            ["GET /gpu-check"] = _ => Done(new { ts = Js.Now(), platform = "windows", log = OllamaLog.GpuLog(LogFile()) }),
            ["POST /gpu/reload-uvm"] = _ =>
                throw new HttpError(400, "nvidia_uvm is a Linux kernel module; on Windows, restart Ollama instead"),
            ["GET /runner-starts"] = ctx =>
            {
                double now = Js.Now();
                double since = Math.Max(now - 86400, Math.Min(Or(Js.Number(ctx.Request.Query("since")), now - 3600), now));
                return Done(Merge(new() { ["ts"] = now }, OllamaLog.RunnerStarts(LogFile(), since)));
            },
            // ?since=<unix seconds>: the requests after it, from Ollama's log
            ["GET /requests"] = ctx =>
            {
                double since = Js.Number(ctx.Request.Query("since"));
                return Done(OllamaLog.Requests(LogFile(), double.IsFinite(since) && since > 0 ? since : Js.Now() - 3600));
            },
        };
    }

    string LogFile() => session.RequireUser().LogFile;

    static Task<object> Done(object body) => Task.FromResult(body);

    /// <summary><c>x || fallback</c> for a number: NaN and 0 take the fallback.</summary>
    static double Or(double x, double fallback) => double.IsNaN(x) || x == 0 ? fallback : x;

    /// <summary><c>{ ...a, ...b }</c>, keeping the Node agent's field order.</summary>
    static Dictionary<string, object?> Merge(Dictionary<string, object?> a, object b)
    {
        foreach (var p in b.GetType().GetProperties()) a[p.Name] = p.GetValue(b);
        return a;
    }
}
