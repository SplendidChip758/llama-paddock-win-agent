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

    public Api(IUserSession session, string? fingerprint)
    {
        this.session = session;
        this.fingerprint = fingerprint;
        Routes = new Dictionary<string, Func<HttpContext, Task<object>>>
        {
            ["GET /health"] = _ => Task.FromResult<object>(new { ok = true, version = Version, protocol = Protocol, platform = "windows", hostname, fingerprint }),
        };
    }
}
