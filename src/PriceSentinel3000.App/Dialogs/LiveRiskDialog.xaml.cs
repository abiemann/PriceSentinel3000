using System.Windows;

namespace PriceSentinel3000.App.Dialogs;

public partial class LiveRiskDialog : Window
{
    public LiveRiskDialog()
    {
        InitializeComponent();
    }

    private void Agree_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
