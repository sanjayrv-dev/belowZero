using Microsoft.Win32.SafeHandles;
using SSDShredder.Core;
using Xunit;
using Xunit.Abstractions;

namespace SSDShredder.Core.Tests;

/// <summary>
/// Diagnostic only (never asserts on the OS verdict). Tries FSCTL_FILE_LEVEL_TRIM several ways on throwaway
/// files and prints each Win32 result. Set BELOWZERO_DIAG_DIR to a folder on another volume to compare volumes.
/// </summary>
public class TrimDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public void Print_trim_results_for_several_variants()
    {
        var dir = Environment.GetEnvironmentVariable("BELOWZERO_DIAG_DIR") ?? Path.GetTempPath();
        output.WriteLine($"dir={dir}");
        var variants = new (string Name, int Size, uint Access, uint Share, bool Flush, ulong Off, ulong Len)[]
        {
            ("1 page, exclusive, flush",        4096 * 3 + 615, Native.GenericRead | Native.GenericWrite, 0, true,  0, 4096),
            ("3 pages, exclusive, flush",       4096 * 3 + 615, Native.GenericRead | Native.GenericWrite, 0, true,  0, 12288),
            ("3 pages, exclusive, no flush",    4096 * 3 + 615, Native.GenericRead | Native.GenericWrite, 0, false, 0, 12288),
            ("3 pages, shared rw, flush",       4096 * 3 + 615, Native.GenericRead | Native.GenericWrite, 3, true,  0, 12288),
            ("1 MiB file, whole, flush",        1 << 20,        Native.GenericRead | Native.GenericWrite, 0, true,  0, 1 << 20),
            ("write-data only access, flush",   4096 * 3 + 615, 0x2 /*FILE_WRITE_DATA*/ | 0x80,            0, true,  0, 12288),
        };
        var sub = new NativeTrimSubmitter();
        foreach (var v in variants)
        {
            var path = Path.Combine(dir, $"belowzero-diag-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(path, new byte[v.Size]);
            try
            {
                using var h = Native.CreateFile(path, v.Access, v.Share, 0, Native.OpenExisting, 0, 0);
                if (h.IsInvalid) { output.WriteLine($"{v.Name}: open failed {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}"); continue; }
                if (v.Flush) Native.FlushFileBuffers(h);
                var r = sub.Submit(h, [(v.Off, v.Len)]);
                output.WriteLine($"{v.Name}: win32={r.Win32Error} returned={r.BytesReturned} processed={(r.RangesProcessed?.ToString() ?? "null")}");
            }
            finally { File.Delete(path); }
        }
    }
}
