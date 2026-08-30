using System.Windows;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Infrastructure.MarketData;
using PriceSentinel3000.Infrastructure.Storage;

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
            TimeProvider.System);
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

        var mainWindow = new MainWindow(viewModel);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }
}
