using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SSDShredder.Core;

internal static unsafe class Win32
{
    /// <summary>DeviceIoControl over managed buffers. Returns 0 on success, else the Win32 error captured immediately.</summary>
    internal static int Ioctl(SafeFileHandle h, uint code, ReadOnlySpan<byte> input, Span<byte> output, out uint returned)
    {
        fixed (byte* i = input)
        fixed (byte* o = output)
        {
            int ok = Native.DeviceIoControl(h, code, (nint)i, (uint)input.Length, (nint)o, (uint)output.Length, out returned, 0);
            return ok != 0 ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    internal static byte[] InfoByHandle(SafeFileHandle h, int infoClass, int size)
    {
        var buf = new byte[size];
        fixed (byte* p = buf)
            if (Native.GetFileInformationByHandleEx(h, infoClass, (nint)p, (uint)size) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        return buf;
    }

    internal static uint PageSize()
    {
        byte* si = stackalloc byte[64];
        new Span<byte>(si, 64).Clear();
        Native.GetSystemInfo(si);
        return *(uint*)(si + 4); // dwPageSize
    }

    internal static string FinalVolumeGuidPath(SafeFileHandle h)
    {
        var buf = new char[1024];
        uint n = Native.GetFinalPathNameByHandle(h, buf, (uint)buf.Length, 0x1 /* VOLUME_NAME_GUID */);
        if (n == 0 || n >= buf.Length) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new string(buf, 0, (int)n);
    }

    /// <summary>Extract \\?\Volume{guid} (no trailing backslash) from a final volume-GUID path.</summary>
    internal static string VolumeDevicePath(string finalPath)
    {
        int end = finalPath.IndexOf('}');
        if (!finalPath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0)
            throw new InvalidDataException("Unexpected volume path form.");
        return finalPath[..(end + 1)];
    }

    internal static SafeFileHandle OpenQuery(string path, uint flags = 0)
        => Native.CreateFile(path, 0, Native.ShareAll, 0, Native.OpenExisting, flags, 0);
}
