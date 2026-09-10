using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class DataRetentionViewModel
{
    public Task<LibraryCoverageTimeline> LoadLibraryCoverageAsync(
        LibraryDaySummary day, CancellationToken cancellationToken) =>
        LoadCoverageAsync(day.Symbol, day.TradingDate, Datasets.ToArray(), cancellationToken);

    public async Task<LibraryCoverageTimeline> LoadDownloadCoverageAsync(
        CollectionJob job, CancellationToken cancellationToken)
    {
        // Queue rows can belong to a previous library folder, and need not have a library tab scan.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        MarketDataLibraryScan scan = await Task.Run(() => _libraryFactory(job.LibraryRootPath).Scan(), lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        return await LoadCoverageAsync(job.Symbol, job.SessionDate, scan.Datasets, lifetime.Token);
    }

    private async Task<LibraryCoverageTimeline> LoadCoverageAsync(string symbol, DateOnly date,
        IEnumerable<HistoricalDatasetInfo> inventory, CancellationToken cancellationToken)
    {
        // Opening a row never downloads candles or requests authentication.
        HistoricalDatasetInfo[] datasets = inventory.Where(dataset =>
            string.Equals(dataset.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        bool? overnight = null;
        if (_isConnected() && Provider is IEquityMarketHoursSource hours)
        {
            try { overnight = await hours.IsTwentyFourHourEligibleAsync(symbol, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Unknown eligibility keeps the full day visible. A broker error must
                // never be interpreted as proof that overnight trading is unavailable.
            }
        }
        lifetime.Token.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.GetUtcNow();
        return await Task.Run(() => LibraryCoverageTimeline.Create(symbol, date,
            TimeZoneInfo.Local, overnight, datasets, now), lifetime.Token);
    }
}