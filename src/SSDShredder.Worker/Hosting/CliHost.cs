using System.IO.Pipes;
using SSDShredder.Worker.Cli;
using SSDShredder.Worker.CoreAdapter;
using SSDShredder.Worker.Ipc;
using SSDShredder.Worker.Operations;
using SSDShredder.Worker.Protocol;
using SSDShredder.Worker.Vault;

namespace SSDShredder.Worker.Hosting;

/// <summary>Runs one published command. Events go to stdout, diagnostics to stderr.</summary>
public static class CliHost
{
    public static ExitCode Run(
        ParsedCommand cmd, WorkerEnvironment env, Stream stdout, TextReader stdin, TextWriter stderr, bool interactive)
    {
        var sink = new StreamFrameSink(stdout);
        string id = Guid.NewGuid().ToString("D");
        var emit = new RequestEmitter(sink, id);

        if (env.Simulated) stderr.WriteLine("SIMULATED: no file, disk or BitLocker state is touched.");

        try
        {
            return Dispatch(cmd, env, sink, emit, id, stdin, stderr, interactive);
        }
        catch (Exception e)
        {
            // Redacted: type and code only, no paths or messages.
            stderr.WriteLine($"internal error: {e.GetType().Name} (0x{e.HResult:X8})");

            // The exit code must agree with the terminal result on stdout.
            if (emit.ResultCode is ExitCode reported) return reported;

            // Code 7 means a verified pre-mutation failure. Nothing was verified here.
            ExitCode code = cmd.Verb switch
            {
                Verb.Shred => ExitCode.Indeterminate,
                Verb.VaultInit or Verb.VaultClose => ExitCode.Recovery,
                _ => ExitCode.Failed,
            };
            try
            {
                return emit.Result(code, null);
            }
            catch (Exception)
            {
                return code;
            }
        }
    }

    private static ExitCode Dispatch(
        ParsedCommand cmd, WorkerEnvironment env, IFrameSink sink, RequestEmitter emit, string id,
        TextReader stdin, TextWriter stderr, bool interactive)
    {
        switch (cmd.Verb)
        {
            case Verb.Inspect:
                if (!env.CoreAvailable) stderr.WriteLine("File disposal is not available in this build.");
                return new FileOperations(env.Core).InspectReadOnly(cmd.Path!, emit);

            case Verb.Shred:
                return Shred(cmd, env, emit, stdin, stderr, interactive);

            case Verb.VaultInit:
                return ViaHelper(new WireRequest(id, Ops.VaultInit, null, false, null), env, sink, emit, stderr);

            case Verb.VaultStatus:
                return ViaOwner(new WireRequest(id, Ops.VaultStatus, cmd.VaultId, false, null), env, sink, emit, stderr);

            case Verb.VaultClose:
                if (!cmd.Confirm)
                {
                    if (!interactive)
                    {
                        stderr.WriteLine("--vault-close needs --confirm when it is not run from a console.");
                        return emit.Result(ExitCode.Invalid, null);
                    }
                    stderr.WriteLine($"About to close vault {cmd.VaultId}.");
                    stderr.WriteLine("  Its folder is removed, the volume is locked and the backing image is deleted.");
                    stderr.WriteLine("  Everything stored in the vault is lost.");
                    if (!Ask(stdin, stderr))
                    {
                        return emit.Result(ExitCode.Cancelled, new VaultEvidence(cmd.VaultId, VaultPhases.Unknown, null, null));
                    }
                }
                return ViaOwner(new WireRequest(id, Ops.VaultClose, cmd.VaultId, true, null), env, sink, emit, stderr);

            default:
                return emit.Result(ExitCode.Invalid, null);
        }
    }

    private static ExitCode Shred(
        ParsedCommand cmd, WorkerEnvironment env, RequestEmitter emit, TextReader stdin, TextWriter stderr, bool interactive)
    {
        var untouched = new ShredEvidence(null, null, null, null, ShredEvidence.EntryPreserved);
        if (!cmd.Confirm && !interactive)
        {
            stderr.WriteLine("--shred needs --confirm when it is not run from a console.");
            return emit.Result(ExitCode.Invalid, untouched);
        }
        if (Native.CurrentProcessIsElevated())
        {
            // An elevated token cannot show what the ordinary user was allowed to do.
            stderr.WriteLine("Run file disposal from an ordinary, non-elevated prompt.");
            return emit.Result(ExitCode.Denied, untouched);
        }
        if (!env.CoreAvailable) stderr.WriteLine("File disposal is not available in this build.");

        Func<InspectFacts, bool>? confirm = cmd.Confirm ? null : facts =>
        {
            stderr.WriteLine($"About to dispose of: {cmd.Path}");
            stderr.WriteLine($"  Trim will be requested for {facts.CoveredBytes} bytes (whole pages).");
            stderr.WriteLine($"  The last {facts.TailBytes} bytes get no trim coverage.");
            stderr.WriteLine("  The file's directory entry is then removed. This cannot be undone.");
            return Ask(stdin, stderr);
        };
        return new FileOperations(env.Core).ShredDirect(cmd.Path!, emit, confirm);
    }

    private static bool Ask(TextReader stdin, TextWriter stderr)
    {
        stderr.Write("Type \"yes\" to proceed: ");
        stderr.Flush();
        string? answer = stdin.ReadLine();
        return string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs a vault request in a helper process: elevated for real, as-is when simulated.</summary>
    private static ExitCode ViaHelper(WireRequest request, WorkerEnvironment env, IFrameSink sink, RequestEmitter emit, TextWriter stderr)
    {
        // Status only reads the journal, so it does not depend on what the platform can do.
        if (!env.Simulated && request.Op != Ops.VaultStatus)
        {
            // Refuse before asking for consent when the platform cannot do it anyway.
            PlatformResult pre = env.Platform.CheckPrerequisites();
            if (!pre.IsOk)
            {
                stderr.WriteLine("Vaults are unavailable: " + (pre.Detail ?? pre.Status.ToString()) + ".");
                string? vault = request.Op == Ops.VaultInit ? null : request.Target;
                return emit.Result(ExitCode.Unsupported, new VaultEvidence(vault, VaultPhases.Unknown, null, null));
            }
        }
        bool elevate = !env.Simulated && !Native.CurrentProcessIsElevated();
        return HelperLauncher.Run(request, sink, elevate, env.RequestPipePrefix);
    }

    /// <summary>
    /// Status and close go to the vault's live session owner. If no owner is running, the
    /// journal answers through the recovery path.
    /// </summary>
    private static ExitCode ViaOwner(WireRequest request, WorkerEnvironment env, IFrameSink sink, RequestEmitter emit, TextWriter stderr)
    {
        Guid vaultId = Guid.Parse(request.Target!);
        string pipe = PipeEndpoints.OwnerPipeName(env.OwnerPipePrefix, vaultId);
        long deadline = Environment.TickCount64 + (long)env.Options.LockWait.TotalMilliseconds;

        while (PipeEndpoints.Exists(pipe))
        {
            using NamedPipeClientStream? client = PipeEndpoints.Connect(pipe, 1000);
            if (client is not null)
            {
                PeerExpectation expected = PeerVerifier.SelfPeer(pid: null, exactImage: true);
                if (PeerVerifier.VerifyServer(client, expected) is not null)
                {
                    stderr.WriteLine("The vault's owner endpoint failed verification.");
                    return emit.Result(ExitCode.Denied, new VaultEvidence(request.Target, VaultPhases.Unknown, null, null));
                }
                return HelperLauncher.RunOver(client, request, sink);
            }
            if (Environment.TickCount64 >= deadline)
            {
                return emit.Result(ExitCode.Busy, new VaultEvidence(request.Target, VaultPhases.Unknown, null, null));
            }
        }

        if (env.Simulated)
        {
            var requests = new PrivilegedRequests(env, env.CreateVaultService());
            return requests.Handle(request, emit, Native.CurrentUser().Value, null);
        }
        return ViaHelper(request, env, sink, emit, stderr);
    }
}
