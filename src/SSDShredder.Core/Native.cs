using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SSDShredder.Core;

/// <summary>Control codes and native layouts, per docs/01. Generated from (device, access, function, method).</summary>
internal static partial class Native
{
    internal static uint CtlCode(uint device, uint access, uint function, uint method)
        => (device << 16) | (access << 14) | (function << 2) | method;

    internal static readonly uint FsctlGetRetrievalPointers = CtlCode(9, 0, 28, 3);   // 0x00090073
    internal static readonly uint FsctlFileLevelTrim = CtlCode(9, 2, 130, 0);         // 0x00098208
    internal static readonly uint IoctlStorageQueryProperty = CtlCode(0x2D, 0, 0x500, 0); // 0x002D1400

    internal const int ErrorMoreData = 234;
    internal const int ErrorHandleEof = 38;

    internal const int RetrievalHeaderSize = 16;   // count@0, pad@4, startingVcn@8
    internal const int RetrievalExtentSize = 16;   // nextVcn@0, lcn@8
    internal const int StorageQuerySize = 12;      // property@0, queryType@4, 1 byte@8, 3 pad
    internal const int TrimDescriptorSize = 12;    // version@0, size@4, trimEnabled@8, 3 pad

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int DeviceIoControl(SafeFileHandle handle, uint code,
        nint input, uint inputBytes, nint output, uint outputBytes,
        out uint bytesReturned, nint overlapped);

    [StructLayout(LayoutKind.Sequential)] internal struct StartingVcnInput { public long StartingVcn; }
    [StructLayout(LayoutKind.Sequential)] internal struct RetrievalHeader { public uint ExtentCount; public long StartingVcn; }
    [StructLayout(LayoutKind.Sequential)] internal struct RetrievalExtent { public long NextVcn, Lcn; }
    [StructLayout(LayoutKind.Sequential)] internal struct TrimHeader { public uint Key, NumRanges; }
    [StructLayout(LayoutKind.Sequential)] internal struct TrimRange { public ulong Offset, Length; }
    [StructLayout(LayoutKind.Sequential)] internal struct TrimOutput { public uint NumRangesProcessed; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceTrimDescriptor { public uint Version, Size; public byte TrimEnabled; }
}
