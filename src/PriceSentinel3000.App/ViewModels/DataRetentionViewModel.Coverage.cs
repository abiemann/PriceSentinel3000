using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class DataRetentionViewModel
{
    public async Task<LibraryCoverageTimeline> LoadLibraryCoverageAsync(
        LibraryDaySummary day, CancellationToken cancellationToken)
    {
        // Use the same validated inventory and Eastern date as the table.
        // Opening a row never downloads candles.
        HistoricalDatasetInfo[] datasets = Datasets.Where(dataset =>
            string.Equals(dataset.Symbol, day.Symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        bool? overnight = null;
        if (_isConnected() && Provider is IEquityMarketHoursSource hours)
        {
            try { overnight = await hours.IsTwentyFourHourEligibleAsync(day.Symbol, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Unknown eligibility keeps the full day visible. A broker error must
                // never be interpreted as proof that overnight trading is unavailable.
            }
        }
        lifetime.Token.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.GetUtcNow();
        return await Task.Run(() => LibraryCoverageTimeline.Create(day.Symbol, day.TradingDate,
            TimeZoneInfo.Local, overnight, datasets, now), lifetime.Token);
    }
}