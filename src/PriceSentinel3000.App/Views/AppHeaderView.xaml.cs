using System.Windows;
using System.Windows.Controls;

namespace PriceSentinel3000.App.Views;

public partial class AppHeaderView : UserControl
{
    // The collection service will supply this state when it is implemented.
    // Keep the default off; opening the tool must not imply a running schedule.
    public static readonly DependencyProperty IsAutomaticDownloadEnabledProperty =
        DependencyProperty.Register(
            nameof(IsAutomaticDownloadEnabled), typeof(bool), typeof(AppHeaderView),
            new PropertyMetadata(false));

    public bool IsAutomaticDownloadEnabled
    {
        get => (bool)GetValue(IsAutomaticDownloadEnabledProperty);
        set => SetValue(IsAutomaticDownloadEnabledProperty, value);
    }

    public event RoutedEventHandler? DataRetentionRequested;

    public AppHeaderView()
    {
        InitializeComponent();
    }

    private void RetainHighResolutionDataButton_Click(object sender, RoutedEventArgs e) =>
        DataRetentionRequested?.Invoke(this, e);
}
