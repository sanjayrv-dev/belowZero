using System.Buffers.Binary;

namespace SSDShredder.Core;

/// <summary>FSCTL_FILE_LEVEL_TRIM input/output layout: key@0 (0), count@4, ranges@8 (offset,length) pairs of file bytes.</summary>
public static class TrimRequest
{
    public static byte[] Build(IReadOnlyList<(ulong Offset, ulong Length)> ranges)
    {
        if (ranges.Count == 0) throw new ArgumentException("At least one range is required.", nameof(ranges));
        int size = checked(8 + 16 * ranges.Count);
        var b = new byte[size];                                  // key = 0
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
        {
            if (ranges[i].Length == 0) throw new ArgumentException("Zero-length range.", nameof(ranges));
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8 + 16 * i), ranges[i].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16 + 16 * i), ranges[i].Length);
        }
        return b;
    }

    /// <summary>Returns NumRangesProcessed, or null when the output is not exactly the 4-byte structure.</summary>
    public static uint? ParseOutput(ReadOnlySpan<byte> output, uint bytesReturned)
        => bytesReturned == 4 && output.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(output) : null;
}
