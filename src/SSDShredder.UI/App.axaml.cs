using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SSDShredder.UI.Activation;
using SSDShredder.UI.Services;
using SSDShredder.UI.ViewModels;
using SSDShredder.UI.Views;
using SSDShredder.UI.Worker;

namespace SSDShredder.UI;

public partial class App : Avalonia.Application
{
    private ActivationRouter? _router;
    private BinViewModel? _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Program.Main already tried to forward this activation to a running
            // owner before starting Avalonia at all; reaching here means this
            // process is becoming the owner, so it applies its own startup
            // envelope to the window it is about to create.
            var startupEnvelope = ParseStartupArgs(desktop.Args ?? []);

            _viewModel = new BinViewModel(CreateWorkerClient(), new AvaloniaFilePickerService(() => _window!));
            _window = new BinWindow { DataContext = _viewModel };
            PositionNearScreenCorner(_window);
            desktop.MainWindow = _window;

            _router = new ActivationRouter(OnActivationReceivedAsync);
            _router.StartOwning();

            ApplyEnvelope(startupEnvelope);

            desktop.ShutdownRequested += (_, _) => _router?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private Window? _window;

    public static ActivationEnvelope ParseStartupArgs(string[] args)
    {
        // Dev/demo convenience for Engineer 3's macOS loop: `dotnet run -- <path>`
        // simulates a terminal quick-launch "select"; no args simulates the
        // desktop icon's bare "open". The real desktop/explorer/terminal
        // entry points call ActivationRouter the same way through their own
        // native or CLI processes once SSDShredder.ShellExtension/Worker exist.
        return args.Length == 1
            ? ActivationEnvelope.CreateSelect(ActivationSource.Terminal, args[0])
            : ActivationEnvelope.CreateOpen(ActivationSource.Desktop);
    }

    private Task<bool> OnActivationReceivedAsync(ActivationEnvelope envelope, CancellationToken cancellationToken)
    {
        ApplyEnvelope(envelope);
        return Task.FromResult(true);
    }

    private void ApplyEnvelope(ActivationEnvelope envelope)
    {
        if (_viewModel is null || _window is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            _window.Show();
            _window.Activate();

            if (envelope.Action == ActivationAction.Select)
            {
                var source = envelope.Source switch
                {
                    ActivationSource.Desktop => SelectionSource.Desktop,
                    ActivationSource.Explorer => SelectionSource.Explorer,
                    _ => SelectionSource.Terminal,
                };
                await _viewModel.RequestSelectionAsync(envelope.Paths[0], source);
            }
        });
    }

    /// <summary>
    /// SSDSHREDDER_REAL_WORKER=1 on Windows opts into the real ProcessWorkerClient
    /// (launches SSDShredder.Worker.exe and speaks the real protocol) instead of
    /// the default, demo-safe SimulatedWorkerClient. Written and reviewed without
    /// a Windows machine available — see ProcessWorkerClient.cs's own header
    /// before trusting it with real data. Falls back to simulated, with a clear
    /// stderr message, if the opt-in is set but the worker can't be found (e.g.
    /// it isn't installed next to this UI yet).
    /// </summary>
    private static IWorkerClient CreateWorkerClient()
    {
        bool wantsReal = Environment.GetEnvironmentVariable("SSDSHREDDER_REAL_WORKER") == "1";
        if (wantsReal && OperatingSystem.IsWindows())
        {
            try
            {
                ProcessWorkerClient.ResolveWorkerExecutablePath(); // throws early, before committing to it
                return new ProcessWorkerClient();
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine($"SSDSHREDDER_REAL_WORKER=1 but {ex.Message} Falling back to the simulated worker.");
            }
        }

        return new SimulatedWorkerClient();
    }

    private static void PositionNearScreenCorner(Window window)
    {
        try
        {
            var screen = window.Screens?.Primary;
            if (screen is null)
            {
                return;
            }

            var area = screen.WorkingArea;
            window.Position = new PixelPoint(
                area.X + area.Width - (int)window.Width - 24,
                area.Y + area.Height - (int)window.Height - 24);
        }
        catch (Exception)
        {
            // Best-effort placement only; the window still opens at its default position.
        }
    }
}
