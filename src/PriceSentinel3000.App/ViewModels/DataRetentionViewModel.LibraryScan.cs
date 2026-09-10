using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class DataRetentionViewModel
{
    private bool _isLibraryScanning;
    private int _libraryScanPercent;
    private int _libraryScanGeneration;
    private string _librarySizeText = "";

    public bool IsLibraryScanning
    {
        get => _isLibraryScanning;
        private set
        {
            _isLibraryScanning = value;
            Changed();
            Changed(nameof(LibraryScanButtonText));
            RefreshState();
        }
    }

    public int LibraryScanPercent
    {
        get => _libraryScanPercent;
        private set
        {
            if (_libraryScanPercent == value) return;
            _libraryScanPercent = value;
            Changed();
            Changed(nameof(LibraryScanButtonText));
        }
    }

    public string LibraryScanButtonText => IsLibraryScanning
        ? $"Processing\n{LibraryScanPercent}% Complete" : "RESCAN LIBRARY";

    public string LibrarySizeText
    {
        get => _librarySizeText;
        private set
        {
            if (_librarySizeText == value) return;
            _librarySizeText = value;
            Changed();
        }
    }

    public async Task ScanLibraryAsync()
    {
        if (IsLibraryScanning || _disposed) return;
        int generation = ++_libraryScanGeneration;
        LibraryScanPercent = 0;
        IsLibraryScanning = true;
        try
        {
            LibraryDiagnostics = "";
            IMarketDataLibrary library = CreateLibrary();
            var progress = new Progress<int>(percent =>
            {
                // Ignore queued callbacks from a finished scan. Reserve 100% for
                // publishing the scanned rows, after disk work and summaries finish.
                if (!_disposed && IsLibraryScanning && generation == _libraryScanGeneration)
                    LibraryScanPercent = Math.Max(LibraryScanPercent, Math.Clamp(percent, 0, 99));
            });
            MarketDataLibraryScan scan = await Collector.ScanLibraryAsync(library, progress, _lifetime.Token);
            var (days, now) = await Task.Run(() =>
            {
                DateTimeOffset at = _clock.GetUtcNow();
                return (LibraryDaySummary.Create(scan.Datasets, at), at);
            }, _lifetime.Token);
            LibraryDaySummary? selected = SelectedLibraryDay;
            LibraryDays.Clear();
            foreach (LibraryDaySummary day in days) LibraryDays.Add(day);
            SelectedLibraryDay = LibraryDays.FirstOrDefault(day => selected is not null && day.Symbol == selected.Symbol && day.TradingDate == selected.TradingDate);
            Changed(nameof(SelectedLibraryDay));
            Datasets.Clear();
            foreach (HistoricalDatasetInfo dataset in scan.Datasets.OrderByDescending(d => d.TradingDate).ThenBy(d => d.Symbol)) Datasets.Add(dataset);
            LibraryDiagnostics = string.Join("\n\n", scan.Diagnostics.Select(d => $"{d.RelativePath} [{d.Code}]\n{d.Message}"));
            Status = $"Found {days.Count} daily entries from {scan.Datasets.Count} saved files. {scan.Diagnostics.Count} scan notices." +
                (HasLibraryDiagnostics ? " Open Library details." : "");
            LibrarySizeText = $"{scan.TotalFileBytes / 1_000_000m:#,0.##} MB";
            _libraryCoverageClockSlot = now.UtcTicks / (15 * TimeSpan.TicksPerSecond);
            RefreshJobRows(now);
            LibraryScanPercent = 100;
        }
        finally { IsLibraryScanning = false; }
    }
}
