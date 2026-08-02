using System.Windows;
using System.Windows.Controls;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
        : this(new MainViewModel())
    {
    }

    internal MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState is WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is null)
        {
            return;
        }

        bool isMaximized = WindowState is WindowState.Maximized;
        MaximizeButton.Content = isMaximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = isMaximized ? "Restore" : "Maximize";
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string modeName } ||
            !Enum.TryParse(modeName, ignoreCase: true, out TradingMode requestedMode))
        {
            return;
        }

        _viewModel.RequestModeSelection(requestedMode);

        if (requestedMode is not TradingMode.Live)
        {
            return;
        }

        if (!_viewModel.LiveRiskAcknowledged)
        {
            var warning = new LiveRiskDialog
            {
                Owner = this,
            };

            if (warning.ShowDialog() is not true)
            {
                _viewModel.CancelModeSelection();
                return;
            }
        }

        await _viewModel.AcknowledgeLiveRiskAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
