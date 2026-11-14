using System.Text.Json;
using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Vault;

public sealed record VaultReceipt(int Code, bool? LockedObserved, DateTimeOffset RetiredUtc, string Detail);

/// <summary>
/// Durable lifecycle record of one vault. It stores identities and observations only.
/// The session passphrase is never written here.
/// </summary>
public sealed class VaultRecord
{
    public int Schema { get; set; } = 1;
    public Guid VaultId { get; set; }
    public string OwnerSid { get; set; } = "";
    public string Phase { get; set; } = VaultPhases.Creating;

    /// <summary>The mutation whose intent was last recorded.</summary>
    public string? Step { get; set; }

    /// <summary>False while the step's intent is recorded and its completion is not.</summary>
    public bool StepDone { get; set; }
    public long CommitSequence { get; set; }
    public bool OrphanRetirementOptIn { get; set; }

    public string BackingPath { get; set; } = "";
    public string? BackingFileId { get; set; }
    public string? HostVolumeId { get; set; }
    public string? VhdId { get; set; }
    public string? GptDiskId { get; set; }
    public string? PartitionId { get; set; }
    public string? VolumeGuid { get; set; }
    public string MountPath { get; set; } = "";

    public VaultReadyFacts? Ready { get; set; }
    public bool? LockedObserved { get; set; }
    public VaultReceipt? Receipt { get; set; }
    public string? ReviewReason { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class JournalCorruptException : Exception
{
    public JournalCorruptException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// One JSON file per vault. Each commit is written to a temporary file, flushed to disk,
/// then renamed over the previous record.
/// </summary>
public sealed class VaultJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public VaultJournal(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    private string RecordPath(Guid id) => Path.Combine(Directory, id.ToString("D") + ".json");

    public VaultRecord? Load(Guid id)
    {
        string path = RecordPath(id);
        byte[] bytes;
        try
        {
            bytes = ReadShared(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            VaultRecord? r = JsonSerializer.Deserialize<VaultRecord>(bytes, Json);
            if (r is null || r.VaultId != id || r.Schema != 1) throw new JournalCorruptException("journal record does not match its name");
            return r;
        }
        catch (JsonException e)
        {
            throw new JournalCorruptException("journal record is unreadable", e);
        }
    }

    public IReadOnlyList<Guid> ListIds()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var ids = new List<Guid>();
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out Guid id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Atomically persists the record with the next commit sequence.</summary>
    public void Commit(VaultRecord record)
    {
        System.IO.Directory.CreateDirectory(Directory);
        record.CommitSequence++;
        string path = RecordPath(record.VaultId);
        string temp = path + ".new";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        Retry(() => File.Move(temp, path, overwrite: true));
    }

    /// <summary>
    /// Reads without blocking a writer: the record may be replaced while it is open here.
    /// </summary>
    private static byte[] ReadShared(string path) => Retry(() =>
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    });

    private static void Retry(Action action) => Retry<object?>(() =>
    {
        action();
        return null;
    });

    /// <summary>A reader and a writer in different processes can collide for a moment.</summary>
    private static T Retry<T>(Func<T> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception e) when (attempt < 100
                && e is IOException or UnauthorizedAccessException
                && e is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// Serialises work on one vault across threads and processes. Returns null if the lock
    /// could not be taken within <paramref name="wait"/>.
    /// </summary>
    public IDisposable? TryLock(Guid id, TimeSpan wait)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, id.ToString("D") + ".lock");
        long deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline) return null;
                Thread.Sleep(20);
            }
        }
    }
}
