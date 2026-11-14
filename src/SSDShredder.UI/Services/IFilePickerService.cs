namespace SSDShredder.UI.Services;

/// <summary>
/// Keyboard file picker used by the bin panel's accessible selection path
/// ("Include a keyboard file picker" — docs/02_SYSTEM_ARCHITECTURE.md,
/// BinPanel and BinViewModel). Kept behind an interface so
/// <c>BinViewModel</c> stays unit-testable without an Avalonia TopLevel.
/// </summary>
public interface IFilePickerService
{
    /// <summary>Returns the chosen absolute local path, or null if cancelled.</summary>
    Task<string?> PickFileAsync(CancellationToken cancellationToken);
}
