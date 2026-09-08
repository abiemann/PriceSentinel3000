using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IAsyncDisposable? _automationServer;
    private DataRetentionDialog? _dataRetentionDialog;
    private bool _closeAfterShutdown;
    private bool _shutdownInProgress;

    internal MainWindow(MainViewModel viewModel, IAsyncDisposable? automationServer = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _automationServer = automationServer;
        _viewModel.ExistingLivePositionPrompt = ShowExistingLivePositionDialog;
        _viewModel.ExistingLivePositionWarning = ShowExistingLivePositionWarning;
        _viewModel.ExternalScriptApprovalPrompt = message => MessageBox.Show(
            this, message, "Review script for LIVE", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.AutomationChartCaptureRequested = ChartWorkspace.CaptureChart;
    }

    private ExistingLivePositionChoice ShowExistingLivePositionDialog(
        ExistingLivePositionPrompt prompt)
    {
        var dialog = new ExistingLivePositionDialog(prompt)
        {
            Owner = this,
        };
        return dialog.ShowDialog() is true
            ? dialog.Choice
            : ExistingLivePositionChoice.Cancel;
    }

    private void ShowExistingLivePositionWarning(string message) =>
        MessageBox.Show(
            this,
            message,
            "LIVE startup stopped",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState is WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void AppHeader_DataRetentionRequested(object sender, RoutedEventArgs e)
    {
        if (_dataRetentionDialog is not null)
        {
            _dataRetentionDialog.Activate();
            return;
        }

        _dataRetentionDialog = new DataRetentionDialog { Owner = this, DataContext = _viewModel.DataRetention };
        _dataRetentionDialog.Closed += (_, _) => _dataRetentionDialog = null;
        // Keep the trading workspace, including STOP, accessible while viewing tools.
        _dataRetentionDialog.Show();
    }

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

        if (!_viewModel.RequestModeSelection(requestedMode))
        {
            return;
        }

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

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAfterShutdown)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        _viewModel.AutomationClosing = true;
        try
        {
            if (Keyboard.FocusedElement is TextBox textBox)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Could not commit the focused input during shutdown: {0}",
                exception.Message);
        }

        try
        {
            bool safeToClose = await _viewModel.PrepareForShutdownAsync();
            if (!safeToClose)
            {
                MessageBoxResult choice = MessageBox.Show(
                    this,
                    "PriceSentinel could not confirm that the active LIVE order reached a final state. The order may still fill after this application closes.\n\nOpen Robinhood now and verify or cancel the order. Exit anyway?",
                    "Unresolved LIVE order",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (choice is not MessageBoxResult.Yes)
                {
                    _shutdownInProgress = false;
                    _viewModel.AutomationClosing = false;
                    return;
                }
            }

            if (_automationServer is not null)
            {
                await _automationServer.DisposeAsync();
            }

            await _viewModel.ShutdownAsync(
                forceUnresolvedLiveOrder: !safeToClose);
            _closeAfterShutdown = true;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"PriceSentinel could not shut down cleanly: {exception.Message}",
                "Shutdown error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _shutdownInProgress = false;
            _viewModel.AutomationClosing = false;
        }
    }
}
