using System.Buffers.Binary;
using SSDShredder.Core;
using Xunit;

namespace SSDShredder.Core.Tests;

public class ParserAndClassifierTests
{
    static byte[] Buf(long startVcn, params (long next, long lcn)[] ext)
    {
        var b = new byte[16 + 16 * ext.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)ext.Length);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), startVcn);
        for (int i = 0; i < ext.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16 + 16 * i), ext[i].next);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24 + 16 * i), ext[i].lcn);
        }
        return b;
    }

    [Fact] public void ControlCodes_match_spec()
    {
        Assert.Equal(0x00090073u, Native.FsctlGetRetrievalPointers);
        Assert.Equal(0x00098208u, Native.FsctlFileLevelTrim);
        Assert.Equal(0x002D1400u, Native.IoctlStorageQueryProperty);
    }

    [Fact] public void FileInfoClasses_match_sdk_enum()
    {
        Assert.Equal(1, Native.FileStandardInfoClass);
        Assert.Equal(4, Native.FileDispositionInfoClass);
        Assert.Equal(7, Native.FileStreamInfoClass);
        Assert.Equal(9, Native.FileAttributeTagInfoClass);
        Assert.Equal(18, Native.FileIdInfoClass);
        Assert.Equal(21, Native.FileDispositionInfoExClass);
    }

    [Fact] public void Parses_extents()
    {
        var b = Buf(0, (2, 100), (5, 400));
        var c = RetrievalPointers.Parse(b, (uint)b.Length);
        Assert.Equal(2, c.Extents.Count);
        Assert.Equal(3, c.Extents[1].Clusters);
    }

    [Fact] public void Rejects_short_and_overcount()
    {
        Assert.Throws<InvalidDataException>(() => RetrievalPointers.Parse(new byte[8], 8));
        var b = Buf(0, (2, 100));
        b[0] = 5; // claims 5 extents
        Assert.Throws<InvalidDataException>(() => RetrievalPointers.Parse(b, (uint)b.Length));
    }

    [Fact] public void Rejects_nonpositive_span()
    {
        var b = Buf(4, (4, 100));
        Assert.Throws<InvalidDataException>(() => RetrievalPointers.Parse(b, (uint)b.Length));
    }

    [Fact] public void Continuation_requires_forward_progress()
    {
        var m = new AllocationMap();
        var b1 = Buf(0, (2, 10)); m.Add(RetrievalPointers.Parse(b1, (uint)b1.Length));
        var b2 = Buf(1, (3, 20));
        Assert.Throws<InvalidDataException>(() => m.Add(RetrievalPointers.Parse(b2, (uint)b2.Length)));
    }

    static FileFacts File(ulong len) => new(true, true, false, false, false, false, 1, 0, len, 4096, 4096);
    static TopologyFacts Nvme => new(1, BusType.Nvme, false, true);
    static AllocationFacts Alloc(ulong clusters)
    {
        var m = new AllocationMap();
        var b = Buf(0, ((long)clusters, 1000));
        m.Add(RetrievalPointers.Parse(b, (uint)b.Length));
        return new(true, false, m);
    }

    [Fact] public void Eligible_with_tail()
    {
        var d = CapabilityClassifier.Decide(File(4711), Nvme, Alloc(2));
        Assert.True(d.Eligible); Assert.Equal(4096UL, d.CoveredBytes); Assert.Equal(615UL, d.TailBytes);
    }

    [Fact] public void Subpage_is_nofullpage() =>
        Assert.Equal(EligibilityReason.NoFullPage, CapabilityClassifier.Decide(File(100), Nvme, Alloc(1)).Reason);

    [Fact] public void Usb_is_topology() =>
        Assert.Equal(EligibilityReason.Topology,
            CapabilityClassifier.Decide(File(8192), new(1, BusType.Usb, false, true), Alloc(2)).Reason);

    [Fact] public void Resident_is_noextents() =>
        Assert.Equal(EligibilityReason.NoExtents,
            CapabilityClassifier.Decide(File(100), Nvme, new(true, false, new AllocationMap())).Reason);

    [Fact] public void Hole_or_failed_query_is_unknown()
    {
        Assert.Equal(EligibilityReason.Unknown,
            CapabilityClassifier.Decide(File(8192), Nvme, new(false, false, null)).Reason);
    }

    [Fact] public void Multilink_is_links() =>
        Assert.Equal(EligibilityReason.Links,
            CapabilityClassifier.Decide(File(8192) with { LinkCount = 2 }, Nvme, Alloc(2)).Reason);
}
