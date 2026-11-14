using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SSDShredder.UI.Services;
using SSDShredder.UI.Worker;

namespace SSDShredder.UI.ViewModels;

/// <summary>
/// Drives the single bin panel. One instance per process (ActivationRouter
/// guarantees one process per user/logon session). Owns the idle → inspecting
/// → awaitingConfirmation → processing → completed|attention lifecycle and
/// the "busy while active" rule for selections that arrive mid-operation.
/// </summary>
public sealed partial class BinViewModel : ObservableObject
{
    private readonly IWorkerClient _workerClient;
    private readonly IFilePickerService _filePicker;
    private CancellationTokenSource? _operationCts;
    private string? _pendingSelectionToken;

    public BinViewModel(IWorkerClient workerClient, IFilePickerService filePicker)
    {
        _workerClient = workerClient;
        _filePicker = filePicker;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private BinState _state = BinState.Idle;

    [ObservableProperty]
    private string? _selectedPath;

    [ObservableProperty]
    private string? _selectedFileName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    private bool _isEligible;

    [ObservableProperty]
    private long _coveredBytes;

    [ObservableProperty]
    private long _tailBytes;

    [ObservableProperty]
    private InspectReason? _ineligibleReason;

    [ObservableProperty]
    private bool _offerVault;

    [ObservableProperty]
    private string? _receiptText;

    [ObservableProperty]
    private bool _lastResultIsMock;

    [ObservableProperty]
    private WorkerPhase? _currentPhase;

    /// <summary>
    /// Accessible, text-only status for AutomationProperties live-region
    /// announcements ("Use text and accessible announcements alongside color").
    /// </summary>
    [ObservableProperty]
    private string _statusAnnouncement = "Idle. Drop a file or press Enter to choose one.";

    [ObservableProperty]
    private string? _busyNotice;

    public ObservableCollection<string> MockLabels { get; } = new();

    public bool IsBusy => State is BinState.Inspecting or BinState.AwaitingConfirmation or BinState.Processing;

    public bool CanApprove => State == BinState.AwaitingConfirmation && IsEligible;

    public bool CanCancel => State is BinState.Inspecting or BinState.AwaitingConfirmation;

    /// <summary>
    /// Entry point for every selection source: desktop drop (via ActivationRouter),
    /// Explorer command, terminal quick launch with an argument, the panel's own
    /// drag-and-drop zone, and the keyboard file picker.
    /// </summary>
    public async Task RequestSelectionAsync(string absolutePath, SelectionSource source)
    {
        if (IsBusy)
        {
            BusyNotice = $"Busy with {SelectedFileName}. Finish or cancel before selecting another file.";
            StatusAnnouncement = BusyNotice;
            return;
        }

        BusyNotice = null;
        SelectedPath = absolutePath;
        SelectedFileName = Path.GetFileName(absolutePath);
        ReceiptText = null;
        OfferVault = false;
        State = BinState.Inspecting;
        StatusAnnouncement = $"Inspecting {SelectedFileName} from {source}.";

        _operationCts = new CancellationTokenSource();
        try
        {
            var result = await _workerClient.InspectAsync(
                absolutePath,
                phase => CurrentPhase = phase.Phase,
                _operationCts.Token).ConfigureAwait(true);

            ApplyInspectResult(result);
        }
        catch (OperationCanceledException)
        {
            ResetToIdle();
        }
    }

    private void ApplyInspectResult(InspectResult result)
    {
        IsEligible = result.Eligible;
        CoveredBytes = result.CoveredBytes;
        TailBytes = result.TailBytes;
        IneligibleReason = result.Eligible ? null : result.Reason;
        _pendingSelectionToken = result.SelectionToken;
        NoteMockObservation(result.IsMock, nameof(InspectResult));

        if (result.Eligible)
        {
            State = BinState.AwaitingConfirmation;
            StatusAnnouncement =
                $"{SelectedFileName}: {CoveredBytes:N0} covered bytes, {TailBytes:N0} uncovered tail. Approve to shred, or cancel.";
        }
        else
        {
            State = BinState.Attention;
            OfferVault = true;
            StatusAnnouncement = $"{SelectedFileName} is not eligible for secure disposal ({result.Reason}). A vault is offered instead.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        if (SelectedPath is null || _pendingSelectionToken is null)
        {
            return;
        }

        State = BinState.Processing;
        StatusAnnouncement = $"Shredding {SelectedFileName}.";
        _operationCts = new CancellationTokenSource();

        try
        {
            var result = await _workerClient.ShredAsync(
                SelectedPath, _pendingSelectionToken,
                phase => CurrentPhase = phase.Phase,
                _operationCts.Token).ConfigureAwait(true);

            ApplyShredResult(result);
        }
        catch (OperationCanceledException)
        {
            // A confirmed shred is never replayed or silently discarded; surface it as indeterminate.
            State = BinState.Attention;
            StatusAnnouncement = $"{SelectedFileName}: confirmed operation lost its connection before a result arrived.";
        }
    }

    private void ApplyShredResult(ShredResult result)
    {
        _pendingSelectionToken = null;
        NoteMockObservation(result.IsMock, nameof(ShredResult));

        ReceiptText = result.State == TerminalState.Completed
            ? $"Removed. {result.CoveredBytes:N0} bytes covered, {result.TailBytes:N0} uncovered tail."
            : $"{result.State}: {result.Entry} — {SelectedFileName} left in place for review.";

        State = result.State == TerminalState.Completed ? BinState.Completed : BinState.Attention;
        StatusAnnouncement = ReceiptText;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _operationCts?.Cancel();
        ResetToIdle();
    }

    [RelayCommand]
    private void Dismiss() => ResetToIdle();

    [RelayCommand]
    private async Task OfferVaultInitAsync()
    {
        StatusAnnouncement = "Creating a new vault.";
        _operationCts = new CancellationTokenSource();
        var result = await _workerClient.VaultInitAsync(
            phase => CurrentPhase = phase.Phase,
            _operationCts.Token).ConfigureAwait(true);

        NoteMockObservation(result.IsMock, nameof(VaultResult));
        ReceiptText = $"Vault {result.VaultId} is {result.Phase}" + (result.Mount is null ? "." : $" at {result.Mount}.");
        State = BinState.Completed;
        StatusAnnouncement = ReceiptText;
    }

    [RelayCommand]
    private async Task PickFileAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var path = await _filePicker.PickFileAsync(CancellationToken.None).ConfigureAwait(true);
        if (path is not null)
        {
            await RequestSelectionAsync(path, SelectionSource.FilePicker).ConfigureAwait(true);
        }
    }

    private void NoteMockObservation(bool isMock, string what)
    {
        LastResultIsMock = isMock;
        if (isMock)
        {
            MockLabels.Add($"{what} is a simulated worker observation, not a real device result.");
        }
    }

    private void ResetToIdle()
    {
        State = BinState.Idle;
        SelectedPath = null;
        SelectedFileName = null;
        OfferVault = false;
        CurrentPhase = null;
        StatusAnnouncement = "Idle. Drop a file or press Enter to choose one.";
    }
}
