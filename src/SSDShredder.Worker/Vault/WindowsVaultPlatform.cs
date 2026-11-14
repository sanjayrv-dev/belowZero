using Microsoft.Win32;

namespace SSDShredder.Worker.Vault;

/// <summary>
/// Placeholder for the real Virtual Disk / BitLocker provider. That code has to be written
/// and qualified on a disposable Windows 11 Pro, Enterprise or Education install. Until
/// then this build refuses every real vault operation before anything is touched.
/// </summary>
public sealed class WindowsVaultPlatform : IVaultPlatform
{
    private const string NotBuilt = "the real vault provider is not part of this build";

    public static string? EditionId()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("EditionID") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public PlatformResult CheckPrerequisites()
    {
        string? edition = EditionId();
        if (edition is not null && edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
        {
            return new PlatformResult(PlatformStatus.Unsupported, "Windows Home cannot BitLocker-encrypt a new data volume");
        }
        return new PlatformResult(PlatformStatus.Unsupported, NotBuilt);
    }

    private static PlatformResult No => new(PlatformStatus.Unsupported, NotBuilt);

    public PlatformResult CreateImage(string path, long capacityBytes, out ImageIdentity? identity)
    {
        identity = null;
        return No;
    }

    public PlatformResult Attach(string path, ImageIdentity expected, bool readOnly, out IVaultAttachment? attachment)
    {
        attachment = null;
        return No;
    }

    public PlatformResult InitializeAndFormat(IVaultAttachment attachment, out VolumeIdentity? volume)
    {
        volume = null;
        return No;
    }

    public PlatformResult FindVolume(IVaultAttachment attachment, out VolumeIdentity? volume)
    {
        volume = null;
        return No;
    }

    public PlatformResult Protect(string volumeGuid, ReadOnlySpan<char> passphrase) => No;

    public PlatformResult StartEncryption(string volumeGuid, int method) => No;

    public PlatformResult QueryEncryption(string volumeGuid, out EncryptionState? state)
    {
        state = null;
        return No;
    }

    public PlatformResult Mount(string volumeGuid, string folder, string ownerSid) => No;

    public PlatformResult Unmount(string volumeGuid, string folder) => No;

    public PlatformResult Quiesce(IVaultAttachment attachment, string volumeGuid) => No;

    public PlatformResult Lock(string volumeGuid) => No;

    public PlatformResult GetLockStatus(string volumeGuid, out int? status)
    {
        status = null;
        return No;
    }

    public PlatformResult Detach(IVaultAttachment attachment) => No;

    public PlatformResult DeleteBackingFile(string path, string expectedFileId) => No;

    /// <summary>
    /// This provider cannot look, and "cannot look" must not read as "nothing is left":
    /// recovery retires a record when it observes no backing file and no mount.
    /// </summary>
    public Observation Observe(VaultRecord record) => throw new NotSupportedException(NotBuilt);
}
