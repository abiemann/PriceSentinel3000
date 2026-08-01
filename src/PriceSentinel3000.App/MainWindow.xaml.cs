using System.Windows;
using System.Windows.Controls;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
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

        _viewModel.AcknowledgeLiveRisk();
    }
}
