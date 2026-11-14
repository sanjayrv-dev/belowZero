namespace SSDShredder.Worker.CoreAdapter;

// This is the Worker's view of SSDShredder.Core (Engineer 1). Until the frozen Core
// interface lands, the Worker runs against Simulation.SimulatedCore. At integration an
// adapter implements these interfaces over the real Core; the Worker logic stays as is.

/// <summary>Inspect reasons of wire contract v1.</summary>
public static class Reasons
{
    public const string Eligible = "eligible";
    public const string Filesystem = "filesystem";
    public const string Topology = "topology";
    public const string Capability = "capability";
    public const string FileType = "fileType";
    public const string Streams = "streams";
    public const string Links = "links";
    public const string NoExtents = "noExtents";
    public const string NoFullPage = "noFullPage";
    public const string Unknown = "unknown";
}

/// <param name="Bus">SDK bus name, for example BusTypeNvme. Null when unknown.</param>
/// <param name="CoveredBytes">Planned file-hint coverage: floor(N/P) x P.</param>
/// <param name="TailBytes">Bytes past the last full page, with no trim coverage.</param>
public sealed record InspectFacts(bool Eligible, string Reason, string? Bus, long CoveredBytes, long TailBytes);

public enum AccessStatus
{
    Ok,
    NotFound,
    Denied,

    /// <summary>Exclusive access was unavailable. Nothing was changed.</summary>
    Busy,
    Failed,
}

public sealed record InspectResult(AccessStatus Status, InspectFacts? Facts);

/// <summary>
/// <see cref="Held"/> is non-null only when the status is Ok and the file is eligible.
/// </summary>
public sealed record AcquireResult(AccessStatus Status, InspectFacts? Facts, IHeldFile? Held);

public enum RevalidateStatus
{
    Ok,
    IdentityChanged,
    NoLongerEligible,
    Failed,
}

public enum TrimStatus
{
    /// <summary>The control returned success with a well-formed output.</summary>
    Completed,

    /// <summary>Verified: the request was never dispatched, the file is unchanged.</summary>
    NotSubmitted,

    /// <summary>Some ranges were processed. Content may have changed.</summary>
    Partial,

    /// <summary>Dispatched with no trustworthy completion. Content may have changed.</summary>
    Unknown,
}

public sealed record TrimResult(TrimStatus Status, int? RangesRequested, int? RangesProcessed);

public enum EntryOutcome
{
    Preserved,

    /// <summary>
    /// Disposition succeeded, the handle closed, and the confirmed identity no longer occupies the name.
    /// </summary>
    Removed,
    Unknown,
}

/// <summary>
/// An exclusive, mutation-capable handle on one eligible file, opened under the caller's own rights.
/// Disposing without unlinking leaves the directory entry in place.
/// </summary>
public interface IHeldFile : IDisposable
{
    InspectFacts Facts { get; }

    /// <summary>Rechecks identity, topology and eligibility immediately before commit.</summary>
    RevalidateStatus Revalidate();

    /// <summary>Flushes and submits the file-relative range [0, CoveredBytes).</summary>
    TrimResult Trim();

    /// <summary>Sets delete disposition on this handle, closes it, and verifies the name.</summary>
    EntryOutcome UnlinkAndClose();
}

/// <summary>Device evidence for one volume. Unknown facts are null.</summary>
public sealed record VolumeProbe(string VolumeGuid, int? DeviceCount, string? Bus, bool? TrimEnabled, bool? SeekPenalty);

public interface IFileDisposalCore
{
    /// <summary>Read-only classification. Opens nothing for mutation.</summary>
    InspectResult Inspect(string path);

    /// <summary>Opens the exclusive mutation handle and classifies through it.</summary>
    AcquireResult Acquire(string path);

    /// <summary>Privileged read-only device probe for a canonical volume GUID path.</summary>
    VolumeProbe ProbeVolume(string volumeGuidPath);
}
