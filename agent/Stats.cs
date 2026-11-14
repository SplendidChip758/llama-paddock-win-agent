using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;

namespace LlamaPaddock.Agent;

/// <summary>
/// GET /stats: the system, nvidia-smi and Ollama's processes, with the Node agent's fields. Windows
/// is read directly, where the Node agent started tasklist and netstat.
/// </summary>
public sealed class Stats : IDisposable
{
    readonly AgentConfig cfg;
    readonly IUserSession session;
    readonly string hostname;
    readonly Timer cpuTimer;

    public Stats(AgentConfig cfg, IUserSession session, string hostname)
    {
        this.cfg = cfg;
        this.session = session;
        this.hostname = hostname;
        cpuPrev = Native.CpuTimesPerCore();
        cpuNow = new(0, new double[cpuPrev.Length]);
        cpuTimer = new Timer(_ => SampleCpu(), null, 2000, 2000);
    }

    public void Dispose() => cpuTimer.Dispose();

    // ---------------------------------------------------------------- CPU
    // Sampled every 2 s, as the Node agent does: a request then reads the last 2 s, not the time
    // since the previous request.
    Native.CpuTimes[] cpuPrev;
    sealed record Cpu(double Total, double[] PerCore);
    volatile Cpu cpuNow;

    void SampleCpu()
    {
        var cur = Native.CpuTimesPerCore();
        long busyAll = 0, totalAll = 0;
        var perCore = new double[cur.Length];
        for (int i = 0; i < cur.Length; i++)
        {
            var p = i < cpuPrev.Length ? cpuPrev[i] : cur[i];
            long total = cur[i].Total - p.Total, idle = cur[i].Idle - p.Idle;
            busyAll += total - idle;
            totalAll += total;
            perCore[i] = total > 0 ? Js.Fixed1((double)(total - idle) / total * 100) : 0;
        }
        cpuNow = new(totalAll > 0 ? Js.Fixed1((double)busyAll / totalAll * 100) : 0, perCore);
        cpuPrev = cur;
    }

    // ---------------------------------------------------------------- network
    (double T, long Rx, long Tx)? netPrev;
    readonly Lock netLock = new();

    /// <summary>Bytes per second over all interfaces since the previous request; null on the first.</summary>
    object NetRates()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var s = nic.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch (NetworkInformationException) { return new { rx_bps = (double?)null, tx_bps = (double?)null }; }
        double now = Js.Now();
        double? rxBps = null, txBps = null;
        lock (netLock)
        {
            if (netPrev is { } prev && now > prev.T)
            {
                rxBps = Math.Max(0, (rx - prev.Rx) / (now - prev.T));
                txBps = Math.Max(0, (tx - prev.Tx) / (now - prev.T));
            }
            netPrev = (now, rx, tx);
        }
        return new { rx_bps = rxBps, tx_bps = txBps };
    }

    // ---------------------------------------------------------------- disks
    static object? DiskUsage(string path)
    {
        if (Native.DiskSpace(path) is not { } d) return null;
        long used = d.Total - d.Free;
        return new { path, total = d.Total, used, free = d.Free, pct = used + d.Free > 0 ? Js.Fixed1((double)used / (used + d.Free) * 100) : 0 };
    }

    List<object> Disks()
    {
        var user = session.User();
        var profile = user?.Profile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new List<string> { Path.GetPathRoot(profile) ?? "C:\\" };
        paths.AddRange(cfg.DiskPaths);
        if (user is not null)
        {
            try { paths.Add(user.ModelsDir()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* the root is enough */ }
        }
        var disks = new List<object>();
        var seen = new HashSet<(long, long)>();
        foreach (var p in paths)
        {
            if (Native.DiskSpace(p) is not { } d || !seen.Add((d.Total, d.Total - d.Free))) continue;
            disks.Add(DiskUsage(p)!);
        }
        return disks;
    }

    // ---------------------------------------------------------------- Ollama's processes
    /// <summary>
    /// Ollama's server and its model runners. Older Ollama ran each model as another ollama.exe; newer
    /// versions run them as llama-server.exe, so both count.
    /// </summary>
    public sealed record OllamaProcess(int pid, long rss_total, int runners);

    static OllamaProcess? OllamaProcesses()
    {
        var servers = Process.GetProcessesByName("ollama");
        var runners = Process.GetProcessesByName("llama-server");
        try
        {
            if (servers.Length == 0) return null;
            long rss = servers.Concat(runners).Sum(WorkingSet);
            // the server is the oldest ollama.exe
            var server = servers.OrderBy(StartTime).ThenBy(p => p.Id).First();
            return new OllamaProcess(server.Id, rss, servers.Length - 1 + runners.Length);
        }
        finally
        {
            foreach (var p in servers.Concat(runners)) p.Dispose();
        }
    }

    // a process can end between listing and reading
    static long WorkingSet(Process p)
    {
        try { return p.WorkingSet64; }
        catch (InvalidOperationException) { return 0; }
    }

    static DateTime StartTime(Process p)
    {
        try { return p.StartTime; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return DateTime.MaxValue; }
    }

    static object ServiceStatus(OllamaProcess? proc) => new
    {
        active = proc is null ? "inactive" : "active",
        sub = proc is null ? "stopped" : "running",
        since = (object?)null,
        main_pid = proc?.pid,
    };

    // ---------------------------------------------------------------- system
    object SystemStats(OllamaProcess? proc)
    {
        var (total, avail) = Native.Memory();
        var cpu = cpuNow;
        return new
        {
            hostname,
            uptime_s = Js.Round(Environment.TickCount64 / 1000.0),
            cpu_count = cpuPrev.Length,
            cpu_pct = cpu.Total,
            cpu_per_core = cpu.PerCore,
            load = new[] { 0, 0, 0 }, // no load average on Windows
            mem = new { total, used = total - avail, available = avail, pct = total > 0 ? Js.Fixed1((double)(total - avail) / total * 100) : 0 },
            swap = new { total = 0, used = 0, pct = 0 },
            disks = Disks(),
            net = NetRates(),
            ollama_process = proc,
        };
    }

    // ---------------------------------------------------------------- nvidia-smi
    static readonly string[] GpuFields =
    [
        "index", "uuid", "name", "driver_version", "pstate", "temperature.gpu", "fan.speed",
        "utilization.gpu", "utilization.memory", "memory.used", "memory.total", "power.draw", "power.limit",
        "clocks.sm", "clocks.max.sm", "clocks.mem", "pcie.link.gen.current", "pcie.link.width.current",
    ];
    static readonly string[] GpuKeys =
    [
        "index", "uuid", "name", "driver", "pstate", "temp_c", "fan_pct", "util_pct", "mem_util_pct",
        "mem_used_mib", "mem_total_mib", "power_w", "power_limit_w", "sm_clock_mhz", "sm_clock_max_mhz",
        "mem_clock_mhz", "pcie_gen", "pcie_width",
    ];
    static readonly HashSet<string> TextKeys = ["uuid", "name", "driver", "pstate"];
    static readonly string[] NvsmiCandidates = ["nvidia-smi", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"];
    string? nvsmi;

    /// <summary>The Node agent's num(): blanks and N/A are null, numbers numbers, anything else stays text.</summary>
    static object? Num(string? v)
    {
        v = Js.Trim(v ?? "undefined");
        if (v is "" or "[N/A]" or "N/A" or "[Not Supported]" or "Not Supported") return null;
        double f = Js.Number(v);
        return double.IsNaN(f) ? v : f;
    }

    static List<string> OutputLines(string stdout) =>
        Js.SplitLines(Js.Trim(stdout)).Where(l => l != "").ToList();

    async Task<object> NvidiaStats()
    {
        Run.Result? q = null;
        foreach (var exe in nvsmi is null ? NvsmiCandidates : [nvsmi])
        {
            q = await Run.Command(exe, [$"--query-gpu={string.Join(",", GpuFields)}", "--format=csv,noheader,nounits"]);
            if (q.Code == 0) { nvsmi = exe; break; }
        }
        if (q is null || q.Code != 0)
        {
            var error = q is { NotFound: true } ? "nvidia-smi not found" : Js.Trim(q?.Stderr is { Length: > 0 } e ? e : q?.Stdout ?? "");
            return new { available = false, error, gpus = Array.Empty<object>(), processes = Array.Empty<object>() };
        }
        var gpus = new List<Dictionary<string, object?>>();
        foreach (var line in OutputLines(q.Stdout))
        {
            var parts = line.Split(',').Select(Js.Trim).ToArray();
            var gpu = new Dictionary<string, object?>();
            for (int i = 0; i < GpuKeys.Length; i++)
            {
                string? part = i < parts.Length ? parts[i] : null;
                // a missing text field is left out, as JSON.stringify drops undefined
                if (TextKeys.Contains(GpuKeys[i])) { if (part is not null) gpu[GpuKeys[i]] = part; }
                else gpu[GpuKeys[i]] = Num(part);
            }
            if (gpu.GetValueOrDefault("uuid") is string { Length: > 0 }) gpus.Add(gpu);
        }
        var p = await Run.Command(nvsmi!, ["--query-compute-apps=gpu_uuid,pid,process_name,used_memory", "--format=csv,noheader,nounits"]);
        // WDDM drivers report per-process memory as [N/A]; that's expected on consumer cards
        var processes = p.Code != 0 ? new List<Dictionary<string, object?>>() : OutputLines(p.Stdout).Select(line =>
        {
            var c = line.Split(',').Select(Js.Trim).ToArray();
            var proc = new Dictionary<string, object?>();
            if (c.Length > 0) proc["gpu_uuid"] = c[0];
            proc["pid"] = Num(c.ElementAtOrDefault(1));
            if (c.Length > 2) proc["name"] = c[2];
            proc["mem_mib"] = Num(c.ElementAtOrDefault(3));
            return proc;
        }).ToList();
        return new { available = true, gpus, processes };
    }

    public async Task<object> Get(string version, int protocol, string? fingerprint)
    {
        var nvidia = NvidiaStats();
        var proc = await Task.Run(OllamaProcesses);
        var system = await Task.Run(() => SystemStats(proc));
        return new
        {
            ts = Js.Now(),
            version,
            protocol,
            platform = "windows",
            fingerprint,
            system,
            nvidia = await nvidia,
            service = ServiceStatus(proc),
        };
    }
}

/// <summary>
/// Running a command and waiting for its output, like the Node agent's run(): an argument list, never
/// a shell, and a time limit.
/// </summary>
public static class Run
{
    public sealed record Result(int Code, string Stdout, string Stderr, bool NotFound = false);

    public static async Task<Result> Command(string exe, IEnumerable<string> args, int timeoutMs = 15_000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3) { return new Result(1, "", e.Message, NotFound: true); }
        catch (Win32Exception e) { return new Result(1, "", e.Message); }
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { /* ended meanwhile */ }
                return new Result(1, await stdout, "timed out");
            }
            return new Result(p.ExitCode, await stdout, await stderr);
        }
    }
}
