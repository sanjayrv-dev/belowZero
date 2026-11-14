using System.Diagnostics;
using System.Text.Json;
using SSDShredder.Worker.Vault;

namespace SSDShredder.Worker.Simulation;

/// <summary>Stands for the worker process dying at a chosen point. Nothing catches it.</summary>
public sealed class SimulatedTerminationException : Exception
{
    public SimulatedTerminationException(string step) : base("simulated termination at " + step) { }
}

public sealed class SimImage
{
    public string Path { get; set; } = "";
    public string FileId { get; set; } = "";
    public string HostVolumeId { get; set; } = "";
    public string VhdId { get; set; } = "";
    public bool Attached { get; set; }
    public int OwnerPid { get; set; }
    public string? GptDiskId { get; set; }
    public string? PartitionId { get; set; }
    public string? VolumeGuid { get; set; }
    public bool Protected { get; set; }
    public int Conversion { get; set; }
    public int PollsLeft { get; set; }
    public int Protection { get; set; }
    public int Method { get; set; }
    public bool Locked { get; set; }
    public bool Quiesced { get; set; }
    public bool OpenHandles { get; set; }
}

public sealed class SimState
{
    public long Counter { get; set; }
    public Dictionary<string, SimImage> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Mounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// In-memory model of the virtual disk, mount and BitLocker behaviour the vault relies on,
/// with fault injection. It creates no disks, volumes, folders or files other than its own
/// optional state file.
/// </summary>
public sealed class SimulatedVaultPlatform : IVaultPlatform
{
    private sealed record Fault(PlatformStatus? Status, bool Crash, bool AfterEffect);

    private readonly object _gate = new();
    private readonly string? _statePath;
    private readonly Dictionary<string, Queue<Fault>> _faults = new();
    private readonly Dictionary<string, PlatformStatus> _persistentFaults = new();
    private SimState _s = new();

    /// <param name="statePath">Optional file that shares state between worker processes.</param>
    public SimulatedVaultPlatform(string? statePath = null) => _statePath = statePath;

    public List<string> Calls { get; } = new();
    public int EncryptionPolls { get; set; } = 1;
    public int[] ProtectorTypes { get; set; } = [8];
    public bool AutoUnlock { get; set; }

    /// <summary>Test hook, memory only: the last passphrase handed to Protect.</summary>
    public string? LastPassphraseForTests { get; private set; }

    // ---- test and demo controls

    public void Inject(string step, PlatformStatus status, bool persistent = false)
    {
        lock (_gate)
        {
            if (persistent) _persistentFaults[step] = status;
            else Enqueue(step, new Fault(status, false, false));
        }
    }

    public void InjectCrash(string step, bool afterEffect)
    {
        lock (_gate) Enqueue(step, new Fault(null, true, afterEffect));
    }

    private void Enqueue(string step, Fault f)
    {
        if (!_faults.TryGetValue(step, out Queue<Fault>? q)) _faults[step] = q = new Queue<Fault>();
        q.Enqueue(f);
    }

    public int CallCount(string step)
    {
        lock (_gate) return Calls.Count(c => c == step);
    }

    /// <summary>The owner process is gone: its attachment handles close and images detach.</summary>
    public void OwnerProcessExited()
    {
        lock (_gate)
        {
            Load();
            foreach (SimImage i in _s.Images.Values)
            {
                i.Attached = false;
                i.Quiesced = false;
            }
            Save();
        }
    }

    public void SetOpenHandles(string imagePath, bool open) => Mutate(imagePath, i => i.OpenHandles = open);

    public void ReplaceBackingFile(string imagePath) => Mutate(imagePath, i => i.FileId = "sim-file-replaced");

    public void SetMount(string folder, string volumeGuid)
    {
        lock (_gate)
        {
            Load();
            _s.Mounts[folder] = volumeGuid;
            Save();
        }
    }

    public SimImage? Image(string imagePath)
    {
        lock (_gate)
        {
            Load();
            return _s.Images.GetValueOrDefault(imagePath);
        }
    }

    private void Mutate(string imagePath, Action<SimImage> change)
    {
        lock (_gate)
        {
            Load();
            change(_s.Images[imagePath]);
            Save();
        }
    }

    // ---- IVaultPlatform

    public PlatformResult CheckPrerequisites() => Run("prereq", () => PlatformResult.Ok);

    public PlatformResult CreateImage(string path, long capacityBytes, out ImageIdentity? identity)
    {
        ImageIdentity? created = null;
        PlatformResult r = Run("create", () =>
        {
            if (_s.Images.ContainsKey(path)) return new PlatformResult(PlatformStatus.Failed, "image exists");
            long n = ++_s.Counter;
            var img = new SimImage { Path = path, FileId = $"sim-file-{n}", HostVolumeId = "sim-host-volume", VhdId = $"sim-vhd-{n}" };
            _s.Images[path] = img;
            created = new ImageIdentity(img.FileId, img.HostVolumeId, img.VhdId);
            return PlatformResult.Ok;
        });
        identity = r.IsOk ? created : null;
        return r;
    }

    public PlatformResult Attach(string path, ImageIdentity expected, bool readOnly, out IVaultAttachment? attachment)
    {
        PlatformResult r = Run("attach", () =>
        {
            if (!_s.Images.TryGetValue(path, out SimImage? img)) return new PlatformResult(PlatformStatus.Failed, "no image");
            if (img.FileId != expected.BackingFileId || img.VhdId != expected.VhdId) return new PlatformResult(PlatformStatus.IdentityMismatch);
            if (img.Attached) return new PlatformResult(PlatformStatus.Busy, "already attached");
            img.Attached = true;
            img.OwnerPid = Environment.ProcessId;
            img.Quiesced = false;
            // A protected volume with no auto-unlock always comes up locked.
            img.Locked = img.Protected && !AutoUnlock;
            return PlatformResult.Ok;
        });
        attachment = r.IsOk ? new Attachment(this, path) : null;
        return r;
    }

    public PlatformResult InitializeAndFormat(IVaultAttachment attachment, out VolumeIdentity? volume)
    {
        VolumeIdentity? made = null;
        PlatformResult r = Run("format", () =>
        {
            SimImage? img = Attached(attachment);
            if (img is null) return new PlatformResult(PlatformStatus.Failed, "not attached");
            // Only a disk that has never carried a volume is ours to format.
            if (img.VolumeGuid is not null) return new PlatformResult(PlatformStatus.IdentityMismatch, "disk is not new");
            img.GptDiskId = Guid.NewGuid().ToString("D");
            img.PartitionId = Guid.NewGuid().ToString("D");
            img.VolumeGuid = @"\\?\Volume{" + Guid.NewGuid().ToString("D") + @"}\";
            made = new VolumeIdentity(img.GptDiskId, img.PartitionId, img.VolumeGuid);
            return PlatformResult.Ok;
        });
        volume = r.IsOk ? made : null;
        return r;
    }

    public PlatformResult FindVolume(IVaultAttachment attachment, out VolumeIdentity? volume)
    {
        VolumeIdentity? found = null;
        PlatformResult r = Run("find", () =>
        {
            SimImage? img = Attached(attachment);
            if (img?.VolumeGuid is null) return new PlatformResult(PlatformStatus.Failed, "no volume");
            found = new VolumeIdentity(img.GptDiskId!, img.PartitionId!, img.VolumeGuid);
            return PlatformResult.Ok;
        });
        volume = r.IsOk ? found : null;
        return r;
    }

    public PlatformResult Protect(string volumeGuid, ReadOnlySpan<char> passphrase)
    {
        bool usable = passphrase.Length >= 8;
        LastPassphraseForTests = passphrase.ToString();
        return Run("protect", () =>
        {
            SimImage? img = ByVolume(volumeGuid);
            if (img is null || !usable) return new PlatformResult(PlatformStatus.Failed);
            img.Protected = true;
            return PlatformResult.Ok;
        });
    }

    public PlatformResult StartEncryption(string volumeGuid, int method) => Run("encrypt", () =>
    {
        SimImage? img = ByVolume(volumeGuid);
        if (img is null || !img.Protected) return new PlatformResult(PlatformStatus.Failed);
        img.Conversion = 2;
        img.PollsLeft = EncryptionPolls;
        img.Method = method;
        return PlatformResult.Ok;
    });

    public PlatformResult QueryEncryption(string volumeGuid, out EncryptionState? state)
    {
        EncryptionState? observed = null;
        PlatformResult r = Run("query", () =>
        {
            SimImage? img = ByVolume(volumeGuid);
            if (img is null) return new PlatformResult(PlatformStatus.Failed);
            if (img.Conversion == 2 && img.PollsLeft-- <= 0)
            {
                img.Conversion = 1;
                img.Protection = 1;
            }
            observed = new EncryptionState(img.Conversion, img.Protection, img.Method, ProtectorTypes, AutoUnlock);
            return PlatformResult.Ok;
        });
        state = r.IsOk ? observed : null;
        return r;
    }

    public PlatformResult Mount(string volumeGuid, string folder, string ownerSid) => Run("mount", () =>
    {
        if (ByVolume(volumeGuid) is null) return new PlatformResult(PlatformStatus.Failed);
        if (_s.Mounts.ContainsKey(folder)) return new PlatformResult(PlatformStatus.Failed, "folder in use");
        _s.Mounts[folder] = volumeGuid;
        return PlatformResult.Ok;
    });

    public PlatformResult Unmount(string volumeGuid, string folder) => Run("unmount", () =>
    {
        if (!_s.Mounts.TryGetValue(folder, out string? mounted)) return PlatformResult.Ok;
        if (!string.Equals(mounted, volumeGuid, StringComparison.OrdinalIgnoreCase)) return new PlatformResult(PlatformStatus.IdentityMismatch);
        _s.Mounts.Remove(folder);
        return PlatformResult.Ok;
    });

    public PlatformResult Quiesce(IVaultAttachment attachment, string volumeGuid) => Run("quiesce", () =>
    {
        SimImage? img = Attached(attachment);
        if (img is null || img.VolumeGuid != volumeGuid) return new PlatformResult(PlatformStatus.Failed);
        if (img.OpenHandles) return new PlatformResult(PlatformStatus.Busy);
        img.Quiesced = true;
        return PlatformResult.Ok;
    });

    public PlatformResult Lock(string volumeGuid) => Run("lock", () =>
    {
        SimImage? img = ByVolume(volumeGuid);
        if (img is null) return new PlatformResult(PlatformStatus.Failed);
        if (img.OpenHandles) return new PlatformResult(PlatformStatus.Busy);
        img.Locked = true;
        return PlatformResult.Ok;
    });

    public PlatformResult GetLockStatus(string volumeGuid, out int? status)
    {
        int? observed = null;
        PlatformResult r = Run("lockstatus", () =>
        {
            SimImage? img = ByVolume(volumeGuid);
            if (img is null) return new PlatformResult(PlatformStatus.Failed);
            observed = img.Locked ? 1 : 0;
            return PlatformResult.Ok;
        });
        status = r.IsOk ? observed : null;
        return r;
    }

    public PlatformResult Detach(IVaultAttachment attachment) => Run("detach", () =>
    {
        if (attachment is not Attachment a || !_s.Images.TryGetValue(a.Path, out SimImage? img)) return new PlatformResult(PlatformStatus.Failed);
        img.Attached = false;
        img.Quiesced = false;
        a.Released = true;
        return PlatformResult.Ok;
    });

    public PlatformResult DeleteBackingFile(string path, string expectedFileId) => Run("delete", () =>
    {
        if (!_s.Images.TryGetValue(path, out SimImage? img)) return new PlatformResult(PlatformStatus.Failed, "no image");
        if (img.FileId != expectedFileId) return new PlatformResult(PlatformStatus.IdentityMismatch);
        if (img.Attached) return new PlatformResult(PlatformStatus.Busy);
        _s.Images.Remove(path);
        return PlatformResult.Ok;
    });

    public Observation Observe(VaultRecord record)
    {
        lock (_gate)
        {
            Load();
            Calls.Add("observe");
            _s.Images.TryGetValue(record.BackingPath, out SimImage? img);
            _s.Mounts.TryGetValue(record.MountPath, out string? mounted);
            return new Observation(img is not null, img?.FileId, img?.Attached ?? false, mounted, img?.OpenHandles ?? false);
        }
    }

    // ---- internals

    private PlatformResult Run(string step, Func<PlatformResult> effect)
    {
        lock (_gate)
        {
            Load();
            Calls.Add(step);

            Fault? fault = null;
            if (_faults.TryGetValue(step, out Queue<Fault>? q) && q.Count > 0) fault = q.Dequeue();
            else if (_persistentFaults.TryGetValue(step, out PlatformStatus persistent)) fault = new Fault(persistent, false, false);

            if (fault is { Crash: true, AfterEffect: false }) throw new SimulatedTerminationException(step);
            if (fault is { Crash: false, Status: not null }) return new PlatformResult(fault.Status.Value, "injected");

            PlatformResult r = effect();
            Save();
            if (fault is { Crash: true, AfterEffect: true }) throw new SimulatedTerminationException(step);
            return r;
        }
    }

    private SimImage? Attached(IVaultAttachment attachment) =>
        attachment is Attachment { Released: false } a && _s.Images.TryGetValue(a.Path, out SimImage? img) && img.Attached ? img : null;

    private SimImage? ByVolume(string volumeGuid) =>
        _s.Images.Values.FirstOrDefault(i => i.Attached && i.VolumeGuid == volumeGuid);

    private void ReleaseHandle(Attachment a)
    {
        lock (_gate)
        {
            if (a.Released) return;
            a.Released = true;
            Load();
            if (_s.Images.TryGetValue(a.Path, out SimImage? img))
            {
                img.Attached = false;
                img.Quiesced = false;
            }
            Save();
        }
    }

    private void Load()
    {
        if (_statePath is null || !File.Exists(_statePath)) return;
        SimState? loaded = JsonSerializer.Deserialize<SimState>(File.ReadAllBytes(_statePath));
        if (loaded is null) return;
        loaded.Images = new Dictionary<string, SimImage>(loaded.Images, StringComparer.OrdinalIgnoreCase);
        loaded.Mounts = new Dictionary<string, string>(loaded.Mounts, StringComparer.OrdinalIgnoreCase);
        foreach (SimImage img in loaded.Images.Values)
        {
            // An attachment does not outlive the process that held its handle.
            if (img.Attached && img.OwnerPid != Environment.ProcessId && !ProcessAlive(img.OwnerPid))
            {
                img.Attached = false;
                img.Quiesced = false;
            }
        }
        _s = loaded;
    }

    private void Save()
    {
        if (_statePath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        string temp = _statePath + "." + Environment.ProcessId + ".new";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_s));
        File.Move(temp, _statePath, overwrite: true);
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed class Attachment : IVaultAttachment
    {
        private readonly SimulatedVaultPlatform _platform;

        public Attachment(SimulatedVaultPlatform platform, string path)
        {
            _platform = platform;
            Path = path;
        }

        public string Path { get; }
        public bool Released { get; set; }

        public void Dispose() => _platform.ReleaseHandle(this);
    }
}
