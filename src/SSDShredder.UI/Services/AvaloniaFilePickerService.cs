using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SSDShredder.UI.Services;

/// <summary>Opens Avalonia's cross-platform storage-provider file picker, scoped to one local file.</summary>
public sealed class AvaloniaFilePickerService(Func<Window> getWindow) : IFilePickerService
{
    public async Task<string?> PickFileAsync(CancellationToken cancellationToken)
    {
        var window = getWindow();
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a file for belowZero",
            AllowMultiple = false,
        });

        var file = files.Count > 0 ? files[0] : null;
        return file?.TryGetLocalPath();
    }
}
