using Microsoft.Win32.SafeHandles;
using SSDShredder.Core;
using Xunit;
using Xunit.Abstractions;

namespace SSDShredder.Core.Tests;

/// <summary>Fault-injection tests on throwaway temp files. Fakes force eligibility so they run on any disk.</summary>
public class TrimEngineWindowsTests(ITestOutputHelper output)
{
    sealed class FakeSubmitter(TrimSubmitResult r) : ITrimSubmitter
    {
        public int Calls;
        public IReadOnlyList<(ulong, ulong)>? Ranges;
        public TrimSubmitResult Submit(SafeFileHandle f, IReadOnlyList<(ulong Offset, ulong Length)> ranges)
        { Calls++; Ranges = ranges.ToList(); return r; }
    }
    sealed class FailingDisposer : IFileDisposer { public int MarkForDelete(SafeFileHandle f) => 5; }

    static string Temp(int len)
    {
        var p = Path.Combine(Path.GetTempPath(), $"belowzero-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(p, new byte[len]);
        return p;
    }

    // Real facts from the handle, verdict forced to eligible with the spec's page math.
    static Func<SafeFileHandle, InspectionResult> ForceEligible => h =>
    {
        var real = FileInspector.InspectHandle(h);
        var plan = TrimPlan.ForLength(real.Identity.Length, 4096);
        return real with { Decision = new(true, EligibilityReason.Eligible, "nvme", plan.CoveredBytes, plan.TailBytes, "forced") };
    };

    static readonly TrimSubmitResult Ack1 = new(0, 4, 1);

    [Fact] public void Full_ack_trims_first_pages_then_unlinks()
    {
        var path = Temp(4096 * 3 + 615);
        var sub = new FakeSubmitter(Ack1);
        var r = new TrimEngine(sub, null, ForceEligible).Shred(path, confirmed: true);
        output.WriteLine(r.ToString());
        Assert.Equal(ShredState.Completed, r.State);
        Assert.Equal(EntryState.Removed, r.Entry);
        Assert.Equal((ulong)12288, r.CoveredBytes); Assert.Equal(615UL, r.TailBytes);
        Assert.Equal([(0UL, 12288UL)], sub.Ranges!.ToArray());
        Assert.False(File.Exists(path));
    }

    [Fact] public void Partial_completion_keeps_the_original_name()
    {
        var path = Temp(4096 * 2);
        try
        {
            var r = new TrimEngine(new FakeSubmitter(new(0, 4, 0)), null, ForceEligible).Shred(path, true);
            Assert.Equal(ShredState.Indeterminate, r.State);
            Assert.Equal(EntryState.Preserved, r.Entry);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact] public void Malformed_output_keeps_the_name()
    {
        var path = Temp(4096 * 2);
        try
        {
            var r = new TrimEngine(new FakeSubmitter(new(0, 3, null)), null, ForceEligible).Shred(path, true);
            Assert.Equal(ShredState.Indeterminate, r.State);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1)] [InlineData(50)] [InlineData(313)] [InlineData(317)] [InlineData(326)]
    public void Unsupported_error_is_a_clean_failure_and_leaves_file(int win32)
    {
        var path = Temp(4096 * 2);
        try
        {
            var r = new TrimEngine(new FakeSubmitter(new(win32, 0, null)), null, ForceEligible).Shred(path, true);
            Assert.Equal(ShredState.Failed, r.State);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact] public void Failed_unlink_is_indeterminate_and_entry_preserved()
    {
        var path = Temp(4096 * 2);
        try
        {
            var r = new TrimEngine(new FakeSubmitter(Ack1), new FailingDisposer(), ForceEligible).Shred(path, true);
            Assert.Equal(ShredState.Indeterminate, r.State);
            Assert.Equal(EntryState.Preserved, r.Entry);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact] public void Unconfirmed_commit_changes_nothing()
    {
        var path = Temp(4096 * 2);
        var sub = new FakeSubmitter(Ack1);
        try
        {
            var r = new TrimEngine(sub, null, ForceEligible).Shred(path, confirmed: false);
            Assert.Equal(ShredState.Invalid, r.State);
            Assert.Equal(0, sub.Calls);
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact] public void Busy_file_is_refused_before_mutation()
    {
        var path = Temp(4096 * 2);
        try
        {
            using var other = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            var r = new TrimEngine(new FakeSubmitter(Ack1), null, ForceEligible).Shred(path, true);
            Assert.Equal(ShredState.Busy, r.State);
        }
        finally { File.Delete(path); }
    }

    [Fact] public void Identity_change_between_inspect_and_commit_is_denied()
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
            Assert.Equal(ShredState.Denied, held.Commit(true).State);
            Assert.Equal(0, sub.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact] public void Real_submitter_on_this_machine_if_eligible()
    {
        var path = Temp(4096 * 3 + 615);
        try
        {
            var insp = FileInspector.Inspect(path);
            output.WriteLine($"{insp.Decision.Eligible} {insp.Decision.Reason} {insp.Decision.Observation}");
            if (!insp.Decision.Eligible) return;                 // ineligible machine: nothing to prove here
            var r = new TrimEngine().Shred(path, true);
            output.WriteLine(r.ToString());
            Assert.True(r.State is ShredState.Completed or ShredState.Indeterminate or ShredState.Failed);
            if (r.State == ShredState.Completed) Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
}
