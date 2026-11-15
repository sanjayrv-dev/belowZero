using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SSDShredder.Worker.Ipc;
using SSDShredder.Worker.Operations;
using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Tests;

public class PipeTests
{
    private static string NewName() => "SSDShredder.test." + PipeEndpoints.NewSuffix();

    [Fact]
    public void Suffix_is_32_lowercase_hex_and_fresh_each_time()
    {
        string a = PipeEndpoints.NewSuffix();
        Assert.True(Validation.IsLowerHex32(a));
        Assert.NotEqual(a, PipeEndpoints.NewSuffix());
    }

    [Fact]
    public void A_second_server_cannot_take_an_existing_pipe_name()
    {
        string name = NewName();
        using NamedPipeServerStream first = PipeEndpoints.CreateServer(name);
        Assert.True(PipeEndpoints.Exists(name));
        Assert.ThrowsAny<Exception>(() => PipeEndpoints.CreateServer(name).Dispose());
    }

    [Fact]
    public async Task Both_ends_verify_pid_user_image_and_session()
    {
        string name = NewName();
        using NamedPipeServerStream server = PipeEndpoints.CreateServer(name);
        Task<NamedPipeClientStream?> connecting = Task.Run(() => PipeEndpoints.Connect(name, 5000));
        Assert.True(PipeEndpoints.WaitForConnection(server, TimeSpan.FromSeconds(5)));
        using NamedPipeClientStream client = (await connecting)!;

        uint me = (uint)Environment.ProcessId;
        PeerExpectation right = PeerVerifier.SelfPeer(me, exactImage: true);
        Assert.Null(PeerVerifier.VerifyClient(server, right));
        Assert.Null(PeerVerifier.VerifyServer(client, right));

        Assert.Equal("unexpected process", PeerVerifier.VerifyClient(server, right with { Pid = me + 4 }));
        Assert.Equal("unexpected process", PeerVerifier.VerifyServer(client, right with { Pid = me + 4 }));
        Assert.Equal("unexpected image", PeerVerifier.VerifyClient(server, right with { ImagePath = @"C:\Windows\System32\cmd.exe" }));
        Assert.Equal("image outside the installed directory",
            PeerVerifier.VerifyServer(client, right with { ImagePath = null, ImageDirectory = @"C:\Windows\System32" }));
        Assert.Equal("unexpected session", PeerVerifier.VerifyClient(server, right with { Session = right.Session + 1 }));
        var other = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1000-2000-3000-1002");
        Assert.Equal("unexpected user", PeerVerifier.VerifyClient(server, right with { User = other }));
        Assert.Equal("unexpected user", PeerVerifier.VerifyServer(client, right with { User = other }));
    }

    [Fact]
    public void Waiting_for_a_client_gives_up_at_the_timeout()
    {
        using NamedPipeServerStream server = PipeEndpoints.CreateServer(NewName());
        var sw = Stopwatch.StartNew();
        Assert.False(PipeEndpoints.WaitForConnection(server, TimeSpan.FromMilliseconds(300)));
        Assert.True(sw.ElapsedMilliseconds < 5000);
        Assert.Null(PipeEndpoints.Connect(NewName(), 200));
    }

    [Fact]
    public async Task A_request_travels_over_the_pipe_and_its_events_are_relayed()
    {
        string name = NewName();
        using NamedPipeServerStream server = PipeEndpoints.CreateServer(name);
        Task serving = Task.Run(() =>
        {
            PipeEndpoints.WaitForConnection(server, TimeSpan.FromSeconds(5));
            new ConnectionSession(server, new StreamFrameSink(server), null, (req, emit) =>
            {
                emit.Progress(Phases.Locking);
                return emit.Result(ExitCode.Completed, new VaultEvidence(req.Target, VaultPhases.Retired, null, true));
            }).Run();
            server.WaitForPipeDrain();
        });

        using NamedPipeClientStream client = PipeEndpoints.Connect(name, 5000)!;
        var sink = new CapturingSink();
        ExitCode code = HelperLauncher.RunOver(client, new WireRequest(Frames.Id2, Ops.VaultClose, Frames.Id3, true, null), sink);
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ExitCode.Completed, code);
        Assert.Equal(2, sink.Lines.Count);
        Assert.Equal("""{"v":1,"id":"549556c4-3919-4ac7-bba8-e88d46ba6095","seq":2,"type":"result","code":0,"state":"completed","evidence":{"vaultId":"31f4fcad-b324-4058-a522-da3c7c443523","phase":"retired","mount":null,"lockedObserved":true}}""", sink.Lines[1]);
    }

    [Theory]
    [InlineData(Ops.VaultClose, true, 9)]
    [InlineData(Ops.VaultInit, false, 9)]
    [InlineData(Ops.VaultStatus, false, 7)]
    [InlineData(Ops.Shred, true, 8)]
    [InlineData(Ops.Inspect, false, 7)]
    public void A_lost_completion_is_reported_once_and_never_replayed(string op, bool confirmed, int expected)
    {
        // The far end reads the request and goes away without answering.
        var silent = new MemoryStream();
        var sink = new CapturingSink();
        string? target = op == Ops.VaultInit ? null : Frames.Id3;
        ExitCode code = HelperLauncher.RunOver(new WriteThenEof(silent), new WireRequest(Frames.Id1, op, target, confirmed, null), sink);

        Assert.Equal(expected, (int)code);
        Assert.Single(sink.Lines);
        Assert.Equal(expected, sink.Last.GetProperty("code").GetInt32());
        Assert.Single(Encoding.UTF8.GetString(silent.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData(1223, 3)] // the user declined the consent prompt
    [InlineData(5, 7)]
    [InlineData(2, 7)]
    public void A_helper_that_never_started_yields_one_synthesized_result(int win32Error, int expected)
    {
        var sink = new CapturingSink();
        HelperLauncher.ProcessStarter refuse = (string exe, string args, bool elevate, out int error) =>
        {
            Assert.True(elevate);
            Assert.Matches(@"^--ipc [0-9a-f]{32} --parent-pid \d+$", args);
            error = win32Error;
            return 0;
        };
        ExitCode code = HelperLauncher.Run(
            new WireRequest(Frames.Id1, Ops.VaultInit, null, false, null), sink, elevate: true, "SSDShredder.test.", refuse);

        Assert.Equal(expected, (int)code);
        Assert.Single(sink.Lines);
        Assert.Equal(1, sink.Last.GetProperty("seq").GetInt32());
        Assert.Equal(expected, sink.Last.GetProperty("code").GetInt32());
        Assert.Equal(Frames.Id1, sink.Last.GetProperty("id").GetString());
    }

    [Fact]
    public void A_helper_that_exits_before_connecting_fails_promptly()
    {
        var sink = new CapturingSink();
        Process? child = null;
        HelperLauncher.ProcessStarter exitsAtOnce = (string exe, string args, bool elevate, out int error) =>
        {
            error = 0;
            child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
            return Native.OpenProcess(Native.Synchronize | Native.ProcessQueryLimitedInformation, 0, (uint)child.Id);
        };
        var sw = Stopwatch.StartNew();
        ExitCode code = HelperLauncher.Run(
            new WireRequest(Frames.Id1, Ops.VaultStatus, Frames.Id3, false, null), sink, elevate: false, "SSDShredder.test.", exitsAtOnce);
        child?.Dispose();

        Assert.Equal(ExitCode.Failed, code);
        Assert.Single(sink.Lines);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Events_for_another_request_are_not_relayed()
    {
        string foreign = "{\"v\":1,\"id\":\"" + Frames.Id2 + "\",\"seq\":1,\"type\":\"result\",\"code\":0,\"state\":\"completed\",\"evidence\":null}\n";
        var sink = new CapturingSink();
        var stream = new WriteThenEof(new MemoryStream(), Encoding.UTF8.GetBytes(foreign));
        ExitCode code = HelperLauncher.RunOver(stream, new WireRequest(Frames.Id1, Ops.VaultStatus, Frames.Id3, false, null), sink);
        Assert.Equal(ExitCode.Failed, code);
        Assert.Single(sink.Lines);
        Assert.Contains(Frames.Id1, sink.Lines[0]);
    }

    [Fact]
    public void Serialized_requests_parse_back_exactly()
    {
        var req = new WireRequest(Frames.Id1, Ops.Shred, @"C:\Demo\fixture.bin", true, "b84d239cd73a26ac9d5f713565040a5f");
        byte[] line = HelperLauncher.Serialize(req);
        Assert.Equal((byte)'\n', line[^1]);
        ParseResult parsed = RequestParser.Parse(line.AsSpan(0, line.Length - 1));
        Assert.Equal(ParseStatus.Ok, parsed.Status);
        Assert.Equal(req, parsed.Request);
    }

    private sealed class WriteThenEof : Stream
    {
        private readonly MemoryStream _written;
        private readonly MemoryStream _reply;

        public WriteThenEof(MemoryStream written, byte[]? reply = null)
        {
            _written = written;
            _reply = new MemoryStream(reply ?? []);
        }

        public override int Read(byte[] buffer, int offset, int count) => _reply.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// Runs the built SSDShredder.Worker.exe in simulated mode: real processes, real pipes,
/// real peer checks, simulated Core and vault platform. No elevation is involved.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string _exe = Path.Combine(AppContext.BaseDirectory, "SSDShredder.Worker.exe");

    private sealed record RunResult(int ExitCode, List<JsonElement> Events, string Stderr)
    {
        public JsonElement Result => Events[^1];
        public JsonElement Evidence => Result.GetProperty("evidence");
    }

    private RunResult Run(string[] args, Dictionary<string, string>? env = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["SSDSHREDDER_SIMULATE"] = "1";
        psi.Environment["SSDSHREDDER_SIM_ROOT"] = _root.Path;
        foreach (var kv in env ?? []) psi.Environment[kv.Key] = kv.Value;

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        Task<string> outText = p.StandardOutput.ReadToEndAsync();
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(60_000), "worker did not exit");
        Assert.True(outText.Wait(10_000), "stdout was held open after the worker exited");
        var events = outText.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        return new RunResult(p.ExitCode, events, err.Result);
    }

    private string Fixture(int bytes)
    {
        string path = Path.Combine(_root.Path, "fixture.bin");
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private List<Process> Owners() => Process.GetProcessesByName("SSDShredder.Worker").Where(p =>
    {
        try
        {
            return string.Equals(p.MainModule?.FileName, _exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }).ToList();

    private void KillOwners()
    {
        foreach (Process p in Owners())
        {
            try
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception)
            {
            }
        }
    }

    public void Dispose()
    {
        KillOwners();
        _root.Dispose();
    }

    [Fact]
    public void Bad_arguments_exit_2_with_no_events()
    {
        RunResult r = Run(["--shred"]);
        Assert.Equal(2, r.ExitCode);
        Assert.Empty(r.Events);
        Assert.Contains("Usage", r.Stderr);
    }

    [Fact]
    public void Inspect_reports_covered_and_tail_bytes_without_a_token()
    {
        string file = Fixture(Environment.SystemPageSize + 615);
        RunResult r = Run(["--inspect", file]);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Evidence.GetProperty("eligible").GetBoolean());
        Assert.Equal(Environment.SystemPageSize, r.Evidence.GetProperty("coveredBytes").GetInt64());
        Assert.Equal(615, r.Evidence.GetProperty("tailBytes").GetInt64());
        Assert.False(r.Evidence.TryGetProperty("selectionToken", out _));
        Assert.Contains("SIMULATED", r.Stderr);
    }

    [Fact]
    public void Shred_needs_confirm_when_not_interactive_and_then_reports_a_receipt()
    {
        string file = Fixture(Environment.SystemPageSize * 2);
        RunResult refused = Run(["--shred", file]);
        Assert.Equal(2, refused.ExitCode);
        Assert.Equal(2, refused.Result.GetProperty("code").GetInt32());

        RunResult done = Run(["--shred", file, "--confirm"]);
        Assert.Equal(0, done.ExitCode);
        Assert.Equal("removed", done.Evidence.GetProperty("entry").GetString());
        Assert.Equal(["inspecting", "confirming", "trimming", "unlinking"],
            done.Events.Where(e => e.GetProperty("type").GetString() == "progress").Select(e => e.GetProperty("phase").GetString()));
        Assert.True(File.Exists(file), "the simulator must never touch a real file");
    }

    [Theory]
    [InlineData("partial", 8, "preserved")]
    [InlineData("usb", 4, "preserved")]
    [InlineData("busy", 6, "preserved")]
    [InlineData("denied", 5, "preserved")]
    public void Shred_scenarios_map_to_their_exit_codes(string scenario, int expected, string entry)
    {
        string file = Fixture(Environment.SystemPageSize * 2);
        RunResult r = Run(["--shred", file, "--confirm"], new() { ["SSDSHREDDER_SIM_SCENARIO"] = scenario });
        Assert.Equal(expected, r.ExitCode);
        Assert.Equal(expected, r.Result.GetProperty("code").GetInt32());
        Assert.Equal(entry, r.Evidence.GetProperty("entry").GetString());
    }

    [Fact]
    public void Vault_lifecycle_through_real_processes_and_pipes()
    {
        RunResult init = Run(["--vault-init"]);
        Assert.True(init.ExitCode == 0, init.Stderr + string.Join("\n", init.Events));
        Assert.Equal("ready", init.Evidence.GetProperty("phase").GetString());
        Assert.Equal("fullyEncrypted", init.Evidence.GetProperty("conversion").GetString());
        string id = init.Evidence.GetProperty("vaultId").GetString()!;
        Assert.Single(Owners());

        RunResult status = Run(["--vault-status", id]);
        Assert.Equal(0, status.ExitCode);
        Assert.Equal("ready", status.Evidence.GetProperty("phase").GetString());
        Assert.Equal(init.Evidence.GetProperty("mount").GetString(), status.Evidence.GetProperty("mount").GetString());

        RunResult unconfirmed = Run(["--vault-close", id]);
        Assert.Equal(2, unconfirmed.ExitCode);
        Assert.Equal("ready", Run(["--vault-status", id]).Evidence.GetProperty("phase").GetString());

        RunResult close = Run(["--vault-close", id, "--confirm"]);
        Assert.True(close.ExitCode == 0, close.Stderr + string.Join("\n", close.Events));
        Assert.Equal("retired", close.Evidence.GetProperty("phase").GetString());
        Assert.True(close.Evidence.GetProperty("lockedObserved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, close.Evidence.GetProperty("mount").ValueKind);

        // The owner retires itself, and a repeated close returns the durable receipt.
        for (int i = 0; i < 50 && Owners().Count > 0; i++) Thread.Sleep(100);
        Assert.Empty(Owners());
        RunResult again = Run(["--vault-close", id, "--confirm"]);
        Assert.Equal(0, again.ExitCode);
        Assert.Equal("retired", again.Evidence.GetProperty("phase").GetString());
        Assert.True(again.Evidence.GetProperty("lockedObserved").GetBoolean());
        Assert.NotEqual(close.Result.GetProperty("id").GetString(), again.Result.GetProperty("id").GetString());
    }

    [Fact]
    public void A_busy_vault_reports_6_and_stays_ready()
    {
        RunResult init = Run(["--vault-init"], new() { ["SSDSHREDDER_SIM_FAULTS"] = "quiesce:Busy" });
        Assert.Equal(0, init.ExitCode);
        string id = init.Evidence.GetProperty("vaultId").GetString()!;

        RunResult close = Run(["--vault-close", id, "--confirm"]);
        Assert.Equal(6, close.ExitCode);
        Assert.Equal("ready", close.Evidence.GetProperty("phase").GetString());
        Assert.Equal("ready", Run(["--vault-status", id]).Evidence.GetProperty("phase").GetString());
    }

    private void WaitForNoOwners()
    {
        for (int i = 0; i < 50 && Owners().Count > 0; i++) Thread.Sleep(100);
        Assert.Empty(Owners());
    }

    [Fact]
    public void A_policy_refusal_ends_as_unsupported_with_nothing_left_behind()
    {
        RunResult init = Run(["--vault-init"], new() { ["SSDSHREDDER_SIM_FAULTS"] = "protect:PolicyRefused" });
        Assert.True(init.ExitCode == 4, init.Stderr + string.Join("\n", init.Events));
        Assert.Equal("retired", init.Evidence.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, init.Evidence.GetProperty("mount").ValueKind);
        Assert.False(init.Evidence.TryGetProperty("conversion", out _));

        // No session owner stays behind for a vault that was never published.
        WaitForNoOwners();
        string id = init.Evidence.GetProperty("vaultId").GetString()!;
        RunResult status = Run(["--vault-status", id]);
        Assert.Equal(0, status.ExitCode);
        Assert.Equal("retired", status.Evidence.GetProperty("phase").GetString());
    }

    [Fact]
    public void A_failed_lock_reports_9_and_keeps_the_vault_closed_to_use()
    {
        RunResult init = Run(["--vault-init"], new() { ["SSDSHREDDER_SIM_FAULTS"] = "lock:RecoveryKeyRequired" });
        Assert.Equal(0, init.ExitCode);
        string id = init.Evidence.GetProperty("vaultId").GetString()!;

        RunResult close = Run(["--vault-close", id, "--confirm"]);
        Assert.True(close.ExitCode == 9, close.Stderr + string.Join("\n", close.Events));
        Assert.Equal("closing", close.Evidence.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, close.Evidence.GetProperty("mount").ValueKind);
        Assert.Equal(JsonValueKind.Null, close.Evidence.GetProperty("lockedObserved").ValueKind);

        // The owner is still there, holding the image, and says the same.
        Assert.Single(Owners());
        RunResult status = Run(["--vault-status", id]);
        Assert.Equal("closing", status.Evidence.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, status.Evidence.GetProperty("mount").ValueKind);
    }

    [Fact]
    public void A_failed_lock_closes_through_the_detach_route_when_that_route_is_allowed()
    {
        RunResult init = Run(["--vault-init"], new()
        {
            ["SSDSHREDDER_SIM_FAULTS"] = "lock:RecoveryKeyRequired",
            ["SSDSHREDDER_SIM_DETACH_ROUTE"] = "1",
        });
        string id = init.Evidence.GetProperty("vaultId").GetString()!;

        RunResult close = Run(["--vault-close", id, "--confirm"]);
        Assert.True(close.ExitCode == 0, close.Stderr + string.Join("\n", close.Events));
        Assert.Equal("retired", close.Evidence.GetProperty("phase").GetString());
        Assert.True(close.Evidence.GetProperty("lockedObserved").GetBoolean());
        WaitForNoOwners();
    }

    [Fact]
    public void An_interrupted_cleanup_is_finished_later_by_recovery()
    {
        var optIn = new Dictionary<string, string> { ["SSDSHREDDER_SIM_OPTIN"] = "1" };
        RunResult init = Run(["--vault-init"], new(optIn) { ["SSDSHREDDER_SIM_FAULTS"] = "delete:Failed" });
        string id = init.Evidence.GetProperty("vaultId").GetString()!;

        // Locked and detached, but the backing image could not be retired.
        RunResult close = Run(["--vault-close", id, "--confirm"]);
        Assert.True(close.ExitCode == 9, close.Stderr + string.Join("\n", close.Events));
        Assert.Equal("closing", close.Evidence.GetProperty("phase").GetString());
        Assert.True(close.Evidence.GetProperty("lockedObserved").GetBoolean());

        KillOwners();
        RunResult later = Run(["--vault-close", id, "--confirm"], optIn);
        Assert.True(later.ExitCode == 0, later.Stderr + string.Join("\n", later.Events));
        Assert.Equal("retired", later.Evidence.GetProperty("phase").GetString());
        Assert.True(later.Evidence.GetProperty("lockedObserved").GetBoolean());
        Assert.Contains(later.Events, e => e.GetProperty("type").GetString() == "progress" && e.GetProperty("phase").GetString() == "reconciling");
    }

    [Fact]
    public async Task Two_simultaneous_closes_both_end_with_the_retired_receipt()
    {
        string id = Run(["--vault-init"]).Evidence.GetProperty("vaultId").GetString()!;
        RunResult[] closed = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => Run(["--vault-close", id, "--confirm"]))));
        Assert.All(closed, r => Assert.True(r.ExitCode == 0, r.Stderr + string.Join("\n", r.Events)));
        Assert.All(closed, r => Assert.Equal("retired", r.Evidence.GetProperty("phase").GetString()));
    }

    [Theory]
    [InlineData(true, 0, "retired")]
    [InlineData(false, 9, "orphan")]
    public void A_killed_owner_is_handled_by_recovery(bool optIn, int expected, string phase)
    {
        var env = new Dictionary<string, string> { ["SSDSHREDDER_SIM_OPTIN"] = optIn ? "1" : "0" };
        string id = Run(["--vault-init"], env).Evidence.GetProperty("vaultId").GetString()!;
        KillOwners();

        RunResult close = Run(["--vault-close", id, "--confirm"], env);
        Assert.Equal(expected, close.ExitCode);
        Assert.Equal(phase, close.Evidence.GetProperty("phase").GetString());
        Assert.Contains(close.Events, e => e.GetProperty("type").GetString() == "progress" && e.GetProperty("phase").GetString() == "reconciling");
        Assert.Equal(phase, Run(["--vault-status", id], env).Evidence.GetProperty("phase").GetString());
    }

    /// <summary>The path Engineer 3's UI uses: the test is the pipe server, the worker is the client.</summary>
    private (int exit, List<JsonElement> events, string stderr) DriveAsUi(
        uint parentPid, Func<Func<string, JsonElement>, int> conversation)
    {
        string suffix = PipeEndpoints.NewSuffix();
        using NamedPipeServerStream server = PipeEndpoints.CreateServer(PipeEndpoints.RequestPrefix + suffix);
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "--ipc", suffix, "--parent-pid", parentPid.ToString() }) psi.ArgumentList.Add(a);
        psi.Environment["SSDSHREDDER_SIMULATE"] = "1";
        psi.Environment["SSDSHREDDER_SIM_ROOT"] = _root.Path;

        using Process worker = Process.Start(psi)!;
        Task<string> err = worker.StandardError.ReadToEndAsync();
        var events = new List<JsonElement>();
        if (PipeEndpoints.WaitForConnection(server, TimeSpan.FromSeconds(15), () => worker.HasExited))
        {
            var reader = new FrameReader(server);
            JsonElement Ask(string request)
            {
                server.Write(Encoding.UTF8.GetBytes(request + "\n"));
                server.Flush();
                while (true)
                {
                    Assert.Equal(FrameStatus.Frame, reader.Read(out byte[] frame));
                    JsonElement e = JsonDocument.Parse(frame).RootElement;
                    events.Add(e);
                    if (e.GetProperty("type").GetString() == "result") return e;
                }
            }
            // A conversation that returns 1 leaves the worker to hang up first.
            if (conversation(Ask) == 1) worker.WaitForExit(20_000);
            server.Disconnect();
        }
        Assert.True(worker.WaitForExit(20_000), "worker did not exit after the connection closed");
        return (worker.ExitCode, events, err.Result);
    }

    [Fact]
    public void A_ui_process_can_inspect_then_shred_with_the_token_over_its_own_pipe()
    {
        string file = Fixture(Environment.SystemPageSize + 615);
        var (exit, events, stderr) = DriveAsUi((uint)Environment.ProcessId, ask =>
        {
            JsonElement inspected = ask(Frames.Request(Frames.Id1, "inspect", file, false));
            Assert.Equal(0, inspected.GetProperty("code").GetInt32());
            string token = inspected.GetProperty("evidence").GetProperty("selectionToken").GetString()!;
            Assert.True(Validation.IsLowerHex32(token));

            JsonElement shredded = ask(Frames.Request(Frames.Id2, "shred", file, true, token));
            Assert.Equal(0, shredded.GetProperty("code").GetInt32());
            Assert.Equal("removed", shredded.GetProperty("evidence").GetProperty("entry").GetString());

            JsonElement replay = ask(Frames.Request(Frames.Id3, "shred", file, true, token));
            Assert.Equal(2, replay.GetProperty("code").GetInt32());
            return 0;
        });

        Assert.True(exit == 0, stderr);
        Assert.Equal(["inspecting", "trimming", "unlinking"],
            events.Where(e => e.GetProperty("type").GetString() == "progress").Select(e => e.GetProperty("phase").GetString()));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_worker_started_by_someone_other_than_the_pipe_server_refuses_to_serve()
    {
        using Process other = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        })!;
        try
        {
            var (exit, events, stderr) = DriveAsUi((uint)other.Id, _ => 1);
            Assert.Equal(5, exit);
            Assert.Empty(events);
            Assert.Equal("The launching process failed verification: unexpected process.", stderr.Trim());
        }
        finally
        {
            other.Kill();
        }
    }

    [Fact]
    public void An_unknown_vault_is_denied()
    {
        RunResult r = Run(["--vault-status", Guid.NewGuid().ToString("D")]);
        Assert.Equal(5, r.ExitCode);
    }
}
