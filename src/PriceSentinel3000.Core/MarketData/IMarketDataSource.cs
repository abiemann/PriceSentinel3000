namespace PriceSentinel3000.Core.MarketData;

public interface IMarketDataSource : IAsyncDisposable
{
    string Name { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MarketQuote>> GetHistoryAsync(
        MarketDataRequest request,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    Task<MarketQuote> GetQuoteAsync(
        MarketDataRequest request,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MarketQuote>> GetReplayHistoryAsync(
        Instrument instrument,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);
}
