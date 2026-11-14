using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SSDShredder.Core;

/// <summary>Values equal the wire-contract exit codes.</summary>
public enum ShredState { Completed = 0, Invalid = 2, Cancelled = 3, Unsupported = 4, Denied = 5, Busy = 6, Failed = 7, Indeterminate = 8 }
public enum EntryState { Preserved, Removed, Unknown }

public sealed record ShredResult(ShredState State, ulong CoveredBytes, ulong TailBytes,
    uint? RangesRequested, uint? RangesProcessed, EntryState Entry, string Message);

public sealed class ShredException(ShredState state, string message) : Exception(message)
{
    public ShredState State { get; } = state;
}

public readonly record struct TrimSubmitResult(int Win32Error, uint BytesReturned, uint? RangesProcessed);

/// <summary>Seams for fault injection (partial completion, failed unlink).</summary>
public interface ITrimSubmitter { TrimSubmitResult Submit(SafeFileHandle file, IReadOnlyList<(ulong Offset, ulong Length)> ranges); }
public interface IFileDisposer { int MarkForDelete(SafeFileHandle file); }

internal sealed class NativeTrimSubmitter : ITrimSubmitter
{
    public TrimSubmitResult Submit(SafeFileHandle file, IReadOnlyList<(ulong Offset, ulong Length)> ranges)
    {
        var input = TrimRequest.Build(ranges);
        var output = new byte[4];
        int err = Win32.Ioctl(file, Native.FsctlFileLevelTrim, input, output, out uint returned);
        return new(err, returned, TrimRequest.ParseOutput(output, returned));
    }
}

internal sealed unsafe class NativeFileDisposer : IFileDisposer
{
    public int MarkForDelete(SafeFileHandle file)
    {
        uint flags = Native.DispositionDelete | Native.DispositionPosix;
        if (Native.SetFileInformationByHandle(file, Native.FileDispositionInfoExClass, (nint)(&flags), 4) != 0) return 0;
        int err = Marshal.GetLastPInvokeError();
        if (err != 87 /* ERROR_INVALID_PARAMETER */ && err != Native.ErrorNotSupported && err != Native.ErrorInvalidFunction) return err;
        byte del = 1;                                           // legacy FILE_DISPOSITION_INFO (BOOLEAN)
        return Native.SetFileInformationByHandle(file, Native.FileDispositionInfoClass, (nint)(&del), 1) != 0
            ? 0 : Marshal.GetLastPInvokeError();
    }
}

public sealed class TrimEngine
{
    internal ITrimSubmitter Submitter { get; }
    internal IFileDisposer Disposer { get; }
    internal Func<SafeFileHandle, InspectionResult> Inspector { get; }

    public TrimEngine() : this(null, null, null) { }
    internal TrimEngine(ITrimSubmitter? s, IFileDisposer? d, Func<SafeFileHandle, InspectionResult>? i)
    {
        Submitter = s ?? new NativeTrimSubmitter();
        Disposer = d ?? new NativeFileDisposer();
        Inspector = i ?? FileInspector.InspectHandle;
    }

    /// <summary>Open with read/write/delete and exclusive sharing, then inspect that same handle. Hold it through confirmation.</summary>
    public HeldFile Open(string absolutePath)
    {
        if (!PathPolicy.IsAcceptable(absolutePath, out var why)) throw new ShredException(ShredState.Invalid, why);
        var h = Native.CreateFile(absolutePath, Native.GenericRead | Native.GenericWrite | Native.Delete, 0, 0,
            Native.OpenExisting, Native.FileFlagOpenReparsePoint, 0);
        if (h.IsInvalid)
        {
            int e = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw e switch
            {
                Native.ErrorSharingViolation or Native.ErrorLockViolation => new ShredException(ShredState.Busy, "file is in use"),
                Native.ErrorAccessDenied => new ShredException(ShredState.Denied, "access denied"),
                Native.ErrorFileNotFound or Native.ErrorPathNotFound => new ShredException(ShredState.Failed, "file not found"),
                _ => new ShredException(ShredState.Failed, new Win32Exception(e).Message),
            };
        }
        try { return new HeldFile(this, h, absolutePath, Inspector(h)); }
        catch { h.Dispose(); throw; }
    }

    /// <summary>One-shot CLI path: open, validate, then commit. No selection token.</summary>
    public ShredResult Shred(string absolutePath, bool confirmed)
    {
        try { using var held = Open(absolutePath); return held.Commit(confirmed); }
        catch (ShredException ex) { return new(ex.State, 0, 0, null, null, EntryState.Preserved, ex.Message); }
    }
}

public sealed class HeldFile : IDisposable
{
    readonly TrimEngine _engine;
    SafeFileHandle? _handle;
    readonly string _path;

    public InspectionResult Inspection { get; }

    internal HeldFile(TrimEngine engine, SafeFileHandle handle, string path, InspectionResult inspection)
    { _engine = engine; _handle = handle; _path = path; Inspection = inspection; }

    ShredResult R(ShredState s, string msg, EntryState e = EntryState.Preserved, uint? req = null, uint? proc = null)
        => new(s, Inspection.Decision.CoveredBytes, Inspection.Decision.TailBytes, req, proc, e, msg);

    /// <summary>Recheck, flush, submit file-relative trim of [0, C), and unlink only after full OS acknowledgement.</summary>
    public ShredResult Commit(bool confirmed)
    {
        if (_handle is null) throw new ObjectDisposedException(nameof(HeldFile));
        if (!confirmed) return R(ShredState.Invalid, "confirmation required");
        if (!Inspection.Decision.Eligible) return R(ShredState.Unsupported, $"ineligible: {Inspection.Decision.Reason}");

        // Recheck bus, capability, coverage, topology, identity and eligibility on the held handle.
        InspectionResult now;
        try { now = _engine.Inspector(_handle); }
        catch (Win32Exception ex) { return R(ShredState.Failed, "recheck failed: " + ex.Message); }
        if (!SameIdentity(now.Identity, Inspection.Identity)) return R(ShredState.Denied, "file identity changed since selection");
        if (!now.Decision.Eligible) return R(ShredState.Unsupported, $"no longer eligible: {now.Decision.Reason}");
        if (now.Decision.CoveredBytes != Inspection.Decision.CoveredBytes || now.Decision.TailBytes != Inspection.Decision.TailBytes)
            return R(ShredState.Denied, "coverage changed since confirmation");

        if (Native.FlushFileBuffers(_handle) == 0)
            return R(ShredState.Failed, "flush failed: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);

        var ranges = new List<(ulong, ulong)> { (0UL, Inspection.Decision.CoveredBytes) };
        uint requested = (uint)ranges.Count;
        var sub = _engine.Submitter.Submit(_handle, ranges);

        // Refusals that mean nothing was processed: clean failure, no mutation, entry kept.
        if (sub.Win32Error is Native.ErrorInvalidFunction or Native.ErrorNotSupported
            or Native.ErrorNoRangesProcessed or Native.ErrorDeviceFeatureNotSupported or Native.ErrorFileLevelTrimNotSupported)
            return R(ShredState.Failed, $"trim refused by the stack ({sub.Win32Error}); file left intact", req: requested, proc: 0);
        if (sub.Win32Error != 0)
            return R(ShredState.Indeterminate, $"trim failed ({sub.Win32Error}); file may have changed", req: requested);
        if (sub.RangesProcessed is null)
            return R(ShredState.Indeterminate, "malformed trim output; file may have changed", req: requested);
        if (sub.RangesProcessed != requested)
            return R(ShredState.Indeterminate, $"partial trim ({sub.RangesProcessed}/{requested}); file may have changed, entry kept",
                req: requested, proc: sub.RangesProcessed);

        int derr = _engine.Disposer.MarkForDelete(_handle);
        if (derr != 0)
            return R(ShredState.Indeterminate, $"trim acknowledged but unlink failed ({derr})", req: requested, proc: requested);

        _handle.Dispose();
        _handle = null;
        var entry = VerifyEntry();
        return entry == EntryState.Removed
            ? R(ShredState.Completed, "trim acknowledged and entry removed", entry, requested, requested)
            : R(ShredState.Indeterminate, "trim acknowledged; entry removal not verified", entry, requested, requested);
    }

    static bool SameIdentity(FileIdentity a, FileIdentity b)
        => a.VolumeSerial == b.VolumeSerial && a.FileId.AsSpan().SequenceEqual(b.FileId) && a.Length == b.Length;

    EntryState VerifyEntry()
    {
        using var h = Native.CreateFile(_path, Native.FileReadAttributes, Native.ShareAll, 0, Native.OpenExisting,
            Native.FileFlagOpenReparsePoint, 0);
        if (h.IsInvalid)
        {
            int e = Marshal.GetLastPInvokeError();
            return e is Native.ErrorFileNotFound or Native.ErrorPathNotFound ? EntryState.Removed : EntryState.Unknown;
        }
        try
        {
            var id = Win32.InfoByHandle(h, Native.FileIdInfoClass, 24);
            bool same = BitConverter.ToUInt64(id, 0) == Inspection.Identity.VolumeSerial
                        && id.AsSpan(8, 16).SequenceEqual(Inspection.Identity.FileId);
            return same ? EntryState.Preserved : EntryState.Removed; // different file reusing the name = confirmed identity is gone
        }
        catch (Win32Exception) { return EntryState.Unknown; }
    }

    public void Dispose() { _handle?.Dispose(); _handle = null; }
}
