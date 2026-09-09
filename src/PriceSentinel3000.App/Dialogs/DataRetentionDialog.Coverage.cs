using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Dialogs;

public partial class DataRetentionDialog
{
    private CancellationTokenSource? _coverageCancellation;
    private IInputElement? _coveragePreviousFocus;

    private async void LocalLibraryGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(LocalLibraryGrid, source) is DataGridRow { Item: LibraryDaySummary day })
        {
            e.Handled = true;
            await ShowLibraryCoverageAsync(day);
        }
    }

    private async void LocalLibraryGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LocalLibraryGrid.SelectedItem is LibraryDaySummary day)
        {
            e.Handled = true;
            await ShowLibraryCoverageAsync(day);
        }
    }

    internal async Task ShowLibraryCoverageAsync(LibraryDaySummary day)
    {
        if (DataContext is not DataRetentionViewModel viewModel) return;
        CloseLibraryCoverage(restoreFocus: false);
        _coveragePreviousFocus = Keyboard.FocusedElement;
        var cancellation = new CancellationTokenSource();
        _coverageCancellation = cancellation;
        LibraryCoverageTitle.Text = $"{day.Symbol} · {day.TradingDate:yyyy-MM-dd}";
        LibraryCoverageMessage.Text = "Loading saved coverage…";
        LibraryCoverageMessage.Visibility = Visibility.Visible;
        LibraryCoverageContent.Visibility = Visibility.Collapsed;
        LibraryCoverageContent.DataContext = null;
        LibraryCoverageOverlay.Visibility = Visibility.Visible;
        LibraryCoverageCloseButton.Focus();
        try
        {
            LibraryCoverageTimeline timeline = await viewModel.LoadLibraryCoverageAsync(day, cancellation.Token);
            if (!ReferenceEquals(_coverageCancellation, cancellation)) return;
            LibraryCoverageContent.DataContext = timeline;
            LibraryCoverageContent.Visibility = Visibility.Visible;
            LibraryCoverageMessage.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (ReferenceEquals(_coverageCancellation, cancellation))
                LibraryCoverageMessage.Text = $"Coverage could not be displayed. {exception.Message}";
        }
    }

    private void LibraryCoverageOverlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, LibraryCoverageOverlay))
        {
            e.Handled = true;
            CloseLibraryCoverage();
        }
    }

    private void LibraryCoverageClose_Click(object sender, RoutedEventArgs e) => CloseLibraryCoverage();

    private void CoverageWindow_Deactivated(object? sender, EventArgs e) => CloseLibraryCoverage(restoreFocus: false);

    private void CloseLibraryCoverage(bool restoreFocus = true)
    {
        _coverageCancellation?.Cancel();
        _coverageCancellation?.Dispose();
        _coverageCancellation = null;
        LibraryCoverageOverlay.Visibility = Visibility.Collapsed;
        LibraryCoverageContent.DataContext = null;
        if (restoreFocus && _coveragePreviousFocus is { } previous) Keyboard.Focus(previous);
        _coveragePreviousFocus = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseLibraryCoverage(restoreFocus: false);
        base.OnClosed(e);
    }
}