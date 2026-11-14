using SSDShredder.Worker.CoreAdapter;
using SSDShredder.Worker.Ipc;
using SSDShredder.Worker.Simulation;
using SSDShredder.Worker.Vault;

namespace SSDShredder.Worker.Hosting;

/// <summary>
/// The providers one worker process runs against. Simulation is chosen only by the
/// SSDSHREDDER_SIMULATE=1 environment variable and touches nothing but its own state folder.
/// </summary>
public sealed class WorkerEnvironment
{
    public required bool Simulated { get; init; }
    public required IFileDisposalCore Core { get; init; }
    public required bool CoreAvailable { get; init; }
    public required IVaultPlatform Platform { get; init; }
    public required VaultJournal Journal { get; init; }
    public required VaultOptions Options { get; init; }
    public required string RequestPipePrefix { get; init; }
    public required string OwnerPipePrefix { get; init; }

    public VaultService CreateVaultService() => new(Journal, Platform, Options);

    public static WorkerEnvironment Create()
    {
        if (Environment.GetEnvironmentVariable("SSDSHREDDER_SIMULATE") == "1") return CreateSimulated();

        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SSDShredder");
        return new WorkerEnvironment
        {
            Simulated = false,
            Core = new UnavailableCore(),
            CoreAvailable = false,
            Platform = new WindowsVaultPlatform(),
            Journal = new VaultJournal(Path.Combine(data, "journal")),
            Options = new VaultOptions
            {
                ImageDirectory = Path.Combine(data, "images"),
                MountRoot = Path.Combine(data, "mounts"),
            },
            RequestPipePrefix = PipeEndpoints.RequestPrefix,
            OwnerPipePrefix = PipeEndpoints.OwnerPrefix,
        };
    }

    private static WorkerEnvironment CreateSimulated()
    {
        string root = Environment.GetEnvironmentVariable("SSDSHREDDER_SIM_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSDShredder", "sim");
        string? scenario = Environment.GetEnvironmentVariable("SSDSHREDDER_SIM_SCENARIO");

        var platform = new SimulatedVaultPlatform(Path.Combine(root, "platform.json"));
        // SSDSHREDDER_SIM_FAULTS=lock:RecoveryKeyRequired,quiesce:Busy
        foreach (string fault in (Environment.GetEnvironmentVariable("SSDSHREDDER_SIM_FAULTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = fault.Split(':');
            if (parts.Length == 2 && Enum.TryParse(parts[1], ignoreCase: true, out PlatformStatus status))
            {
                platform.Inject(parts[0], status, persistent: true);
            }
        }

        return new WorkerEnvironment
        {
            Simulated = true,
            Core = new SimulatedCore(path => File.Exists(path) ? SimScenario.Named(scenario, new FileInfo(path).Length) : null,
                Environment.SystemPageSize),
            CoreAvailable = true,
            Platform = platform,
            Journal = new VaultJournal(Path.Combine(root, "journal")),
            Options = new VaultOptions
            {
                ImageDirectory = Path.Combine(root, "images"),
                MountRoot = Path.Combine(root, "mounts"),
                OrphanRetirementOptIn = Environment.GetEnvironmentVariable("SSDSHREDDER_SIM_OPTIN") == "1",
                AllowDetachCloseRoute = Environment.GetEnvironmentVariable("SSDSHREDDER_SIM_DETACH_ROUTE") == "1",
                EncryptPoll = TimeSpan.FromMilliseconds(50),
                LockWait = TimeSpan.FromSeconds(10),
            },
            // Request pipes are random per request, so both modes share one prefix.
            RequestPipePrefix = PipeEndpoints.RequestPrefix,
            OwnerPipePrefix = "SSDShredder.sim.owner.",
        };
    }

    /// <summary>
    /// Used until Engineer 1's Core is integrated. Every file request ends as unsupported
    /// and no file is opened.
    /// </summary>
    private sealed class UnavailableCore : IFileDisposalCore
    {
        public InspectResult Inspect(string path) => new(AccessStatus.NotFound, null);

        public AcquireResult Acquire(string path) => new(AccessStatus.NotFound, null, null);

        public VolumeProbe ProbeVolume(string volumeGuidPath) => new(volumeGuidPath, null, null, null, null);
    }
}
