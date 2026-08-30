using System.Windows;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Dialogs;

public partial class ExistingLivePositionDialog : Window
{
    public ExistingLivePositionDialog(ExistingLivePositionPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        InitializeComponent();
        DataContext = prompt;
        SellNowButton.Content = $"SELL NOW (~{prompt.EstimatedSellPrice:C2})";
        SellNowButton.IsEnabled = prompt.CanSellNow;
        SellNowButton.ToolTip = prompt.SellNowBlockReason;
        MonitorButton.IsEnabled = prompt.CanMonitorForProfit;
        MonitorButton.ToolTip = prompt.MonitorBlockReason;

        string[] blockers =
        [
            .. new[]
            {
                prompt.SellNowBlockReason,
                prompt.MonitorBlockReason,
            }.Where(reason => !string.IsNullOrWhiteSpace(reason))!,
        ];
        if (blockers.Length > 0)
        {
            ActionBlockWarningText.Text = string.Join(" ", blockers);
            ActionBlockWarningText.Visibility = Visibility.Visible;
        }
    }

    public ExistingLivePositionChoice Choice { get; private set; } =
        ExistingLivePositionChoice.Cancel;

    private void SellNow_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExistingLivePositionChoice.SellNow;
        DialogResult = true;
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExistingLivePositionChoice.MonitorForProfit;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
