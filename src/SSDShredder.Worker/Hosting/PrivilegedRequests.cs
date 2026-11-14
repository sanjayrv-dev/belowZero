using SSDShredder.Worker.CoreAdapter;
using SSDShredder.Worker.Protocol;
using SSDShredder.Worker.Vault;

namespace SSDShredder.Worker.Hosting;

/// <summary>Maps the privileged wire verbs onto the vault service and the device probe.</summary>
public sealed class PrivilegedRequests
{
    private readonly WorkerEnvironment _env;
    private readonly VaultService _service;

    public PrivilegedRequests(WorkerEnvironment env, VaultService service)
    {
        _env = env;
        _service = service;
    }

    /// <summary>
    /// Runs after a vault became ready and before its result is reported, so the session
    /// owner is reachable by the time anyone learns the vault ID.
    /// </summary>
    public Action<Guid>? BeforeReadyResult { get; set; }

    /// <param name="callerSid">The authenticated user the request is made for.</param>
    /// <param name="onlyVault">When set, the one vault this endpoint serves.</param>
    public ExitCode Handle(WireRequest req, RequestEmitter emit, string callerSid, Guid? onlyVault)
    {
        switch (req.Op)
        {
            case Ops.VaultInit:
            {
                if (onlyVault is not null) return emit.Result(ExitCode.Denied, null);
                VaultOutcome o = _service.Init(callerSid, emit.Progress);
                if (o.Code == ExitCode.Completed && o.Evidence.VaultId is not null)
                {
                    BeforeReadyResult?.Invoke(Guid.Parse(o.Evidence.VaultId));
                }
                return emit.Result(o.Code, o.Evidence);
            }

            case Ops.VaultStatus:
            case Ops.VaultClose:
            {
                Guid id = Guid.Parse(req.Target!);
                if (onlyVault is not null && onlyVault != id)
                {
                    return emit.Result(ExitCode.Denied, new VaultEvidence(req.Target, VaultPhases.Unknown, null, null));
                }
                VaultOutcome o = req.Op == Ops.VaultStatus
                    ? _service.Status(id, callerSid)
                    : _service.Close(id, callerSid, emit.Progress);
                return emit.Result(o.Code, o.Evidence);
            }

            case Ops.ProbeVolume:
            {
                if (onlyVault is not null) return emit.Result(ExitCode.Denied, null);
                if (!_env.CoreAvailable) return emit.Result(ExitCode.Unsupported, new ProbeEvidence(req.Target!, null, null, null, null));
                VolumeProbe p = _env.Core.ProbeVolume(req.Target!);
                return emit.Result(ExitCode.Completed, new ProbeEvidence(p.VolumeGuid, p.DeviceCount, p.Bus, p.TrimEnabled, p.SeekPenalty));
            }

            default:
                return emit.Result(ExitCode.Invalid, null);
        }
    }
}
