using System.Text.Json;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Core.Tests.MarketData;

public sealed class RobinhoodMarketDataParserTests
{
    [Fact]
    public void ParseQuote_PrefersNewerNonRegularTradeAndPreservesBook()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "data": {
                "results": [{
                  "quote": {
                    "symbol": "SOFI",
                    "last_trade_price": "16.31",
                    "venue_last_trade_time": "2026-07-31T19:59:59Z",
                    "last_non_reg_trade_price": "16.21",
                    "venue_last_non_reg_trade_time": "2026-07-31T23:59:15Z",
                    "bid_price": "16.20",
                    "ask_price": "16.25",
                    "has_traded": true,
                    "state": "active"
                  }
                }]
              }
            }
            """);
        var instrument = new Instrument("SOFI", AssetClass.Equity);
        DateTimeOffset observedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

        MarketQuote quote = RobinhoodMarketDataParser.ParseQuote(
            document.RootElement,
            instrument,
            observedAt);

        Assert.Equal(16.21m, quote.Last);
        Assert.Equal(16.20m, quote.Bid);
        Assert.Equal(16.25m, quote.Ask);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-31T23:59:15Z"),
            quote.SourceTimestampUtc);
    }

    [Fact]
    public void ParseHistory_DropsInterpolatedBarsAndOrdersRealObservations()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "data": {
                "results": [{
                  "symbol": "SOFI",
                  "interval": "15second",
                  "bounds": "extended",
                  "bars": [
                    {
                      "begins_at": "2026-07-31T18:00:15Z",
                      "open_price": "16.38",
                      "high_price": "16.42",
                      "low_price": "16.37",
                      "close_price": "16.40",
                      "volume": 200
                    },
                    {
                      "begins_at": "2026-07-31T18:00:00Z",
                      "close_price": "16.39",
                      "volume": 100
                    },
                    {
                      "begins_at": "2026-07-31T18:00:30Z",
                      "close_price": "16.40",
                      "volume": 0,
                      "interpolated": true
                    }
                  ]
                }]
              }
            }
            """);
        var instrument = new Instrument("SOFI", AssetClass.Equity);

        IReadOnlyList<MarketQuote> quotes =
            RobinhoodMarketDataParser.ParseHistory(
                document.RootElement,
                instrument,
                DateTimeOffset.Parse("2026-08-01T00:00:00Z"));

        Assert.Equal(2, quotes.Count);
        Assert.Equal(16.39m, quotes[0].Last);
        Assert.Equal(16.40m, quotes[1].Last);
        Assert.Equal(100m, quotes[0].Volume);
        Assert.Equal(16.38m, quotes[1].OpenPrice);
        Assert.Equal(16.42m, quotes[1].HighPrice);
        Assert.Equal(16.37m, quotes[1].LowPrice);
        Assert.Equal(16.40m, quotes[1].ClosePrice);
        Assert.Equal(16.39m, quotes[0].CandleOpen);
        Assert.All(quotes, quote => Assert.Equal(0m, quote.Bid));
    }
}
