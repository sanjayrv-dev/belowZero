namespace SSDShredder.Worker.Vault;

public enum PlatformStatus
{
    Ok,

    /// <summary>The edition, provider or build cannot do this.</summary>
    Unsupported,

    /// <summary>BitLocker policy refused. The Worker never changes policy.</summary>
    PolicyRefused,

    /// <summary>Open handles or an exclusive lock held elsewhere. Nothing was forced.</summary>
    Busy,

    /// <summary>Lock refused because the volume has no recovery protector (0x80310022).</summary>
    RecoveryKeyRequired,
    IdentityMismatch,
    Failed,
}

public readonly record struct PlatformResult(PlatformStatus Status, string? Detail = null)
{
    public bool IsOk => Status == PlatformStatus.Ok;

    public static readonly PlatformResult Ok = new(PlatformStatus.Ok);
}

public sealed record ImageIdentity(string BackingFileId, string HostVolumeId, string VhdId);

public sealed record VolumeIdentity(string GptDiskId, string PartitionId, string VolumeGuid);

/// <summary>Live BitLocker state. Unknown facts are null.</summary>
/// <param name="Conversion">GetConversionStatus: 1 is FullyEncrypted, 2 is in progress.</param>
/// <param name="Protection">GetProtectionStatus: 1 is ProtectionOn.</param>
/// <param name="Method">6 or 7 for XTS-AES-128/256.</param>
public sealed record EncryptionState(int? Conversion, int? Protection, int? Method, int[]? ProtectorTypes, bool? AutoUnlock);

/// <summary>What the platform can see right now of a recorded vault's objects.</summary>
/// <param name="MountedVolumeGuid">The volume the recorded mount folder resolves to, or null if none.</param>
public sealed record Observation(
    bool BackingExists,
    string? BackingFileId,
    bool? Attached,
    string? MountedVolumeGuid,
    bool? OpenHandles);

/// <summary>
/// The attachment handle of one image. The session owner keeps it for the vault's lifetime.
/// Disposing it releases the handle, which detaches a non-permanent attachment.
/// </summary>
public interface IVaultAttachment : IDisposable
{
}

/// <summary>
/// Virtual Disk API, storage management, mount points and BitLocker WMI, as the vault state
/// machine uses them. Every mutation verifies its own effect before returning Ok.
/// </summary>
public interface IVaultPlatform
{
    PlatformResult CheckPrerequisites();

    /// <summary>Creates a fresh dynamically expanding VHDX. Fails if the file exists.</summary>
    PlatformResult CreateImage(string path, long capacityBytes, out ImageIdentity? identity);

    /// <summary>Attaches with no drive letter after matching the image to its recorded identity.</summary>
    PlatformResult Attach(string path, ImageIdentity expected, bool readOnly, out IVaultAttachment? attachment);

    /// <summary>GPT, one basic-data partition, NTFS. Only for a disk that has no volume yet.</summary>
    PlatformResult InitializeAndFormat(IVaultAttachment attachment, out VolumeIdentity? volume);

    /// <summary>Rediscovers the volume of an attached image by identity.</summary>
    PlatformResult FindVolume(IVaultAttachment attachment, out VolumeIdentity? volume);

    /// <summary>PrepareVolume and ProtectKeyWithPassphrase.</summary>
    PlatformResult Protect(string volumeGuid, ReadOnlySpan<char> passphrase);

    PlatformResult StartEncryption(string volumeGuid, int method);

    PlatformResult QueryEncryption(string volumeGuid, out EncryptionState? state);

    /// <summary>Mounts on a restricted empty folder, verifies it, then grants the owner access.</summary>
    PlatformResult Mount(string volumeGuid, string folder, string ownerSid);

    /// <summary>Removes the mount if it resolves to this volume. Ok if there is no mount.</summary>
    PlatformResult Unmount(string volumeGuid, string folder);

    /// <summary>Flushes and takes exclusive volume access. Busy if anything is open.</summary>
    PlatformResult Quiesce(IVaultAttachment attachment, string volumeGuid);

    PlatformResult Lock(string volumeGuid);

    /// <summary>GetLockStatus: 1 is locked.</summary>
    PlatformResult GetLockStatus(string volumeGuid, out int? status);

    PlatformResult Detach(IVaultAttachment attachment);

    /// <summary>Deletes the backing file only if it still has the recorded identity.</summary>
    PlatformResult DeleteBackingFile(string path, string expectedFileId);

    Observation Observe(VaultRecord record);
}
