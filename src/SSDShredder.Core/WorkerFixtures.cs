namespace SSDShredder.Core;

/// <summary>Canned results for the worker team to test against without touching a disk.</summary>
public static class WorkerFixtures
{
    public static readonly ShredResult Eligible = new(ShredState.Completed, 12288, 615, 1, 1, EntryState.Removed, "trim acknowledged and entry removed");
    public static readonly ShredResult Unsupported = new(ShredState.Unsupported, 0, 0, null, null, EntryState.Preserved, "ineligible: Topology");
    public static readonly ShredResult Partial = new(ShredState.Indeterminate, 12288, 615, 1, 0, EntryState.Preserved, "partial trim (0/1); file may have changed, entry kept");

    public static readonly CapabilityDecision EligibleDecision = new(true, EligibilityReason.Eligible, "nvme", 12288, 615, "all checks passed");
    public static readonly CapabilityDecision UnsupportedDecision = new(false, EligibilityReason.Topology, "usb", 0, 0, "bus Usb not allowed");
}
