using System.Buffers.Binary;
using SSDShredder.Core;
using Xunit;

namespace SSDShredder.Core.Tests;

public class TrimRequestTests
{
    [Fact] public void One_range_layout_is_24_bytes()
    {
        var b = TrimRequest.Build([(0UL, 4096UL)]);
        Assert.Equal(24, b.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(b));            // key
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));  // count
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8)));
        Assert.Equal(4096UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16)));
    }

    [Fact] public void Two_ranges_are_40_bytes() => Assert.Equal(40, TrimRequest.Build([(0UL, 4096UL), (8192UL, 4096UL)]).Length);

    [Fact] public void Rejects_empty_and_zero_length()
    {
        Assert.Throws<ArgumentException>(() => TrimRequest.Build([]));
        Assert.Throws<ArgumentException>(() => TrimRequest.Build([(0UL, 0UL)]));
    }

    [Fact] public void Output_must_be_exactly_four_bytes()
    {
        Assert.Equal(1u, TrimRequest.ParseOutput([1, 0, 0, 0], 4));
        Assert.Null(TrimRequest.ParseOutput([1, 0, 0, 0], 3));
        Assert.Null(TrimRequest.ParseOutput([1, 0, 0, 0, 0], 5));
    }
}
