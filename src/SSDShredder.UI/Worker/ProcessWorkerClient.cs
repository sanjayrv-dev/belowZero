using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace SSDShredder.UI.Worker;

/// <summary>
/// Real IWorkerClient: launches SSDShredder.Worker.exe with "--ipc &lt;suffix&gt;
/// --parent-pid &lt;this process's id&gt;" and speaks the NDJSON protocol over the
/// pipe it creates first, per docs/02_SYSTEM_ARCHITECTURE.md ("Worker contract
/// v1") and src/SSDShredder.Worker/README.md's "Integration seams" section —
/// "create a pipe named SSDShredder.v1.&lt;32 hex&gt;, start SSDShredder.Worker
/// --ipc ... unelevated, send inspect, then one shred with the returned
/// selectionToken on the same connection."
///
/// Written and reviewed without a Windows machine available (same caveat as
/// SSDShredder.ShellExtension and the Core/Worker wiring): the pipe DACL here
/// mirrors Worker's own already-hardware-verified
/// SSDShredder.Worker.Ipc.PipeEndpoints.CreateServer exactly (deny Network SID,
/// allow the current user ReadWrite+Synchronize, no PipeOptions.CurrentUserOnly
/// — that flag cannot be combined with an explicit PipeSecurity, which is
/// exactly the bug ActivationRouter had and this deliberately avoids repeating).
/// Needs a real Windows run (ideally with SSDShredder.UI.exe and
/// SSDShredder.Worker.exe actually installed side by side — see
/// <see cref="ResolveWorkerExecutablePath"/>) before trusting it with real data.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessWorkerClient : IWorkerClient, IAsyncDisposable
{
    private const int MaxFrameBytes = 65_536;
    private const int ConnectTimeoutMs = 10_000;

    private Process? _workerProcess;
    private NamedPipeServerStream? _pipe;

    public async Task<InspectResult> InspectAsync(
        string absolutePath, Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        // A fresh inspect always starts a fresh connection — mirrors Worker's
        // own FileOperations.InspectAndHold, which releases whatever the
        // previous connection held before opening a new one.
        await ReleaseSessionAsync().ConfigureAwait(false);
        await StartSessionAsync(cancellationToken).ConfigureAwait(false);

        string id = Guid.NewGuid().ToString();
        await SendAsync(new
        {
            v = 1,
            id,
            op = "inspect",
            target = absolutePath,
            confirmed = false,
        }, cancellationToken).ConfigureAwait(false);

        return await ReadInspectResultAsync(id, onProgress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShredResult> ShredAsync(
        string absolutePath, string selectionToken, Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            // No live session to consume a token on: same outcome the wire
            // contract gives a token mismatch — denied, nothing touched.
            return new ShredResult(TerminalState.Denied, null, null, 0, 0, EntryOutcome.Preserved, IsMock: false);
        }

        string id = Guid.NewGuid().ToString();
        try
        {
            await SendAsync(new
            {
                v = 1,
                id,
                op = "shred",
                target = absolutePath,
                confirmed = true,
                selectionToken,
            }, cancellationToken).ConfigureAwait(false);

            return await ReadShredResultAsync(id, onProgress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A connection shred is consumed once, whatever the outcome.
            await ReleaseSessionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Vault ops don't go through the GUI-session pipe at all (that's the
    /// file-disposal-only connection) — Worker's own CLI already does the
    /// right thing for a direct, ordinary invocation, UAC prompt included
    /// when needed, so this just runs it and reads its NDJSON stdout.
    /// </summary>
    public async Task<VaultResult> VaultInitAsync(Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        var psi = BuildWorkerStartInfo("--vault-init");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start SSDShredder.Worker.exe.");

        VaultResult? result = null;
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "result")
            {
                if (WireMapping.TryGetPhase(root, out var phase))
                {
                    onProgress(new WorkerProgress(phase, IsMock: false));
                }
                continue;
            }

            var evidence = root.GetProperty("evidence");
            result = new VaultResult(
                VaultId: evidence.GetProperty("vaultId").GetString() ?? "",
                Phase: WireMapping.ParseVaultPhase(evidence.GetProperty("phase").GetString()),
                Mount: WireMapping.GetNullableString(evidence, "mount"),
                LockedObserved: WireMapping.GetNullableBool(evidence, "lockedObserved"),
                IsMock: false);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return result ?? new VaultResult("", VaultPhase.Unknown, null, null, IsMock: false);
    }

    // ---- GUI-session connection (inspect, then one held shred) --------------

    private async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        string suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string pipeName = "SSDShredder.v1." + suffix;

        _pipe = CreateGuiSessionServer(pipeName);

        var psi = BuildWorkerStartInfo($"--ipc {suffix} --parent-pid {Environment.ProcessId}");
        _workerProcess = Process.Start(psi) ?? throw new InvalidOperationException("Could not start SSDShredder.Worker.exe.");

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeoutMs);
        await _pipe.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors SSDShredder.Worker.Ipc.PipeEndpoints.CreateServer exactly: deny
    /// the Network SID, allow only the current user, first-instance
    /// protection. Deliberately does not combine PipeOptions.CurrentUserOnly
    /// with a custom PipeSecurity — Windows rejects that combination (the
    /// ActivationRouter bug this project already found and fixed once).
    /// </summary>
    private static NamedPipeServerStream CreateGuiSessionServer(string name)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current Windows identity for pipe ACL."),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0, outBufferSize: 0, security);
    }

    private async Task<InspectResult> ReadInspectResultAsync(
        string requestId, Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        await foreach (var frame in ReadFramesForAsync(requestId, onProgress, cancellationToken))
        {
            var evidence = frame.GetProperty("evidence");
            bool eligible = evidence.GetProperty("eligible").GetBoolean();
            return new InspectResult(
                Eligible: eligible,
                Reason: WireMapping.ParseReason(evidence.GetProperty("reason").GetString()),
                Bus: WireMapping.GetNullableString(evidence, "bus"),
                CoveredBytes: WireMapping.GetNullableLong(evidence, "coveredBytes") ?? 0,
                TailBytes: WireMapping.GetNullableLong(evidence, "tailBytes") ?? 0,
                SelectionToken: evidence.TryGetProperty("selectionToken", out var tok) ? tok.GetString() : null,
                IsMock: false);
        }

        // Connection dropped before a result arrived.
        return new InspectResult(false, InspectReason.Unknown, null, 0, 0, null, IsMock: false);
    }

    private async Task<ShredResult> ReadShredResultAsync(
        string requestId, Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        await foreach (var frame in ReadFramesForAsync(requestId, onProgress, cancellationToken))
        {
            var evidence = frame.GetProperty("evidence");
            return new ShredResult(
                State: WireMapping.ParseTerminalState(frame.GetProperty("state").GetString()),
                RangesRequested: WireMapping.GetNullableLong(evidence, "rangesRequested"),
                RangesProcessed: WireMapping.GetNullableLong(evidence, "rangesProcessed"),
                CoveredBytes: WireMapping.GetNullableLong(evidence, "coveredBytes") ?? 0,
                TailBytes: WireMapping.GetNullableLong(evidence, "tailBytes") ?? 0,
                Entry: WireMapping.ParseEntryOutcome(evidence.GetProperty("entry").GetString()),
                IsMock: false);
        }

        // Transport lost after a confirmed shred: never assume success, never retry.
        return new ShredResult(TerminalState.Indeterminate, null, null, 0, 0, EntryOutcome.Unknown, IsMock: false);
    }

    /// <summary>Yields progress events via the callback and stops at (and returns) the matching result frame.</summary>
    private async IAsyncEnumerable<JsonElement> ReadFramesForAsync(
        string requestId, Action<WorkerProgress> onProgress, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            yield break;
        }

        while (true)
        {
            byte[]? frameBytes = await ReadFrameAsync(_pipe, cancellationToken).ConfigureAwait(false);
            if (frameBytes is null)
            {
                yield break; // disconnected
            }

            using var doc = JsonDocument.Parse(frameBytes);
            var root = doc.RootElement;
            if (root.GetProperty("id").GetString() != requestId)
            {
                continue; // stale/unrelated frame; keep waiting for ours
            }

            if (root.GetProperty("type").GetString() == "progress")
            {
                if (WireMapping.TryGetPhase(root, out var phase))
                {
                    onProgress(new WorkerProgress(phase, IsMock: false));
                }
                continue;
            }

            yield return root;
            yield break; // exactly one terminal result per request
        }
    }

    private async Task SendAsync(object request, CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            throw new InvalidOperationException("No active worker session.");
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request);
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"Request frame exceeds {MaxFrameBytes} bytes.");
        }

        await _pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _pipe.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }

            if (read == 0)
            {
                return null; // disconnected mid-frame or cleanly between frames
            }

            int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                buffer.Write(chunk, 0, newline);
                return buffer.Length > MaxFrameBytes ? null : buffer.ToArray();
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxFrameBytes)
            {
                return null;
            }
        }
    }

    private async Task ReleaseSessionAsync()
    {
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        if (_workerProcess is not null)
        {
            try
            {
                if (!_workerProcess.HasExited)
                {
                    await _workerProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited/disposed.
            }
            _workerProcess.Dispose();
            _workerProcess = null;
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseSessionAsync().ConfigureAwait(false);

    // ---- Resolving and launching the worker ----------------------------------

    private static ProcessStartInfo BuildWorkerStartInfo(string arguments)
    {
        return new ProcessStartInfo(ResolveWorkerExecutablePath())
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    /// <summary>
    /// Real mode's peer check requires the pipe server's (this UI's own)
    /// executable to sit in Worker's own install directory — "the UI and the
    /// worker must be installed side by side" per
    /// src/SSDShredder.Worker/README.md. So the first and normally-correct
    /// answer is simply "next to this UI's own executable"; an environment
    /// variable override exists for development before an installer places
    /// them together.
    /// </summary>
    internal static string ResolveWorkerExecutablePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("SSDSHREDDER_WORKER_PATH");
        if (!string.IsNullOrEmpty(overridePath))
        {
            return overridePath;
        }

        string sibling = Path.Combine(AppContext.BaseDirectory, "SSDShredder.Worker.exe");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        throw new FileNotFoundException(
            "SSDShredder.Worker.exe was not found next to SSDShredder.UI.exe, and " +
            "SSDSHREDDER_WORKER_PATH is not set. The real worker client needs the two " +
            "installed side by side (see src/SSDShredder.Worker/README.md).");
    }

}
