using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace SSDShredder.Worker.Ipc;

/// <summary>
/// Local named pipes for the mixed-integrity protocol. PipeOptions.CurrentUserOnly cannot
/// be used here because it also compares elevation, so the DACL and the peer checks are
/// done explicitly.
/// </summary>
public static class PipeEndpoints
{
    public const string RequestPrefix = "SSDShredder.v1.";
    public const string OwnerPrefix = "SSDShredder.v1.owner.";

    public static string NewSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static string OwnerPipeName(string prefix, Guid vaultId) => prefix + vaultId.ToString("N");

    /// <summary>
    /// Creates the first and only instance of a pipe that the current logon session may
    /// open and that network clients may not.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string name)
    {
        SecurityIdentifier allowed = Native.CurrentLogonSid() ?? Native.CurrentUser();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            allowed, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    /// <summary>Connects with identification-only impersonation quality.</summary>
    public static NamedPipeClientStream? Connect(string name, int timeoutMs)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            client.Connect(timeoutMs);
            return client;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            client.Dispose();
            return null;
        }
    }

    public static bool Exists(string name)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\", name).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool WaitForConnection(NamedPipeServerStream server, TimeSpan timeout, Func<bool>? abandon = null)
    {
        using var cts = new CancellationTokenSource();
        Task wait = server.WaitForConnectionAsync(cts.Token);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            try
            {
                if (wait.Wait(100)) return true;
            }
            catch (AggregateException)
            {
                return false;
            }
            if (Environment.TickCount64 >= deadline || (abandon?.Invoke() ?? false))
            {
                cts.Cancel();
                try
                {
                    wait.Wait();
                    return true;
                }
                catch (AggregateException)
                {
                    return false;
                }
            }
        }
    }

    /// <summary>User SID of the connected client, read through identification impersonation.</summary>
    public static SecurityIdentifier? ClientUser(NamedPipeServerStream server)
    {
        SecurityIdentifier? user = null;
        try
        {
            server.RunAsClient(() =>
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                user = identity.User;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return null;
        }
        return user;
    }
}

/// <summary>Who must be on the other end of a pipe.</summary>
/// <param name="Pid">Exact process, or null when only the other facts are known.</param>
/// <param name="ImagePath">Exact image, or null to accept any image in <paramref name="ImageDirectory"/>.</param>
public sealed record PeerExpectation(uint? Pid, string? ImagePath, string? ImageDirectory, SecurityIdentifier User, uint Session);

public static class PeerVerifier
{
    public static PeerExpectation SelfPeer(uint? pid, bool exactImage)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        uint session = Native.SessionOfPid((uint)Environment.ProcessId) ?? throw new InvalidOperationException("No session.");
        return new PeerExpectation(pid, exactImage ? exe : null, Path.GetDirectoryName(exe), Native.CurrentUser(), session);
    }

    /// <summary>Checks the client of a server pipe. Returns null when it matches, else the reason.</summary>
    public static string? VerifyClient(NamedPipeServerStream server, PeerExpectation expected)
    {
        if (Native.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid) == 0) return "client pid unavailable";
        SecurityIdentifier? user = PipeEndpoints.ClientUser(server);
        return Verify(pid, user, expected);
    }

    /// <summary>Checks the server of a client pipe.</summary>
    public static string? VerifyServer(NamedPipeClientStream client, PeerExpectation expected)
    {
        if (Native.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid) == 0) return "server pid unavailable";
        return Verify(pid, Native.UserOfPid(pid), expected);
    }

    private static string? Verify(uint pid, SecurityIdentifier? user, PeerExpectation expected)
    {
        if (expected.Pid is not null && pid != expected.Pid) return "unexpected process";
        if (user is null || !user.Equals(expected.User)) return "unexpected user";
        if (Native.SessionOfPid(pid) != expected.Session) return "unexpected session";

        // No image expectation at all is used only by simulated runs.
        if (expected.ImagePath is null && expected.ImageDirectory is null) return null;

        string? image = Native.ImagePathOfPid(pid);
        if (image is null) return "image unavailable";
        if (expected.ImagePath is not null)
        {
            if (!string.Equals(image, expected.ImagePath, StringComparison.OrdinalIgnoreCase)) return "unexpected image";
        }
        else if (!string.Equals(Path.GetDirectoryName(image), expected.ImageDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return "image outside the installed directory";
        }
        return null;
    }
}
