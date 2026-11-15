using System.Text.Json;

namespace SSDShredder.UI.Worker;

/// <summary>
/// Pure string/JSON ↔ enum mapping for the worker wire contract (docs/02_SYSTEM_ARCHITECTURE.md,
/// contracts/v1/). Deliberately has no Windows dependency at all — unlike the pipe/process
/// plumbing in <see cref="ProcessWorkerClient"/>, this logic is exactly as testable on macOS
/// as it will be on Windows, so it's kept separate and covered by WireMappingTests.
/// </summary>
internal static class WireMapping
{
    public static InspectReason ParseReason(string? reason) => reason switch
    {
        "eligible" => InspectReason.Eligible,
        "filesystem" => InspectReason.Filesystem,
        "topology" => InspectReason.Topology,
        "capability" => InspectReason.Capability,
        "fileType" => InspectReason.FileType,
        "streams" => InspectReason.Streams,
        "links" => InspectReason.Links,
        "noExtents" => InspectReason.NoExtents,
        "noFullPage" => InspectReason.NoFullPage,
        _ => InspectReason.Unknown,
    };

    public static TerminalState ParseTerminalState(string? state) => state switch
    {
        "completed" => TerminalState.Completed,
        "cancelled" => TerminalState.Cancelled,
        "unsupported" => TerminalState.Unsupported,
        "denied" => TerminalState.Denied,
        "busy" => TerminalState.Busy,
        "failed" => TerminalState.Failed,
        _ => TerminalState.Indeterminate, // "indeterminate" and "recovery" (vault-only) both fail safe this way
    };

    public static EntryOutcome ParseEntryOutcome(string? entry) => entry switch
    {
        "preserved" => EntryOutcome.Preserved,
        "removed" => EntryOutcome.Removed,
        _ => EntryOutcome.Unknown,
    };

    public static VaultPhase ParseVaultPhase(string? phase) => phase switch
    {
        "creating" => VaultPhase.Creating,
        "ready" => VaultPhase.Ready,
        "closing" => VaultPhase.Closing,
        "retired" => VaultPhase.Retired,
        "orphan" => VaultPhase.Orphan,
        _ => VaultPhase.Unknown,
    };

    public static bool TryParsePhase(string? phase, out WorkerPhase result)
    {
        var parsed = phase switch
        {
            "inspecting" => WorkerPhase.Inspecting,
            "confirming" => WorkerPhase.Confirming,
            "trimming" => WorkerPhase.Trimming,
            "unlinking" => WorkerPhase.Unlinking,
            "creating" => WorkerPhase.Creating,
            "encrypting" => WorkerPhase.Encrypting,
            "locking" => WorkerPhase.Locking,
            "reconciling" => WorkerPhase.Reconciling,
            _ => (WorkerPhase?)null,
        };
        result = parsed ?? default;
        return parsed is not null;
    }

    public static bool TryGetPhase(JsonElement progressFrame, out WorkerPhase phase)
    {
        phase = default;
        return progressFrame.TryGetProperty("phase", out var phaseProp) && TryParsePhase(phaseProp.GetString(), out phase);
    }

    public static string? GetNullableString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    public static long? GetNullableLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetInt64() : null;

    public static bool? GetNullableBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetBoolean() : null;
}
