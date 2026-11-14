namespace SSDShredder.UI.ViewModels;

/// <summary>
/// "The bin has explicit inspecting, awaiting-confirmation, processing,
/// completed and attention states in its Avalonia panel."
/// (docs/01_DEEP_RESEARCH_AND_SPECS.md / docs/02_SYSTEM_ARCHITECTURE.md)
/// </summary>
public enum BinState
{
    Idle,
    Inspecting,
    AwaitingConfirmation,
    Processing,
    Completed,
    Attention,
}
