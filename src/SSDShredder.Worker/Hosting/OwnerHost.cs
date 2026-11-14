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

    public void Serve(VaultService service, PrivilegedRequests requests, TimeSpan lifetime)
    {
        long deadline = Environment.TickCount64 + (long)lifetime.TotalMilliseconds;
        PeerExpectation expected = PeerVerifier.SelfPeer(pid: null, exactImage: false);

        while (service.OwnsLiveSession(_vaultId))
        {
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return;
            if (!PipeEndpoints.WaitForConnection(_server, TimeSpan.FromMilliseconds(remaining))) return;

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
                        privileged: (req, emit) => requests.Handle(req, emit, callerSid, _vaultId));
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
                try
                {
                    _server.Disconnect();
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
                {
                }
            }
        }
    }

    public void Dispose() => _server.Dispose();
}
