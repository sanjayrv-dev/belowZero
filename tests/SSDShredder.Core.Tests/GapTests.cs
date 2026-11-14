using SSDShredder.Core;
using Xunit;
using Xunit.Abstractions;

namespace SSDShredder.Core.Tests;

public class ClassifierEdgeTests
{
    static FileFacts F(ulong len) => new(true, true, false, false, false, false, 1, 0, len, 4096, 4096);
    static TopologyFacts Nvme => new(1, BusType.Nvme, false, true);
    static AllocationFacts Alloc(long clusters)
    {
        var b = new byte[32];
        BitConverter.TryWriteBytes(b.AsSpan(0), 1u);
        BitConverter.TryWriteBytes(b.AsSpan(16), clusters);
        BitConverter.TryWriteBytes(b.AsSpan(24), 1000L);
        var m = new AllocationMap(); m.Add(RetrievalPointers.Parse(b, (uint)b.Length));
        return new(true, false, m);
    }
    static EligibilityReason R(FileFacts f, TopologyFacts? t, AllocationFacts a) => CapabilityClassifier.Decide(f, t, a).Reason;

    [Fact] public void Sparse_is_streams() => Assert.Equal(EligibilityReason.Streams, R(F(8192) with { IsSparse = true }, Nvme, Alloc(2)));
    [Fact] public void Compressed_is_streams() => Assert.Equal(EligibilityReason.Streams, R(F(8192) with { IsCompressed = true }, Nvme, Alloc(2)));
    [Fact] public void Efs_is_streams() => Assert.Equal(EligibilityReason.Streams, R(F(8192) with { IsEncrypted = true }, Nvme, Alloc(2)));
    [Fact] public void Named_stream_is_streams() => Assert.Equal(EligibilityReason.Streams, R(F(8192) with { NamedStreamCount = 1 }, Nvme, Alloc(2)));
    [Fact] public void Reparse_is_filetype() => Assert.Equal(EligibilityReason.FileType, R(F(8192) with { HasReparsePoint = true }, Nvme, Alloc(2)));
    [Fact] public void Non_ntfs_is_filesystem() => Assert.Equal(EligibilityReason.Filesystem, R(F(8192) with { IsLocalNtfs = false }, Nvme, Alloc(2)));
    [Fact] public void Unknown_bridge_is_topology() => Assert.Equal(EligibilityReason.Topology, R(F(8192), new(1, BusType.Unknown, false, true), Alloc(2)));
    [Fact] public void Unobserved_topology_is_unknown() => Assert.Equal(EligibilityReason.Unknown, R(F(8192), null, Alloc(2)));
    [Fact] public void Multi_disk_is_topology() => Assert.Equal(EligibilityReason.Topology, R(F(8192), new(2, BusType.Nvme, false, true), Alloc(2)));
    [Fact] public void Rotational_is_topology() => Assert.Equal(EligibilityReason.Topology, R(F(8192), new(1, BusType.Sata, true, true), Alloc(2)));
    [Fact] public void Trim_disabled_is_capability() => Assert.Equal(EligibilityReason.Capability, R(F(8192), new(1, BusType.Nvme, false, false), Alloc(2)));
    [Fact] public void Unsupported_extent_query_is_unknown() => Assert.Equal(EligibilityReason.Unknown, R(F(8192), Nvme, new(false, true, null)));
    [Fact] public void Short_extent_coverage_is_unknown() => Assert.Equal(EligibilityReason.Unknown, R(F(3 * 4096), Nvme, Alloc(1)));
    [Fact] public void Empty_is_noextents() => Assert.Equal(EligibilityReason.NoExtents, R(F(0), Nvme, Alloc(1)));

    [Fact] public void Page_aligned_has_no_tail()
    {
        var d = CapabilityClassifier.Decide(F(8192), Nvme, Alloc(2));
        Assert.True(d.Eligible); Assert.Equal(8192UL, d.CoveredBytes); Assert.Equal(0UL, d.TailBytes);
    }

    [Fact] public void Worker_fixtures_are_internally_consistent()
    {
        Assert.True(WorkerFixtures.EligibleDecision.Eligible);
        Assert.False(WorkerFixtures.UnsupportedDecision.Eligible);
        Assert.Equal(EntryState.Removed, WorkerFixtures.Eligible.Entry);
        Assert.Equal(EntryState.Preserved, WorkerFixtures.Partial.Entry);
        Assert.NotEqual(WorkerFixtures.Partial.RangesRequested, WorkerFixtures.Partial.RangesProcessed);
        Assert.Equal(8, (int)WorkerFixtures.Partial.State);
    }
}

/// <summary>Sizing experiment on a synthetic scratch file in %TEMP%: grow, flush, re-inspect, record the transition.</summary>
public class SizingExperimentTests(ITestOutputHelper output)
{
    [Fact] public void Growing_file_leaves_resident_state_and_never_returns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"belowzero-size-{Guid.NewGuid():N}.bin");
        try
        {
            bool nonResident = false; ulong firstNonResident = 0;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                foreach (ulong target in new ulong[] { 1, 100, 600, 700, 900, 1000, 1500, 2048, 4095, 4096, 4097, 8192, 12288 + 615 })
                {
                    fs.SetLength(0); fs.Write(new byte[target]); fs.Flush(true);
                    var r = FileInspector.Inspect(path);
                    output.WriteLine($"len={target} reason={r.Decision.Reason} eligible={r.Decision.Eligible} covered={r.Decision.CoveredBytes} tail={r.Decision.TailBytes} cluster={r.Facts.BytesPerCluster} page={r.Facts.PageSize} ntfs={r.Facts.IsLocalNtfs} :: {r.Decision.Observation}");
                    if (!r.Facts.IsLocalNtfs) return;                     // experiment is NTFS-specific
                    if (target < r.Facts.PageSize) Assert.False(r.Decision.Eligible);
                    bool nr = r.Decision.Reason != EligibilityReason.NoExtents && r.Decision.Reason != EligibilityReason.Unknown;
                    if (nr && !nonResident) { nonResident = true; firstNonResident = target; }
                    if (nonResident) Assert.NotEqual(EligibilityReason.NoExtents, r.Decision.Reason);
                }
            }
            output.WriteLine($"first non-resident length observed: {(nonResident ? firstNonResident : 0)}");
        }
        finally { File.Delete(path); }
    }
}
