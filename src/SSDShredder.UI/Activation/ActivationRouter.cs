using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SSDShredder.UI.Activation;

/// <summary>
/// "Own one UI instance per user/logon session. Receive bounded requests over
/// authenticated local IPC. Start the installed UI if absent and wait for an
/// acknowledgement. Use an activation UUID to deduplicate delivery."
/// (docs/02_SYSTEM_ARCHITECTURE.md, ActivationRouter)
///
/// This class is the whole convergence point for desktop drop, Explorer command
/// and terminal quick launch. On Windows the pipe additionally carries a
/// per-user DACL and uses PipeOptions.CurrentUserOnly; on other platforms
/// (used here for Engineer 3's macOS development loop) it falls back to a
/// same-user, best-effort local socket with no cross-user guarantee — never
/// wire this fallback path to a production installer.
/// </summary>
public sealed class ActivationRouter : IAsyncDisposable
{
    // Short and fixed-length on purpose: on non-Windows, NamedPipe* maps this to a Unix
    // domain socket path under the OS temp directory, which has a ~104-character limit
    // that a long username plus a descriptive prefix can blow through (observed on macOS
    // with TMPDIR's per-session subdirectory already consuming most of that budget).
    private const string PipeNamePrefix = "bzo-ar1-";
    private const int MaxFrameBytes = 65_536;
    private const int AckTimeoutMs = 3_000;

    private readonly Func<ActivationEnvelope, CancellationToken, Task<bool>> _onActivation;
    private readonly string _pipeName;
    private readonly HashSet<string> _seenIds = new();
    private readonly object _dedupeLock = new();
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    /// <param name="channelId">
    /// Identifies the per-user channel. Production code always omits this and
    /// gets the real per-user channel; tests pass a unique id so concurrent
    /// test runs don't share one pipe/socket.
    /// </param>
    public ActivationRouter(Func<ActivationEnvelope, CancellationToken, Task<bool>> onActivation, string? channelId = null)
    {
        _onActivation = onActivation;
        _pipeName = ResolvePipeName(channelId);
    }

    private static string ResolvePipeName(string? channelId) =>
        PipeNamePrefix + ShortUserHash(channelId ?? Environment.UserName);

    /// <summary>10 lowercase-hex characters derived from the user name, so the pipe
    /// stays per-user without the name growing with the account's display name.</summary>
    private static string ShortUserHash(string userName)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(userName));
        return Convert.ToHexStringLower(hash)[..10];
    }

    /// <summary>
    /// Attempts a short connection to an already-running owner and forwards one
    /// activation to it. Returns true only after that owner acknowledged.
    /// Call this before falling back to becoming the owner yourself.
    /// </summary>
    public static async Task<bool> TrySendToExistingOwnerAsync(
        ActivationEnvelope envelope, string? channelId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ResolvePipeName(channelId), PipeDirection.InOut,
                OperatingSystem.IsWindows() ? PipeOptions.CurrentUserOnly : PipeOptions.None);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(AckTimeoutMs);
            await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            await WriteFrameAsync(client, JsonSerializer.SerializeToUtf8Bytes(envelope), connectCts.Token)
                .ConfigureAwait(false);

            var ackBytes = await ReadFrameAsync(client, connectCts.Token).ConfigureAwait(false);
            if (ackBytes is null)
            {
                return false;
            }

            var ack = JsonSerializer.Deserialize<ActivationAck>(ackBytes);
            return ack is { Accepted: true } && ack.Id == envelope.Id;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            // No owner listening (first launch) or it didn't answer in time.
            return false;
        }
    }

    /// <summary>
    /// Becomes the owner for this user/logon session: listens for activations
    /// and dispatches each new (deduplicated) one to the callback supplied at
    /// construction, until disposed.
    /// </summary>
    public void StartOwning()
    {
        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("ActivationRouter is already owning.");
        }

        _listenerCts = new CancellationTokenSource();
        _listenerTask = RunListenerLoopAsync(_listenerCts.Token);
    }

    private async Task RunListenerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServerStream();
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposing.
            }
            catch (IOException)
            {
                // Peer dropped mid-handshake; keep serving future activations.
            }
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        // PipeOptions.CurrentUserOnly already implies its own security descriptor —
        // .NET rejects combining it with an explicit PipeSecurity (confirmed on real
        // Windows: NamedPipeServerStreamAcl.Create with both throws). So this never
        // builds a custom PipeSecurity at all; CurrentUserOnly alone, on the plain
        // constructor, gives the same same-user restriction on every platform.
        var options = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows())
        {
            options |= PipeOptions.CurrentUserOnly;
        }
        // Non-Windows (the macOS dev loop): same-user local socket, no PID attestation —
        // the installed product only ships this router behind Windows' CurrentUserOnly.
        return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, options, inBufferSize: 0, outBufferSize: 0);
    }

    private async Task HandleConnectionAsync(PipeStream connection, CancellationToken cancellationToken)
    {
        var requestBytes = await ReadFrameAsync(connection, cancellationToken).ConfigureAwait(false);
        if (requestBytes is null)
        {
            return;
        }

        ActivationEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ActivationEnvelope>(requestBytes)
                ?? throw new JsonException("Null envelope.");
        }
        catch (JsonException)
        {
            // Invalid frame with no trustworthy request ID: close without executing anything.
            return;
        }

        var accepted = envelope.IsStructurallyValid() && IsNewActivation(envelope.Id)
            && await _onActivation(envelope, cancellationToken).ConfigureAwait(false);

        var ack = ActivationAck.For(envelope, accepted);
        await WriteFrameAsync(connection, JsonSerializer.SerializeToUtf8Bytes(ack), cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsNewActivation(string id)
    {
        lock (_dedupeLock)
        {
            return _seenIds.Add(id);
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"Activation frame exceeds {MaxFrameBytes} bytes.");
        }

        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
                return buffer.Length == 0 ? null : null; // disconnected mid-frame: treat as invalid
            }

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
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

    public async ValueTask DisposeAsync()
    {
        if (_listenerCts is null)
        {
            return;
        }

        await _listenerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_listenerTask is not null)
            {
                await _listenerTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _listenerCts.Dispose();
    }
}
