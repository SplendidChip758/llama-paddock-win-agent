using System.Text.RegularExpressions;

namespace LlamaPaddock.Agent;

/// <summary>
/// Reading Ollama's server.log: its tail, the GPU lines, model runner starts and the request lines.
/// A port of the Node agent's functions, which answer the same. Ollama keeps the log open while it
/// runs, so it's opened shared.
/// </summary>
public static partial class OllamaLog
{
    static FileStream Open(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);

    /// <summary>len bytes from pos; a file that shrank meanwhile leaves zeros, as Node's readSync does.</summary>
    static string Read(FileStream f, long pos, int len)
    {
        var buf = new byte[len];
        int done = 0;
        while (done < len)
        {
            int n = RandomAccess.Read(f.SafeFileHandle, buf.AsSpan(done), pos + done);
            if (n == 0) break;
            done += n;
        }
        return Js.Utf8(buf);
    }

    /// <summary>The last non-empty lines of a file, from at most its last 4 MB.</summary>
    public static List<string> Tail(string file, int lines)
    {
        try
        {
            using var f = Open(file);
            long size = f.Length;
            int len = (int)Math.Min(size, 4 * 1024 * 1024);
            var all = Js.SplitLines(Read(f, size - len, len)).Where(l => l.Length > 0).ToList();
            return all.Skip(Math.Max(0, all.Count - lines)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [$"cannot read {file}: {Js.IoMessage(e, file)}"];
        }
    }

    // ---------------------------------------------------------------- GPU check
    // Ollama falls back to the CPU without an error when it can't use the GPU. The dashboard compares
    // where models are loaded with nvidia-smi, and these lines from Ollama's log since its last start
    // give it the evidence. Keep GpuLogRe and GpuLogLines() the same in all agents.
    // The Node agent matches case-insensitively; the line is lowered (A-Z only, as JavaScript's /i does
    // for this ASCII pattern) and ECMAScript keeps \b to ASCII word characters.
    [GeneratedRegex(@"cuda|cudart|nvcuda|nvml|nvidia|rocm|\bhip\b|amdgpu|gpu|vram|inference compute|offload|library=", RegexOptions.ECMAScript)]
    private static partial Regex GpuLogRe();
    const int GpuLogKeep = 150;

    static string AsciiLower(string s) => string.Create(s.Length, s, (span, src) =>
    {
        for (int i = 0; i < src.Length; i++) span[i] = src[i] is >= 'A' and <= 'Z' ? (char)(src[i] + 32) : src[i];
    });

    static List<string> GpuLogLines(IEnumerable<string> lines)
    {
        var hits = lines.Where(l => GpuLogRe().IsMatch(AsciiLower(l)) && !l.Contains("[GIN]", StringComparison.Ordinal))
            .Select(l => l.Length > 500 ? l[..500] + "…" : l).ToList();
        // the start of the run holds discovery, the end the latest errors
        return hits.Count > 2 * GpuLogKeep ? [.. hits.Take(GpuLogKeep), .. hits.Skip(hits.Count - GpuLogKeep)] : hits;
    }

    /// <summary>Ollama for Windows starts a new server.log each time it starts, so the file is the current run.</summary>
    public static object GpuLog(string file)
    {
        const int Head = 4 * 1024 * 1024, TailBytes = 16 * 1024 * 1024;
        try
        {
            using var f = Open(file);
            long size = f.Length;
            // a long run's log is read at both ends: discovery is at the start, the latest errors at the end
            List<string> lines;
            if (size <= Head + TailBytes) lines = Js.SplitLines(Read(f, 0, (int)size));
            else
            {
                var head = Js.SplitLines(Read(f, 0, Head));
                var tail = Js.SplitLines(Read(f, size - TailBytes, TailBytes));
                lines = [.. head.Take(head.Count - 1), .. tail.Skip(1)];
            }
            return new { source = "server.log", lines = GpuLogLines(lines) };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new { source = "server.log", lines = Array.Empty<string>(), error = $"cannot read {file}: {Js.IoMessage(e, file)}" };
        }
    }

    // ---------------------------------------------------------------- model loads
    // Ollama logs "llama runner started in N seconds" once per model load. The dashboard only sees
    // loads that last until a poll, so these times tell it about models that load and unload between
    // polls. Keep the match the same in all agents.
    const string RunnerStarted = "llama runner started in";

    [GeneratedRegex("time=([^" + Js.Space + "]+)")]
    private static partial Regex TimeField();

    /// <summary>server.log only covers the current run of Ollama, so starts before it are missing.</summary>
    public static object RunnerStarts(string file, double since)
    {
        const int TailBytes = 16 * 1024 * 1024;
        try
        {
            using var f = Open(file);
            long size = f.Length;
            int len = (int)Math.Min(size, TailBytes);
            var starts = new List<double>();
            foreach (var l in Js.SplitLines(Read(f, size - len, len)))
            {
                if (!l.Contains(RunnerStarted, StringComparison.Ordinal)) continue;
                var m = TimeField().Match(l);
                double t = Js.ParseDate(m.Success ? m.Groups[1].Value : "") / 1000;
                if (t >= since) starts.Add(t);
            }
            return new { starts };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new { starts = Array.Empty<double>(), error = $"cannot read {file}: {Js.IoMessage(e, file)}" };
        }
    }

    // ---------------------------------------------------------------- request log
    // Ollama logs each HTTP request through gin, one line per request:
    //   [GIN] 2026/11/02 - 09:14:07 | 200 |  4.551040958s |   192.168.30.20 | POST     "/api/chat"
    // Only these fields are read; Ollama's log has no prompts or responses. A line that starts like one
    // but doesn't parse, such as from a newer Ollama, is counted as skipped rather than misread.
    // Keep this section the same in all agents.
    const string GinPrefix = "[GIN] ";
    /// <summary>Requests one call returns at most; the dashboard asks again from where it stopped.</summary>
    const int RequestsMax = 5000;
    /// <summary>A log file is read from its end, at most this much of it.</summary>
    const int RequestLogTailBytes = 8 * 1024 * 1024;

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)(h|ms|m|s|µs|us|ns)")]
    private static partial Regex DurationPart();
    [GeneratedRegex(@"^([0-9]{4})/([0-9]{2})/([0-9]{2}) - ([0-9]{2}):([0-9]{2}):([0-9]{2})\z")]
    private static partial Regex GinTime();
    [GeneratedRegex(@"^[0-9]{3}\z")]
    private static partial Regex GinStatus();
    [GeneratedRegex(@"^([A-Z]+) +""([^""]*)""\z")]
    private static partial Regex GinCall();
    [GeneratedRegex("[" + Js.Space + "]")]
    private static partial Regex AnySpace();

    /// <summary>Go's time.Duration text ("4.55s", "1m2.5s", "735.1µs") in milliseconds, or null.</summary>
    static double? GoDurationMs(string text)
    {
        if (text == "") return null;
        double ms = 0;
        var rest = text;
        while (rest != "")
        {
            var m = DurationPart().Match(rest);
            if (!m.Success) return null;
            double unit = m.Groups[2].Value switch
            {
                "h" => 3600e3, "m" => 60e3, "s" => 1e3, "ms" => 1, "µs" or "us" => 1e-3, _ => 1e-6,
            };
            ms += Js.Number(m.Groups[1].Value) * unit;
            rest = rest[m.Length..];
        }
        return ms;
    }

    public sealed record Request(double t, int status, double duration_ms, string client, string method, string path);

    /// <summary>A gin request line: null if the line isn't one, skipped if it looks like one but doesn't parse.</summary>
    static (Request? Request, bool Skipped) ParseRequestLine(string line)
    {
        int at = line.IndexOf(GinPrefix, StringComparison.Ordinal);
        if (at < 0) return (null, false);
        var parts = line[(at + GinPrefix.Length)..].Split('|').Select(Js.Trim).ToArray();
        if (parts.Length != 5) return (null, true);
        var d = GinTime().Match(parts[0]);
        var c = GinCall().Match(parts[4]);
        var ms = GoDurationMs(parts[2]);
        var client = parts[3];
        if (!d.Success || !GinStatus().IsMatch(parts[1]) || ms is null || !c.Success || client == "" || AnySpace().IsMatch(client))
            return (null, true);
        int G(int i) => int.Parse(d.Groups[i].Value);
        // gin writes the host's local time without a zone
        double local = Js.LocalDate(G(1), G(2), G(3), G(4), G(5), G(6)) / 1000;
        // the query string is dropped: it isn't needed to tell endpoints apart
        var path = c.Groups[2].Value.Split('?')[0];
        return (new Request(local, int.Parse(parts[1]), Js.Round(ms.Value * 1000) / 1000, client, c.Groups[1].Value, path), false);
    }

    /// <summary>The last maxBytes of a file as lines (the first, likely cut, is dropped when the file is longer).</summary>
    static List<string> TailLines(string file, int maxBytes)
    {
        using var f = Open(file);
        long size = f.Length;
        int len = (int)Math.Min(size, maxBytes);
        var lines = Js.SplitLines(Read(f, size - len, len));
        return size > len ? lines.Skip(1).ToList() : lines;
    }

    /// <summary>
    /// The requests after since and up to until, in log order. A log line's time has whole seconds, so
    /// one stamped with the second since falls in may have been written after it: those come again,
    /// and the dashboard drops the ones it already has. At most RequestsMax are returned; until then
    /// says how far they reach, and more that the caller should ask again from there.
    /// </summary>
    static object CollectRequests(IEnumerable<string> lines, double since, double until)
    {
        var requests = new List<Request>();
        int skipped = 0;
        foreach (var line in lines)
        {
            var (r, skip) = ParseRequestLine(line);
            if (skip) { skipped++; continue; }
            if (r is null || r.t + 1 <= since || r.t > until) continue;
            if (requests.Count == RequestsMax)
            {
                // end before the last request's time, so the others at that time come with the next call
                double last = requests[^1].t;
                return new { requests = requests.Where(x => x.t < last).ToList(), until = last, more = true, skipped };
            }
            requests.Add(r);
        }
        return new { requests, until, more = false, skipped };
    }

    public static object Requests(string file, double since)
    {
        double now = Js.Now();
        List<string> lines;
        try { lines = TailLines(file, RequestLogTailBytes); }
        // Ollama hasn't written a log yet
        catch (Exception e) when (Js.IsNotFound(e)) { return CollectRequests([], since, now); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HttpError(500, $"can't read {file}: {Js.IoMessage(e, file)}");
        }
        return CollectRequests(lines, since, now);
    }
}
