using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Infrastructure.Automation;
using PriceSentinel3000.Infrastructure.MarketData;
using PriceSentinel3000.Infrastructure.Storage;
using PriceSentinel3000.Infrastructure.Strategies;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Avoid stale/clipped GPU text surfaces when a window moves off-screen and back.
        // Apply before creating any windows; this preference affects only this process.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        RobinhoodMcpGateway robinhoodGateway = RobinhoodMcpGateway.CreateDefault();
        var viewModel = new MainViewModel(
            robinhoodGateway,
            robinhoodGateway,
            robinhoodGateway,
            robinhoodGateway,
            new SqliteTradingJournal(AppDataPaths.JournalDatabase),
            new JsonUserPreferencesStore(AppDataPaths.UserPreferences),
            TimeProvider.System,
            FileSystemStrategyCatalog.CreateDefault());
        try
        {
            var collector = await Task.Run(() => new MarketDataCollector(
                new JsonCollectionStateStore(AppDataPaths.CollectionState), robinhoodGateway,
                root => new JsonMarketDataLibrary(root)));
            viewModel.DataRetention = new DataRetentionViewModel(collector, robinhoodGateway,
                robinhoodGateway, robinhoodGateway, root => new JsonMarketDataLibrary(root),
                async token => { if (!robinhoodGateway.HasActiveConnection) await viewModel.ConnectRobinhoodAtStartupAsync(token); },
                () => robinhoodGateway.HasActiveConnection, Dispatcher, async token =>
                {
                    if (viewModel.IsSessionRunning || viewModel.StartSessionCommand.ExecutionTask is { IsCompleted: false })
                        throw new InvalidOperationException("Stop the trading session before reconnecting Robinhood.");
                    await robinhoodGateway.ReconnectAsync(token);
                    await viewModel.ConnectRobinhoodAtStartupAsync(token);
                });
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            MessageBox.Show($"The saved data-collection settings could not be loaded: {exception.Message}\n\nRestore collection-state.json from a valid copy before using automatic downloads.",
                "Data library settings", MessageBoxButton.OK, MessageBoxImage.Error);
            await viewModel.ShutdownAsync();
            Shutdown();
            return;
        }
        bool enableAutomation = e.Args.Contains("--automation", StringComparer.OrdinalIgnoreCase);
        // Automation can inspect and configure the OFF workspace without logging in.
        // Starting a data session still uses the normal Robinhood connection flow.
        if (!enableAutomation)
        {
            using var restoreCancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool restored = await viewModel.TryRestoreRobinhoodAtStartupAsync(
                restoreCancellation.Token);

            if (!restored)
            {
                var welcome = new WelcomeDialog(
                    viewModel.ConnectRobinhoodAtStartupAsync);

                if (welcome.ShowDialog() is not true)
                {
                    await viewModel.ShutdownAsync();
                    Shutdown();
                    return;
                }
            }
        }

        AutomationPipeServer? automationServer = null;
        if (enableAutomation)
        {
            try
            {
                int pipeArgument = Array.FindIndex(e.Args, argument =>
                    argument.Equals("--automation-pipe", StringComparison.OrdinalIgnoreCase));
                string? pipeName = pipeArgument < 0 ? null :
                    pipeArgument + 1 < e.Args.Length ? e.Args[pipeArgument + 1] :
                    throw new ArgumentException("--automation-pipe requires a pipe name.");
                automationServer = new AutomationPipeServer(
                    (request, cancellationToken) => Dispatcher.InvokeAsync(
                        () => viewModel.HandleAutomationAsync(request),
                        System.Windows.Threading.DispatcherPriority.Normal,
                        cancellationToken).Task.Unwrap(),
                    pipeName);
                automationServer.Start();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
            {
                if (automationServer is not null)
                {
                    await automationServer.DisposeAsync();
                    automationServer = null;
                }
                MessageBox.Show($"Local automation could not start: {exception.Message}",
                    "PriceSentinel automation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        var mainWindow = new MainWindow(viewModel, automationServer);
        if (automationServer is not null)
        {
            mainWindow.Title += " — Automation enabled (Replay / Paper)";
        }
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
        viewModel.DataRetention?.Start();
    }
}
