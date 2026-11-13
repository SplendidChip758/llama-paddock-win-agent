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
}
