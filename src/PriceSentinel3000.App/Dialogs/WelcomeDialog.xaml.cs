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

        ErrorPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "Waiting for Robinhood approval. In the browser, finish any authorization shown and wait for the PriceSentinel completion page...";
        StatusPanel.Visibility = Visibility.Visible;
        LoginButton.IsEnabled = false;
        LoginButton.Content = "WAITING FOR APPROVAL...";
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
                ShowError(_loginCancellation.IsCancellationRequested
                    ? "Robinhood login was cancelled. Click LOGIN to try again, or EXIT."
                    : CreateConnectionErrorMessage(new TimeoutException()));
            }
        }
        catch (Exception exception)
        {
            if (!_isClosing)
            {
                ShowError(CreateConnectionErrorMessage(exception));
            }
        }
        finally
        {
            _loginCancellation?.Dispose();
            _loginCancellation = null;

            if (!_isClosing)
            {
                LoginButton.IsEnabled = true;
                LoginButton.Content = "LOGIN";
            }
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _isClosing = true;
        _loginCancellation?.Cancel();
        DialogResult = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Exit_Click(sender, e);

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
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static string CreateConnectionErrorMessage(Exception exception)
    {
        if (IsTimeout(exception))
        {
            return "Robinhood did not finish authorization in time. Click LOGIN again, approve PriceSentinel in the browser, and wait for the completion page before returning to the app.";
        }

        return $"Could not connect to Robinhood: {exception.Message} Check the connection and try LOGIN again.";
    }

    private static bool IsTimeout(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is TimeoutException ||
                current.Message.Contains(
                    "Initialization timed out",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
