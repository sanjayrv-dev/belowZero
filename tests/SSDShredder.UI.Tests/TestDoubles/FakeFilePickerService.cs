using SSDShredder.UI.Services;

namespace SSDShredder.UI.Tests.TestDoubles;

public sealed class FakeFilePickerService : IFilePickerService
{
    public string? NextPath { get; set; }

    public Task<string?> PickFileAsync(CancellationToken cancellationToken) => Task.FromResult(NextPath);
}
