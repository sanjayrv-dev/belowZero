using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SSDShredder.Worker.Ipc;
using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Hosting;

/// <summary>
/// <c>bzo.exe</c> with no arguments: open or focus the unelevated bin panel through the UI's
/// ActivationRouter (docs/02, "/bzo command and shell integration"). Returns 0 only after the
/// UI owner acknowledged the activation, 7 if it could not be launched or did not answer.
/// Writes no worker NDJSON.
/// </summary>
public static class QuickLaunch
{
    private const string PipePrefix = "bzo-ar1-";
    internal const string UiFileName = "SSDShredder.UI.exe";
    internal static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(20);

    /// <summary>True when this process is the branded launcher rather than the worker proper.</summary>
    public static bool IsBzoImage() =>
        string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "bzo", StringComparison.OrdinalIgnoreCase);

    /// <param name="channelId">Tests only: another per-user channel. Production omits it.</param>
    /// <param name="uiPath">Tests only: the UI executable. Production resolves the installed one.</param>
    /// <param name="start">Tests only: how the UI is started. Production starts it unelevated.</param>
    public static ExitCode Run(
        TextWriter stderr, string? channelId = null, string? uiPath = null,
        Func<string, bool>? start = null, TimeSpan? wait = null)
    {
        if (TrySend(channelId)) return ExitCode.Completed;

        string? ui = uiPath ?? ResolveUi();
        if (ui is null || !File.Exists(ui))
        {
            stderr.WriteLine("The belowZero panel is not installed beside bzo.exe.");
            return ExitCode.Failed;
        }

        bool started;
        try
        {
            started = (start ?? StartUnelevated)(ui);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            started = false;
        }
        if (!started)
        {
            stderr.WriteLine("The belowZero panel could not be started.");
            return ExitCode.Failed;
        }

        // The first instance becomes the owner and listens a moment later.
        long deadline = Environment.TickCount64 + (long)(wait ?? DefaultWait).TotalMilliseconds;
        do
        {
            if (TrySend(channelId)) return ExitCode.Completed;
            Thread.Sleep(150);
        }
        while (Environment.TickCount64 < deadline);

        stderr.WriteLine("The belowZero panel did not accept the request.");
        return ExitCode.Failed;
    }

    /// <summary>The installed UI sits beside bzo.exe; a development build sits in its own output folder.</summary>
    internal static string? ResolveUi()
    {
        string here = AppContext.BaseDirectory;
        string beside = Path.Combine(here, UiFileName);
        if (File.Exists(beside)) return beside;
        return null;
    }

    // ---- the activation client (the UI owns the server side)

    private static string PipeName(string? channelId) =>
        PipePrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(channelId ?? Environment.UserName)))[..10];

    internal static bool TrySend(string? channelId)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(channelId), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(1000);

            string id = Guid.NewGuid().ToString();
            byte[] frame = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { v = 1, id, action = "open", source = "terminal", paths = Array.Empty<string>() }) + "\n");
            client.Write(frame);
            client.Flush();

            using var reader = new StreamReader(client, Encoding.UTF8);
            Task<string?> line = reader.ReadLineAsync();
            if (!line.Wait(3000) || line.Result is not { } text) return false;
            using JsonDocument ack = JsonDocument.Parse(text);
            return ack.RootElement.TryGetProperty("accepted", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
                && ack.RootElement.TryGetProperty("id", out JsonElement back) && back.GetString() == id;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or JsonException or AggregateException or InvalidOperationException)
        {
            return false;
        }
    }

    // ---- starting the UI without the caller's elevation

    internal static bool StartUnelevated(string ui)
    {
        if (!Native.CurrentProcessIsElevated())
        {
            using Process? p = Process.Start(new ProcessStartInfo(ui) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ui)! });
            return p is not null;
        }
        return StartWithShellToken(ui);
    }

    /// <summary>
    /// An elevated terminal must not hand its authority to the panel. The panel is started
    /// with the primary token of the desktop shell, which is the user's ordinary token.
    /// </summary>
    private static bool StartWithShellToken(string ui)
    {
        IntPtr shell = GetShellWindow();
        if (shell == 0) return false;
        GetWindowThreadProcessId(shell, out uint pid);
        using SafeProcessHandle process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return false;
        if (!OpenProcessToken(process, 0x0002 | 0x0008, out SafeFileHandle shellToken)) return false; // DUPLICATE | QUERY
        using (shellToken)
        {
            if (!DuplicateTokenEx(shellToken, 0x02000000, 0, 2, 1, out SafeFileHandle primary)) return false;
            using (primary)
            {
                var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
                string cmd = "\"" + ui + "\"";
                if (!CreateProcessWithTokenW(primary, 0, null, cmd, 0, 0, Path.GetDirectoryName(ui), ref si, out ProcessInformation pi)) return false;
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                return true;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeFileHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeFileHandle token, uint access, nint attributes, int impersonationLevel, int tokenType, out SafeFileHandle duplicate);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(SafeFileHandle token, uint logonFlags, string? application, string commandLine,
        uint creationFlags, nint environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
