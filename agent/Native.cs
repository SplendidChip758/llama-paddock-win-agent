using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LlamaPaddock.Agent;

/// <summary>The few Windows calls .NET has no API for.</summary>
static class Native
{
    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint TokenQuery = 0x0008;
    const int TokenUser = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(SafeAccessTokenHandle token, int cls, IntPtr info, int length, out int returned);

    /// <summary>The account a process runs as, or null when it's gone or closed to us.</summary>
    public static string? ProcessUserSid(int pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token)) return null;
        using (token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int length);
            if (length == 0) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, length, out _)) return null;
                // TOKEN_USER starts with the SID's pointer
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeoutMs, out UIntPtr result);

    /// <summary>
    /// Tell the session's programs that the user's variables changed, as the System dialog does:
    /// Explorer reloads them, so Ollama started from it afterwards sees the new values. Works only
    /// from inside that session.
    /// </summary>
    public static void BroadcastEnvironmentChange()
    {
        const uint WmSettingChange = 0x001A, SmtoAbortIfHung = 0x0002;
        SendMessageTimeout(new IntPtr(0xFFFF), WmSettingChange, UIntPtr.Zero, "Environment", SmtoAbortIfHung, 5000, out _);
    }

    // ---------------------------------------------------------------- stats

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessorPerformance
    {
        public long Idle, Kernel, User, Dpc, Interrupt;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int cls, [Out] ProcessorPerformance[] info, int length, out int returned);

    public readonly record struct CpuTimes(long User, long Sys, long Idle, long Irq)
    {
        public long Total => User + Sys + Idle + Irq;
    }

    /// <summary>Each core's times in milliseconds, as Node's os.cpus() reads them (libuv: the same call, the same sums).</summary>
    public static CpuTimes[] CpuTimesPerCore()
    {
        var info = new ProcessorPerformance[Math.Max(Environment.ProcessorCount, 1) * 2];
        int status = NtQuerySystemInformation(8 /* SystemProcessorPerformanceInformation */, info, info.Length * Marshal.SizeOf<ProcessorPerformance>(), out int returned);
        if (status != 0) return [];
        int count = returned / Marshal.SizeOf<ProcessorPerformance>();
        var times = new CpuTimes[count];
        // 100 ns units to whole milliseconds; kernel time includes idle time
        for (int i = 0; i < count; i++)
            times[i] = new CpuTimes(info[i].User / 10000, (info[i].Kernel - info[i].Idle) / 10000, info[i].Idle / 10000, info[i].Interrupt / 10000);
        return times;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Physical memory and what's available of it: Node's os.totalmem() and os.freemem().</summary>
    public static (long Total, long Available) Memory()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref s) ? ((long)s.TotalPhys, (long)s.AvailPhys) : (0, 0);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceW(string path, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    /// <summary>
    /// A drive's size and free space in whole clusters, as Node's fs.statfsSync() gives them; null when
    /// the path can't be read. A file's folder is used, as libuv does.
    /// </summary>
    public static (long Total, long Free)? DiskSpace(string path)
    {
        if (!GetDiskFreeSpaceW(path, out var spc, out var bps, out var free, out var total))
        {
            const int ErrorDirectory = 267;
            if (Marshal.GetLastWin32Error() != ErrorDirectory || Path.GetDirectoryName(Path.GetFullPath(path)) is not { } dir
                || !GetDiskFreeSpaceW(dir + "\\", out spc, out bps, out free, out total))
                return null;
        }
        long cluster = (long)spc * bps;
        return (total * cluster, free * cluster);
    }

    // ---------------------------------------------------------------- starting Ollama

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? app, System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, string? environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Start a process on its own, as Node's spawn with detached and stdio "ignore": no console of
    /// ours, none of our handles, its own process group. env null keeps ours. Returns its PID.
    /// </summary>
    public static int StartDetached(string exe, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env, string? cwd, bool hidden)
    {
        const uint DetachedProcess = 0x08, CreateNewProcessGroup = 0x200, CreateUnicodeEnvironment = 0x400;
        const int StartfUseShowWindow = 0x01;
        var commandLine = new System.Text.StringBuilder(string.Join(' ', new[] { exe }.Concat(args).Select(QuoteArgument)));
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        if (hidden) { startup.dwFlags = StartfUseShowWindow; startup.wShowWindow = 0; /* SW_HIDE */ }
        string? block = env is null ? null
            : string.Concat(env.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => $"{e.Key}={e.Value}\0")) + "\0";
        // with no folder in the name, CreateProcess searches the PATH like a shell would
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, DetachedProcess | CreateNewProcessGroup | CreateUnicodeEnvironment,
                block, cwd, ref startup, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"can't start {exe}");
        CloseHandle(info.hThread);
        CloseHandle(info.hProcess);
        return info.dwProcessId;
    }

    /// <summary>One argument as CommandLineToArgvW reads it back.</summary>
    static string QuoteArgument(string arg)
    {
        if (arg != "" && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }
}
