using SSDShredder.UI.Worker;

namespace SSDShredder.UI.Tests.TestDoubles;

/// <summary>Deterministic <see cref="IWorkerClient"/> double for BinViewModel tests.</summary>
public sealed class FakeWorkerClient : IWorkerClient
{
    public InspectResult NextInspectResult { get; set; } =
        new(Eligible: false, InspectReason.Unknown, Bus: null, 0, 0, SelectionToken: null, IsMock: true);

    public ShredResult NextShredResult { get; set; } =
        new(TerminalState.Completed, 1, 1, 4096, 0, EntryOutcome.Removed, IsMock: true);

    public List<string> InspectedPaths { get; } = new();
    public List<(string Path, string Token)> ShreddedPaths { get; } = new();

    /// <summary>When set, InspectAsync awaits this before returning, so a test can
    /// observe the "Inspecting" state and exercise the busy-while-active rule.</summary>
    public TaskCompletionSource? InspectGate { get; set; }

    public async Task<InspectResult> InspectAsync(
        string absolutePath, Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        InspectedPaths.Add(absolutePath);
        onProgress(new WorkerProgress(WorkerPhase.Inspecting, IsMock: true));

        if (InspectGate is not null)
        {
            await InspectGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return NextInspectResult;
    }

    public Task<ShredResult> ShredAsync(
        string absolutePath, string selectionToken, Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        ShreddedPaths.Add((absolutePath, selectionToken));
        onProgress(new WorkerProgress(WorkerPhase.Trimming, IsMock: true));
        return Task.FromResult(NextShredResult);
    }

    public Task<VaultResult> VaultInitAsync(Action<WorkerProgress> onProgress, CancellationToken cancellationToken)
    {
        onProgress(new WorkerProgress(WorkerPhase.Creating, IsMock: true));
        return Task.FromResult(new VaultResult(Guid.NewGuid().ToString(), VaultPhase.Ready, "/mock/vault", false, IsMock: true));
    }
}
