using System.Windows;
using System.Windows.Input;

namespace PriceSentinel3000.App.Dialogs;

public partial class DataRetentionDialog : Window
{
    public DataRetentionDialog()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key is Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
