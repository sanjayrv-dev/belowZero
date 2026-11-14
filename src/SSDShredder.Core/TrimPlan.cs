namespace SSDShredder.Core;

/// <summary>Page-aligned coverage for a stream of length N: C = floor(N/P)*P, T = N - C.</summary>
public readonly record struct TrimPlan(ulong CoveredBytes, ulong TailBytes)
{
    public bool HasCoverage => CoveredBytes > 0;

    public static TrimPlan ForLength(ulong length, uint pageSize)
    {
        if (pageSize == 0 || (pageSize & (pageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be a power of two.");
        ulong covered = checked(length / pageSize * pageSize);
        return new TrimPlan(covered, length - covered);
    }
}
