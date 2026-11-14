namespace SSDShredder.UI.Worker;

/// <summary>
/// UI-facing shapes mirroring the worker NDJSON contract in
/// docs/02_SYSTEM_ARCHITECTURE.md ("Worker contract v1"). SSDShredder.Worker
/// is Engineer 2's track and does not exist in this checkout yet — these
/// types exist so the UI can compile and be demoed against
/// <see cref="SimulatedWorkerClient"/> today, then swap to a real pipe/stdio
/// client without touching ViewModels.
/// </summary>
public enum InspectReason
{
    Eligible,
    Filesystem,
    Topology,
    Capability,
    FileType,
    Streams,
    Links,
    NoExtents,
    NoFullPage,
    Unknown,
}

public sealed record InspectResult(
    bool Eligible,
    InspectReason Reason,
    string? Bus,
    long CoveredBytes,
    long TailBytes,
    string? SelectionToken,
    bool IsMock);

public enum EntryOutcome
{
    Preserved,
    Removed,
    Unknown,
}

public enum TerminalState
{
    Completed,
    Cancelled,
    Unsupported,
    Denied,
    Busy,
    Failed,
    Indeterminate,
}

public sealed record ShredResult(
    TerminalState State,
    long? RangesRequested,
    long? RangesProcessed,
    long CoveredBytes,
    long TailBytes,
    EntryOutcome Entry,
    bool IsMock);

public enum VaultPhase
{
    Creating,
    Ready,
    Closing,
    Retired,
    Orphan,
    Unknown,
}

public sealed record VaultResult(
    string VaultId,
    VaultPhase Phase,
    string? Mount,
    bool? LockedObserved,
    bool IsMock);

public enum WorkerPhase
{
    Inspecting,
    Confirming,
    Trimming,
    Unlinking,
    Creating,
    Encrypting,
    Locking,
    Reconciling,
}

public sealed record WorkerProgress(WorkerPhase Phase, bool IsMock);
