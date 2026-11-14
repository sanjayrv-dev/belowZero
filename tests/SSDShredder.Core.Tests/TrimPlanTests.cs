using SSDShredder.Core;
using Xunit;

namespace SSDShredder.Core.Tests;

public class TrimPlanTests
{
    [Theory]
    [InlineData(0UL, 0UL, 0UL)]
    [InlineData(100UL, 0UL, 100UL)]          // sub-page: ineligible
    [InlineData(4096UL, 4096UL, 0UL)]        // page-aligned
    [InlineData(4097UL, 4096UL, 1UL)]        // spec example
    [InlineData(4711UL, 4096UL, 615UL)]      // wire-contract example
    public void Splits_covered_and_tail(ulong n, ulong c, ulong t)
    {
        var p = TrimPlan.ForLength(n, 4096);
        Assert.Equal(c, p.CoveredBytes);
        Assert.Equal(t, p.TailBytes);
        Assert.Equal(c > 0, p.HasCoverage);
    }

    [Fact]
    public void Rejects_bad_page_size() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TrimPlan.ForLength(10, 3000));
}
