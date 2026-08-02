using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Core.Tests.MarketData;

public sealed class SyntheticMarketDataSourceTests
{
    [Fact]
    public void Reconciliation_ReturnsTheSameMarketValuesForTheSameSourceTimestamp()
    {
        var source = new SyntheticMarketDataSource();
        var request = new MarketDataRequest(
            new("SOFI"),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(4));
        DateTimeOffset timestamp =
            new(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);

        MarketQuote first = source.GetHistory(
            request,
            timestamp,
            timestamp,
            timestamp.AddSeconds(1)).Single();
        MarketQuote verification = source.GetHistory(
            request,
            timestamp,
            timestamp,
            timestamp.AddSeconds(45)).Single();

        Assert.Equal(first.SourceTimestampUtc, verification.SourceTimestampUtc);
        Assert.Equal(first.Bid, verification.Bid);
        Assert.Equal(first.Ask, verification.Ask);
        Assert.Equal(first.Last, verification.Last);
        Assert.NotEqual(first.ObservedAtUtc, verification.ObservedAtUtc);
    }

    [Fact]
    public void WarmStart_UsesTheConfiguredPollingInterval()
    {
        var source = new SyntheticMarketDataSource();
        var request = new MarketDataRequest(
            new("SOFI"),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(4));
        DateTimeOffset start =
            new(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);

        IReadOnlyList<MarketQuote> quotes = source.GetHistory(
            request,
            start,
            start.AddMinutes(4),
            start.AddMinutes(4));

        Assert.Equal(49, quotes.Count);
        Assert.All(
            quotes.Zip(quotes.Skip(1)),
            pair => Assert.Equal(
                TimeSpan.FromSeconds(5),
                pair.Second.SourceTimestampUtc - pair.First.SourceTimestampUtc));
    }
}
