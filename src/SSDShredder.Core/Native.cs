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

internal static partial class Native
{
    internal const uint GenericRead = 0x80000000, FileReadAttributes = 0x80;
    internal const uint ShareAll = 7, OpenExisting = 3;
    internal const uint FileFlagOpenReparsePoint = 0x00200000, FileFlagBackupSemantics = 0x02000000;

    internal const int FileStandardInfoClass = 1, FileStreamInfoClass = 7, FileIdInfoClass = 18, FileAttributeTagInfoClass = 9;
    internal const uint AttrDirectory = 0x10, AttrSparse = 0x200, AttrReparse = 0x400, AttrCompressed = 0x800, AttrEncrypted = 0x4000;

    internal static readonly uint IoctlVolumeGetVolumeDiskExtents = CtlCode(0x56, 0, 0, 0); // 0x00560000
    internal const uint PropDevice = 0, PropSeekPenalty = 7, PropTrim = 8;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, nint buffer, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(SafeFileHandle h, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial int GetVolumeInformationByHandle(SafeFileHandle h, nint volName, uint volNameLen,
        out uint serial, out uint maxComponent, out uint flags, [Out] char[] fsName, uint fsNameLen);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial int GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll")]
    internal static unsafe partial void GetSystemInfo(byte* systemInfo);
}

internal static partial class Native
{
    internal const uint GenericWrite = 0x40000000, Delete = 0x00010000;
    internal const int FileDispositionInfoClass = 4, FileDispositionInfoExClass = 21;
    internal const uint DispositionDelete = 0x1, DispositionPosix = 0x2;
    internal const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5,
        ErrorSharingViolation = 32, ErrorLockViolation = 33, ErrorNotSupported = 50, ErrorInvalidFunction = 1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int FlushFileBuffers(SafeFileHandle h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int SetFileInformationByHandle(SafeFileHandle h, int infoClass, nint buffer, uint size);
}
