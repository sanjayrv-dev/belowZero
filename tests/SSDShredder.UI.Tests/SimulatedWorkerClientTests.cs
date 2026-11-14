using SSDShredder.UI.Worker;

namespace SSDShredder.UI.Tests;

public class SimulatedWorkerClientTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("bz-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteFixture(int byteCount)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[byteCount]);
        return path;
    }

    [Fact]
    public async Task Inspect_MissingFile_IsIneligible_And_Labeled_Mock()
    {
        var client = new SimulatedWorkerClient();

        var result = await client.InspectAsync(
            Path.Combine(_tempDir, "does-not-exist.bin"), _ => { }, CancellationToken.None);

        Assert.False(result.Eligible);
        Assert.Equal(InspectReason.Filesystem, result.Reason);
        Assert.True(result.IsMock);
        Assert.Null(result.SelectionToken);
    }

    [Fact]
    public async Task Inspect_SubPageFile_IsIneligible_With_NoFullPage()
    {
        var client = new SimulatedWorkerClient();
        var path = WriteFixture(byteCount: 100); // well under the 4096-byte page.

        var result = await client.InspectAsync(path, _ => { }, CancellationToken.None);

        Assert.False(result.Eligible);
        Assert.Equal(InspectReason.NoFullPage, result.Reason);
        Assert.Equal(0, result.CoveredBytes);
        Assert.Equal(100, result.TailBytes);
    }

    [Fact]
    public async Task Inspect_PageAndAHalfFile_SplitsCoveredAndTail_AndReturnsToken()
    {
        var client = new SimulatedWorkerClient();
        var path = WriteFixture(byteCount: 4096 + 615);

        var result = await client.InspectAsync(path, _ => { }, CancellationToken.None);

        Assert.True(result.Eligible);
        Assert.Equal(4096, result.CoveredBytes);
        Assert.Equal(615, result.TailBytes);
        Assert.NotNull(result.SelectionToken);
        Assert.Matches("^[0-9a-f]{32}$", result.SelectionToken);
    }

    [Fact]
    public async Task Shred_WithoutPriorInspect_IsDenied()
    {
        var client = new SimulatedWorkerClient();
        var path = WriteFixture(byteCount: 4096);

        var result = await client.ShredAsync(path, selectionToken: "not-a-real-token", _ => { }, CancellationToken.None);

        Assert.Equal(TerminalState.Denied, result.State);
        Assert.Equal(EntryOutcome.Preserved, result.Entry);
    }

    [Fact]
    public async Task Shred_WithValidToken_FromInspect_Completes()
    {
        var client = new SimulatedWorkerClient();
        var path = WriteFixture(byteCount: 4096);
        var inspect = await client.InspectAsync(path, _ => { }, CancellationToken.None);

        var result = await client.ShredAsync(path, inspect.SelectionToken!, _ => { }, CancellationToken.None);

        Assert.Equal(TerminalState.Completed, result.State);
        Assert.Equal(EntryOutcome.Removed, result.Entry);
        Assert.Equal(4096, result.CoveredBytes);
    }

    [Fact]
    public async Task Shred_ConsumesToken_SoARepeatShredIsDenied()
    {
        var client = new SimulatedWorkerClient();
        var path = WriteFixture(byteCount: 4096);
        var inspect = await client.InspectAsync(path, _ => { }, CancellationToken.None);

        await client.ShredAsync(path, inspect.SelectionToken!, _ => { }, CancellationToken.None);
        var repeat = await client.ShredAsync(path, inspect.SelectionToken!, _ => { }, CancellationToken.None);

        Assert.Equal(TerminalState.Denied, repeat.State);
    }
}
