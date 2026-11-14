using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Operations;

/// <summary>Handles one privileged request (vault verbs, probe-volume) and emits its result.</summary>
public delegate ExitCode PrivilegedHandler(WireRequest request, RequestEmitter emit);

/// <summary>
/// Serves one authenticated connection. File verbs may repeat: inspect, then one confirmed
/// shred with the token from that inspect. A privileged request is one-shot and ends the
/// connection. Frames with no trustworthy request ID end the connection without executing.
/// </summary>
public sealed class ConnectionSession
{
    private readonly Stream _input;
    private readonly IFrameSink _sink;
    private readonly FileOperations? _files;
    private readonly PrivilegedHandler? _privileged;
    private readonly TimeProvider _time;

    public ConnectionSession(
        Stream input, IFrameSink sink, FileOperations? files, PrivilegedHandler? privileged, TimeProvider? time = null)
    {
        _input = input;
        _sink = sink;
        _files = files;
        _privileged = privileged;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Number of requests that reached a handler.</summary>
    public int Executed { get; private set; }

    public void Run()
    {
        var reader = new FrameReader(_input);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool shredUsed = false;
        using var store = new SelectionTokenStore(_time);

        while (true)
        {
            FrameStatus status;
            byte[] frame;
            try
            {
                status = reader.Read(out frame);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                return;
            }
            if (status != FrameStatus.Frame) return;

            ParseResult parsed = RequestParser.Parse(frame);
            if (parsed.Status == ParseStatus.Fatal) return;
            if (!seen.Add(parsed.Id!)) return;

            var emit = new RequestEmitter(_sink, parsed.Id!);
            if (parsed.Status == ParseStatus.Reject)
            {
                emit.Result(ExitCode.Invalid, null);
                continue;
            }

            WireRequest req = parsed.Request!;
            bool endAfter = false;
            try
            {
                if (Ops.IsPrivilegedOp(req.Op))
                {
                    endAfter = true;
                    if (_privileged is null)
                    {
                        emit.Result(ExitCode.Invalid, null);
                    }
                    else
                    {
                        Executed++;
                        _privileged(req, emit);
                    }
                }
                else if (_files is null)
                {
                    emit.Result(ExitCode.Invalid, null);
                }
                else if (req.Op == Ops.Inspect)
                {
                    Executed++;
                    _files.InspectAndHold(req.Target!, emit, store);
                }
                else if (req.SelectionToken is null || shredUsed)
                {
                    // A connection shred needs the token of a held selection, once.
                    emit.Result(ExitCode.Invalid, null);
                }
                else
                {
                    shredUsed = true;
                    Executed++;
                    _files.ShredHeld(req.Target!, req.SelectionToken, emit, store);
                }
            }
            catch (Exception)
            {
                if (!emit.Completed)
                {
                    emit.Result(req.Op is Ops.VaultInit or Ops.VaultClose ? ExitCode.Recovery : ExitCode.Failed, null);
                }
            }

            if (endAfter || emit.TransportLost) return;
        }
    }
}
