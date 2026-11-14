using SSDShredder.Worker.CoreAdapter;
using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Operations;

/// <summary>
/// File disposal as the Worker sees it: classify, hold, confirm, trim, and unlink only
/// after the whole requested interval was acknowledged. Every method emits exactly one
/// terminal result and returns its code.
/// </summary>
public sealed class FileOperations
{
    private readonly IFileDisposalCore _core;

    public FileOperations(IFileDisposalCore core) => _core = core;

    /// <summary>Direct CLI inspection: read-only, no token.</summary>
    public ExitCode InspectReadOnly(string path, RequestEmitter emit)
    {
        emit.Progress(Phases.Inspecting);
        InspectResult r;
        try
        {
            r = _core.Inspect(path);
        }
        catch (Exception)
        {
            return emit.Result(ExitCode.Failed, UnknownInspect());
        }

        if (r.Status != AccessStatus.Ok || r.Facts is null)
        {
            return emit.Result(MapAccess(r.Status), UnknownInspect());
        }
        return emit.Result(ExitCode.Completed, ToEvidence(r.Facts, null));
    }

    /// <summary>
    /// Connection inspection: opens the mutation handle, and for an eligible file holds it
    /// behind a single-use token.
    /// </summary>
    public ExitCode InspectAndHold(string path, RequestEmitter emit, SelectionTokenStore store)
    {
        emit.Progress(Phases.Inspecting);

        // A new inspect replaces whatever this connection held. Let go first: the old handle
        // is exclusive, so inspecting the same file again would otherwise find it busy.
        store.Release();

        AcquireResult a;
        try
        {
            a = _core.Acquire(path);
        }
        catch (Exception)
        {
            return emit.Result(ExitCode.Failed, UnknownInspect());
        }

        if (a.Status != AccessStatus.Ok || a.Facts is null)
        {
            a.Held?.Dispose();
            return emit.Result(MapAccess(a.Status), UnknownInspect());
        }
        if (a.Held is null)
        {
            return emit.Result(ExitCode.Completed, ToEvidence(a.Facts, null));
        }

        string token = store.Hold(path, a.Held);
        return emit.Result(ExitCode.Completed, ToEvidence(a.Facts, token));
    }

    /// <summary>
    /// Direct CLI disposal. <paramref name="confirm"/> is null when --confirm was given;
    /// otherwise it asks the console and returns the user's answer.
    /// </summary>
    public ExitCode ShredDirect(string path, RequestEmitter emit, Func<InspectFacts, bool>? confirm)
    {
        emit.Progress(Phases.Inspecting);
        AcquireResult a;
        try
        {
            a = _core.Acquire(path);
        }
        catch (Exception)
        {
            return emit.Result(ExitCode.Failed, Untouched(null));
        }

        if (a.Status != AccessStatus.Ok || a.Facts is null)
        {
            a.Held?.Dispose();
            return emit.Result(MapAccess(a.Status), Untouched(null));
        }
        if (a.Held is null)
        {
            return emit.Result(ExitCode.Unsupported, Untouched(a.Facts));
        }

        using IHeldFile held = a.Held;
        emit.Progress(Phases.Confirming);
        if (confirm is not null)
        {
            bool approved;
            try
            {
                approved = confirm(held.Facts);
            }
            catch (Exception)
            {
                approved = false;
            }
            if (!approved) return emit.Result(ExitCode.Cancelled, Untouched(held.Facts));
        }
        return Commit(held, emit);
    }

    /// <summary>Confirmed disposal of the selection held on this connection.</summary>
    public ExitCode ShredHeld(string path, string token, RequestEmitter emit, SelectionTokenStore store)
    {
        TakeStatus take = store.Take(path, token, out IHeldFile? taken);
        if (take != TakeStatus.Ok || taken is null)
        {
            return emit.Result(ExitCode.Denied, Untouched(null));
        }
        using IHeldFile held = taken;
        return Commit(held, emit);
    }

    private static ExitCode Commit(IHeldFile held, RequestEmitter emit)
    {
        InspectFacts f = held.Facts;

        RevalidateStatus rv;
        try
        {
            rv = held.Revalidate();
        }
        catch (Exception)
        {
            rv = RevalidateStatus.Failed;
        }
        if (rv != RevalidateStatus.Ok)
        {
            ExitCode code = rv switch
            {
                RevalidateStatus.IdentityChanged => ExitCode.Denied,
                RevalidateStatus.NoLongerEligible => ExitCode.Unsupported,
                _ => ExitCode.Failed,
            };
            return emit.Result(code, Untouched(f));
        }

        // From here on the file may change. Never retry, never unlink on doubt.
        emit.Progress(Phases.Trimming);
        TrimResult trim;
        try
        {
            trim = held.Trim();
        }
        catch (Exception)
        {
            return emit.Result(ExitCode.Indeterminate,
                new ShredEvidence(null, null, f.CoveredBytes, f.TailBytes, ShredEvidence.EntryPreserved));
        }

        if (trim.Status == TrimStatus.NotSubmitted)
        {
            return emit.Result(ExitCode.Failed, Receipt(f, trim, ShredEvidence.EntryPreserved));
        }

        bool acknowledged = trim.Status == TrimStatus.Completed
            && trim.RangesRequested is > 0
            && trim.RangesProcessed == trim.RangesRequested;
        if (!acknowledged)
        {
            return emit.Result(ExitCode.Indeterminate, Receipt(f, trim, ShredEvidence.EntryPreserved));
        }

        emit.Progress(Phases.Unlinking);
        EntryOutcome entry;
        try
        {
            entry = held.UnlinkAndClose();
        }
        catch (Exception)
        {
            entry = EntryOutcome.Unknown;
        }

        return entry switch
        {
            EntryOutcome.Removed => emit.Result(ExitCode.Completed, Receipt(f, trim, ShredEvidence.EntryRemoved)),
            EntryOutcome.Preserved => emit.Result(ExitCode.Indeterminate, Receipt(f, trim, ShredEvidence.EntryPreserved)),
            _ => emit.Result(ExitCode.Indeterminate, Receipt(f, trim, ShredEvidence.EntryUnknown)),
        };
    }

    private static ShredEvidence Receipt(InspectFacts f, TrimResult t, string entry) =>
        new(t.RangesRequested, t.RangesProcessed, f.CoveredBytes, f.TailBytes, entry);

    private static ShredEvidence Untouched(InspectFacts? f) =>
        new(null, null, f?.CoveredBytes, f?.TailBytes, ShredEvidence.EntryPreserved);

    private static InspectEvidence UnknownInspect() => new(false, Reasons.Unknown, null, null, null);

    private static InspectEvidence ToEvidence(InspectFacts f, string? token) =>
        new(f.Eligible, f.Reason, f.Bus, f.CoveredBytes, f.TailBytes, token);

    private static ExitCode MapAccess(AccessStatus s) => s switch
    {
        AccessStatus.NotFound => ExitCode.Unsupported,
        AccessStatus.Denied => ExitCode.Denied,
        AccessStatus.Busy => ExitCode.Busy,
        _ => ExitCode.Failed,
    };
}
