using System.Collections.Concurrent;
using System.Security.Cryptography;
using SSDShredder.Worker.Protocol;
using SSDShredder.Worker.Simulation;

namespace SSDShredder.Worker.Vault;

public sealed class VaultOptions
{
    public required string ImageDirectory { get; init; }
    public required string MountRoot { get; init; }
    public long CapacityBytes { get; init; } = 1L << 30;

    /// <summary>7 is XTS-AES-256, 6 is XTS-AES-128.</summary>
    public int EncryptionMethod { get; init; } = 7;

    /// <summary>Whether recovery may retire a verified orphan on its own.</summary>
    public bool OrphanRetirementOptIn { get; init; }

    /// <summary>
    /// Allows detach, private read-only reattach and lock observation when Lock is refused
    /// for want of a recovery protector. Enable only after qualifying it on the target build.
    /// </summary>
    public bool AllowDetachCloseRoute { get; init; }
    public TimeSpan EncryptPoll { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan EncryptTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(30);
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;
}

public sealed record VaultOutcome(ExitCode Code, VaultEvidence Evidence);

/// <summary>
/// Vault lifecycle: creating, ready, closing, retired, orphan. Each mutation is preceded by
/// a durable intent and followed by a durable completion. An instance that created a vault
/// owns its live session: the attachment handle and the transient passphrase.
/// </summary>
public sealed class VaultService
{
    private sealed class Session
    {
        public IVaultAttachment? Attachment;
        public char[]? Passphrase;
        public bool Detached;

        public void ClearSecret()
        {
            if (Passphrase is not null) Array.Clear(Passphrase);
            Passphrase = null;
        }
    }

    private readonly VaultJournal _journal;
    private readonly IVaultPlatform _platform;
    private readonly VaultOptions _o;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public VaultService(VaultJournal journal, IVaultPlatform platform, VaultOptions options, TimeProvider? time = null)
    {
        _journal = journal;
        _platform = platform;
        _o = options;
        _time = time ?? TimeProvider.System;
    }

    public bool OwnsLiveSession(Guid id) => _sessions.ContainsKey(id);

    // ---------------------------------------------------------------- init

    public VaultOutcome Init(string ownerSid, Action<string>? progress = null)
    {
        PlatformResult pre = _platform.CheckPrerequisites();
        if (!pre.IsOk)
        {
            return new VaultOutcome(RefusalCode(pre.Status), new VaultEvidence(null, VaultPhases.Unknown, null, null));
        }

        Guid id = Guid.NewGuid();
        var rec = new VaultRecord
        {
            VaultId = id,
            OwnerSid = ownerSid,
            Phase = VaultPhases.Creating,
            OrphanRetirementOptIn = _o.OrphanRetirementOptIn,
            BackingPath = Path.Combine(_o.ImageDirectory, id.ToString("D") + ".vhdx"),
            MountPath = Path.Combine(_o.MountRoot, id.ToString("D")),
        };

        using IDisposable? vaultLock = _journal.TryLock(id, _o.LockWait);
        if (vaultLock is null) return new VaultOutcome(ExitCode.Busy, Evidence(rec));

        var session = new Session();
        bool mountAttempted = false;
        try
        {
            Begin(rec, "create-image");
            progress?.Invoke(Phases.Creating);

            PlatformResult r = _platform.CreateImage(rec.BackingPath, _o.CapacityBytes, out ImageIdentity? image);
            if (!r.IsOk || image is null) return FailInit(rec, session, mountAttempted, r);
            rec.BackingFileId = image.BackingFileId;
            rec.HostVolumeId = image.HostVolumeId;
            rec.VhdId = image.VhdId;
            Done(rec);

            Begin(rec, "attach");
            r = _platform.Attach(rec.BackingPath, image, readOnly: false, out IVaultAttachment? attachment);
            if (!r.IsOk || attachment is null) return FailInit(rec, session, mountAttempted, r);
            session.Attachment = attachment;
            Done(rec);

            Begin(rec, "format");
            r = _platform.InitializeAndFormat(attachment, out VolumeIdentity? volume);
            if (!r.IsOk || volume is null) return FailInit(rec, session, mountAttempted, r);
            rec.GptDiskId = volume.GptDiskId;
            rec.PartitionId = volume.PartitionId;
            rec.VolumeGuid = volume.VolumeGuid;
            Done(rec);

            progress?.Invoke(Phases.Encrypting);
            Begin(rec, "protect");
            session.Passphrase = GeneratePassphrase();
            r = _platform.Protect(volume.VolumeGuid, session.Passphrase);
            if (!r.IsOk) return FailInit(rec, session, mountAttempted, r);
            Done(rec);

            Begin(rec, "encrypt");
            r = _platform.StartEncryption(volume.VolumeGuid, _o.EncryptionMethod);
            if (!r.IsOk) return FailInit(rec, session, mountAttempted, r);
            EncryptionState? state;
            DateTimeOffset deadline = _time.GetUtcNow() + _o.EncryptTimeout;
            while (true)
            {
                r = _platform.QueryEncryption(volume.VolumeGuid, out state);
                if (!r.IsOk || state is null) return FailInit(rec, session, mountAttempted, r);
                if (state.Conversion == 1) break;
                if (_time.GetUtcNow() >= deadline)
                {
                    return FailInit(rec, session, mountAttempted, new PlatformResult(PlatformStatus.Failed, "encryption timed out"));
                }
                _o.Sleep(_o.EncryptPoll);
            }
            Done(rec);

            // Readiness gate: nothing is published unless every fact is verified.
            Begin(rec, "qualify");
            bool qualified = state.Conversion == 1
                && state.Protection == 1
                && state.Method == _o.EncryptionMethod
                && state.Method is 6 or 7
                && state.ProtectorTypes is [8]
                && state.AutoUnlock == false;
            if (!qualified)
            {
                return FailInit(rec, session, mountAttempted, new PlatformResult(PlatformStatus.Failed, "readiness gate failed"));
            }
            rec.Ready = new VaultReadyFacts("fullyEncrypted", true, [8], false, state.Method!.Value);
            Done(rec);

            Begin(rec, "mount");
            mountAttempted = true;
            r = _platform.Mount(volume.VolumeGuid, rec.MountPath, ownerSid);
            if (!r.IsOk) return FailInit(rec, session, mountAttempted, r);

            rec.Phase = VaultPhases.Ready;
            rec.Step = null;
            rec.StepDone = true;
            Save(rec);
            _sessions[id] = session;
            return new VaultOutcome(ExitCode.Completed, Evidence(rec));
        }
        catch (Exception e) when (e is not SimulatedTerminationException)
        {
            return FailInit(rec, session, mountAttempted, new PlatformResult(PlatformStatus.Failed, e.GetType().Name));
        }
    }

    /// <summary>Undoes a failed creation. Retires the vault only if every undo step is verified.</summary>
    private VaultOutcome FailInit(VaultRecord rec, Session session, bool mountAttempted, PlatformResult cause)
    {
        bool clean = true;
        string detail = cause.Detail ?? cause.Status.ToString();
        try
        {
            rec.Step = "rollback";
            rec.StepDone = false;
            Save(rec);

            if (mountAttempted && rec.VolumeGuid is not null)
            {
                clean &= _platform.Unmount(rec.VolumeGuid, rec.MountPath).IsOk;
            }
            if (session.Attachment is not null)
            {
                clean &= _platform.Detach(session.Attachment).IsOk;
                session.Attachment.Dispose();
                session.Attachment = null;
            }
            session.ClearSecret();

            if (clean)
            {
                if (rec.BackingFileId is not null)
                {
                    clean = _platform.DeleteBackingFile(rec.BackingPath, rec.BackingFileId).IsOk;
                }
                else
                {
                    // The image identity was never recorded. Anything on disk is not provably ours.
                    clean = !_platform.Observe(rec).BackingExists;
                }
            }
        }
        catch (Exception e) when (e is not SimulatedTerminationException)
        {
            clean = false;
        }

        ExitCode code;
        if (clean)
        {
            code = RefusalCode(cause.Status);
            rec.Phase = VaultPhases.Retired;
            rec.Step = null;
            rec.StepDone = true;
            rec.Receipt = new VaultReceipt((int)code, null, _time.GetUtcNow(), "creation failed and was undone: " + detail);
        }
        else
        {
            code = ExitCode.Recovery;
            rec.Phase = VaultPhases.Orphan;
            rec.ReviewReason = "creation failed and cleanup was not verified: " + detail;
        }
        Save(rec);
        return new VaultOutcome(code, Evidence(rec));
    }

    // -------------------------------------------------------------- status

    public VaultOutcome Status(Guid id, string callerSid)
    {
        VaultRecord? rec;
        try
        {
            rec = _journal.Load(id);
        }
        catch (JournalCorruptException)
        {
            return new VaultOutcome(ExitCode.Recovery, Unknown(id));
        }
        if (rec is null || !SameSid(rec.OwnerSid, callerSid)) return new VaultOutcome(ExitCode.Denied, Unknown(id));
        return new VaultOutcome(ExitCode.Completed, Evidence(rec));
    }

    // --------------------------------------------------------------- close

    public VaultOutcome Close(Guid id, string callerSid, Action<string>? progress = null)
    {
        VaultRecord? rec;
        try
        {
            rec = _journal.Load(id);
        }
        catch (JournalCorruptException)
        {
            return new VaultOutcome(ExitCode.Recovery, Unknown(id));
        }
        if (rec is null || !SameSid(rec.OwnerSid, callerSid)) return new VaultOutcome(ExitCode.Denied, Unknown(id));

        using IDisposable? vaultLock = _journal.TryLock(id, _o.LockWait);
        if (vaultLock is null) return new VaultOutcome(ExitCode.Busy, Evidence(rec));

        try
        {
            rec = _journal.Load(id);
        }
        catch (JournalCorruptException)
        {
            return new VaultOutcome(ExitCode.Recovery, Unknown(id));
        }
        if (rec is null) return new VaultOutcome(ExitCode.Denied, Unknown(id));

        // A retired vault answers every further close with its durable receipt.
        if (rec.Phase == VaultPhases.Retired) return new VaultOutcome(ExitCode.Completed, Evidence(rec));

        bool closable = rec.Phase is VaultPhases.Ready or VaultPhases.Closing;
        if (!closable || !_sessions.TryGetValue(id, out Session? session) || session.Attachment is null)
        {
            progress?.Invoke(Phases.Reconciling);
            return ReconcileLocked(rec);
        }

        try
        {
            return CloseLocked(rec, session, progress);
        }
        catch (Exception e) when (e is not SimulatedTerminationException)
        {
            return Stuck(rec, "close failed: " + e.GetType().Name);
        }
    }

    private VaultOutcome CloseLocked(VaultRecord rec, Session session, Action<string>? progress)
    {
        string volume = rec.VolumeGuid!;
        IVaultAttachment attachment = session.Attachment!;
        progress?.Invoke(Phases.Locking);

        bool resumed = rec.Phase == VaultPhases.Closing;
        rec.Phase = VaultPhases.Closing;
        Begin(rec, "quiesce");
        PlatformResult r = session.Detached ? PlatformResult.Ok : _platform.Quiesce(attachment, volume);
        if (r.Status == PlatformStatus.Busy)
        {
            // A close that was already under way may have removed the mount. It cannot go back to ready.
            if (resumed) return Stuck(rec, "volume busy while resuming a close");

            // A busy volume stays open. User handles are never forced closed.
            rec.Phase = VaultPhases.Ready;
            rec.Step = null;
            rec.StepDone = true;
            Save(rec);
            return new VaultOutcome(ExitCode.Busy, Evidence(rec));
        }
        if (!r.IsOk) return Stuck(rec, "quiesce failed");
        Done(rec);

        Begin(rec, "unmount");
        if (!_platform.Unmount(volume, rec.MountPath).IsOk) return Stuck(rec, "mount removal was not verified");
        Done(rec);

        if (rec.LockedObserved != true)
        {
            Begin(rec, "lock");
            r = session.Detached ? new PlatformResult(PlatformStatus.RecoveryKeyRequired) : _platform.Lock(volume);
            if (r.IsOk)
            {
                if (!_platform.GetLockStatus(volume, out int? status).IsOk || status != 1) return Stuck(rec, "lock was not observed");
            }
            else if (r.Status == PlatformStatus.RecoveryKeyRequired && _o.AllowDetachCloseRoute)
            {
                if (!ObserveLockByReattach(rec, session)) return Stuck(rec, "detach route did not observe a locked volume");
            }
            else
            {
                return Stuck(rec, "lock failed");
            }
            rec.LockedObserved = true;
            Done(rec);
        }

        Begin(rec, "detach");
        if (!session.Detached)
        {
            if (!_platform.Detach(attachment).IsOk) return Stuck(rec, "detach was not verified");
            session.Detached = true;
        }
        attachment.Dispose();
        session.ClearSecret();
        Done(rec);

        Begin(rec, "retire");
        if (!_platform.DeleteBackingFile(rec.BackingPath, rec.BackingFileId!).IsOk) return Stuck(rec, "backing file retirement was not verified");

        _sessions.TryRemove(rec.VaultId, out _);
        Retire(rec, ExitCode.Completed, "closed by its owner");
        return new VaultOutcome(ExitCode.Completed, Evidence(rec));
    }

    /// <summary>
    /// Detaches under quiescence, reattaches privately read-only, rediscovers the volume by
    /// identity and requires GetLockStatus = 1.
    /// </summary>
    private bool ObserveLockByReattach(VaultRecord rec, Session session)
    {
        if (!session.Detached)
        {
            if (!_platform.Detach(session.Attachment!).IsOk) return false;
            session.Detached = true;
        }
        return ReattachAndObserveLock(rec);
    }

    private bool ReattachAndObserveLock(VaultRecord rec)
    {
        var identity = new ImageIdentity(rec.BackingFileId!, rec.HostVolumeId ?? "", rec.VhdId ?? "");
        if (!_platform.Attach(rec.BackingPath, identity, readOnly: true, out IVaultAttachment? probe).IsOk || probe is null) return false;
        bool locked = false;
        try
        {
            if (_platform.FindVolume(probe, out VolumeIdentity? found).IsOk
                && found is not null
                && string.Equals(found.VolumeGuid, rec.VolumeGuid, StringComparison.OrdinalIgnoreCase)
                && string.Equals(found.GptDiskId, rec.GptDiskId, StringComparison.OrdinalIgnoreCase)
                && _platform.GetLockStatus(found.VolumeGuid, out int? status).IsOk
                && status == 1)
            {
                locked = true;
            }
        }
        finally
        {
            bool detached = _platform.Detach(probe).IsOk;
            probe.Dispose();
            locked &= detached;
        }
        return locked;
    }

    // ----------------------------------------------------------- reconcile

    /// <summary>Runs recovery over every vault that has no verified terminal state.</summary>
    public IReadOnlyList<VaultOutcome> ReconcileAll()
    {
        var results = new List<VaultOutcome>();
        foreach (Guid id in _journal.ListIds())
        {
            using IDisposable? vaultLock = _journal.TryLock(id, TimeSpan.FromMilliseconds(200));
            if (vaultLock is null) continue;
            VaultRecord? rec;
            try
            {
                rec = _journal.Load(id);
            }
            catch (JournalCorruptException)
            {
                results.Add(new VaultOutcome(ExitCode.Recovery, Unknown(id)));
                continue;
            }
            if (rec is null || rec.Phase == VaultPhases.Retired) continue;
            results.Add(ReconcileLocked(rec));
        }
        return results;
    }

    /// <summary>
    /// Recovery for one vault whose lock is held. Retires only what is recorded, verified
    /// and opted in. Anything uncertain is preserved for review.
    /// </summary>
    private VaultOutcome ReconcileLocked(VaultRecord rec)
    {
        if (rec.Phase == VaultPhases.Ready && _sessions.ContainsKey(rec.VaultId))
        {
            return new VaultOutcome(ExitCode.Completed, Evidence(rec));
        }

        try
        {
            Observation obs = _platform.Observe(rec);

            if (obs.Attached != false && obs.BackingExists)
            {
                // Something still holds the image, possibly a live owner. Change nothing.
                return new VaultOutcome(ExitCode.Recovery, Evidence(rec));
            }

            if (!obs.BackingExists && obs.MountedVolumeGuid is null)
            {
                Retire(rec, ExitCode.Completed, "reconciled: no owned objects remained");
                return new VaultOutcome(ExitCode.Completed, Evidence(rec));
            }

            string? keep = null;
            if (!obs.BackingExists) keep = "mount folder resolves to a volume but the image is gone";
            else if (rec.BackingFileId is null) keep = "image identity was never recorded";
            else if (!string.Equals(obs.BackingFileId, rec.BackingFileId, StringComparison.Ordinal)) keep = "backing file identity does not match the record";
            else if (obs.OpenHandles != false) keep = "open handles, or handle state unknown";
            else if (obs.MountedVolumeGuid is not null
                && !string.Equals(obs.MountedVolumeGuid, rec.VolumeGuid, StringComparison.OrdinalIgnoreCase)) keep = "mount folder resolves to a different volume";
            else if (!rec.OrphanRetirementOptIn) keep = "automatic orphan retirement is not enabled for this vault";

            if (keep is null && obs.MountedVolumeGuid is not null)
            {
                if (!_platform.Unmount(rec.VolumeGuid!, rec.MountPath).IsOk) keep = "stale mount could not be removed";
            }

            if (keep is null && rec.Ready is not null && rec.LockedObserved != true)
            {
                // The vault was published, so it may hold data. Require a locked observation.
                if (ReattachAndObserveLock(rec)) rec.LockedObserved = true;
                else keep = "lock state could not be verified";
            }

            if (keep is null && !_platform.DeleteBackingFile(rec.BackingPath, rec.BackingFileId!).IsOk)
            {
                keep = "backing file retirement was not verified";
            }

            if (keep is not null)
            {
                rec.Phase = VaultPhases.Orphan;
                rec.ReviewReason = keep;
                Save(rec);
                return new VaultOutcome(ExitCode.Recovery, Evidence(rec));
            }

            Retire(rec, ExitCode.Completed, "reconciled after an interrupted transition");
            return new VaultOutcome(ExitCode.Completed, Evidence(rec));
        }
        catch (Exception e) when (e is not SimulatedTerminationException)
        {
            return new VaultOutcome(ExitCode.Recovery, Evidence(rec));
        }
    }

    // ------------------------------------------------------------- helpers

    private void Retire(VaultRecord rec, ExitCode code, string detail)
    {
        rec.Phase = VaultPhases.Retired;
        rec.Step = null;
        rec.StepDone = true;
        rec.ReviewReason = null;
        rec.Receipt = new VaultReceipt((int)code, rec.LockedObserved, _time.GetUtcNow(), detail);
        Save(rec);
    }

    private VaultOutcome Stuck(VaultRecord rec, string reason)
    {
        rec.ReviewReason = reason;
        Save(rec);
        return new VaultOutcome(ExitCode.Recovery, Evidence(rec));
    }

    private void Begin(VaultRecord rec, string step)
    {
        rec.Step = step;
        rec.StepDone = false;
        Save(rec);
    }

    private void Done(VaultRecord rec)
    {
        rec.StepDone = true;
        Save(rec);
    }

    private void Save(VaultRecord rec)
    {
        rec.UpdatedUtc = _time.GetUtcNow();
        _journal.Commit(rec);
    }

    private static VaultEvidence Evidence(VaultRecord rec)
    {
        bool ready = rec.Phase == VaultPhases.Ready;
        return new VaultEvidence(
            rec.VaultId.ToString("D"),
            rec.Phase,
            ready ? rec.MountPath : null,
            rec.LockedObserved,
            ready ? rec.Ready : null);
    }

    private static VaultEvidence Unknown(Guid id) => new(id.ToString("D"), VaultPhases.Unknown, null, null);

    private static ExitCode RefusalCode(PlatformStatus status) =>
        status is PlatformStatus.Unsupported or PlatformStatus.PolicyRefused ? ExitCode.Unsupported : ExitCode.Failed;

    private static bool SameSid(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>32 bytes from the OS CSPRNG, hex-encoded. Held in memory only.</summary>
    private static char[] GeneratePassphrase()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[64];
        Convert.TryToHexStringLower(bytes, chars, out _);
        CryptographicOperations.ZeroMemory(bytes);
        return chars;
    }
}
