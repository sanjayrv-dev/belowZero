using System.Text;
using SSDShredder.Worker.CoreAdapter;
using SSDShredder.Worker.Operations;
using SSDShredder.Worker.Protocol;
using SSDShredder.Worker.Simulation;

namespace SSDShredder.Worker.Tests;

public class FileOperationTests
{
    private const string Path = @"C:\Demo\fixture.bin";

    private static (FileOperations ops, SimulatedCore core, CapturingSink sink, RequestEmitter emit) Setup(SimScenario? scenario)
    {
        var core = new SimulatedCore(_ => scenario);
        var sink = new CapturingSink();
        return (new FileOperations(core), core, sink, new RequestEmitter(sink, Frames.Id1));
    }

    private static string Entry(CapturingSink s) => s.Last.GetProperty("evidence").GetProperty("entry").GetString()!;

    [Fact]
    public void Eligible_file_is_trimmed_then_unlinked()
    {
        var (ops, core, sink, emit) = Setup(new SimScenario());
        Assert.Equal(ExitCode.Completed, ops.ShredDirect(Path, emit, confirm: null));
        Assert.Equal(["inspecting", "confirming", "trimming", "unlinking"], sink.Phases());
        var e = sink.Last.GetProperty("evidence");
        Assert.Equal(4096, e.GetProperty("coveredBytes").GetInt64());
        Assert.Equal(615, e.GetProperty("tailBytes").GetInt64());
        Assert.Equal(1, e.GetProperty("rangesProcessed").GetInt32());
        Assert.Equal("removed", Entry(sink));
        Assert.Equal(1, core.UnlinkCalls);
        Assert.Equal(0, core.ActiveHolds);
    }

    [Fact]
    public void Partial_trim_stops_before_unlink_and_reports_indeterminate()
    {
        var (ops, core, sink, emit) = Setup(new SimScenario { Trim = TrimStatus.Partial, RangesProcessed = 0 });
        Assert.Equal(ExitCode.Indeterminate, ops.ShredDirect(Path, emit, null));
        Assert.Equal("preserved", Entry(sink));
        Assert.Equal(0, core.UnlinkCalls);
        Assert.Equal(0, core.ActiveHolds);
    }

    [Fact]
    public void Success_with_fewer_ranges_processed_than_requested_is_not_unlinked()
    {
        var (ops, core, sink, emit) = Setup(new SimScenario { Trim = TrimStatus.Completed, RangesProcessed = 0 });
        Assert.Equal(ExitCode.Indeterminate, ops.ShredDirect(Path, emit, null));
        Assert.Equal(0, core.UnlinkCalls);
        Assert.Equal("preserved", Entry(sink));
    }

    [Fact]
    public void Unknown_completion_and_a_throwing_trim_are_indeterminate_and_never_retried()
    {
        var (ops, core, _, emit) = Setup(new SimScenario { Trim = TrimStatus.Unknown });
        Assert.Equal(ExitCode.Indeterminate, ops.ShredDirect(Path, emit, null));
        Assert.Equal(1, core.TrimCalls);
        Assert.Equal(0, core.UnlinkCalls);

        var (ops2, core2, _, emit2) = Setup(new SimScenario { TrimThrows = true });
        Assert.Equal(ExitCode.Indeterminate, ops2.ShredDirect(Path, emit2, null));
        Assert.Equal(1, core2.TrimCalls);
        Assert.Equal(0, core2.UnlinkCalls);
    }

    [Fact]
    public void A_trim_that_was_never_dispatched_is_a_plain_failure()
    {
        var (ops, core, sink, emit) = Setup(new SimScenario { Trim = TrimStatus.NotSubmitted });
        Assert.Equal(ExitCode.Failed, ops.ShredDirect(Path, emit, null));
        Assert.Equal("preserved", Entry(sink));
        Assert.Equal(0, core.UnlinkCalls);
    }

    [Theory]
    [InlineData(EntryOutcome.Preserved, "preserved")]
    [InlineData(EntryOutcome.Unknown, "unknown")]
    public void A_failed_unlink_after_trim_is_indeterminate(EntryOutcome outcome, string entry)
    {
        var (ops, _, sink, emit) = Setup(new SimScenario { Entry = outcome });
        Assert.Equal(ExitCode.Indeterminate, ops.ShredDirect(Path, emit, null));
        Assert.Equal(entry, Entry(sink));
    }

    [Theory]
    [InlineData(RevalidateStatus.IdentityChanged, ExitCode.Denied)]
    [InlineData(RevalidateStatus.NoLongerEligible, ExitCode.Unsupported)]
    [InlineData(RevalidateStatus.Failed, ExitCode.Failed)]
    public void Failed_recheck_at_commit_leaves_the_file_alone(RevalidateStatus status, ExitCode expected)
    {
        var (ops, core, sink, emit) = Setup(new SimScenario { Revalidate = status });
        Assert.Equal(expected, ops.ShredDirect(Path, emit, null));
        Assert.Equal(0, core.TrimCalls);
        Assert.DoesNotContain("trimming", sink.Phases());
        Assert.Equal("preserved", Entry(sink));
    }

    [Theory]
    [InlineData(AccessStatus.Busy, ExitCode.Busy)]
    [InlineData(AccessStatus.Denied, ExitCode.Denied)]
    [InlineData(AccessStatus.Failed, ExitCode.Failed)]
    public void Access_problems_map_to_their_codes(AccessStatus access, ExitCode expected)
    {
        var (ops, core, _, emit) = Setup(new SimScenario { Access = access });
        Assert.Equal(expected, ops.ShredDirect(Path, emit, null));
        Assert.Equal(0, core.TrimCalls);
    }

    [Fact]
    public void A_missing_file_is_unsupported()
    {
        var (ops, _, _, emit) = Setup(null);
        Assert.Equal(ExitCode.Unsupported, ops.ShredDirect(Path, emit, null));
    }

    [Fact]
    public void Ineligible_file_is_unsupported_for_shred_but_a_completed_inspect()
    {
        var usb = new SimScenario { Reason = Reasons.Topology, Bus = "BusTypeUsb" };
        var (ops, core, sink, emit) = Setup(usb);
        Assert.Equal(ExitCode.Unsupported, ops.ShredDirect(Path, emit, null));
        Assert.Equal("preserved", Entry(sink));
        Assert.Equal(0, core.TrimCalls);

        var (ops2, _, sink2, emit2) = Setup(usb);
        Assert.Equal(ExitCode.Completed, ops2.InspectReadOnly(Path, emit2));
        var e = sink2.Last.GetProperty("evidence");
        Assert.False(e.GetProperty("eligible").GetBoolean());
        Assert.Equal("topology", e.GetProperty("reason").GetString());
        Assert.Equal("BusTypeUsb", e.GetProperty("bus").GetString());
        Assert.False(e.TryGetProperty("selectionToken", out _));
    }

    [Fact]
    public void A_file_smaller_than_one_page_has_nothing_to_trim()
    {
        var (ops, _, sink, emit) = Setup(new SimScenario { Length = 300 });
        Assert.Equal(ExitCode.Completed, ops.InspectReadOnly(Path, emit));
        var e = sink.Last.GetProperty("evidence");
        Assert.False(e.GetProperty("eligible").GetBoolean());
        Assert.Equal("noFullPage", e.GetProperty("reason").GetString());
        Assert.Equal(300, e.GetProperty("tailBytes").GetInt64());
    }

    [Fact]
    public void Declining_the_prompt_cancels_before_any_mutation()
    {
        var (ops, core, sink, emit) = Setup(new SimScenario());
        InspectFacts? shown = null;
        Assert.Equal(ExitCode.Cancelled, ops.ShredDirect(Path, emit, f => { shown = f; return false; }));
        Assert.Equal(4096, shown!.CoveredBytes);
        Assert.Equal(615, shown.TailBytes);
        Assert.Equal(0, core.TrimCalls);
        Assert.Equal("preserved", Entry(sink));
        Assert.Equal(0, core.ActiveHolds);
    }
}

public class SelectionTokenTests
{
    private sealed class Held : IHeldFile
    {
        public bool Disposed;
        public InspectFacts Facts { get; } = new(true, Reasons.Eligible, "BusTypeNvme", 4096, 0);
        public RevalidateStatus Revalidate() => RevalidateStatus.Ok;
        public TrimResult Trim() => new(TrimStatus.Completed, 1, 1);
        public EntryOutcome UnlinkAndClose() => EntryOutcome.Removed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Token_is_32_lowercase_hex_and_works_exactly_once()
    {
        using var store = new SelectionTokenStore(new FakeTime());
        var held = new Held();
        string token = store.Hold(@"C:\a.bin", held);
        Assert.True(Validation.IsLowerHex32(token));

        Assert.Equal(TakeStatus.Ok, store.Take(@"C:\a.bin", token, out IHeldFile? taken));
        Assert.Same(held, taken);
        Assert.False(held.Disposed);
        Assert.Equal(TakeStatus.None, store.Take(@"C:\a.bin", token, out _));
    }

    [Fact]
    public void Token_expires_after_60_seconds_and_the_handle_is_released()
    {
        var time = new FakeTime();
        using var store = new SelectionTokenStore(time);
        var held = new Held();
        string token = store.Hold(@"C:\a.bin", held);

        time.Advance(TimeSpan.FromSeconds(59));
        store.Sweep();
        Assert.False(held.Disposed);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TakeStatus.Expired, store.Take(@"C:\a.bin", token, out _));
        Assert.True(held.Disposed);
    }

    [Fact]
    public void Wrong_token_or_wrong_path_releases_the_selection()
    {
        using var store = new SelectionTokenStore(new FakeTime());
        var a = new Held();
        string token = store.Hold(@"C:\a.bin", a);
        Assert.Equal(TakeStatus.Mismatch, store.Take(@"C:\a.bin", new string('0', 32), out _));
        Assert.True(a.Disposed);
        Assert.Equal(TakeStatus.None, store.Take(@"C:\a.bin", token, out _));

        var b = new Held();
        string token2 = store.Hold(@"C:\a.bin", b);
        Assert.Equal(TakeStatus.Mismatch, store.Take(@"C:\other.bin", token2, out _));
        Assert.True(b.Disposed);
    }

    [Fact]
    public void A_new_selection_or_disposal_releases_the_previous_handle()
    {
        var store = new SelectionTokenStore(new FakeTime());
        var a = new Held();
        var b = new Held();
        store.Hold(@"C:\a.bin", a);
        store.Hold(@"C:\b.bin", b);
        Assert.True(a.Disposed);
        store.Dispose();
        Assert.True(b.Disposed);
    }
}

public class ConnectionSessionTests
{
    private const string Path = @"C:\Demo\fixture.bin";

    private static (CapturingSink sink, SimulatedCore core, ConnectionSession session) Run(
        string input, SimScenario? scenario = null, PrivilegedHandler? privileged = null, bool files = true, FakeTime? time = null)
    {
        var core = new SimulatedCore(_ => scenario ?? new SimScenario());
        var sink = new CapturingSink();
        var session = new ConnectionSession(
            new MemoryStream(Encoding.UTF8.GetBytes(input)), sink, files ? new FileOperations(core) : null, privileged, time);
        session.Run();
        return (sink, core, session);
    }

    /// <summary>Feeds requests one at a time so a later frame can use an earlier reply.</summary>
    private sealed class Scripted : Stream
    {
        private readonly Queue<Func<byte[]>> _steps;
        private byte[] _current = [];
        private int _pos;

        public Scripted(params Func<string>[] steps) =>
            _steps = new Queue<Func<byte[]>>(steps.Select(s => (Func<byte[]>)(() => Encoding.UTF8.GetBytes(s() + "\n"))));

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos == _current.Length)
            {
                if (_steps.Count == 0) return 0;
                _current = _steps.Dequeue()();
                _pos = 0;
            }
            int n = Math.Min(count, _current.Length - _pos);
            Array.Copy(_current, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string TokenFrom(CapturingSink sink) =>
        sink.Lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .First(e => e.GetProperty("type").GetString() == "result")
            .GetProperty("evidence").GetProperty("selectionToken").GetString()!;

    [Fact]
    public void Inspect_then_one_confirmed_shred_on_the_same_connection()
    {
        var core = new SimulatedCore(_ => new SimScenario());
        var sink = new CapturingSink();
        var input = new Scripted(
            () => Frames.Request(Frames.Id1, "inspect", Path, false),
            () => Frames.Request(Frames.Id2, "shred", Path, true, TokenFrom(sink)),
            () => Frames.Request(Frames.Id3, "shred", Path, true, TokenFrom(sink)));
        new ConnectionSession(input, sink, new FileOperations(core), null).Run();

        var results = sink.Lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == "result").ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal(0, results[0].GetProperty("code").GetInt32());
        Assert.True(results[0].GetProperty("evidence").GetProperty("eligible").GetBoolean());
        Assert.Equal(0, results[1].GetProperty("code").GetInt32());
        Assert.Equal("removed", results[1].GetProperty("evidence").GetProperty("entry").GetString());
        // The token was consumed and the connection's one shred is spent.
        Assert.Equal(2, results[2].GetProperty("code").GetInt32());
        Assert.Equal(1, core.TrimCalls);
        Assert.Equal(1, core.UnlinkCalls);

        // Sequence numbers restart at 1 for each request.
        foreach (string id in new[] { Frames.Id1, Frames.Id2, Frames.Id3 })
        {
            var seqs = sink.Lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("id").GetString() == id).Select(e => e.GetProperty("seq").GetInt32()).ToList();
            Assert.Equal(Enumerable.Range(1, seqs.Count), seqs);
        }
    }

    private static List<System.Text.Json.JsonElement> Results(CapturingSink sink) =>
        sink.Lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == "result").ToList();

    [Fact]
    public void Inspecting_the_same_file_again_replaces_the_held_selection()
    {
        // The first inspect holds the file exclusively. A second inspect of that file on the
        // same connection must give up the old hold first, or it locks itself out.
        var core = new SimulatedCore(_ => new SimScenario());
        var sink = new CapturingSink();
        var input = new Scripted(
            () => Frames.Request(Frames.Id1, "inspect", Path, false),
            () => Frames.Request(Frames.Id2, "inspect", Path, false),
            () => Frames.Request(Frames.Id3, "shred", Path, true,
                Results(sink)[^1].GetProperty("evidence").GetProperty("selectionToken").GetString()));
        new ConnectionSession(input, sink, new FileOperations(core), null).Run();

        List<System.Text.Json.JsonElement> results = Results(sink);
        Assert.Equal(3, results.Count);
        Assert.Equal(0, results[1].GetProperty("code").GetInt32());
        Assert.True(results[1].GetProperty("evidence").GetProperty("eligible").GetBoolean());
        Assert.NotEqual(
            results[0].GetProperty("evidence").GetProperty("selectionToken").GetString(),
            results[1].GetProperty("evidence").GetProperty("selectionToken").GetString());
        Assert.Equal(0, results[2].GetProperty("code").GetInt32());
        Assert.Equal(1, core.UnlinkCalls);
        Assert.Equal(0, core.ActiveHolds);
    }

    /// <summary>Fails in a way the emitter does not treat as a lost transport.</summary>
    private sealed class FaultySink : IFrameSink
    {
        private readonly CapturingSink _inner;
        private readonly string _marker;

        public FaultySink(CapturingSink inner, string marker)
        {
            _inner = inner;
            _marker = marker;
        }

        public void Write(ReadOnlySpan<byte> line)
        {
            if (Encoding.UTF8.GetString(line).Contains(_marker)) throw new NotSupportedException();
            _inner.Write(line);
        }
    }

    [Fact]
    public void An_unexpected_error_after_the_trim_was_sent_is_indeterminate_not_a_plain_failure()
    {
        var core = new SimulatedCore(_ => new SimScenario());
        var sink = new CapturingSink();
        var input = new Scripted(
            () => Frames.Request(Frames.Id1, "inspect", Path, false),
            () => Frames.Request(Frames.Id2, "shred", Path, true, TokenFrom(sink)));
        new ConnectionSession(input, new FaultySink(sink, "unlinking"), new FileOperations(core), null).Run();

        Assert.Equal(1, core.TrimCalls);
        Assert.Equal(0, core.UnlinkCalls);
        Assert.Equal(Frames.Id2, sink.Last.GetProperty("id").GetString());
        Assert.Equal(8, sink.Last.GetProperty("code").GetInt32());
        Assert.Equal(0, core.ActiveHolds);
    }

    [Fact]
    public void Held_shred_emits_trimming_before_dispatch()
    {
        var core = new SimulatedCore(_ => new SimScenario());
        var sink = new CapturingSink();
        var input = new Scripted(
            () => Frames.Request(Frames.Id1, "inspect", Path, false),
            () => Frames.Request(Frames.Id2, "shred", Path, true, TokenFrom(sink)));
        new ConnectionSession(input, sink, new FileOperations(core), null).Run();
        var shredEvents = sink.Lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("id").GetString() == Frames.Id2).ToList();
        Assert.Equal("trimming", shredEvents[0].GetProperty("phase").GetString());
        Assert.Equal(1, shredEvents[0].GetProperty("seq").GetInt32());
    }

    [Fact]
    public void Shred_without_a_token_or_with_a_wrong_token_changes_nothing()
    {
        var (sink, core, _) = Run(Frames.Request(Frames.Id1, "shred", Path, true) + "\n");
        Assert.Equal(2, sink.Last.GetProperty("code").GetInt32());
        Assert.Equal(0, core.TrimCalls);

        var (sink2, core2, _) = Run(
            Frames.Request(Frames.Id1, "inspect", Path, false) + "\n" +
            Frames.Request(Frames.Id2, "shred", Path, true, new string('0', 32)) + "\n");
        Assert.Equal(5, sink2.Last.GetProperty("code").GetInt32());
        Assert.Equal(0, core2.TrimCalls);
        Assert.Equal(0, core2.ActiveHolds);
    }

    [Fact]
    public void An_expired_token_is_denied_and_the_file_is_released()
    {
        var time = new FakeTime();
        var core = new SimulatedCore(_ => new SimScenario());
        var sink = new CapturingSink();
        var input = new Scripted(
            () => Frames.Request(Frames.Id1, "inspect", Path, false),
            () =>
            {
                time.Advance(TimeSpan.FromSeconds(61));
                return Frames.Request(Frames.Id2, "shred", Path, true, TokenFrom(sink));
            });
        new ConnectionSession(input, sink, new FileOperations(core), null, time).Run();
        Assert.Equal(5, sink.Last.GetProperty("code").GetInt32());
        Assert.Equal(0, core.TrimCalls);
        Assert.Equal(0, core.ActiveHolds);
    }

    [Fact]
    public void Disconnect_releases_a_held_selection()
    {
        var (sink, core, _) = Run(Frames.Request(Frames.Id1, "inspect", Path, false) + "\n");
        Assert.True(sink.Last.GetProperty("evidence").TryGetProperty("selectionToken", out _));
        Assert.Equal(0, core.ActiveHolds);
        Assert.Equal(0, core.TrimCalls);
    }

    [Fact]
    public void A_repeated_request_id_closes_the_connection_without_executing()
    {
        var (sink, _, session) = Run(
            Frames.Request(Frames.Id1, "inspect", Path, false) + "\n" +
            Frames.Request(Frames.Id1, "inspect", Path, false) + "\n" +
            Frames.Request(Frames.Id2, "inspect", Path, false) + "\n");
        Assert.Equal(1, session.Executed);
        Assert.All(sink.Lines, l => Assert.Contains(Frames.Id1, l));
    }

    [Theory]
    [InlineData("garbage\n")]
    [InlineData("{\"v\":1,\"op\":\"inspect\",\"target\":\"C:\\\\a.bin\",\"confirmed\":false}\n")]
    public void A_frame_with_no_trustworthy_id_closes_the_connection_silently(string bad)
    {
        var (sink, _, session) = Run(bad + Frames.Request(Frames.Id1, "inspect", Path, false) + "\n");
        Assert.Empty(sink.Lines);
        Assert.Equal(0, session.Executed);
    }

    [Fact]
    public void An_oversized_frame_closes_the_connection_silently()
    {
        var (sink, _, session) = Run(new string('a', Wire.MaxFrameBytes + 1) + "\n" + Frames.Request(Frames.Id1, "inspect", Path, false) + "\n");
        Assert.Empty(sink.Lines);
        Assert.Equal(0, session.Executed);
    }

    [Fact]
    public void A_schema_violation_is_answered_with_code_2_and_the_connection_continues()
    {
        var (sink, _, session) = Run(
            "{\"v\":1,\"id\":\"" + Frames.Id1 + "\",\"op\":\"inspect\",\"target\":\"C:\\\\a.bin\",\"confirmed\":false,\"x\":1}\n" +
            Frames.Request(Frames.Id2, "inspect", Path, false) + "\n");
        Assert.Equal(2, sink.Json(0).GetProperty("code").GetInt32());
        Assert.Equal(1, session.Executed);
        Assert.Equal(0, sink.Last.GetProperty("code").GetInt32());
    }

    [Fact]
    public void An_ordinary_connection_refuses_vault_verbs_and_an_elevated_one_refuses_file_verbs()
    {
        var (sink, _, session) = Run(Frames.Request(Frames.Id1, "vault-init", null, false) + "\n");
        Assert.Equal(2, sink.Last.GetProperty("code").GetInt32());
        Assert.Equal(0, session.Executed);

        int handled = 0;
        PrivilegedHandler handler = (req, emit) => { handled++; return emit.Result(ExitCode.Completed, null); };
        var (sink2, core2, _) = Run(Frames.Request(Frames.Id1, "inspect", Path, false) + "\n", privileged: handler, files: false);
        Assert.Equal(2, sink2.Last.GetProperty("code").GetInt32());
        Assert.Equal(0, handled);
        Assert.Equal(0, core2.ActiveHolds);
    }

    [Fact]
    public void A_privileged_connection_is_one_shot()
    {
        int handled = 0;
        PrivilegedHandler handler = (req, emit) => { handled++; return emit.Result(ExitCode.Completed, null); };
        var (sink, _, _) = Run(
            Frames.Request(Frames.Id1, "vault-status", Frames.Id3, false) + "\n" +
            Frames.Request(Frames.Id2, "vault-close", Frames.Id3, true) + "\n",
            privileged: handler, files: false);
        Assert.Equal(1, handled);
        Assert.Single(sink.Lines);
    }
}
