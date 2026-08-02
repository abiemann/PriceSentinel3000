using System.Windows;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var viewModel = new MainViewModel();
        var welcome = new WelcomeDialog(
            viewModel.ConnectRobinhoodAtStartupAsync);

        if (welcome.ShowDialog() is not true)
        {
            viewModel.Dispose();
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow(viewModel);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }
}
