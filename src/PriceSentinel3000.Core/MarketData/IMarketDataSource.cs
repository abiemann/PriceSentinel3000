namespace PriceSentinel3000.Core.MarketData;

public interface IMarketDataSource
{
    string Name { get; }
    bool IsSynthetic { get; }

    IReadOnlyList<MarketQuote> GetHistory(
        MarketDataRequest request,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        DateTimeOffset observedAtUtc);

    MarketQuote GetQuote(MarketDataRequest request, DateTimeOffset sourceTimestampUtc);
}
