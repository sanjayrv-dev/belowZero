using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Ipc;

/// <summary>
/// Launches the fixed helper (this same executable with --ipc), binds a one-request pipe to
/// it, sends one request and relays its events.
/// </summary>
public static class HelperLauncher
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <param name="elevate">True asks Windows for consent (runas). False starts the helper at the caller's level.</param>
    /// <returns>The terminal code. A result event is always written to <paramref name="output"/>.</returns>
    public static ExitCode Run(WireRequest request, IFrameSink output, bool elevate, string pipePrefix) =>
        Run(request, output, elevate, pipePrefix, Start);

    /// <summary>Starts the helper and returns its process handle, or 0 with the Win32 error.</summary>
    internal delegate nint ProcessStarter(string exe, string arguments, bool elevate, out int error);

    internal static ExitCode Run(WireRequest request, IFrameSink output, bool elevate, string pipePrefix, ProcessStarter start)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        string suffix = PipeEndpoints.NewSuffix();
        var relay = new Relay(output, request);

        using NamedPipeServerStream server = PipeEndpoints.CreateServer(pipePrefix + suffix);

        nint process = start(exe, $"--ipc {suffix} --parent-pid {Environment.ProcessId}", elevate, out int error);
        if (process == 0)
        {
            // 1223: the user declined the consent prompt. No helper ever existed.
            return relay.Synthesize(error == Native.ErrorCancelled ? ExitCode.Cancelled : ExitCode.Failed);
        }

        try
        {
            uint helperPid = Native.GetProcessId(process);
            bool connected = PipeEndpoints.WaitForConnection(
                server, ConnectTimeout, abandon: () => Native.WaitForSingleObject(process, 0) == Native.WaitObject0);
            if (!connected) return relay.Synthesize(ExitCode.Failed);

            // The peer must be the live process we started, running this image, as this user, in this session.
            PeerExpectation expected = PeerVerifier.SelfPeer(helperPid, exactImage: true);
            if (PeerVerifier.VerifyClient(server, expected) is not null) return relay.Synthesize(ExitCode.Denied);

            byte[] line = Serialize(request);
            try
            {
                server.Write(line);
                server.Flush();
            }
            catch (IOException)
            {
                return relay.Synthesize(ExitCode.Failed);
            }

            return relay.Pump(server);
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    /// <summary>Sends one request over an already connected, already verified stream.</summary>
    public static ExitCode RunOver(Stream stream, WireRequest request, IFrameSink output)
    {
        var relay = new Relay(output, request);
        try
        {
            stream.Write(Serialize(request));
            stream.Flush();
        }
        catch (IOException)
        {
            return relay.Synthesize(ExitCode.Failed);
        }
        return relay.Pump(stream);
    }

    public static byte[] Serialize(WireRequest r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Wire.Version);
            w.WriteString("id", r.Id);
            w.WriteString("op", r.Op);
            w.WriteString("target", r.Target);
            w.WriteBoolean("confirmed", r.Confirmed);
            if (r.SelectionToken is not null) w.WriteString("selectionToken", r.SelectionToken);
            w.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    private static unsafe nint Start(string exe, string arguments, bool elevate, out int error)
    {
        error = 0;
        nint verb = Marshal.StringToHGlobalUni(elevate ? "runas" : "open");
        nint file = Marshal.StringToHGlobalUni(exe);
        nint args = Marshal.StringToHGlobalUni(arguments);
        try
        {
            var info = new Native.ShellExecuteInfo
            {
                cbSize = (uint)sizeof(Native.ShellExecuteInfo),
                fMask = Native.SeeMaskNoCloseProcess | Native.SeeMaskNoAsync | Native.SeeMaskFlagNoUi,
                lpVerb = verb,
                lpFile = file,
                lpParameters = args,
                nShow = 0, // SW_HIDE
            };
            if (Native.ShellExecuteEx(ref info) == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return 0;
            }
            return info.hProcess;
        }
        finally
        {
            Marshal.FreeHGlobal(verb);
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(args);
        }
    }

    /// <summary>Forwards a helper's events and guarantees exactly one terminal result.</summary>
    private sealed class Relay
    {
        private readonly IFrameSink _output;
        private readonly WireRequest _request;
        private int _lastSeq;

        public Relay(IFrameSink output, WireRequest request)
        {
            _output = output;
            _request = request;
        }

        public ExitCode Pump(Stream stream)
        {
            var reader = new FrameReader(stream);
            while (true)
            {
                FrameStatus status;
                byte[] frame;
                try
                {
                    status = reader.Read(out frame);
                }
                catch (IOException)
                {
                    status = FrameStatus.End;
                    frame = [];
                }
                if (status != FrameStatus.Frame) return Synthesize(LostCode());

                if (!TryReadEvent(frame, out int seq, out ExitCode? result)) return Synthesize(LostCode());
                _lastSeq = seq;
                Forward(frame);
                if (result is not null) return result.Value;
            }
        }

        /// <summary>A confirmed request that lost its completion is never replayed.</summary>
        private ExitCode LostCode() => _request.Op switch
        {
            Ops.VaultInit or Ops.VaultClose => ExitCode.Recovery,
            Ops.Shred => ExitCode.Indeterminate,
            _ => ExitCode.Failed,
        };

        public ExitCode Synthesize(ExitCode code)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("v", Wire.Version);
                w.WriteString("id", _request.Id);
                w.WriteNumber("seq", ++_lastSeq);
                w.WriteString("type", "result");
                w.WriteNumber("code", (int)code);
                w.WriteString("state", Wire.StateName(code));
                w.WriteNull("evidence");
                w.WriteEndObject();
            }
            Forward(ms.ToArray());
            return code;
        }

        private void Forward(byte[] frame)
        {
            byte[] line = new byte[frame.Length + 1];
            frame.CopyTo(line, 0);
            line[^1] = (byte)'\n';
            try
            {
                _output.Write(line);
            }
            catch (IOException)
            {
            }
        }

        private bool TryReadEvent(byte[] frame, out int seq, out ExitCode? result)
        {
            seq = 0;
            result = null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(frame);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (root.GetProperty("v").GetInt32() != Wire.Version) return false;
                if (root.GetProperty("id").GetString() != _request.Id) return false;
                seq = root.GetProperty("seq").GetInt32();
                if (seq != _lastSeq + 1) return false;
                string? type = root.GetProperty("type").GetString();
                if (type == "progress") return true;
                if (type != "result") return false;
                int code = root.GetProperty("code").GetInt32();
                if (!Enum.IsDefined(typeof(ExitCode), code)) return false;
                result = (ExitCode)code;
                return true;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return false;
            }
        }
    }
}
