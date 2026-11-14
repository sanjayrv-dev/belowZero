using Avalonia;
using SSDShredder.UI.Activation;

namespace SSDShredder.UI;

internal static class Program
{
    // Avalonia requires a static Main that never touches types that trigger
    // visual-tree initialization before BuildAvaloniaApp runs.
    [STAThread]
    public static int Main(string[] args)
    {
        // Forwarding happens *before* any Avalonia type is touched: Avalonia's
        // ClassicDesktopStyleApplicationLifetime has no supported way to abort
        // between OnFrameworkInitializationCompleted and MainLoop once Start()
        // has begun (Shutdown() there leaves the Dispatcher in a half-stopped
        // state that then throws). A second process that only needs to hand
        // off one activation and exit should never start that lifetime at all.
        var envelope = App.ParseStartupArgs(args);
        if (ActivationRouter.TrySendToExistingOwnerAsync(envelope).GetAwaiter().GetResult())
        {
            return 0;
        }

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
