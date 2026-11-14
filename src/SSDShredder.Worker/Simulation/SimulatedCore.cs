using SSDShredder.Worker.CoreAdapter;

namespace SSDShredder.Worker.Simulation;

/// <summary>Scripted behaviour of the simulated Core for one path.</summary>
public sealed class SimScenario
{
    public AccessStatus Access { get; set; } = AccessStatus.Ok;
    public string Reason { get; set; } = Reasons.Eligible;
    public string? Bus { get; set; } = "BusTypeNvme";
    public long Length { get; set; } = 4711;
    public RevalidateStatus Revalidate { get; set; } = RevalidateStatus.Ok;
    public TrimStatus Trim { get; set; } = TrimStatus.Completed;

    /// <summary>Ranges processed. Null means "all requested" when the trim completes.</summary>
    public int? RangesProcessed { get; set; }
    public bool TrimThrows { get; set; }
    public EntryOutcome Entry { get; set; } = EntryOutcome.Removed;

    public static SimScenario Named(string? name, long length) => (name ?? "eligible").ToLowerInvariant() switch
    {
        "eligible" => new SimScenario { Length = length },
        "unsupported" or "usb" => new SimScenario { Length = length, Reason = Reasons.Topology, Bus = "BusTypeUsb" },
        "partial" => new SimScenario { Length = length, Trim = TrimStatus.Partial, RangesProcessed = 0 },
        "busy" => new SimScenario { Length = length, Access = AccessStatus.Busy },
        "denied" => new SimScenario { Length = length, Access = AccessStatus.Denied },
        _ => new SimScenario { Length = length, Reason = Reasons.Unknown, Bus = null },
    };
}

/// <summary>
/// Stand-in for SSDShredder.Core. It never opens, changes or deletes a real file:
/// every outcome comes from the scenario.
/// </summary>
public sealed class SimulatedCore : IFileDisposalCore
{
    private readonly Func<string, SimScenario?> _resolve;
    private readonly long _pageSize;
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public SimulatedCore(Func<string, SimScenario?> resolve, long pageSize = 4096)
    {
        _resolve = resolve;
        _pageSize = pageSize;
    }

    public int TrimCalls { get; private set; }
    public int UnlinkCalls { get; private set; }
    public int ActiveHolds { get { lock (_gate) return _held.Count; } }

    public InspectResult Inspect(string path)
    {
        SimScenario? s = _resolve(path);
        if (s is null) return new InspectResult(AccessStatus.NotFound, null);
        if (s.Access != AccessStatus.Ok) return new InspectResult(s.Access, null);
        return new InspectResult(AccessStatus.Ok, Classify(s));
    }

    public AcquireResult Acquire(string path)
    {
        SimScenario? s = _resolve(path);
        if (s is null) return new AcquireResult(AccessStatus.NotFound, null, null);
        if (s.Access != AccessStatus.Ok) return new AcquireResult(s.Access, null, null);
        InspectFacts facts = Classify(s);
        if (!facts.Eligible) return new AcquireResult(AccessStatus.Ok, facts, null);
        lock (_gate)
        {
            if (!_held.Add(path)) return new AcquireResult(AccessStatus.Busy, null, null);
        }
        return new AcquireResult(AccessStatus.Ok, facts, new Held(this, path, s, facts));
    }

    public VolumeProbe ProbeVolume(string volumeGuidPath) => new(volumeGuidPath, null, null, null, null);

    private InspectFacts Classify(SimScenario s)
    {
        long covered = s.Length / _pageSize * _pageSize;
        long tail = s.Length - covered;
        string reason = s.Reason;
        if (reason == Reasons.Eligible && covered == 0) reason = Reasons.NoFullPage;
        return new InspectFacts(reason == Reasons.Eligible, reason, s.Bus, covered, tail);
    }

    private sealed class Held : IHeldFile
    {
        private readonly SimulatedCore _core;
        private readonly string _path;
        private readonly SimScenario _s;
        private bool _released;

        public Held(SimulatedCore core, string path, SimScenario s, InspectFacts facts)
        {
            _core = core;
            _path = path;
            _s = s;
            Facts = facts;
        }

        public InspectFacts Facts { get; }

        public RevalidateStatus Revalidate() => _s.Revalidate;

        public TrimResult Trim()
        {
            _core.TrimCalls++;
            if (_s.TrimThrows) throw new IOException("simulated transport failure during trim");
            return _s.Trim switch
            {
                TrimStatus.Completed => new TrimResult(TrimStatus.Completed, 1, _s.RangesProcessed ?? 1),
                TrimStatus.NotSubmitted => new TrimResult(TrimStatus.NotSubmitted, 1, null),
                TrimStatus.Partial => new TrimResult(TrimStatus.Partial, 1, _s.RangesProcessed ?? 0),
                _ => new TrimResult(TrimStatus.Unknown, 1, null),
            };
        }

        public EntryOutcome UnlinkAndClose()
        {
            _core.UnlinkCalls++;
            Dispose();
            return _s.Entry;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            lock (_core._gate) _core._held.Remove(_path);
        }
    }
}
