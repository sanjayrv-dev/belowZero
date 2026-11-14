namespace SSDShredder.UI.ViewModels;

/// <summary>
/// Where the current selection came from. <see cref="Activation.ActivationSource"/>
/// covers desktop/explorer/terminal per the wire contract; <see cref="FilePicker"/>
/// and <see cref="PanelDrop"/> are UI-local paths that never leave this process
/// (the keyboard picker and the panel's own Avalonia drag-and-drop zone).
/// </summary>
public enum SelectionSource
{
    Desktop,
    Explorer,
    Terminal,
    FilePicker,
    PanelDrop,
}
