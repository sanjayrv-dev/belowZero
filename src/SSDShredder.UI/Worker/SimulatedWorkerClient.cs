using System.Security.Cryptography;

namespace SSDShredder.UI.Worker;

/// <summary>
/// Mock <see cref="IWorkerClient"/> for Engineer 3's own compile/run/demo
/// loop, used because SSDShredder.Worker (Engineer 2's track) does not exist
/// in this checkout. Every result sets <c>IsMock = true</c> so the view
/// model can label observations as required by the coding plan
/// ("Label mock observations."). It reads real file length to compute
/// plausible covered/tail byte counts using the File 1 formula, but issues
/// no device I/O, no FSCTL and no deletion — nothing here can destroy data.
/// </summary>
public sealed class SimulatedWorkerClient : IWorkerClient
{
    private readonly Dictionary<string, string> _tokensByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tokenLock = new();

    public async Task<InspectResult> InspectAsync(
        string absolutePath, Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        onProgress(new WorkerProgress(WorkerPhase.Inspecting, IsMock: true));
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(absolutePath))
        {
            return new InspectResult(false, InspectReason.Filesystem, Bus: null, 0, 0, SelectionToken: null, IsMock: true);
        }

        var length = new FileInfo(absolutePath).Length;
        const int pageSize = 4096; // Environment.SystemPageSize mirrors this on the platforms we target.
        var covered = length / pageSize * pageSize;
        var tail = length - covered;

        if (covered <= 0)
        {
            return new InspectResult(false, InspectReason.NoFullPage, Bus: "mock-nvme", covered, tail, SelectionToken: null, IsMock: true);
        }

        var token = CreateSelectionToken();
        lock (_tokenLock)
        {
            _tokensByPath[absolutePath] = token;
        }

        return new InspectResult(true, InspectReason.Eligible, Bus: "mock-nvme", covered, tail, token, IsMock: true);
    }

    public async Task<ShredResult> ShredAsync(
        string absolutePath, string selectionToken, Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        bool tokenValid;
        lock (_tokenLock)
        {
            tokenValid = _tokensByPath.TryGetValue(absolutePath, out var expected) && expected == selectionToken;
            _tokensByPath.Remove(absolutePath); // tokens are single-use, same as the real worker.
        }

        if (!tokenValid)
        {
            return new ShredResult(TerminalState.Denied, null, null, 0, 0, EntryOutcome.Preserved, IsMock: true);
        }

        onProgress(new WorkerProgress(WorkerPhase.Trimming, IsMock: true));
        await Task.Delay(900, cancellationToken).ConfigureAwait(false);
        onProgress(new WorkerProgress(WorkerPhase.Unlinking, IsMock: true));
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(absolutePath))
        {
            return new ShredResult(TerminalState.Indeterminate, 1, null, 0, 0, EntryOutcome.Unknown, IsMock: true);
        }

        var length = new FileInfo(absolutePath).Length;
        const int pageSize = 4096;
        var covered = length / pageSize * pageSize;
        var tail = length - covered;

        return new ShredResult(TerminalState.Completed, 1, 1, covered, tail, EntryOutcome.Removed, IsMock: true);
    }

    public async Task<VaultResult> VaultInitAsync(Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        onProgress(new WorkerProgress(WorkerPhase.Creating, IsMock: true));
        await Task.Delay(600, cancellationToken).ConfigureAwait(false);
        onProgress(new WorkerProgress(WorkerPhase.Encrypting, IsMock: true));
        await Task.Delay(600, cancellationToken).ConfigureAwait(false);

        return new VaultResult(Guid.NewGuid().ToString(), VaultPhase.Ready, Mount: "/mock/vault", LockedObserved: false, IsMock: true);
    }

    private static string CreateSelectionToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)); // 32 lowercase hex chars, matching the spec shape.
}
