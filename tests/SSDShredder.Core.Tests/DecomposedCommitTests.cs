using Microsoft.Win32.SafeHandles;
using SSDShredder.Core;
using Xunit;

namespace SSDShredder.Core.Tests;

/// <summary>
/// Fault-injection tests for HeldFile.RevalidateForCommit/TrimOnly/UnlinkOnly — the
/// decomposed mirror of Commit() added for the Worker's GUI-session adapter
/// (SSDShredder.Worker.CoreAdapter.CoreFileDisposalAdapter). Each scenario here has a
/// matching scenario in TrimEngineWindowsTests.cs exercising the same fault through the
/// atomic Commit() path; the two should always agree, since the decomposed methods apply
/// exactly the same checks in the same order. Needs real Windows file handles to run — see
/// that file's header note for why these fail on macOS.
/// </summary>
public class DecomposedCommitTests
{
    sealed class FakeSubmitter(TrimSubmitResult r) : ITrimSubmitter
    {
        public int Calls;
        public TrimSubmitResult Submit(SafeFileHandle f, IReadOnlyList<(ulong Offset, ulong Length)> ranges)
        { Calls++; return r; }
    }
    sealed class FailingDisposer : IFileDisposer { public int MarkForDelete(SafeFileHandle f) => 5; }

    static string Temp(int len)
    {
        var p = Path.Combine(Path.GetTempPath(), $"belowzero-decomposed-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(p, new byte[len]);
        return p;
    }

    static Func<SafeFileHandle, InspectionResult> ForceEligible => h =>
    {
        var real = FileInspector.InspectHandle(h);
        var plan = TrimPlan.ForLength(real.Identity.Length, 4096);
        return real with { Decision = new(true, EligibilityReason.Eligible, "nvme", plan.CoveredBytes, plan.TailBytes, "forced") };
    };

    static readonly TrimSubmitResult Ack1 = new(0, 4, 1);

    [Fact]
    public void Full_ack_trims_then_unlinks_matching_atomic_commit()
    {
        var path = Temp(4096 * 3 + 615);
        var sub = new FakeSubmitter(Ack1);
        using var held = new TrimEngine(sub, null, ForceEligible).Open(path);

        Assert.Equal(RevalidateOutcome.Ok, held.RevalidateForCommit());
        TrimAttemptResult trim = held.TrimOnly();
        Assert.Equal(TrimAttemptOutcome.Completed, trim.Outcome);
        Assert.Equal(1u, trim.RangesRequested);
        Assert.Equal(1u, trim.RangesProcessed);

        Assert.Equal(EntryState.Removed, held.UnlinkOnly());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Partial_completion_reports_partial_and_leaves_the_name()
    {
        var path = Temp(4096 * 2);
        try
        {
            using var held = new TrimEngine(new FakeSubmitter(new(0, 4, 0)), null, ForceEligible).Open(path);
            Assert.Equal(RevalidateOutcome.Ok, held.RevalidateForCommit());
            TrimAttemptResult trim = held.TrimOnly();
            Assert.Equal(TrimAttemptOutcome.Partial, trim.Outcome);
            // Per Commit()'s own rule: anything but a full ack must never reach UnlinkOnly.
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Malformed_output_is_unknown_and_leaves_the_name()
    {
        var path = Temp(4096 * 2);
        try
        {
            using var held = new TrimEngine(new FakeSubmitter(new(0, 3, null)), null, ForceEligible).Open(path);
            Assert.Equal(RevalidateOutcome.Ok, held.RevalidateForCommit());
            Assert.Equal(TrimAttemptOutcome.Unknown, held.TrimOnly().Outcome);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1)] [InlineData(50)] [InlineData(313)] [InlineData(317)] [InlineData(326)]
    public void Unsupported_error_is_not_submitted_and_leaves_the_name(int win32)
    {
        var path = Temp(4096 * 2);
        try
        {
            using var held = new TrimEngine(new FakeSubmitter(new(win32, 0, null)), null, ForceEligible).Open(path);
            Assert.Equal(RevalidateOutcome.Ok, held.RevalidateForCommit());
            Assert.Equal(TrimAttemptOutcome.NotSubmitted, held.TrimOnly().Outcome);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Failed_unlink_preserves_the_entry()
    {
        var path = Temp(4096 * 2);
        try
        {
            using var held = new TrimEngine(new FakeSubmitter(Ack1), new FailingDisposer(), ForceEligible).Open(path);
            Assert.Equal(RevalidateOutcome.Ok, held.RevalidateForCommit());
            Assert.Equal(TrimAttemptOutcome.Completed, held.TrimOnly().Outcome);
            Assert.Equal(EntryState.Preserved, held.UnlinkOnly());
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Identity_change_between_open_and_revalidate_is_reported()
    {
        var path = Temp(4096 * 2);
        try
        {
            int n = 0;
            Func<SafeFileHandle, InspectionResult> shifty = h =>
            {
                var r = ForceEligible(h);
                return n++ == 0 ? r : r with { Identity = r.Identity with { Length = r.Identity.Length + 1 } };
            };
            var sub = new FakeSubmitter(Ack1);
            using var held = new TrimEngine(sub, null, shifty).Open(path);
            Assert.Equal(RevalidateOutcome.IdentityChanged, held.RevalidateForCommit());
            Assert.Equal(0, sub.Calls); // Never submit trim after a failed revalidate.
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Decomposed_steps_agree_with_atomic_commit_on_the_same_fault()
    {
        // Same fault (partial ack) through both paths must agree on outcome shape.
        var pathA = Temp(4096 * 2);
        var pathB = Temp(4096 * 2);
        try
        {
            var atomic = new TrimEngine(new FakeSubmitter(new(0, 4, 0)), null, ForceEligible).Shred(pathA, confirmed: true);
            Assert.Equal(ShredState.Indeterminate, atomic.State);
            Assert.Equal(EntryState.Preserved, atomic.Entry);

            using var held = new TrimEngine(new FakeSubmitter(new(0, 4, 0)), null, ForceEligible).Open(pathB);
            Assert.Equal(RevalidateOutcome.Ok, held.RevalidateForCommit());
            Assert.Equal(TrimAttemptOutcome.Partial, held.TrimOnly().Outcome);
            // Decomposed callers must not call UnlinkOnly() after anything but Completed —
            // confirming the file is left exactly as the atomic path left it.
            Assert.True(File.Exists(pathB));
        }
        finally { File.Delete(pathA); File.Delete(pathB); }
    }
}
