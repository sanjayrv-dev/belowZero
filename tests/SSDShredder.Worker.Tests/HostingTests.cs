using System.IO.Pipes;
using System.Text;
using SSDShredder.Worker.Cli;
using SSDShredder.Worker.Hosting;
using SSDShredder.Worker.Ipc;
using SSDShredder.Worker.Protocol;
using SSDShredder.Worker.Simulation;
using SSDShredder.Worker.Vault;

namespace SSDShredder.Worker.Tests;

internal static class TestEnvironment
{
    public static WorkerEnvironment Simulated(VaultFixture f, SimScenario? file = null) => new()
    {
        Simulated = true,
        Core = new SimulatedCore(_ => file),
        CoreAvailable = true,
        Platform = f.Platform,
        Journal = f.Journal,
        Options = new VaultOptions { ImageDirectory = f.JournalDir, MountRoot = f.JournalDir },
        RequestPipePrefix = "SSDShredder.test.",
        OwnerPipePrefix = "SSDShredder.test.owner.",
    };
}

public class CliHostTests
{
    /// <summary>A stdout that breaks, in an unexpected way, on one particular event.</summary>
    private sealed class FaultyStdout : MemoryStream
    {
        private readonly string _marker;

        public FaultyStdout(string marker) => _marker = marker;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Encoding.UTF8.GetString(buffer).Contains(_marker)) throw new NotSupportedException();
            base.Write(buffer);
        }
    }

    [Fact]
    public void An_unexpected_error_during_a_confirmed_shred_still_ends_with_one_result_and_code_8()
    {
        using var f = new VaultFixture();
        WorkerEnvironment env = TestEnvironment.Simulated(f, new SimScenario());
        using var stdout = new FaultyStdout("unlinking");
        var stderr = new StringWriter();

        ExitCode code = CliHost.Run(
            new ParsedCommand(Verb.Shred, Path: @"C:\Demo\fixture.bin", Confirm: true),
            env, stdout, new StringReader(""), stderr, interactive: false);

        Assert.Equal(ExitCode.Indeterminate, code);
        string[] lines = Encoding.UTF8.GetString(stdout.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"type\":\"result\",\"code\":8", lines[^1]);
        Assert.Single(lines, l => l.Contains("\"type\":\"result\""));
        Assert.Contains("internal error", stderr.ToString());
    }
}

public class RealModeGateTests
{
    private static WorkerEnvironment Real(TempDir dir) => new()
    {
        Simulated = false,
        Core = new SimulatedCore(_ => null),
        CoreAvailable = false,
        Platform = new WindowsVaultPlatform(),
        Journal = new VaultJournal(Path.Combine(dir.Path, "journal")),
        Options = new VaultOptions { ImageDirectory = dir.Path, MountRoot = dir.Path },
        RequestPipePrefix = "SSDShredder.test.",
        OwnerPipePrefix = "SSDShredder.test.owner.",
    };

    [Theory]
    [InlineData(Verb.VaultInit)]
    [InlineData(Verb.VaultClose)]
    public void While_the_real_provider_is_a_stub_creation_and_close_are_refused_before_any_consent_prompt(Verb verb)
    {
        using var dir = new TempDir();
        using var stdout = new MemoryStream();
        var stderr = new StringWriter();
        string? vault = verb == Verb.VaultInit ? null : Frames.Id3;

        ExitCode code = CliHost.Run(
            new ParsedCommand(verb, VaultId: vault, Confirm: true),
            Real(dir), stdout, new StringReader(""), stderr, interactive: false);

        // Reaching the launcher would show a UAC prompt and hang this test.
        Assert.Equal(ExitCode.Unsupported, code);
        Assert.Contains("\"code\":4", Encoding.UTF8.GetString(stdout.ToArray()));
        Assert.Contains("Vaults are unavailable", stderr.ToString());
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "journal")));
    }

    [Fact]
    public void The_stub_provider_never_passes_its_own_prerequisite_check()
    {
        PlatformResult r = new WindowsVaultPlatform().CheckPrerequisites();
        Assert.Equal(PlatformStatus.Unsupported, r.Status);
        Assert.False(string.IsNullOrEmpty(r.Detail));
    }
}

public class OwnerHostTests
{
    private static NamedPipeClientStream Connect(string pipe)
    {
        for (int i = 0; i < 50; i++)
        {
            NamedPipeClientStream? c = PipeEndpoints.Connect(pipe, 200);
            if (c is not null) return c;
        }
        throw new TimeoutException("the owner endpoint never accepted a connection");
    }

    private static ExitCode Ask(string pipe, string op, Guid id, CapturingSink sink)
    {
        using NamedPipeClientStream client = Connect(pipe);
        return HelperLauncher.RunOver(
            client, new WireRequest(Guid.NewGuid().ToString("D"), op, id.ToString("D"), op == Ops.VaultClose, null), sink);
    }

    private static (VaultService service, Guid id, string prefix, string pipe) Vault(VaultFixture f)
    {
        VaultService service = f.NewService();
        VaultOutcome made = service.Init(Native.CurrentUser().Value);
        Assert.Equal(ExitCode.Completed, made.Code);
        Guid id = Guid.Parse(made.Evidence.VaultId!);
        string prefix = "SSDShredder.test.owner." + PipeEndpoints.NewSuffix() + ".";
        return (service, id, prefix, PipeEndpoints.OwnerPipeName(prefix, id));
    }

    [Fact]
    public async Task A_client_that_came_and_went_before_the_owner_listened_does_not_end_the_owner()
    {
        using var f = new VaultFixture();
        var (service, id, prefix, pipe) = Vault(f);
        using OwnerHost owner = OwnerHost.Open(prefix, id);

        // The endpoint exists before the owner starts to listen. Anyone in the logon
        // session can open it and hang up in that gap.
        Connect(pipe).Dispose();

        var requests = new PrivilegedRequests(TestEnvironment.Simulated(f), service);
        Task serving = Task.Run(() => owner.Serve(service, requests, TimeSpan.FromSeconds(30)));

        var sink = new CapturingSink();
        Assert.Equal(ExitCode.Completed, Ask(pipe, Ops.VaultStatus, id, sink));
        Assert.Equal("ready", sink.Last.GetProperty("evidence").GetProperty("phase").GetString());
        Assert.True(service.OwnsLiveSession(id));

        Assert.Equal(ExitCode.Completed, Ask(pipe, Ops.VaultClose, id, sink));
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_client_that_connects_and_says_nothing_cannot_hold_the_owner_endpoint()
    {
        using var f = new VaultFixture();
        var (service, id, prefix, pipe) = Vault(f);
        using OwnerHost owner = OwnerHost.Open(prefix, id);
        var requests = new PrivilegedRequests(TestEnvironment.Simulated(f), service);
        Task serving = Task.Run(() => owner.Serve(
            service, requests, TimeSpan.FromSeconds(30), requestTimeout: TimeSpan.FromMilliseconds(300)));

        using NamedPipeClientStream silent = Connect(pipe);

        var sink = new CapturingSink();
        Assert.Equal(ExitCode.Completed, Ask(pipe, Ops.VaultStatus, id, sink));
        Assert.Equal("ready", sink.Last.GetProperty("evidence").GetProperty("phase").GetString());

        Assert.Equal(ExitCode.Completed, Ask(pipe, Ops.VaultClose, id, sink));
        Assert.Equal("retired", sink.Last.GetProperty("evidence").GetProperty("phase").GetString());
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

public class OwnerHostStressTests
{
    [Fact]
    public async Task The_owner_keeps_serving_through_many_silent_and_vanishing_clients()
    {
        using var f = new VaultFixture();
        VaultService service = f.NewService();
        Guid id = Guid.Parse(service.Init(Native.CurrentUser().Value).Evidence.VaultId!);
        string prefix = "SSDShredder.test.owner." + PipeEndpoints.NewSuffix() + ".";
        string pipe = PipeEndpoints.OwnerPipeName(prefix, id);
        using OwnerHost owner = OwnerHost.Open(prefix, id);
        var requests = new PrivilegedRequests(TestEnvironment.Simulated(f), service);
        Task serving = Task.Run(() => owner.Serve(
            service, requests, TimeSpan.FromSeconds(120), requestTimeout: TimeSpan.FromMilliseconds(40)));

        NamedPipeClientStream Open()
        {
            for (int i = 0; i < 100; i++)
            {
                NamedPipeClientStream? c = PipeEndpoints.Connect(pipe, 200);
                if (c is not null) return c;
            }
            throw new TimeoutException("the owner endpoint never accepted a connection");
        }

        ExitCode Send(string op, CapturingSink sink)
        {
            using NamedPipeClientStream client = Open();
            return HelperLauncher.RunOver(
                client, new WireRequest(Guid.NewGuid().ToString("D"), op, id.ToString("D"), op == Ops.VaultClose, null), sink);
        }

        for (int round = 0; round < 40; round++)
        {
            if (round % 3 == 0) Open().Dispose();              // comes and goes
            if (round % 2 == 0)
            {
                using NamedPipeClientStream silent = Open();   // says nothing until it is dropped
                var sink = new CapturingSink();
                Assert.Equal(ExitCode.Completed, Send(Ops.VaultStatus, sink));
                Assert.Equal("ready", sink.Last.GetProperty("evidence").GetProperty("phase").GetString());
            }
            else
            {
                var sink = new CapturingSink();
                Assert.Equal(ExitCode.Completed, Send(Ops.VaultStatus, sink));
            }
            Assert.False(serving.IsCompleted, "the owner stopped serving in round " + round);
        }

        var last = new CapturingSink();
        Assert.Equal(ExitCode.Completed, Send(Ops.VaultClose, last));
        Assert.Equal("retired", last.Last.GetProperty("evidence").GetProperty("phase").GetString());
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

public class PlaceholderProviderTests
{
    [Fact]
    public void A_provider_that_cannot_observe_never_lets_recovery_retire_a_record()
    {
        using var dir = new TempDir();
        var journal = new VaultJournal(dir.Path);
        var rec = new VaultRecord
        {
            VaultId = Guid.NewGuid(),
            OwnerSid = VaultFixture.Owner,
            Phase = VaultPhases.Closing,
            BackingPath = Path.Combine(dir.Path, "a.vhdx"),
            BackingFileId = "recorded",
            MountPath = Path.Combine(dir.Path, "mount"),
        };
        journal.Commit(rec);

        var service = new VaultService(journal, new WindowsVaultPlatform(),
            new VaultOptions { ImageDirectory = dir.Path, MountRoot = dir.Path });
        VaultOutcome o = service.Close(rec.VaultId, VaultFixture.Owner);

        // "Cannot look" is not "nothing is left".
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.Equal(VaultPhases.Closing, journal.Load(rec.VaultId)!.Phase);
        Assert.Null(journal.Load(rec.VaultId)!.Receipt);
    }
}
