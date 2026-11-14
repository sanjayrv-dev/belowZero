using System.Text.Json.Serialization;

namespace SSDShredder.UI.Activation;

/// <summary>
/// The UI activation envelope from docs/02_SYSTEM_ARCHITECTURE.md
/// ("The UI activation envelope is separate from a worker request").
/// Carries no mutation authority: an accepted activation only opens/focuses
/// the bin panel and stages a selection for the worker's own inspect/confirm flow.
/// </summary>
public sealed record ActivationEnvelope
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("action")]
    public required ActivationAction Action { get; init; }

    [JsonPropertyName("source")]
    public required ActivationSource Source { get; init; }

    [JsonPropertyName("paths")]
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>
    /// Structural validation only (arity, absolute-path shape). This is a display
    /// hint and routing envelope, not an authorization decision — the worker still
    /// performs its own identity/eligibility checks before any mutation.
    /// </summary>
    public bool IsStructurallyValid() => Action switch
    {
        ActivationAction.Open => Paths.Count == 0,
        ActivationAction.Select => Paths.Count == 1 && IsAbsoluteLocalPath(Paths[0]),
        _ => false,
    };

    private static bool IsAbsoluteLocalPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathRooted(path) &&
        !path.StartsWith(@"\\", StringComparison.Ordinal);

    public static ActivationEnvelope CreateOpen(ActivationSource source) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Action = ActivationAction.Open,
        Source = source,
        Paths = [],
    };

    public static ActivationEnvelope CreateSelect(ActivationSource source, string absolutePath) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Action = ActivationAction.Select,
        Source = source,
        Paths = [absolutePath],
    };
}

// CamelCase lowercases these single-word names to match the wire contract's
// exact literals ("open"/"select", "desktop"/"explorer"/"terminal").
[JsonConverter(typeof(JsonStringEnumConverter<ActivationAction>))]
public enum ActivationAction
{
    [JsonStringEnumMemberName("open")]
    Open,
    [JsonStringEnumMemberName("select")]
    Select,
}

[JsonConverter(typeof(JsonStringEnumConverter<ActivationSource>))]
public enum ActivationSource
{
    [JsonStringEnumMemberName("desktop")]
    Desktop,
    [JsonStringEnumMemberName("explorer")]
    Explorer,
    [JsonStringEnumMemberName("terminal")]
    Terminal,
}

/// <summary>Acknowledgement: same version/id, plus whether activation was accepted.</summary>
public sealed record ActivationAck
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("accepted")]
    public required bool Accepted { get; init; }

    public static ActivationAck For(ActivationEnvelope envelope, bool accepted) => new()
    {
        Version = envelope.Version,
        Id = envelope.Id,
        Accepted = accepted,
    };
}
