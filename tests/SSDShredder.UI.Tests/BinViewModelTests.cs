using SSDShredder.UI.Tests.TestDoubles;
using SSDShredder.UI.ViewModels;
using SSDShredder.UI.Worker;

namespace SSDShredder.UI.Tests;

public class BinViewModelTests
{
    private static (BinViewModel ViewModel, FakeWorkerClient Worker) CreateSut()
    {
        var worker = new FakeWorkerClient();
        var viewModel = new BinViewModel(worker, new FakeFilePickerService());
        return (viewModel, worker);
    }

    [Fact]
    public async Task EligibleSelection_MovesTo_AwaitingConfirmation()
    {
        var (viewModel, worker) = CreateSut();
        worker.NextInspectResult = new InspectResult(true, InspectReason.Eligible, "nvme", 4096, 615, "abc123token", IsMock: true);

        await viewModel.RequestSelectionAsync("/tmp/demo.bin", SelectionSource.PanelDrop);

        Assert.Equal(BinState.AwaitingConfirmation, viewModel.State);
        Assert.True(viewModel.CanApprove);
        Assert.Equal(4096, viewModel.CoveredBytes);
        Assert.Equal(615, viewModel.TailBytes);
        Assert.Equal(["/tmp/demo.bin"], worker.InspectedPaths);
    }

    [Fact]
    public async Task IneligibleSelection_MovesTo_Attention_AndOffersVault()
    {
        var (viewModel, _) = CreateSut();
        // CreateSut's default NextInspectResult is already ineligible.

        await viewModel.RequestSelectionAsync("/tmp/demo.bin", SelectionSource.Desktop);

        Assert.Equal(BinState.Attention, viewModel.State);
        Assert.True(viewModel.OfferVault);
        Assert.False(viewModel.CanApprove);
    }

    [Fact]
    public async Task Approve_SendsTheTokenInspectReturned_AndCompletes()
    {
        var (viewModel, worker) = CreateSut();
        worker.NextInspectResult = new InspectResult(true, InspectReason.Eligible, "nvme", 4096, 0, "the-real-token", IsMock: true);
        worker.NextShredResult = new ShredResult(TerminalState.Completed, 1, 1, 4096, 0, EntryOutcome.Removed, IsMock: true);
        await viewModel.RequestSelectionAsync("/tmp/demo.bin", SelectionSource.PanelDrop);

        await viewModel.ApproveCommand.ExecuteAsync(null);

        Assert.Equal(BinState.Completed, viewModel.State);
        Assert.Equal(("/tmp/demo.bin", "the-real-token"), worker.ShreddedPaths.Single());
        Assert.Contains("Removed", viewModel.ReceiptText);
    }

    [Fact]
    public async Task SecondSelection_WhileBusy_IsRejectedWithoutDisturbingTheFirst()
    {
        var (viewModel, worker) = CreateSut();
        worker.InspectGate = new TaskCompletionSource();
        var firstSelection = viewModel.RequestSelectionAsync("/tmp/first.bin", SelectionSource.Desktop);

        // The first inspect is still pending (gated); a second activation must not hijack it.
        await viewModel.RequestSelectionAsync("/tmp/second.bin", SelectionSource.Terminal);

        Assert.Equal("first.bin", viewModel.SelectedFileName);
        Assert.NotNull(viewModel.BusyNotice);
        Assert.DoesNotContain("/tmp/second.bin", worker.InspectedPaths);

        worker.InspectGate.SetResult();
        await firstSelection;
    }

    [Fact]
    public async Task Cancel_WhileInspecting_ReturnsToIdle()
    {
        var (viewModel, worker) = CreateSut();
        worker.InspectGate = new TaskCompletionSource();
        var selection = viewModel.RequestSelectionAsync("/tmp/first.bin", SelectionSource.Desktop);

        viewModel.CancelCommand.Execute(null);
        worker.InspectGate.SetResult();
        await selection;

        Assert.Equal(BinState.Idle, viewModel.State);
        Assert.Null(viewModel.SelectedPath);
    }

    [Fact]
    public async Task MockResults_AreLabeled_OnTheViewModel()
    {
        var (viewModel, worker) = CreateSut();
        worker.NextInspectResult = new InspectResult(true, InspectReason.Eligible, "nvme", 4096, 0, "tok", IsMock: true);

        await viewModel.RequestSelectionAsync("/tmp/demo.bin", SelectionSource.PanelDrop);

        Assert.True(viewModel.LastResultIsMock);
        Assert.NotEmpty(viewModel.MockLabels);
    }
}
