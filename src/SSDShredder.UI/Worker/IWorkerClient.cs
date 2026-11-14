namespace SSDShredder.UI.Worker;

/// <summary>
/// The subset of the worker protocol the bin panel needs: inspect a path,
/// then (on approval) shred it using the token inspect returned, or manage a
/// vault. Engineer 2 owns the real implementation (NDJSON over the elevated
/// pipe / direct stdio per docs/02_SYSTEM_ARCHITECTURE.md). Until that
/// project exists, <see cref="SimulatedWorkerClient"/> implements this same
/// interface so SSDShredder.UI builds, runs and demos end to end on its own.
/// </summary>
public interface IWorkerClient
{
    /// <summary>
    /// Opens the inspect→(confirmed shred) connection described by the spec
    /// ("accepts inspect followed by one confirmed shred on the same
    /// connection") and reports progress through <paramref name="onProgress"/>.
    /// </summary>
    Task<InspectResult> InspectAsync(
        string absolutePath,
        Action<WorkerProgress> onProgress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Consumes the <paramref name="selectionToken"/> returned by a prior
    /// eligible <see cref="InspectAsync"/> on the same logical session and
    /// requests the confirmed shred.
    /// </summary>
    Task<ShredResult> ShredAsync(
        string absolutePath,
        string selectionToken,
        Action<WorkerProgress> onProgress,
        CancellationToken cancellationToken);

    Task<VaultResult> VaultInitAsync(
        Action<WorkerProgress> onProgress,
        CancellationToken cancellationToken);
}
