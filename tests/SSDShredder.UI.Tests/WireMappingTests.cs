using System.Text.Json;
using SSDShredder.UI.Worker;

namespace SSDShredder.UI.Tests;

/// <summary>
/// WireMapping has no Windows dependency at all (unlike ProcessWorkerClient's
/// pipe/process plumbing), so this is genuine, fully-verified coverage — not
/// just "fails the same environment-only way" like the native/Core tests.
/// </summary>
public class WireMappingTests
{
    [Theory]
    [InlineData("eligible", InspectReason.Eligible)]
    [InlineData("filesystem", InspectReason.Filesystem)]
    [InlineData("topology", InspectReason.Topology)]
    [InlineData("capability", InspectReason.Capability)]
    [InlineData("fileType", InspectReason.FileType)]
    [InlineData("streams", InspectReason.Streams)]
    [InlineData("links", InspectReason.Links)]
    [InlineData("noExtents", InspectReason.NoExtents)]
    [InlineData("noFullPage", InspectReason.NoFullPage)]
    [InlineData("unknown", InspectReason.Unknown)]
    [InlineData("something-the-wire-never-sends", InspectReason.Unknown)]
    [InlineData(null, InspectReason.Unknown)]
    public void ParseReason_matches_the_wire_contracts_exact_literal(string? wire, InspectReason expected)
    {
        Assert.Equal(expected, WireMapping.ParseReason(wire));
    }

    [Theory]
    [InlineData("completed", TerminalState.Completed)]
    [InlineData("cancelled", TerminalState.Cancelled)]
    [InlineData("unsupported", TerminalState.Unsupported)]
    [InlineData("denied", TerminalState.Denied)]
    [InlineData("busy", TerminalState.Busy)]
    [InlineData("failed", TerminalState.Failed)]
    [InlineData("indeterminate", TerminalState.Indeterminate)]
    [InlineData("recovery", TerminalState.Indeterminate)] // vault-only state; file ops fail safe into Indeterminate
    [InlineData(null, TerminalState.Indeterminate)]
    public void ParseTerminalState_fails_safe_into_indeterminate(string? wire, TerminalState expected)
    {
        Assert.Equal(expected, WireMapping.ParseTerminalState(wire));
    }

    [Theory]
    [InlineData("preserved", EntryOutcome.Preserved)]
    [InlineData("removed", EntryOutcome.Removed)]
    [InlineData("unknown", EntryOutcome.Unknown)]
    [InlineData(null, EntryOutcome.Unknown)]
    public void ParseEntryOutcome_matches_the_wire_contract(string? wire, EntryOutcome expected)
    {
        Assert.Equal(expected, WireMapping.ParseEntryOutcome(wire));
    }

    [Theory]
    [InlineData("creating", VaultPhase.Creating)]
    [InlineData("ready", VaultPhase.Ready)]
    [InlineData("closing", VaultPhase.Closing)]
    [InlineData("retired", VaultPhase.Retired)]
    [InlineData("orphan", VaultPhase.Orphan)]
    [InlineData("unknown", VaultPhase.Unknown)]
    [InlineData(null, VaultPhase.Unknown)]
    public void ParseVaultPhase_matches_the_wire_contract(string? wire, VaultPhase expected)
    {
        Assert.Equal(expected, WireMapping.ParseVaultPhase(wire));
    }

    [Theory]
    [InlineData("inspecting", WorkerPhase.Inspecting)]
    [InlineData("confirming", WorkerPhase.Confirming)]
    [InlineData("trimming", WorkerPhase.Trimming)]
    [InlineData("unlinking", WorkerPhase.Unlinking)]
    [InlineData("creating", WorkerPhase.Creating)]
    [InlineData("encrypting", WorkerPhase.Encrypting)]
    [InlineData("locking", WorkerPhase.Locking)]
    [InlineData("reconciling", WorkerPhase.Reconciling)]
    public void TryParsePhase_accepts_every_published_phase(string wire, WorkerPhase expected)
    {
        Assert.True(WireMapping.TryParsePhase(wire, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-phase")]
    public void TryParsePhase_rejects_anything_else(string? wire)
    {
        Assert.False(WireMapping.TryParsePhase(wire, out _));
    }

    [Fact]
    public void TryGetPhase_reads_the_phase_property_off_a_progress_frame()
    {
        using var doc = JsonDocument.Parse("""{"v":1,"id":"x","seq":1,"type":"progress","phase":"trimming"}""");

        Assert.True(WireMapping.TryGetPhase(doc.RootElement, out var phase));
        Assert.Equal(WorkerPhase.Trimming, phase);
    }

    [Fact]
    public void TryGetPhase_returns_false_when_the_frame_has_no_phase()
    {
        using var doc = JsonDocument.Parse("""{"v":1,"id":"x","seq":1,"type":"result"}""");

        Assert.False(WireMapping.TryGetPhase(doc.RootElement, out _));
    }

    [Fact]
    public void GetNullable_helpers_distinguish_absent_null_and_present()
    {
        using var doc = JsonDocument.Parse("""{"a":"x","b":null,"d":42,"e":null,"f":true,"g":null}""");
        var root = doc.RootElement;

        Assert.Equal("x", WireMapping.GetNullableString(root, "a"));
        Assert.Null(WireMapping.GetNullableString(root, "b"));
        Assert.Null(WireMapping.GetNullableString(root, "missing"));

        Assert.Equal(42, WireMapping.GetNullableLong(root, "d"));
        Assert.Null(WireMapping.GetNullableLong(root, "e"));
        Assert.Null(WireMapping.GetNullableLong(root, "missing"));

        Assert.True(WireMapping.GetNullableBool(root, "f"));
        Assert.Null(WireMapping.GetNullableBool(root, "g"));
        Assert.Null(WireMapping.GetNullableBool(root, "missing"));
    }
}
