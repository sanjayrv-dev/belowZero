using System.IO.Pipes;
using System.Security.Principal;
using SSDShredder.Worker.Ipc;
using SSDShredder.Worker.Operations;
using SSDShredder.Worker.Protocol;
using SSDShredder.Worker.Vault;

namespace SSDShredder.Worker.Hosting;

/// <summary>
/// The session owner's endpoint. The process that created a vault keeps its attachment
/// handle and transient passphrase, and serves status and close for that one vault until it
/// is retired or the owner's bounded lifetime ends.
/// </summary>
public sealed class OwnerHost : IDisposable
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(8);

    private readonly NamedPipeServerStream _server;
    private readonly Guid _vaultId;

    private OwnerHost(NamedPipeServerStream server, Guid vaultId)
    {
        _server = server;
        _vaultId = vaultId;
    }

    public static OwnerHost Open(string pipePrefix, Guid vaultId) =>
        new(PipeEndpoints.CreateServer(PipeEndpoints.OwnerPipeName(pipePrefix, vaultId)), vaultId);

    /// <summary>How long a connected client may take to deliver its request.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive failed waits, 50 ms apart, before the endpoint is given up.</summary>
    private const int MaxFailedWaits = 100;

    /// <param name="requestTimeout">
    /// A client that has not delivered a request by then is dropped. It does not limit how
    /// long the request itself may run.
    /// </param>
    public void Serve(VaultService service, PrivilegedRequests requests, TimeSpan lifetime, TimeSpan? requestTimeout = null)
    {
        long deadline = Environment.TickCount64 + (long)lifetime.TotalMilliseconds;
        TimeSpan patience = requestTimeout ?? DefaultRequestTimeout;
        PeerExpectation expected = PeerVerifier.SelfPeer(pid: null, exactImage: false);
        int failedWaits = 0;

        while (service.OwnsLiveSession(_vaultId))
        {
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return;

            bool connected;
            try
            {
                connected = PipeEndpoints.WaitForConnection(_server, TimeSpan.FromMilliseconds(remaining));
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                connected = false;
            }
            if (!connected)
            {
                // Either the lifetime ran out, which the next pass sees, or the wait itself
                // failed: a client opened the endpoint and hung up before the owner listened.
                // That must not end the owner, because the vault dies with it.
                // An endpoint that keeps failing is given up, though: an owner nobody can
                // reach would keep the image attached and block both close and recovery.
                if (++failedWaits > MaxFailedWaits) return;
                ResetInstance();
                if (deadline - Environment.TickCount64 > 0) Thread.Sleep(50);
                continue;
            }
            failedWaits = 0;

            // Drops a client that connects and never sends a request. Without it one silent
            // connection would hold the only instance and shut out status and close.
            var gate = new object();
            bool waitingForRequest = true;
            using var watchdog = new Timer(
                _ =>
                {
                    lock (gate)
                    {
                        if (waitingForRequest) Disconnect();
                    }
                },
                null, patience, Timeout.InfiniteTimeSpan);

            try
            {
                // Each request is authorised against the vault's recorded owner inside the service.
                SecurityIdentifier? caller = PipeEndpoints.ClientUser(_server);
                if (caller is not null && PeerVerifier.VerifyClient(_server, expected) is null)
                {
                    string callerSid = caller.Value;
                    var session = new ConnectionSession(
                        _server,
                        new StreamFrameSink(_server),
                        files: null,
                        privileged: (req, emit) =>
                        {
                            lock (gate) waitingForRequest = false;
                            return requests.Handle(req, emit, callerSid, _vaultId);
                        });
                    session.Run();
                    _server.WaitForPipeDrain();
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // A client that goes away does not take the owner with it.
            }
            finally
            {
                lock (gate) waitingForRequest = false;
                Disconnect();
            }
        }
    }

    /// <summary>
    /// Returns the instance to a state that can accept a client after a failed wait. The
    /// stream never saw a connection, so its own Disconnect refuses to act; the instance
    /// itself is still stuck on the client that left.
    /// </summary>
    private void ResetInstance()
    {
        try
        {
            Native.DisconnectNamedPipe(_server.SafePipeHandle);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Disconnect()
    {
        try
        {
            _server.Disconnect();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _server.Dispose();
}
