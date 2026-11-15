using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SSDShredder.Worker.Hosting;
using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Tests;

public class QuickLaunchTests
{
    /// <summary>Stands in for the UI's ActivationRouter on a private test channel.</summary>
    private sealed class FakeOwner : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _received;

        public FakeOwner(string channel, bool accept = true)
        {
            string name = "bzo-ar1-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(channel)))[..10];
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await server.WaitForConnectionAsync(_cts.Token);
                        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                        string? line = await reader.ReadLineAsync(_cts.Token);
                        if (line is null) continue;
                        using JsonDocument doc = JsonDocument.Parse(line);
                        bool shaped = doc.RootElement.GetProperty("action").GetString() == "open"
                            && doc.RootElement.GetProperty("source").GetString() == "terminal"
                            && doc.RootElement.GetProperty("paths").GetArrayLength() == 0;
                        if (shaped) Interlocked.Increment(ref _received);
                        string ack = JsonSerializer.Serialize(new { v = 1, id = doc.RootElement.GetProperty("id").GetString(), accepted = accept && shaped }) + "\n";
                        await server.WriteAsync(Encoding.UTF8.GetBytes(ack), _cts.Token);
                        await server.FlushAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }
            });
        }

        public int Received => Volatile.Read(ref _received);

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop.Wait(2000); } catch (AggregateException) { }
        }
    }

    private static string Channel() => "bzo-test-" + Guid.NewGuid().ToString("N");

    private static string FakeUi(TempDir dir)
    {
        string exe = Path.Combine(dir.Path, "SSDShredder.UI.exe");
        File.WriteAllText(exe, "");
        return exe;
    }

    [Fact]
    public void A_running_panel_is_activated_and_not_started_again()
    {
        string channel = Channel();
        using var owner = new FakeOwner(channel);
        bool started = false;

        ExitCode code = QuickLaunch.Run(new StringWriter(), channel, uiPath: null, start: _ => started = true);

        Assert.Equal(ExitCode.Completed, code);
        Assert.False(started);
        Assert.Equal(1, owner.Received);
    }

    [Fact]
    public void An_absent_panel_is_started_and_the_exit_code_waits_for_its_acknowledgement()
    {
        string channel = Channel();
        using var ui = new TempDir();
        string exe = FakeUi(ui);
        FakeOwner? owner = null;
        try
        {
            ExitCode code = QuickLaunch.Run(new StringWriter(), channel, exe, start: path =>
            {
                Assert.Equal(exe, path);
                // The new instance takes a moment to start listening.
                Task.Run(() =>
                {
                    Thread.Sleep(400);
                    owner = new FakeOwner(channel);
                });
                return true;
            }, wait: TimeSpan.FromSeconds(10));

            Assert.Equal(ExitCode.Completed, code);
        }
        finally
        {
            owner?.Dispose();
        }
    }

    [Fact]
    public void A_panel_that_cannot_be_started_is_a_launch_failure()
    {
        using var ui = new TempDir();
        string exe = FakeUi(ui);
        var err = new StringWriter();

        Assert.Equal(ExitCode.Failed, QuickLaunch.Run(err, Channel(), exe, start: _ => false));
        Assert.Equal(ExitCode.Failed, QuickLaunch.Run(err, Channel(), exe, start: _ => throw new System.ComponentModel.Win32Exception(2)));
        Assert.Contains("could not be started", err.ToString());
    }

    [Fact]
    public void A_missing_panel_executable_is_a_launch_failure_and_starts_nothing()
    {
        bool started = false;
        var err = new StringWriter();
        ExitCode code = QuickLaunch.Run(err, Channel(), Path.Combine(Path.GetTempPath(), "no-such-ui.exe"), start: _ => started = true);

        Assert.Equal(ExitCode.Failed, code);
        Assert.False(started);
        Assert.Contains("not installed", err.ToString());
    }

    [Fact]
    public void A_panel_that_starts_but_never_answers_is_a_launch_failure()
    {
        using var ui = new TempDir();
        string exe = FakeUi(ui);
        var err = new StringWriter();

        Assert.Equal(ExitCode.Failed, QuickLaunch.Run(err, Channel(), exe, start: _ => true, wait: TimeSpan.FromMilliseconds(600)));
        Assert.Contains("did not accept", err.ToString());
    }

    [Fact]
    public void A_panel_that_refuses_the_activation_is_a_launch_failure()
    {
        string channel = Channel();
        using var owner = new FakeOwner(channel, accept: false);
        using var ui = new TempDir();
        string exe = FakeUi(ui);

        Assert.Equal(ExitCode.Failed, QuickLaunch.Run(new StringWriter(), channel, exe, start: _ => true, wait: TimeSpan.FromMilliseconds(800)));
    }
}

/// <summary>The branded executable is the same program: its verbs keep their events and exit codes.</summary>
public class BzoExecutableTests
{
    private static string Exe => Path.Combine(AppContext.BaseDirectory, "bzo.exe");

    private static (int code, string stdout, string stderr) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["SSDSHREDDER_SIMULATE"] = "1";
        psi.Environment["SSDSHREDDER_SIM_ROOT"] = Path.Combine(Path.GetTempPath(), "ssdshredder-tests", "bzo-" + Guid.NewGuid().ToString("N"));
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        string err = p.StandardError.ReadToEnd();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, err);
    }

    [Fact]
    public void bzo_exe_is_built_beside_the_worker_executable()
    {
        Assert.True(File.Exists(Exe));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "SSDShredder.Worker.exe")));
    }

    [Fact]
    public void An_operation_flag_keeps_its_events_and_exit_code()
    {
        using var dir = new TempDir();
        string file = Path.Combine(dir.Path, "f.bin");
        File.WriteAllBytes(file, new byte[10000]);

        var (code, stdout, _) = Run("--inspect", file);

        Assert.Equal(0, code);
        string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"type\":\"progress\",\"phase\":\"inspecting\"", lines[0]);
        Assert.Contains("\"type\":\"result\",\"code\":0", lines[^1]);
    }

    [Fact]
    public void A_bad_command_line_still_exits_2_with_usage()
    {
        var (code, stdout, stderr) = Run("--inspect", "relative.bin");

        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.Contains("Usage:", stderr);
    }
}
