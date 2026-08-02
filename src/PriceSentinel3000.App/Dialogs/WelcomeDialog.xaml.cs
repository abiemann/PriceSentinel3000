using System.ComponentModel;
using System.Windows;

namespace PriceSentinel3000.App.Dialogs;

public partial class WelcomeDialog : Window
{
    private readonly Func<CancellationToken, Task> _loginAsync;
    private CancellationTokenSource? _loginCancellation;
    private bool _isClosing;

    public WelcomeDialog(Func<CancellationToken, Task> loginAsync)
    {
        _loginAsync = loginAsync ?? throw new ArgumentNullException(nameof(loginAsync));
        InitializeComponent();
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_loginCancellation is not null)
        {
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Text = "Connecting to Robinhood. Complete the secure browser authorization if it opens...";
        StatusPanel.Visibility = Visibility.Visible;
        LoginButton.IsEnabled = false;
        _loginCancellation = new CancellationTokenSource();

        try
        {
            await _loginAsync(_loginCancellation.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                ShowError("Robinhood login was cancelled. Check your internet connection and try LOGIN again, or EXIT.");
            }
        }
        catch (Exception exception)
        {
            if (!_isClosing)
            {
                ShowError($"Could not connect to Robinhood: {exception.Message} Check your internet connection and try again.");
            }
        }
        finally
        {
            _loginCancellation?.Dispose();
            _loginCancellation = null;

            if (!_isClosing)
            {
                LoginButton.IsEnabled = true;
            }
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _isClosing = true;
        _loginCancellation?.Cancel();
        DialogResult = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _loginCancellation?.Cancel();
        base.OnClosing(e);
    }

    private void ShowError(string message)
    {
        StatusPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
