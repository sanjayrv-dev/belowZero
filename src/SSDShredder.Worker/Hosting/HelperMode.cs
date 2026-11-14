using System.IO.Pipes;
using SSDShredder.Worker.Cli;
using SSDShredder.Worker.Ipc;
using SSDShredder.Worker.Operations;
using SSDShredder.Worker.Protocol;
using SSDShredder.Worker.Vault;

namespace SSDShredder.Worker.Hosting;

/// <summary>
/// The internal "--ipc &lt;suffix&gt; --parent-pid &lt;pid&gt;" form. An unelevated helper is the
/// ordinary file worker: inspect, then one confirmed shred. An elevated helper is one-shot
/// and accepts only vault verbs and the volume probe. After a successful vault-init the
/// process stays on as that vault's session owner.
/// </summary>
public static class HelperMode
{
    public static ExitCode Run(ParsedCommand cmd, WorkerEnvironment env)
    {
        using NamedPipeClientStream? client = PipeEndpoints.Connect(env.RequestPipePrefix + cmd.PipeSuffix, 10_000);
        if (client is null) return ExitCode.Failed;

        // The server must be our expected parent: that PID, a trusted installed image, our user and session.
        // A simulated run skips the image rule so a UI or test host in another folder can drive it.
        PeerExpectation expected = PeerVerifier.SelfPeer(cmd.ParentPid, exactImage: false);
        if (env.Simulated) expected = expected with { ImageDirectory = null };
        string? mismatch = PeerVerifier.VerifyServer(client, expected);
        if (mismatch is not null)
        {
            Console.Error.WriteLine("The launching process failed verification: " + mismatch + ".");
            return ExitCode.Denied;
        }

        bool elevated = Native.CurrentProcessIsElevated();
        string callerSid = Native.CurrentUser().Value;

        VaultService service = env.CreateVaultService();
        var requests = new PrivilegedRequests(env, service);
        OwnerHost? owner = null;
        requests.BeforeReadyResult = id => owner = OwnerHost.Open(env.OwnerPipePrefix, id);

        // File disposal always runs under the user's ordinary token, never an elevated one.
        FileOperations? files = elevated ? null : new FileOperations(env.Core);
        PrivilegedHandler? privileged = elevated || env.Simulated
            ? (req, emit) => requests.Handle(req, emit, callerSid, null)
            : null;

        try
        {
            new ConnectionSession(client, new StreamFrameSink(client), files, privileged).Run();
            try
            {
                client.WaitForPipeDrain();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            client.Dispose();

            owner?.Serve(service, requests, OwnerHost.DefaultLifetime);
            return ExitCode.Completed;
        }
        finally
        {
            owner?.Dispose();
        }
    }
}
