using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SSDShredder.Worker.Ipc;

internal static unsafe partial class Native
{
    internal const int ErrorCancelled = 1223;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint Synchronize = 0x00100000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevation = 20;
    internal const int TokenLogonSid = 28;
    internal const uint SeeMaskNoCloseProcess = 0x00000040;
    internal const uint SeeMaskNoAsync = 0x00000100;
    internal const uint SeeMaskFlagNoUi = 0x00000400;
    internal const uint WaitObject0 = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellExecuteInfo
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpFile;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public nint lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint access, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetProcessId(nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    internal static partial int QueryFullProcessImageName(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetTokenInformation(nint token, int infoClass, void* buffer, uint length, out uint returned);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    internal static partial int ShellExecuteEx(ref ShellExecuteInfo info);

    /// <summary>Full image path of a process, or null if it cannot be read.</summary>
    internal static string? ImagePath(nint process)
    {
        char* buffer = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageName(process, 0, buffer, ref size) != 0 ? new string(buffer, 0, (int)size) : null;
    }

    internal static string? ImagePathOfPid(uint pid)
    {
        nint h = OpenProcess(ProcessQueryLimitedInformation, 0, pid);
        if (h == 0) return null;
        try
        {
            return ImagePath(h);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    internal static uint? SessionOfPid(uint pid) => ProcessIdToSessionId(pid, out uint session) != 0 ? session : null;

    /// <summary>User SID of a process's token, or null if it cannot be read.</summary>
    internal static SecurityIdentifier? UserOfPid(uint pid)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, 0, pid);
        if (process == 0) return null;
        try
        {
            if (OpenProcessToken(process, TokenQuery, out nint token) == 0) return null;
            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception e) when (e is ArgumentException or System.Security.SecurityException or Win32Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static SecurityIdentifier CurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The process token has no user.");
    }

    /// <summary>The logon SID of this process's token, or null when it has none.</summary>
    internal static SecurityIdentifier? CurrentLogonSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        nint token = identity.AccessToken.DangerousGetHandle();
        GetTokenInformation(token, TokenLogonSid, null, 0, out uint needed);
        if (needed == 0) return null;
        byte* buffer = stackalloc byte[(int)needed];
        if (GetTokenInformation(token, TokenLogonSid, buffer, needed, out _) == 0) return null;
        // TOKEN_GROUPS: DWORD GroupCount, then SID_AND_ATTRIBUTES aligned to the pointer size.
        if (*(uint*)buffer == 0) return null;
        nint sid = *(nint*)(buffer + nint.Size);
        return new SecurityIdentifier(sid);
    }

    internal static bool CurrentProcessIsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        uint elevated = 0;
        int ok = GetTokenInformation(identity.AccessToken.DangerousGetHandle(), TokenElevation, &elevated, sizeof(uint), out _);
        // Fail closed: an unreadable token is treated as elevated.
        return ok == 0 || elevated != 0;
    }
}
