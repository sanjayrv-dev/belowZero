using System.Text;
using SSDShredder.Worker.Cli;
using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Tests;

public class CommandLineTests
{
    private static bool Parse(out ParsedCommand? cmd, params string[] args) => CommandLine.TryParse(args, out cmd, out _);

    [Fact]
    public void Parses_each_published_form()
    {
        Assert.True(Parse(out var c, "--inspect", @"C:\Demo\fixture.bin"));
        Assert.Equal(Verb.Inspect, c!.Verb);

        Assert.True(Parse(out c, "--shred", @"C:\Demo\fixture.bin"));
        Assert.False(c!.Confirm);
        Assert.True(Parse(out c, "--shred", @"C:\Demo\fixture.bin", "--confirm"));
        Assert.True(c!.Confirm);

        Assert.True(Parse(out c, "--vault-init"));
        Assert.True(Parse(out c, "--vault-status", Frames.Id3));
        Assert.True(Parse(out c, "--vault-close", Frames.Id3.ToUpperInvariant(), "--confirm"));
        Assert.Equal(Frames.Id3, c!.VaultId);
        Assert.True(c.Confirm);

        Assert.True(Parse(out c, "--ipc", "b84d239cd73a26ac9d5f713565040a5f", "--parent-pid", "4242"));
        Assert.Equal(4242u, c!.ParentPid);
    }

    [Theory]
    [InlineData]
    [InlineData("--inspect")]
    [InlineData("--inspect", @"C:\a.bin", "extra")]
    [InlineData("--shred", @"C:\a.bin", "--force")]
    [InlineData("--shred", "--confirm", @"C:\a.bin")]
    [InlineData("--shred", @"C:\a.bin", "--confirm", "--confirm")]
    [InlineData("--vault-init", "C:\\source.bin")]
    [InlineData("--vault-status")]
    [InlineData("--vault-status", "not-a-uuid")]
    [InlineData("--vault-status", Frames.Id3, "--confirm")]
    [InlineData("--vault-close", Frames.Id3, "--yes")]
    [InlineData("--unknown")]
    [InlineData("--INSPECT", @"C:\a.bin")]
    [InlineData("--ipc", "B84D239CD73A26AC9D5F713565040A5F", "--parent-pid", "1")]
    [InlineData("--ipc", "b84d239cd73a26ac9d5f713565040a5f", "--parent-pid", "-1")]
    [InlineData("--ipc", "b84d239cd73a26ac9d5f713565040a5f", "--parent-pid", "0")]
    [InlineData("--ipc", "b84d239cd73a26ac9d5f713565040a5f", "4242")]
    public void Rejects_wrong_arity_and_unknown_switches(params string[] args)
    {
        Assert.False(Parse(out _, args));
    }

    [Theory]
    [InlineData(@"\\server\share\a.bin")]
    [InlineData(@"\\?\C:\a.bin")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\a.bin:stream")]
    [InlineData(@"C:\a.bin::$DATA")]
    [InlineData(@"a.bin")]
    [InlineData(@"C:a.bin")]
    [InlineData(@"C:\dir\..\a.bin")]
    [InlineData(@"C:/dir/a.bin")]
    [InlineData(@"C:\dir\")]
    [InlineData(@"C:\a.bin ")]
    [InlineData(@"C:\a*.bin")]
    public void Rejects_paths_that_are_not_plain_absolute_local_files(string path)
    {
        Assert.False(Validation.IsAbsoluteLocalFilePath(path));
        Assert.False(Parse(out _, "--inspect", path));
    }
}

public class RequestParserTests
{
    private static ParseResult Parse(string json) => RequestParser.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Accepts_the_published_shred_example()
    {
        ParseResult r = Parse("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"shred","target":"C:\\Demo\\fixture.bin","confirmed":true,"selectionToken":"b84d239cd73a26ac9d5f713565040a5f"}""");
        Assert.Equal(ParseStatus.Ok, r.Status);
        Assert.Equal(@"C:\Demo\fixture.bin", r.Request!.Target);
        Assert.True(r.Request.Confirmed);
        Assert.Equal("b84d239cd73a26ac9d5f713565040a5f", r.Request.SelectionToken);
    }

    [Fact]
    public void Accepts_the_published_vault_close_example()
    {
        ParseResult r = Parse("""{"v":1,"id":"549556c4-3919-4ac7-bba8-e88d46ba6095","op":"vault-close","target":"31f4fcad-b324-4058-a522-da3c7c443523","confirmed":true}""");
        Assert.Equal(ParseStatus.Ok, r.Status);
    }

    [Fact]
    public void Accepts_init_status_inspect_and_probe()
    {
        Assert.Equal(ParseStatus.Ok, Parse(Frames.Request(Frames.Id1, "vault-init", null, false)).Status);
        Assert.Equal(ParseStatus.Ok, Parse(Frames.Request(Frames.Id1, "vault-status", Frames.Id3, false)).Status);
        Assert.Equal(ParseStatus.Ok, Parse(Frames.Request(Frames.Id1, "inspect", @"C:\Demo\a.bin", false)).Status);
        Assert.Equal(ParseStatus.Ok, Parse(Frames.Request(Frames.Id1, "probe-volume", @"\\?\Volume{31f4fcad-b324-4058-a522-da3c7c443523}\", false)).Status);
    }

    [Theory]
    [InlineData("""not json""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":false} trailing""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","id":"549556c4-3919-4ac7-bba8-e88d46ba6095","op":"inspect","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"op":"inspect","target":"C:\\a.bin","confirmed":false,"confirmed":false,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd"}""")]
    [InlineData("""{"v":1,"op":"inspect","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"id":"not-a-uuid","op":"inspect","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"id":"BA09878A-81F9-48F4-832A-BEF3818E2DDD","op":"inspect","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"id":7,"op":"inspect","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":false,}""")]
    public void Frames_without_a_trustworthy_id_are_fatal(string json)
    {
        Assert.Equal(ParseStatus.Fatal, Parse(json).Status);
    }

    [Fact]
    public void Invalid_utf8_and_oversized_frames_are_fatal()
    {
        byte[] bad = [.. Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"" + Frames.Id1 + "\",\"op\":\"inspect\",\"target\":\"C:\\\\"), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\",\"confirmed\":false}")];
        Assert.Equal(ParseStatus.Fatal, RequestParser.Parse(bad).Status);

        string big = Frames.Request(Frames.Id1, "inspect", @"C:\" + new string('a', Wire.MaxFrameBytes), false);
        Assert.Equal(ParseStatus.Fatal, Parse(big).Status);
    }

    [Theory]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":false,"extra":1}""")]
    [InlineData("""{"v":2,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1.0,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":"1","id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"delete","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":["C:\\a.bin"],"confirmed":false}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":"false"}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin"}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":true}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"\\\\server\\share\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"inspect","target":"C:\\a.bin","confirmed":false,"selectionToken":"b84d239cd73a26ac9d5f713565040a5f"}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"shred","target":"C:\\a.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"shred","target":"C:\\a.bin","confirmed":true,"selectionToken":"B84D239CD73A26AC9D5F713565040A5F"}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"shred","target":"C:\\a.bin","confirmed":true,"selectionToken":"abc"}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"vault-init","target":"C:\\source.bin","confirmed":false}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"vault-init","target":null,"confirmed":true}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"vault-close","target":"31f4fcad-b324-4058-a522-da3c7c443523","confirmed":false}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"vault-status","target":"31f4fcad-b324-4058-a522-da3c7c443523","confirmed":true}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"vault-status","target":null,"confirmed":false}""")]
    [InlineData("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","op":"probe-volume","target":"C:\\","confirmed":false}""")]
    public void Schema_violations_with_a_good_id_are_rejected_under_that_id(string json)
    {
        ParseResult r = Parse(json);
        Assert.Equal(ParseStatus.Reject, r.Status);
        Assert.Equal(Frames.Id1, r.Id);
    }
}

public class FrameReaderTests
{
    [Fact]
    public void Splits_on_lf_and_reports_clean_end()
    {
        var reader = new FrameReader(new MemoryStream(Encoding.UTF8.GetBytes("one\ntwo\n")));
        Assert.Equal(FrameStatus.Frame, reader.Read(out byte[] a));
        Assert.Equal("one", Encoding.UTF8.GetString(a));
        Assert.Equal(FrameStatus.Frame, reader.Read(out byte[] b));
        Assert.Equal("two", Encoding.UTF8.GetString(b));
        Assert.Equal(FrameStatus.End, reader.Read(out _));
    }

    [Fact]
    public void A_frame_of_exactly_the_limit_passes_and_one_more_byte_does_not()
    {
        byte[] ok = [.. Enumerable.Repeat((byte)'a', Wire.MaxFrameBytes), (byte)'\n'];
        Assert.Equal(FrameStatus.Frame, new FrameReader(new MemoryStream(ok)).Read(out byte[] frame));
        Assert.Equal(Wire.MaxFrameBytes, frame.Length);

        byte[] tooBig = [.. Enumerable.Repeat((byte)'a', Wire.MaxFrameBytes + 1), (byte)'\n'];
        Assert.Equal(FrameStatus.Oversized, new FrameReader(new MemoryStream(tooBig)).Read(out _));
    }

    [Fact]
    public void A_line_without_lf_is_truncated()
    {
        Assert.Equal(FrameStatus.Truncated, new FrameReader(new MemoryStream(Encoding.UTF8.GetBytes("partial"))).Read(out _));
    }
}

public class EventTests
{
    [Fact]
    public void Shred_transcript_lines_match_the_published_example_byte_for_byte()
    {
        var sink = new CapturingSink();
        var emit = new RequestEmitter(sink, Frames.Id1);
        emit.Progress(Phases.Trimming);
        emit.Result(ExitCode.Completed, new ShredEvidence(1, 1, 4096, 615, ShredEvidence.EntryRemoved));

        Assert.Equal("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","seq":1,"type":"progress","phase":"trimming"}""", sink.Lines[0]);
        Assert.Equal("""{"v":1,"id":"ba09878a-81f9-48f4-832a-bef3818e2ddd","seq":2,"type":"result","code":0,"state":"completed","evidence":{"rangesRequested":1,"rangesProcessed":1,"coveredBytes":4096,"tailBytes":615,"entry":"removed"}}""", sink.Lines[1]);
    }

    [Fact]
    public void Vault_close_result_matches_the_published_example_byte_for_byte()
    {
        var sink = new CapturingSink();
        new RequestEmitter(sink, Frames.Id2).Result(ExitCode.Completed, new VaultEvidence(Frames.Id3, VaultPhases.Retired, null, true));
        Assert.Equal("""{"v":1,"id":"549556c4-3919-4ac7-bba8-e88d46ba6095","seq":1,"type":"result","code":0,"state":"completed","evidence":{"vaultId":"31f4fcad-b324-4058-a522-da3c7c443523","phase":"retired","mount":null,"lockedObserved":true}}""", sink.Lines[0]);
    }

    [Fact]
    public void Ready_vault_evidence_carries_the_readiness_facts()
    {
        var sink = new CapturingSink();
        new RequestEmitter(sink, Frames.Id2).Result(ExitCode.Completed,
            new VaultEvidence(Frames.Id3, VaultPhases.Ready, @"C:\m", null, new VaultReadyFacts("fullyEncrypted", true, [8], false, 7)));
        var e = sink.Last.GetProperty("evidence");
        Assert.Equal("fullyEncrypted", e.GetProperty("conversion").GetString());
        Assert.True(e.GetProperty("protectionOn").GetBoolean());
        Assert.Equal(8, e.GetProperty("protectorTypes")[0].GetInt32());
        Assert.False(e.GetProperty("autoUnlock").GetBoolean());
        Assert.Equal(7, e.GetProperty("encryptionMethod").GetInt32());
    }

    [Fact]
    public void Only_one_terminal_result_and_nothing_after_it()
    {
        var sink = new CapturingSink();
        var emit = new RequestEmitter(sink, Frames.Id1);
        emit.Result(ExitCode.Busy, null);
        emit.Progress(Phases.Trimming);
        Assert.Throws<InvalidOperationException>(() => emit.Result(ExitCode.Completed, null));
        Assert.Single(sink.Lines);
        Assert.Equal("busy", sink.Last.GetProperty("state").GetString());
    }

    [Fact]
    public void Every_exit_code_has_its_published_state_name()
    {
        Assert.Equal(
            ["completed", "invalid", "cancelled", "unsupported", "denied", "busy", "failed", "indeterminate", "recovery"],
            new[] { 0, 2, 3, 4, 5, 6, 7, 8, 9 }.Select(c => Wire.StateName((ExitCode)c)));
    }
}
