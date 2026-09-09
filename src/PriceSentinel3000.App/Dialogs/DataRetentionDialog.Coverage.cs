using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Dialogs;

public partial class DataRetentionDialog
{
    private CancellationTokenSource? _coverageCancellation;
    private IInputElement? _coveragePreviousFocus;
    private DataRetentionViewModel? _coverageViewModel;
    private LibraryDaySummary? _coverageDay;
    private bool _coverageDownloadRequested;
    private int _coverageSelectionVersion;
    private int? _coverageDownloadSelectionVersion;

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
        CloseDownloadInfo(restoreFocus: false);
        CloseLibraryCoverage(restoreFocus: false);
        _coveragePreviousFocus = Keyboard.FocusedElement;
        _coverageViewModel = viewModel;
        _coverageDay = day;
        _coverageDownloadRequested = false;
        _coverageDownloadSelectionVersion = null;
        LibraryCoverageDownloadStatus.Text = string.Empty;
        viewModel.CoverageDownloadUpdated += OnCoverageDownloadUpdated;
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

    private void LibraryCoverageBlockSelectionChanged(object? sender, EventArgs e)
    {
        _coverageSelectionVersion++;
        LibraryCoverageDownloadStatus.Text = string.Empty;
    }

    private async void LibraryCoverageDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_coverageViewModel is not { } viewModel || !viewModel.CanDownloadCoverage ||
            LibraryCoverageContent.DataContext is not LibraryCoverageTimeline timeline ||
            LibraryCoverageTimeline.SelectedBlock is not { State: LibraryCoverageBlockState.Missing } selected) return;
        _coverageDownloadRequested = true;
        int selectionVersion = ++_coverageSelectionVersion;
        _coverageDownloadSelectionVersion = selectionVersion;
        CancellationTokenSource? popup = _coverageCancellation;
        LibraryCoverageDownloadStatus.Text = "Downloading connected missing blocks…";
        await viewModel.DownloadCoverageAsync(timeline, selected);
        if (ReferenceEquals(_coverageCancellation, popup) && _coverageSelectionVersion == selectionVersion)
            LibraryCoverageDownloadStatus.Text = viewModel.CoverageDownloadStatus;
    }

    private async void OnCoverageDownloadUpdated(object? sender, EventArgs e)
    {
        if (!_coverageDownloadRequested || _coverageViewModel is not { } viewModel ||
            _coverageDay is not { } day || _coverageCancellation is not { } cancellation) return;
        int? selectionVersion = _coverageDownloadSelectionVersion;
        try
        {
            LibraryCoverageTimeline timeline = await viewModel.LoadLibraryCoverageAsync(day, cancellation.Token);
            if (!ReferenceEquals(_coverageCancellation, cancellation)) return;
            // Keep the latest selection if the user moved while coverage was loading.
            DateTimeOffset? selectedFrom = LibraryCoverageTimeline.SelectedBlock?.FromUtc;
            LibraryCoverageContent.DataContext = timeline;
            LibraryCoverageTimeline.GetBindingExpression(Controls.LibraryCoverageTimelineControl.TimelineProperty)?.UpdateTarget();
            if (selectedFrom is { } from) LibraryCoverageTimeline.SelectBlockStartingAt(from);
            if (_coverageSelectionVersion == selectionVersion)
                LibraryCoverageDownloadStatus.Text = viewModel.CoverageDownloadStatus;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (ReferenceEquals(_coverageCancellation, cancellation) && _coverageSelectionVersion == selectionVersion)
                LibraryCoverageDownloadStatus.Text = $"Coverage could not be refreshed. {exception.Message}";
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
        if (_coverageViewModel is { } viewModel) viewModel.CoverageDownloadUpdated -= OnCoverageDownloadUpdated;
        _coverageViewModel = null;
        _coverageDay = null;
        _coverageDownloadRequested = false;
        _coverageDownloadSelectionVersion = null;
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
        CloseDownloadInfo(restoreFocus: false);
        CloseLibraryCoverage(restoreFocus: false);
        base.OnClosed(e);
    }
}