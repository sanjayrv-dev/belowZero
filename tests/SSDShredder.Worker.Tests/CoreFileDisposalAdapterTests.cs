using SSDShredder.Core;
using SSDShredder.Worker.CoreAdapter;
using Xunit;

namespace SSDShredder.Worker.Tests;

/// <summary>
/// Unit tests for CoreFileDisposalAdapter's pure mapping logic — the parts that don't need
/// a real Windows file handle, so they run on any platform. The handle-dependent parts
/// (Inspect/Acquire against a real file) have no test here; they need the same real
/// Windows I/O that SSDShredder.Core.Tests' DecomposedCommitTests.cs already needs.
/// </summary>
public class CoreFileDisposalAdapterTests
{
    [Theory]
    [InlineData(EligibilityReason.Eligible, Reasons.Eligible)]
    [InlineData(EligibilityReason.Filesystem, Reasons.Filesystem)]
    [InlineData(EligibilityReason.Topology, Reasons.Topology)]
    [InlineData(EligibilityReason.Capability, Reasons.Capability)]
    [InlineData(EligibilityReason.FileType, Reasons.FileType)]
    [InlineData(EligibilityReason.Streams, Reasons.Streams)]
    [InlineData(EligibilityReason.Links, Reasons.Links)]
    [InlineData(EligibilityReason.NoExtents, Reasons.NoExtents)]
    [InlineData(EligibilityReason.NoFullPage, Reasons.NoFullPage)]
    [InlineData(EligibilityReason.Unknown, Reasons.Unknown)]
    public void MapReason_matches_the_wire_contracts_exact_literal(EligibilityReason reason, string expectedWireLiteral)
    {
        Assert.Equal(expectedWireLiteral, CoreFileDisposalAdapter.MapReason(reason));
    }

    [Theory]
    [InlineData(BusType.Nvme, "BusTypeNvme")]
    [InlineData(BusType.Sata, "BusTypeSata")]
    [InlineData(BusType.Usb, "BusTypeUsb")]
    [InlineData(BusType.Unknown, null)]
    [InlineData(null, null)]
    public void MapBus_uses_the_SDK_name_not_Cores_lowercase_display_form(BusType? bus, string? expected)
    {
        // IFileDisposalCore.cs's own doc comment: "SDK bus name, for example BusTypeNvme."
        Assert.Equal(expected, CoreFileDisposalAdapter.MapBus(bus));
    }

    [Theory]
    [InlineData(2, AccessStatus.NotFound)]  // ERROR_FILE_NOT_FOUND
    [InlineData(3, AccessStatus.NotFound)]  // ERROR_PATH_NOT_FOUND
    [InlineData(5, AccessStatus.Denied)]    // ERROR_ACCESS_DENIED
    [InlineData(32, AccessStatus.Busy)]     // ERROR_SHARING_VIOLATION
    [InlineData(33, AccessStatus.Busy)]     // ERROR_LOCK_VIOLATION
    [InlineData(1, AccessStatus.Failed)]    // anything else: fail closed
    public void MapAccess_classifies_known_win32_errors(int win32Error, AccessStatus expected)
    {
        Assert.Equal(expected, CoreFileDisposalAdapter.MapAccess(win32Error));
    }

    [Theory]
    [InlineData(ShredState.Busy, AccessStatus.Busy)]
    [InlineData(ShredState.Denied, AccessStatus.Denied)]
    [InlineData(ShredState.Failed, AccessStatus.Failed)]
    [InlineData(ShredState.Invalid, AccessStatus.Failed)] // Open()'s own PathPolicy path; defensive default.
    public void MapShredState_classifies_opens_failure_states(ShredState state, AccessStatus expected)
    {
        Assert.Equal(expected, CoreFileDisposalAdapter.MapShredState(state));
    }
}
