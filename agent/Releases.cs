using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaPaddock.Agent;

/// <summary>
/// The Windows agent's releases on GitHub, for updates from the dashboard (Updater) and the tray
/// app's check (paddock-agent.exe check). Tests serve their own (PADDOCK_UPDATE_URL):
/// &lt;url&gt;/releases/latest, &lt;url&gt;/releases and &lt;url&gt;/releases/download/&lt;tag&gt;/&lt;file&gt;.
/// </summary>
public sealed partial class Releases
{
    public const string SetupName = "llama-paddock-agent-setup.exe";
    const string Repo = "SplendidChip758/llama-paddock-win-agent";

    readonly string apiBase;
    readonly string webBase;
    readonly Action<string> say;
    readonly HttpClient http;

    public Releases(string updateUrl, Action<string> say)
    {
        var url = updateUrl.TrimEnd('/');
        apiBase = url != "" ? url : $"https://api.github.com/repos/{Repo}";
        webBase = url != "" ? url : $"https://github.com/{Repo}";
        this.say = say;
        http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"llama-paddock-agent/{Api.Version}");
    }

    /// <summary>The channel an install follows unless told otherwise: dev for a prerelease, as the Linux installer does.</summary>
    public static string DefaultChannel => Api.Version.Contains('-') ? "dev" : "stable";

    /// <summary>Whether a release tag is a newer version than this one.</summary>
    public static bool IsNewer(string tag) => Semver.Compare(Semver.Parse(tag), Semver.Parse(Api.Version)) > 0;

    /// <summary>The release's page, for its notes.</summary>
    public string Page(string tag) => $"{webBase}/releases/tag/{tag}";

    /// <summary>The URL of a file of a release.</summary>
    public string File(string tag, string name) => $"{webBase}/releases/download/{tag}/{name}";

    /// <summary>
    /// The tag of the newest release on the channel: stable = the newest release, dev = the newest
    /// including prereleases, as the Linux installer picks them. GitHub's API allows 60 calls an hour
    /// per address, so the releases feed answers when it says no.
    /// </summary>
    public async Task<string?> Newest(string channel)
    {
        var url = channel == "dev" ? $"{apiBase}/releases?per_page=30" : $"{apiBase}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        HttpResponseMessage? res = null;
        say($"asking {url}");
        using (var api = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try { res = await http.SendAsync(req, api.Token); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { say($"GitHub's API didn't answer ({e.Message}); trying the releases feed"); }
        }
        using (res)
        {
            if (res?.StatusCode == HttpStatusCode.NotFound && channel == "stable") return null;
            if (res is { IsSuccessStatusCode: true })
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
                // a release gets its setup and SHA256SUMS a few minutes after it's published
                return Highest(list
                    .Where(r => !(r.TryGetProperty("draft", out var d) && d.GetBoolean()))
                    .Where(r => r.TryGetProperty("assets", out var a) && a.EnumerateArray().Count(x => x.GetProperty("name").GetString() is SetupName or "SHA256SUMS") == 2)
                    .Select(r => r.GetProperty("tag_name").GetString()!));
            }
            if (res is not null) say($"GitHub's API answered {(int)res.StatusCode}; trying the releases feed");
        }
        var feed = Js.Trim(System.Text.Encoding.UTF8.GetString(await Download($"{webBase}/releases.atom")));
        // the feed lists published releases only; prereleases are the tags with a "-"
        var tags = FeedTag().Matches(feed).Select(m => m.Groups[1].Value);
        return Highest(channel == "dev" ? tags : tags.Where(t => !t.Contains('-')));
    }

    [GeneratedRegex("href=\"https://github\\.com/[^\"]+/releases/tag/([^\"]+)\"")]
    private static partial Regex FeedTag();

    static string? Highest(IEnumerable<string> tags) =>
        tags.Select(t => (tag: t, ver: Semver.Parse(t))).Where(x => x.ver is not null)
            .OrderByDescending(x => x.ver!, Comparer<Semver>.Create(Semver.Compare)).Select(x => x.tag).FirstOrDefault();

    /// <summary>A download, tried again on GitHub's intermittent 5xx and timeouts.</summary>
    public async Task<byte[]> Download(string url)
    {
        int[] delays = [2, 5, 10, 0];
        for (int i = 0; ; i++)
        {
            try
            {
                say($"downloading {url}");
                using var res = await http.GetAsync(url);
                if (res.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException($"{url} isn't there (404)");
                if (res.IsSuccessStatusCode) return await res.Content.ReadAsByteArrayAsync();
                if (i == delays.Length - 1) throw new InvalidOperationException($"{url} answered {(int)res.StatusCode}");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (i == delays.Length - 1) throw new InvalidOperationException($"can't download {url}: {e.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(delays[i]));
        }
    }

    /// <summary>v1.2.3 and v1.2.3-dev.4, ordered as semantic versions: a prerelease before its release.</summary>
    sealed record Semver(int Major, int Minor, int Patch, string[]? Pre)
    {
        public static Semver? Parse(string tag)
        {
            var m = Regex.Match(tag, @"^v?(\d+)\.(\d+)\.(\d+)(?:-(.+))?$");
            if (!m.Success) return null;
            return new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                m.Groups[4].Success ? m.Groups[4].Value.Split('.') : null);
        }

        public static int Compare(Semver? a, Semver? b)
        {
            if (a is null || b is null) return (a is null ? 0 : 1) - (b is null ? 0 : 1);
            int c = a.Major.CompareTo(b.Major);
            if (c == 0) c = a.Minor.CompareTo(b.Minor);
            if (c == 0) c = a.Patch.CompareTo(b.Patch);
            if (c != 0) return c;
            if (a.Pre is null || b.Pre is null) return (a.Pre is null ? 0 : -1) - (b.Pre is null ? 0 : -1);
            for (int i = 0; i < Math.Max(a.Pre.Length, b.Pre.Length); i++)
            {
                if (i >= a.Pre.Length) return -1;
                if (i >= b.Pre.Length) return 1;
                var (x, y) = (a.Pre[i], b.Pre[i]);
                if (x == y) continue;
                return int.TryParse(x, out var nx) && int.TryParse(y, out var ny) ? nx.CompareTo(ny) : string.CompareOrdinal(x, y);
            }
            return 0;
        }
    }
}
