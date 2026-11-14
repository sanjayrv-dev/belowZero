using System.ComponentModel;
using SSDShredder.Core;

namespace SSDShredder.Worker.CoreAdapter;

/// <summary>
/// Adapts the real SSDShredder.Core (Engineer 1) behind the Worker's IFileDisposalCore
/// seam, per src/SSDShredder.Worker/README.md's "Integration seams" note. Wire this in from
/// WorkerEnvironment.Create() with CoreAvailable = true to replace Simulation.SimulatedCore
/// in real (non-simulated) runs.
///
/// Written and reviewed without a Windows machine available. Core's own native paths
/// (FileInspector, TrimEngine) have Windows-only tests in SSDShredder.Core.Tests that this
/// adapter does not change the outcome of (HeldFile.Commit is untouched; this adapter only
/// calls the new, additive RevalidateForCommit/TrimOnly/UnlinkOnly, which mirror Commit()'s
/// checks exactly and have their own fault-injection tests in
/// tests/SSDShredder.Core.Tests/DecomposedCommitTests.cs). Both test suites, plus a live
/// GUI inspect-then-shred pass on a disposable file, need to actually run on Windows and
/// pass before this should be trusted with real data.
/// </summary>
public sealed class CoreFileDisposalAdapter : IFileDisposalCore
{
    private readonly TrimEngine _engine;

    public CoreFileDisposalAdapter() : this(new TrimEngine()) { }

    /// <summary>Testing seam: an engine built with Core's own fault-injection fakes.</summary>
    internal CoreFileDisposalAdapter(TrimEngine engine) => _engine = engine;

    public InspectResult Inspect(string path)
    {
        try
        {
            SSDShredder.Core.InspectionResult r = SSDShredder.Core.FileInspector.Inspect(path);
            return new InspectResult(AccessStatus.Ok, ToFacts(r));
        }
        catch (Win32Exception ex) { return new InspectResult(MapAccess(ex.NativeErrorCode), null); }
        catch (ArgumentException) { return new InspectResult(AccessStatus.NotFound, null); } // PathPolicy rejection.
    }

    public AcquireResult Acquire(string path)
    {
        SSDShredder.Core.HeldFile held;
        try { held = _engine.Open(path); }
        catch (ShredException ex) { return new AcquireResult(MapShredState(ex.State), null, null); }

        InspectFacts facts = ToFacts(held.Inspection);
        if (!facts.Eligible)
        {
            held.Dispose();
            return new AcquireResult(AccessStatus.Ok, facts, null);
        }
        return new AcquireResult(AccessStatus.Ok, facts, new HeldFileAdapter(held));
    }

    // Core does not yet expose a standalone, by-volume-GUID device probe (only as part of a
    // full file inspection). This is the vault helper's private evidence-gathering verb, not
    // the file-disposal path above, and is unrelated to today's integration step.
    public VolumeProbe ProbeVolume(string volumeGuidPath) => new(volumeGuidPath, null, null, null, null);

    internal static InspectFacts ToFacts(SSDShredder.Core.InspectionResult r)
    {
        CapabilityDecision d = r.Decision;
        return new InspectFacts(d.Eligible, MapReason(d.Reason), MapBus(r.Topology?.Bus), (long)d.CoveredBytes, (long)d.TailBytes);
    }

    internal static string MapReason(EligibilityReason r) => r switch
    {
        EligibilityReason.Eligible => Reasons.Eligible,
        EligibilityReason.Filesystem => Reasons.Filesystem,
        EligibilityReason.Topology => Reasons.Topology,
        EligibilityReason.Capability => Reasons.Capability,
        EligibilityReason.FileType => Reasons.FileType,
        EligibilityReason.Streams => Reasons.Streams,
        EligibilityReason.Links => Reasons.Links,
        EligibilityReason.NoExtents => Reasons.NoExtents,
        EligibilityReason.NoFullPage => Reasons.NoFullPage,
        _ => Reasons.Unknown,
    };

    // The wire contract's "bus" field is the raw SDK bus-type name (IFileDisposalCore.cs's own
    // doc comment: "SDK bus name, for example BusTypeNvme"), not Core's lowercased display form.
    internal static string? MapBus(BusType? bus) => bus switch
    {
        BusType.Nvme => "BusTypeNvme",
        BusType.Sata => "BusTypeSata",
        BusType.Usb => "BusTypeUsb",
        _ => null,
    };

    internal static AccessStatus MapAccess(int win32Error) => win32Error switch
    {
        2 or 3 => AccessStatus.NotFound,  // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
        5 => AccessStatus.Denied,          // ERROR_ACCESS_DENIED
        32 or 33 => AccessStatus.Busy,     // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        _ => AccessStatus.Failed,
    };

    internal static AccessStatus MapShredState(ShredState s) => s switch
    {
        ShredState.Busy => AccessStatus.Busy,
        ShredState.Denied => AccessStatus.Denied,
        _ => AccessStatus.Failed,
    };

    private sealed class HeldFileAdapter(SSDShredder.Core.HeldFile held) : IHeldFile
    {
        public InspectFacts Facts { get; } = ToFacts(held.Inspection);

        public RevalidateStatus Revalidate() => held.RevalidateForCommit() switch
        {
            RevalidateOutcome.Ok => RevalidateStatus.Ok,
            RevalidateOutcome.IdentityChanged => RevalidateStatus.IdentityChanged,
            RevalidateOutcome.NoLongerEligible => RevalidateStatus.NoLongerEligible,
            // Commit() treats a coverage change the same as an identity change (Denied) — mirror that here.
            RevalidateOutcome.CoverageChanged => RevalidateStatus.IdentityChanged,
            _ => RevalidateStatus.Failed,
        };

        public TrimResult Trim()
        {
            TrimAttemptResult r = held.TrimOnly();
            TrimStatus status = r.Outcome switch
            {
                TrimAttemptOutcome.Completed => TrimStatus.Completed,
                TrimAttemptOutcome.NotSubmitted => TrimStatus.NotSubmitted,
                TrimAttemptOutcome.Partial => TrimStatus.Partial,
                _ => TrimStatus.Unknown,
            };
            return new TrimResult(status, (int?)r.RangesRequested, (int?)r.RangesProcessed);
        }

        public EntryOutcome UnlinkAndClose() => held.UnlinkOnly() switch
        {
            EntryState.Removed => EntryOutcome.Removed,
            EntryState.Preserved => EntryOutcome.Preserved,
            _ => EntryOutcome.Unknown,
        };

        public void Dispose() => held.Dispose();
    }
}
