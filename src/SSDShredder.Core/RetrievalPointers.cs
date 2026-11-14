using System.Buffers.Binary;

namespace SSDShredder.Core;

public readonly record struct Extent(long StartVcn, long NextVcn, long Lcn)
{
    public long Clusters => NextVcn - StartVcn;
    /// <summary>LCN -1 denotes a sparse hole or partial compression unit.</summary>
    public bool IsHole => Lcn == -1;
}

public sealed record RetrievalChunk(long StartingVcn, IReadOnlyList<Extent> Extents);

public static class RetrievalPointers
{
    /// <summary>
    /// Parses one FSCTL_GET_RETRIEVAL_POINTERS output buffer. Validates returned &gt;= 16 and
    /// count &lt;= (returned-16)/16, treats VCN/LCN as diagnostics only, requires positive spans.
    /// Complete entries are returned even when the call ended with ERROR_MORE_DATA.
    /// </summary>
    public static RetrievalChunk Parse(ReadOnlySpan<byte> buffer, uint bytesReturned)
    {
        if (bytesReturned > buffer.Length)
            throw new InvalidDataException("Driver reported more bytes than the buffer holds.");
        if (bytesReturned < Native.RetrievalHeaderSize)
            throw new InvalidDataException("Retrieval output shorter than its header.");

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        long startingVcn = BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]);
        if (startingVcn < 0) throw new InvalidDataException("Negative starting VCN.");
        uint max = (bytesReturned - Native.RetrievalHeaderSize) / Native.RetrievalExtentSize;
        if (count > max) throw new InvalidDataException("Extent count exceeds returned bytes.");

        var extents = new List<Extent>((int)count);
        long current = startingVcn;
        for (int i = 0; i < count; i++)
        {
            var off = Native.RetrievalHeaderSize + i * Native.RetrievalExtentSize;
            long next = BinaryPrimitives.ReadInt64LittleEndian(buffer[off..]);
            long lcn = BinaryPrimitives.ReadInt64LittleEndian(buffer[(off + 8)..]);
            if (next <= current) throw new InvalidDataException("Extent span is not positive.");
            if (lcn < -1) throw new InvalidDataException("Invalid LCN.");
            extents.Add(new Extent(current, next, lcn));
            current = next;
        }
        return new RetrievalChunk(startingVcn, extents);
    }
}

/// <summary>Accumulates chunks across ERROR_MORE_DATA continuations, requiring forward progress.</summary>
public sealed class AllocationMap
{
    private readonly List<Extent> _extents = new();
    private readonly int _maxExtents;
    public AllocationMap(int maxExtents = 1 << 20) => _maxExtents = maxExtents;

    public IReadOnlyList<Extent> Extents => _extents;
    public long NextVcn => _extents.Count == 0 ? 0 : _extents[^1].NextVcn;

    public void Add(RetrievalChunk chunk)
    {
        if (chunk.Extents.Count == 0) return;
        if (_extents.Count > 0 && chunk.Extents[0].StartVcn < NextVcn)
            throw new InvalidDataException("Continuation made no forward progress.");
        if (_extents.Count + chunk.Extents.Count > _maxExtents)
            throw new InvalidDataException("Extent list exceeds the configured bound.");
        _extents.AddRange(chunk.Extents);
    }

    public bool HasHole => _extents.Any(e => e.IsHole);

    /// <summary>Allocated (non-hole) bytes via checked arithmetic.</summary>
    public ulong AllocatedBytes(uint bytesPerCluster)
    {
        ulong total = 0;
        foreach (var e in _extents)
            if (!e.IsHole) total = checked(total + checked((ulong)e.Clusters * bytesPerCluster));
        return total;
    }
}
