using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaPaddock.Agent;

/// <summary>
/// GET and POST /update: the dashboard updates the agent, as it does the Linux agent. The agent finds
/// the newest release on the channel, downloads the setup and checks it against the release's
/// SHA256SUMS. The setup stops this service, so a runner outside it (update.ps1) runs the setup,
/// and puts the previous programs back when the setup fails or the new agent doesn't stay up.
/// Everything lives in update\ in the data folder, which only SYSTEM and administrators can change:
/// nobody else can swap the setup between the check and the run.
/// </summary>
public sealed partial class Updater
{
    const string SetupName = "llama-paddock-agent-setup.exe";
    const string Repo = "SplendidChip758/llama-paddock-win-agent";
    const int LogLines = 40;

    readonly string dir;
    readonly string programDir;
    readonly string apiBase;
    readonly string webBase;
    readonly bool canUpdate;
    readonly bool dryRun;
    readonly ILogger log;
    readonly Lock gate = new();
    readonly HttpClient http;

    public Updater(AgentConfig cfg, bool canUpdate, ILogger log)
    {
        dir = Path.Combine(cfg.DataDir, "update");
        programDir = AppContext.BaseDirectory.TrimEnd('\\');
        // tests serve their own releases: <url>/releases/latest, <url>/releases, <url>/releases/download/<tag>/<file>
        var url = cfg.Get("PADDOCK_UPDATE_URL").TrimEnd('/');
        apiBase = url != "" ? url : $"https://api.github.com/repos/{Repo}";
        webBase = url != "" ? url : $"https://github.com/{Repo}";
        this.canUpdate = canUpdate;
        dryRun = cfg.DryRun;
        this.log = log;
        http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"llama-paddock-agent/{Api.Version}");
    }

    /// <summary>For /health and /stats: whether the dashboard can update this agent.</summary>
    public bool SelfUpdate => canUpdate || dryRun;

    string StateFile => Path.Combine(dir, "state.json");
    string LogFile => Path.Combine(dir, "update.log");

    sealed class State
    {
        public string state { get; set; } = "idle";
        public string channel { get; set; } = "stable";
        public string from { get; set; } = "";
        public string? tag { get; set; }
        public double started { get; set; }
        /// <summary>The process doing the work: this agent while it downloads, then the runner.</summary>
        public int pid { get; set; }
        public double pid_started { get; set; }
        public string? result { get; set; }
        public double? ended { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    State? Read()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile).TrimStart('﻿'), Json); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning("can't read {File}: {Message}", StateFile, e.Message);
            return null;
        }
    }

    void Write(State s)
    {
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        File.Move(tmp, StateFile, overwrite: true);
    }

    void Say(string text)
    {
        log.LogInformation("update: {Text}", text);
        try { File.AppendAllText(LogFile, $"{DateTime.Now:s} {text}\n"); }
        catch (IOException) { /* the agent's log has it */ }
    }

    static double StartTime(Process p) => new DateTimeOffset(p.StartTime).ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>Whether the process that was doing the update still is; a PID can be reused, its start time not.</summary>
    static bool Alive(State s)
    {
        try
        {
            using var p = Process.GetProcessById(s.pid);
            return Math.Abs(StartTime(p) - s.pid_started) < 1;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>GET /update: idle, running, or failed with why and the end of the update's log.</summary>
    public object Status()
    {
        if (dryRun) return new { state = "idle", dry_run = true };
        State? s;
        lock (gate) s = Current();
        return s?.state switch
        {
            "running" => new { state = "running" },
            "failed" => new { state = "failed", result = s.result, log = LogTail() },
            // a finished update looks like no update, as on Linux; the dashboard compares versions
            _ => (object)new { state = "idle" },
        };
    }

    /// <summary>The state, with an update whose process is gone marked as failed.</summary>
    State? Current()
    {
        var s = Read();
        if (s is { state: "running" } && !Alive(s))
        {
            s.state = "failed";
            s.result = "the update stopped before it finished: the PC restarted, or the updater was ended";
            s.ended = Js.Now();
            Write(s);
        }
        return s;
    }

    List<string> LogTail()
    {
        try
        {
            var lines = File.ReadAllLines(LogFile).Select(l => l.TrimStart('﻿')).Where(l => l != "").ToList();
            return lines.Skip(Math.Max(0, lines.Count - LogLines)).ToList();
        }
        catch (IOException) { return []; }
    }

    /// <summary>POST /update {channel}: start updating to the newest release on the channel.</summary>
    public object Start(JsonElement body)
    {
        var name = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("channel", out var c) ? c : default;
        var channel = name.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => "stable",
            JsonValueKind.String when name.GetString() is "stable" or "dev" => name.GetString()!,
            _ => throw new HttpError(400, "channel must be \"stable\" or \"dev\""),
        };
        if (!SelfUpdate) throw new HttpError(409, "only the agent's Windows service can update itself: install the agent with its setup");
        if (dryRun) return new { ok = true, dry_run = true, channel };
        lock (gate)
        {
            if (Current() is { state: "running" }) throw new HttpError(409, "an update is already running");
            Directory.CreateDirectory(dir);
            File.WriteAllText(LogFile, "");
            using var self = Process.GetCurrentProcess();
            Write(new State
            {
                state = "running", channel = channel, from = Api.Version, started = Js.Now(),
                pid = self.Id, pid_started = StartTime(self),
            });
        }
        _ = Task.Run(() => Run(channel));
        return new { ok = true, channel };
    }

    async Task Run(string channel)
    {
        try
        {
            Say($"looking for the newest {channel} release");
            var tag = await Newest(channel);
            if (tag is null)
            {
                Finish(channel == "stable" ? "there is no stable release yet" : "there is no release yet", failed: true);
                return;
            }
            Update(s => s.tag = tag);
            if (tag.TrimStart('v') == Api.Version)
            {
                Say($"already on {tag}");
                Finish("already on the newest release", failed: false);
                return;
            }
            Say($"downloading {tag}");
            var setup = Path.Combine(dir, SetupName);
            var sums = await Download($"{webBase}/releases/download/{tag}/SHA256SUMS");
            var want = ExpectedHash(System.Text.Encoding.UTF8.GetString(sums))
                ?? throw new InvalidOperationException($"{tag}'s SHA256SUMS doesn't list {SetupName}");
            var bytes = await Download($"{webBase}/releases/download/{tag}/{SetupName}");
            var got = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (got != want) throw new InvalidOperationException($"the downloaded setup doesn't match {tag}'s SHA256SUMS: nothing was installed");
            await File.WriteAllBytesAsync(setup, bytes);
            Say($"the setup matches SHA256SUMS ({got[..12]}…)");

            var script = Path.Combine(dir, "update.ps1");
            await File.WriteAllTextAsync(script, RunnerScript);
            var pid = Native.StartDetached("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Dir", dir, "-App", programDir, "-Setup", setup],
                null, dir, hidden: true);
            using var runner = Process.GetProcessById(pid);
            var startedAt = StartTime(runner);
            Update(s => { s.pid = pid; s.pid_started = startedAt; });
            Say($"started the updater (PID {pid}); the agent restarts now");
        }
        catch (Exception e)
        {
            Say($"failed: {e.Message}");
            Finish(e.Message, failed: true);
        }
    }

    void Update(Action<State> change)
    {
        lock (gate)
        {
            var s = Read() ?? new State();
            change(s);
            Write(s);
        }
    }

    void Finish(string result, bool failed) => Update(s =>
    {
        s.state = failed ? "failed" : "done";
        s.result = result;
        s.ended = Js.Now();
    });

    /// <summary>The SHA256SUMS line for the setup: "&lt;hash&gt;  llama-paddock-agent-setup.exe".</summary>
    static string? ExpectedHash(string sums) =>
        sums.Split('\n').Select(l => l.Trim().Split(' ', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == SetupName)?[0].ToLowerInvariant();

    // ---------------------------------------------------------------- releases

    /// <summary>
    /// The tag of the newest release on the channel: stable = the newest release, dev = the newest
    /// including prereleases, as the Linux installer picks them. GitHub's API allows 60 calls an hour
    /// per address, so the releases feed answers when it says no.
    /// </summary>
    async Task<string?> Newest(string channel)
    {
        var url = channel == "dev" ? $"{apiBase}/releases?per_page=30" : $"{apiBase}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        HttpResponseMessage? res = null;
        Say($"asking {url}");
        using (var api = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try { res = await http.SendAsync(req, api.Token); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Say($"GitHub's API didn't answer ({e.Message}); trying the releases feed"); }
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
            if (res is not null) Say($"GitHub's API answered {(int)res.StatusCode}; trying the releases feed");
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
    async Task<byte[]> Download(string url)
    {
        int[] delays = [2, 5, 10, 0];
        for (int i = 0; ; i++)
        {
            try
            {
                Say($"downloading {url}");
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

    // ---------------------------------------------------------------- the runner

    /// <summary>
    /// Runs outside the service, which the setup stops (Windows PowerShell, on every Windows). Keeps a
    /// copy of the programs, runs the setup, and puts the copy back when the setup fails or the new
    /// agent doesn't stay up. Either way the agent runs again, and state.json says how it went.
    /// </summary>
    const string RunnerScript = """
        # Written and started by the agent (Updater.cs) to update it from the dashboard.
        param([string]$Dir, [string]$App, [string]$Setup)
        $ErrorActionPreference = 'Continue'
        $service = 'llama-paddock-agent'
        $state = Join-Path $Dir 'state.json'
        $log = Join-Path $Dir 'update.log'
        $setupLog = Join-Path $Dir 'setup.log'
        $previous = Join-Path $Dir 'previous'

        function Say([string]$text) { Add-Content -LiteralPath $log -Value "$((Get-Date).ToString('s')) $text" -Encoding UTF8 }
        function Finish([string]$to, [string]$result) {
          $s = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
          $s.state = $to
          $s.result = $result
          $s.ended = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000
          $s | ConvertTo-Json -Compress | Set-Content -LiteralPath "$state.tmp" -Encoding UTF8
          Move-Item -Force -LiteralPath "$state.tmp" $state
        }
        # running, and still running a little later: an agent that can't start stops again at once
        function Up {
          for ($i = 0; $i -lt 60 -and (Get-Service $service -ErrorAction SilentlyContinue).Status -ne 'Running'; $i++) { Start-Sleep 1 }
          Start-Sleep 10
          (Get-Service $service -ErrorAction SilentlyContinue).Status -eq 'Running'
        }
        function Mirror([string]$from, [string]$to) {
          robocopy $from $to /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
          $LASTEXITCODE -lt 8
        }

        Say "keeping a copy of $App"
        if (-not (Mirror $App $previous)) {
          Say 'copying the programs failed'
          Finish 'failed' "couldn't keep a copy of the programs to go back to, so nothing was changed"
          exit 1
        }

        Say 'running the setup'
        $p = Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/UPDATE=1', "/LOG=`"$setupLog`"" -PassThru
        $null = $p.Handle  # without it, ExitCode stays empty
        if ($p.WaitForExit(600000)) { $code = $p.ExitCode } else { Stop-Process -Id $p.Id -Force; $code = 'none: it ran for 10 minutes' }
        Say "the setup exited with $code"
        if ($code -eq 0 -and (Up)) {
          Say 'the new agent is running'
          Remove-Item -Recurse -Force -LiteralPath $previous -ErrorAction SilentlyContinue
          Finish 'done' $null
          exit 0
        }

        $why = if ($code -ne 0) { "the setup failed (exit code $code)" } else { "the new agent didn't keep running" }
        Say "$why; putting the previous version back"
        if (Test-Path -LiteralPath $setupLog) { Get-Content -LiteralPath $setupLog -Tail 15 | ForEach-Object { Say "setup: $_" } }
        # the new agent's own stop notes the tray apps' sessions; if it can't run, stop them here
        & (Join-Path $App 'paddock-agent.exe') stop *> $null
        Stop-Service $service -Force -ErrorAction SilentlyContinue
        Get-Process paddock-agent-tray -ErrorAction SilentlyContinue | Stop-Process -Force
        $back = (Mirror $previous $App)
        if ($back) {
          Start-Service $service -ErrorAction SilentlyContinue
          $back = Up
        }
        if ($back) {
          Say 'the previous version is running again'
          Finish 'failed' "$why; the previous version was put back"
        } else {
          Say 'the previous version did not start either'
          Finish 'failed' "$why, and the previous version didn't start again either: run the setup on the PC"
        }
        exit 1
        """;
}
