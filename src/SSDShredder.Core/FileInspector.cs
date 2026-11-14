using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SSDShredder.Core;

/// <summary>Identity bound at inspection (volume serial + 128-bit file ID).</summary>
public sealed record FileIdentity(ulong VolumeSerial, byte[] FileId, ulong Length);

public sealed record InspectionResult(CapabilityDecision Decision, FileIdentity Identity, FileFacts Facts, TopologyFacts? Topology);

/// <summary>Read-only capability decision for one local file. Never mutates anything.</summary>
public static class FileInspector
{
    public static InspectionResult Inspect(string absolutePath)
    {
        if (!PathPolicy.IsAcceptable(absolutePath, out var why))
            throw new ArgumentException(why, nameof(absolutePath));

        using var h = Native.CreateFile(absolutePath, Native.GenericRead | Native.FileReadAttributes,
            Native.ShareAll, 0, Native.OpenExisting, Native.FileFlagOpenReparsePoint, 0);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return InspectHandle(h);
    }

    /// <summary>Inspect an already-open handle (used again at commit time by the executor).</summary>
    internal static InspectionResult InspectHandle(SafeFileHandle h)
    {
        var std = Win32.InfoByHandle(h, Native.FileStandardInfoClass, 24);
        ulong eof = BinaryPrimitives.ReadUInt64LittleEndian(std.AsSpan(8));
        uint links = BinaryPrimitives.ReadUInt32LittleEndian(std.AsSpan(16));
        bool isDir = std[21] != 0;

        var tag = Win32.InfoByHandle(h, Native.FileAttributeTagInfoClass, 8);
        uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(tag);

        var id = Win32.InfoByHandle(h, Native.FileIdInfoClass, 24);
        var identity = new FileIdentity(BinaryPrimitives.ReadUInt64LittleEndian(id), id.AsSpan(8, 16).ToArray(), eof);

        int namedStreams = CountNamedStreams(h);

        // Volume
        var fs = new char[64];
        if (Native.GetVolumeInformationByHandle(h, 0, 0, out _, out _, out _, fs, (uint)fs.Length) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        bool ntfs = new string(fs).TrimEnd('\0').Equals("NTFS", StringComparison.OrdinalIgnoreCase);

        string volPath = Win32.FinalVolumeGuidPath(h);
        string volDevice = Win32.VolumeDevicePath(volPath);
        uint cluster = 0;
        if (Native.GetDiskFreeSpace(volDevice + "\\", out var spc, out var bps, out _, out _) != 0)
            cluster = checked(spc * bps);

        var facts = new FileFacts(ntfs, !isDir, (attrs & Native.AttrReparse) != 0,
            (attrs & Native.AttrSparse) != 0, (attrs & Native.AttrCompressed) != 0, (attrs & Native.AttrEncrypted) != 0,
            links, namedStreams, eof, cluster, Win32.PageSize());

        TopologyFacts? topo = null;
        try { topo = QueryTopology(volDevice); } catch (Win32Exception) { /* leave unknown */ }

        AllocationFacts alloc = cluster == 0 ? new(false, false, null) : QueryAllocation(h);
        return new InspectionResult(CapabilityClassifier.Decide(facts, topo, alloc), identity, facts, topo);
    }

    static int CountNamedStreams(SafeFileHandle h)
    {
        var buf = new byte[16 * 1024];
        unsafe
        {
            fixed (byte* p = buf)
                if (Native.GetFileInformationByHandleEx(h, Native.FileStreamInfoClass, (nint)p, (uint)buf.Length) == 0)
                {
                    int e = Marshal.GetLastPInvokeError();
                    if (e == Native.ErrorHandleEof) return 0;
                    return int.MaxValue; // too many/unreadable → treated as "streams present" (fail closed)
                }
        }
        int count = 0, off = 0;
        while (true)
        {
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off));
            uint nameLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 4));
            if (24 + nameLen > buf.Length - off) return int.MaxValue;
            string name = Encoding.Unicode.GetString(buf, off + 24, (int)nameLen);
            if (!name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase)) count++;
            if (next == 0) break;
            off = checked(off + (int)next);
            if (off >= buf.Length) return int.MaxValue;
        }
        return count;
    }

    static AllocationFacts QueryAllocation(SafeFileHandle h)
    {
        var map = new AllocationMap();
        long vcn = 0;
        var inBuf = new byte[8];
        var outBuf = new byte[Native.RetrievalHeaderSize + Native.RetrievalExtentSize * 256];
        for (int guard = 0; guard < 4096; guard++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(inBuf, vcn);
            int err = Win32.Ioctl(h, Native.FsctlGetRetrievalPointers, inBuf, outBuf, out uint returned);
            if (err == Native.ErrorHandleEof) return new(true, false, map);        // nothing at/after this VCN
            if (err != 0 && err != Native.ErrorMoreData)
                return new(false, err == 1 /* ERROR_INVALID_FUNCTION */, null);
            try
            {
                var chunk = RetrievalPointers.Parse(outBuf, returned);
                map.Add(chunk);
                if (err == 0) return new(true, false, map);
                if (chunk.Extents.Count == 0) return new(false, false, null);       // no progress
                vcn = chunk.Extents[^1].NextVcn;
            }
            catch (InvalidDataException) { return new(false, false, null); }
        }
        return new(false, false, null);
    }

    static TopologyFacts QueryTopology(string volumeDevicePath)
    {
        using var vol = Win32.OpenQuery(volumeDevicePath);
        if (vol.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());

        var ext = new byte[8 + 24 * 16];
        int err = Win32.Ioctl(vol, Native.IoctlVolumeGetVolumeDiskExtents, default, ext, out uint r);
        if (err != 0 || r < 8) return new(0, BusType.Unknown, null, null);   // incl. ERROR_MORE_DATA → multi-disk
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(ext);
        if (n != 1 || r < 8 + 24) return new((int)n, BusType.Unknown, null, null);
        uint diskNo = BinaryPrimitives.ReadUInt32LittleEndian(ext.AsSpan(8));

        using var disk = Win32.OpenQuery($@"\\.\PhysicalDrive{diskNo}");
        if (disk.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());

        var o = new byte[1024];
        BusType bus = BusType.Unknown;
        if (Win32.Ioctl(disk, Native.IoctlStorageQueryProperty, DeviceDescriptors.BuildPropertyQuery(Native.PropDevice), o, out r) == 0 && r >= 32)
        {
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(28));
            bus = Enum.IsDefined(typeof(BusType), (int)b) ? (BusType)b : BusType.Unknown;
        }
        bool? seek = Win32.Ioctl(disk, Native.IoctlStorageQueryProperty, DeviceDescriptors.BuildPropertyQuery(Native.PropSeekPenalty), o, out r) == 0
            ? DeviceDescriptors.ParseSeekPenalty(o, r) : null;
        bool? trim = Win32.Ioctl(disk, Native.IoctlStorageQueryProperty, DeviceDescriptors.BuildPropertyQuery(Native.PropTrim), o, out r) == 0
            ? DeviceDescriptors.ParseTrimEnabled(o, r) : null;
        return new(1, bus, seek, trim);
    }
}
