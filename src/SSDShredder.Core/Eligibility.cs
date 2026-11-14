namespace SSDShredder.Core;

public enum EligibilityReason { Eligible, Filesystem, Topology, Capability, FileType, Streams, Links, NoExtents, NoFullPage, Unknown }

/// <summary>SDK STORAGE_BUS_TYPE values used by v1 policy.</summary>
public enum BusType { Unknown = 0, Usb = 7, Sata = 11, Nvme = 17 }

/// <summary>Facts read from the open file handle and volume (null = could not be observed).</summary>
public sealed record FileFacts(
    bool IsLocalNtfs, bool IsRegularFile, bool HasReparsePoint,
    bool IsSparse, bool IsCompressed, bool IsEncrypted,
    uint LinkCount, int NamedStreamCount, ulong Length,
    uint BytesPerCluster, uint PageSize);

public sealed record TopologyFacts(
    int DiskCount, BusType Bus, bool? IncursSeekPenalty, bool? TrimEnabled);

public sealed record AllocationFacts(bool QuerySucceeded, bool QueryUnsupported, AllocationMap? Map);

public sealed record CapabilityDecision(
    bool Eligible, EligibilityReason Reason, string BusName,
    ulong CoveredBytes, ulong TailBytes, string Observation);

public static class CapabilityClassifier
{
    public static CapabilityDecision Decide(FileFacts f, TopologyFacts? t, AllocationFacts a)
    {
        string bus = t is null ? "unknown" : t.Bus.ToString().ToLowerInvariant();
        CapabilityDecision No(EligibilityReason r, string why, ulong c = 0, ulong tail = 0)
            => new(false, r, bus, c, tail, why);

        if (!f.IsLocalNtfs) return No(EligibilityReason.Filesystem, "not a local NTFS volume");
        if (!f.IsRegularFile || f.HasReparsePoint) return No(EligibilityReason.FileType, "not a plain regular file");
        if (f.LinkCount != 1) return No(EligibilityReason.Links, $"link count {f.LinkCount}");
        if (f.IsSparse || f.IsCompressed || f.IsEncrypted || f.NamedStreamCount > 0)
            return No(EligibilityReason.Streams, "sparse/compressed/EFS or named stream present");

        if (t is null || t.DiskCount != 1 || t.IncursSeekPenalty is null || t.TrimEnabled is null)
            return No(t is null ? EligibilityReason.Unknown : EligibilityReason.Topology, "topology ambiguous or unobserved");
        if (t.Bus is not (BusType.Nvme or BusType.Sata)) return No(EligibilityReason.Topology, $"bus {t.Bus} not allowed");
        if (t.IncursSeekPenalty == true) return No(EligibilityReason.Topology, "rotational media");
        if (t.TrimEnabled == false) return No(EligibilityReason.Capability, "device reports TRIM disabled");

        if (f.Length == 0) return No(EligibilityReason.NoExtents, "empty file");
        if (a.QueryUnsupported) return No(EligibilityReason.Unknown, "extent query unsupported");
        if (!a.QuerySucceeded || a.Map is null) return No(EligibilityReason.Unknown, "extent query failed");
        if (a.Map.Extents.Count == 0) return No(EligibilityReason.NoExtents, "no external extents (resident)");
        if (a.Map.HasHole) return No(EligibilityReason.Unknown, "hole in allocation without sparse flag");

        // Extents must cover the logical length (allocation is cluster-rounded, so >=).
        if (a.Map.AllocatedBytes(f.BytesPerCluster) < f.Length)
            return No(EligibilityReason.Unknown, "extent coverage shorter than file length");

        var plan = TrimPlan.ForLength(f.Length, f.PageSize);
        if (!plan.HasCoverage) return No(EligibilityReason.NoFullPage, "no complete page", 0, plan.TailBytes);
        return new(true, EligibilityReason.Eligible, bus, plan.CoveredBytes, plan.TailBytes, "all checks passed");
    }
}
