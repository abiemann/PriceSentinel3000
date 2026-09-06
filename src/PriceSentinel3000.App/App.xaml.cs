using System.Windows;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Infrastructure.Automation;
using PriceSentinel3000.Infrastructure.MarketData;
using PriceSentinel3000.Infrastructure.Storage;
using PriceSentinel3000.Infrastructure.Strategies;

namespace PriceSentinel3000.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
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
    }
}
