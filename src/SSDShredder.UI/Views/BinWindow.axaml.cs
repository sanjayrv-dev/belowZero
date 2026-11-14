using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SSDShredder.UI.ViewModels;

namespace SSDShredder.UI.Views;

/// <summary>
/// The Avalonia half of "a persistent, borderless, transparent, topmost
/// desktop drop-bin icon ... that supports drag-and-drop file ingestion".
/// Avalonia's DragDrop is cross-platform, so this one drop zone works
/// identically for Engineer 3's macOS dev loop and for Windows users who
/// drag onto the live panel; the *desktop icon's* OS-level drop target is a
/// separate native concern owned by SSDShredder.ShellExtension's
/// DesktopBroker, which hands off through ActivationRouter instead.
/// </summary>
public partial class BinWindow : Window
{
    public BinWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private BinViewModel ViewModel => (BinViewModel)DataContext!;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Accept exactly one filesystem item; a MOVE-only drag source is never granted deletion.
        var files = e.Data.GetFiles();
        e.DragEffects = files is not null && files.Count() == 1 && !ViewModel.IsBusy
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = e.Data.GetFiles()?.ToList();
        if (files is not { Count: 1 })
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (path is not null)
        {
            await ViewModel.RequestSelectionAsync(path, SelectionSource.PanelDrop);
        }
    }
}
