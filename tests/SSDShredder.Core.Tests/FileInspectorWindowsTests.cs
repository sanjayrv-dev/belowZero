using SSDShredder.Core;
using Xunit;
using Xunit.Abstractions;

namespace SSDShredder.Core.Tests;

/// <summary>Read-only smoke tests on a throwaway temp file. The verdict depends on the machine; these just print it.</summary>
public class FileInspectorWindowsTests(ITestOutputHelper output)
{
    [Fact]
    public void Inspect_page_plus_tail_file_prints_decision()
    {
        var path = Path.Combine(Path.GetTempPath(), $"belowzero-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[4096 * 3 + 615]);
        try
        {
            var r = FileInspector.Inspect(path);
            output.WriteLine($"eligible={r.Decision.Eligible} reason={r.Decision.Reason} bus={r.Decision.BusName} " +
                             $"covered={r.Decision.CoveredBytes} tail={r.Decision.TailBytes} :: {r.Decision.Observation}");
            output.WriteLine($"cluster={r.Facts.BytesPerCluster} page={r.Facts.PageSize} ntfs={r.Facts.IsLocalNtfs} topo={r.Topology}");
            Assert.Equal((ulong)(4096 * 3 + 615), r.Identity.Length);
            Assert.Equal(16, r.Identity.FileId.Length);
            if (r.Decision.Eligible) { Assert.Equal(12288UL, r.Decision.CoveredBytes); Assert.Equal(615UL, r.Decision.TailBytes); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Inspect_tiny_file_is_never_eligible()
    {
        var path = Path.Combine(Path.GetTempPath(), $"belowzero-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[100]);
        try { Assert.False(FileInspector.Inspect(path).Decision.Eligible); }
        finally { File.Delete(path); }
    }
}
