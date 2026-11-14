using System.Buffers.Binary;

namespace SSDShredder.Core;

public static class DeviceDescriptors
{
    /// <summary>STORAGE_PROPERTY_QUERY: property@0, queryType@4 (standard=0), full 12 bytes zeroed first.</summary>
    public static byte[] BuildPropertyQuery(uint propertyId)
    {
        var q = new byte[Native.StorageQuerySize];
        BinaryPrimitives.WriteUInt32LittleEndian(q, propertyId);
        return q;
    }

    /// <summary>DEVICE_TRIM_DESCRIPTOR → TrimEnabled; null (unknown) on absent/short/wrong-version results.</summary>
    public static bool? ParseTrimEnabled(ReadOnlySpan<byte> buffer, uint returned)
    {
        if (returned > buffer.Length || returned < 9) return null;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (size < 9 || size > returned) return null;
        return buffer[8] != 0;
    }

    /// <summary>DEVICE_SEEK_PENALTY_DESCRIPTOR → IncursSeekPenalty (BOOLEAN@8); null if unknown.</summary>
    public static bool? ParseSeekPenalty(ReadOnlySpan<byte> buffer, uint returned)
    {
        if (returned > buffer.Length || returned < 9) return null;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (size < 9 || size > returned) return null;
        return buffer[8] != 0;
    }
}
