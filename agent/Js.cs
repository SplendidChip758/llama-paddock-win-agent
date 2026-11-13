using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LlamaPaddock.Agent;

/// <summary>
/// The bits of JavaScript the Node agent's answers depend on, so both agents answer the same: the
/// comparison test checks them side by side. .NET differs in small ways (\d and \s match more
/// characters, Trim() trims others, $ matches before a final newline, dates throw instead of rolling).
/// </summary>
public static partial class Js
{
    /// <summary>JavaScript's \s, for regexes and trim().</summary>
    public const string Space = @"\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff";

    public static bool IsSpace(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680'
        or (>= '\u2000' and <= '\u200a') or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';

    public static string Trim(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start])) start++;
        while (end > start && IsSpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary><c>Date.now() / 1000</c>.</summary>
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary><c>Math.round</c>: halves go up, also for negative numbers.</summary>
    public static double Round(double x)
    {
        var r = Math.Floor(x);
        return x - r >= 0.5 ? r + 1 : r;
    }

    /// <summary>
    /// <c>+x.toFixed(1)</c>: rounded on the double's exact value, halves away from zero. Math.Round
    /// scales first and can round 0.15 (really 0.1499…) up.
    /// </summary>
    public static double Fixed1(double x)
    {
        if (!double.IsFinite(x) || Math.Abs(x) >= 1e21) return x;
        long bits = BitConverter.DoubleToInt64Bits(Math.Abs(x));
        int exp = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp++; else mantissa |= 1L << 52;
        exp -= 1075; // |x| = mantissa * 2^exp
        System.Numerics.BigInteger n;
        if (exp >= 0) n = (System.Numerics.BigInteger)mantissa * 10 << exp;
        else
        {
            var den = System.Numerics.BigInteger.One << -exp;
            n = ((System.Numerics.BigInteger)mantissa * 20 + den) / (den * 2);
        }
        double r = (double)n / 10;
        return x < 0 ? -r : r;
    }

    /// <summary><c>s.split(/\r?\n/)</c>.</summary>
    public static List<string> SplitLines(string s)
    {
        var lines = new List<string>();
        int from = 0;
        for (int i = s.IndexOf('\n'); i >= 0; i = s.IndexOf('\n', from))
        {
            int end = i > from && s[i - 1] == '\r' ? i - 1 : i;
            lines.Add(s[from..end]);
            from = i + 1;
        }
        lines.Add(s[from..]);
        return lines;
    }

    /// <summary>
    /// JavaScript's <c>Number(s)</c>: null and blank give 0, anything that isn't a number NaN. The routes
    /// keep the Node agent's fallbacks (<c>Number(x) || default</c>, <c>Number.isFinite</c>) on top.
    /// </summary>
    public static double Number(string? s)
    {
        if (s is null) return 0;
        s = Trim(s);
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

    [GeneratedRegex(@"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?\z")]
    private static partial Regex DecimalLiteral();

    /// <summary>
    /// <c>new Date(y, m - 1, d, h, mi, s).getTime()</c>: local time, out-of-range parts roll over, and
    /// years 0 to 99 mean 1900 to 1999.
    /// </summary>
    public static double LocalDate(int year, int month, int day, int hour, int minute, int second)
    {
        if (year is >= 0 and <= 99) year += 1900;
        int m0 = month - 1;
        long y = year + (long)Math.Floor(m0 / 12.0);
        int mn = ((m0 % 12) + 12) % 12;
        long days = DaysFromCivil(y, mn + 1, 1) + day - 1;
        long localMs = days * 86_400_000 + hour * 3_600_000L + minute * 60_000L + second * 1000L;
        return LocalToUtc(localMs);
    }

    /// <summary>
    /// <c>Date.parse</c> for ISO 8601 date-times, the format Ollama writes. A time without a zone is
    /// local and a date alone is UTC, as in JavaScript. V8 also guesses at other formats; those are NaN.
    /// </summary>
    public static double ParseDate(string s)
    {
        var m = IsoDate().Match(s);
        if (!m.Success) return double.NaN;
        int Part(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        long year = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int month = Part(2), day = Part(3), hour = Part(4), minute = Part(5), second = Part(6);
        int ms = m.Groups[7].Success ? int.Parse(m.Groups[7].Value.PadRight(3, '0')[..3], CultureInfo.InvariantCulture) : 0;
        if (month is < 1 or > 12 || day < 1 || day > DaysInMonth(year, month)) return double.NaN;
        if (minute > 59 || second > 59 || hour > 24 || (hour == 24 && (minute | second | ms) != 0)) return double.NaN;
        long localMs = DaysFromCivil(year, month, 1) * 86_400_000 + (day - 1) * 86_400_000L
            + hour * 3_600_000L + minute * 60_000L + second * 1000L + ms;
        if (!m.Groups[4].Success) return localMs; // a date alone is UTC
        var zone = m.Groups[8].Value;
        if (zone == "") return LocalToUtc(localMs);
        if (zone is "Z" or "z") return localMs;
        int zh = int.Parse(zone[1..3], CultureInfo.InvariantCulture), zm = int.Parse(zone[4..6], CultureInfo.InvariantCulture);
        if (zh > 23 || zm > 59) return double.NaN;
        long offset = (zh * 60 + zm) * 60_000L;
        return zone[0] == '+' ? localMs - offset : localMs + offset;
    }

    [GeneratedRegex(@"^([+-][0-9]{6}|[0-9]{4})-([0-9]{2})-([0-9]{2})(?:[T ]([0-9]{2}):([0-9]{2})(?::([0-9]{2})(?:\.([0-9]+))?)?(Z|z|[+-][0-9]{2}:[0-9]{2})?)?\z")]
    private static partial Regex IsoDate();

    static int DaysInMonth(long year, int month) =>
        month == 2 ? (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : 28) : month is 4 or 6 or 9 or 11 ? 30 : 31;

    // days since 1970-01-01 (Howard Hinnant's algorithm), for any year
    static long DaysFromCivil(long y, int m, int d)
    {
        y -= m <= 2 ? 1 : 0;
        long era = (y >= 0 ? y : y - 399) / 400;
        long yoe = y - era * 400;
        long doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    /// <summary>
    /// Local milliseconds to UTC milliseconds the way JavaScript does it: a time that occurs twice
    /// when the clocks go back is the first one, and one skipped when they go forward uses the offset
    /// from before the change.
    /// </summary>
    static double LocalToUtc(long localMs)
    {
        DateTime local;
        try { local = DateTime.SpecifyKind(DateTime.UnixEpoch.AddMilliseconds(localMs), DateTimeKind.Unspecified); }
        catch (ArgumentOutOfRangeException) { return double.NaN; }
        var tz = TimeZoneInfo.Local;
        TimeSpan offset;
        if (tz.IsAmbiguousTime(local)) offset = tz.GetAmbiguousTimeOffsets(local).Max();
        else if (tz.IsInvalidTime(local)) offset = tz.GetUtcOffset(local.AddDays(-1));
        else offset = tz.GetUtcOffset(local);
        return localMs - (long)offset.TotalMilliseconds;
    }

    /// <summary>The message Node gives for a failed file call, which the Node agent passes on.</summary>
    public static string IoMessage(Exception e, string path, string syscall = "open") => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => $"ENOENT: no such file or directory, {syscall} '{path}'",
        // libuv reports ERROR_ACCESS_DENIED as EPERM
        UnauthorizedAccessException => $"EPERM: operation not permitted, {syscall} '{path}'",
        IOException when (e.HResult & 0xFFFF) is 32 or 33 => $"EBUSY: resource busy or locked, {syscall} '{path}'",
        _ => e.Message,
    };

    public static bool IsNotFound(Exception e) => e is FileNotFoundException or DirectoryNotFoundException;

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
