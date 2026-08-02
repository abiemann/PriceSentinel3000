using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Tests.Strategy;

public sealed class PriceActionSignalEngineTests
{
    private static readonly Instrument Instrument = new("SOFI");
    private static readonly DateTimeOffset Start =
        new(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SimpleRsi_UsesFourteenSimplePriceChanges()
    {
        decimal? rising = PriceActionSignalEngine.CalculateSimpleRsi(
            Enumerable.Range(1, 15).Select(value => (decimal)value).ToArray());
        decimal? flat = PriceActionSignalEngine.CalculateSimpleRsi(
            Enumerable.Repeat(10m, 15).ToArray());

        Assert.Equal(100m, rising);
        Assert.Equal(50m, flat);
    }

    [Fact]
    public void Evaluate_ConfirmsLingeringBottomAfterPositiveTurn()
    {
        decimal[] prices =
        [
            100.00m, 99.95m, 99.90m, 99.85m, 99.80m,
            99.75m, 99.70m, 99.65m, 99.60m, 99.55m,
            99.50m, 99.48m, 99.46m, 99.45m, 99.46m,
            99.45m, 99.46m, 99.45m, 99.47m, 99.50m,
        ];
        IReadOnlyList<MarketQuote> quotes = Quotes(prices);

        StrategyDecision decision = new PriceActionSignalEngine().Evaluate(
            quotes,
            StrategyPositionContext.Flat);

        Assert.Equal(StrategySignalKind.Buy, decision.Signal);
        Assert.Equal("BOTTOM CONFIRMED", decision.State);
        Assert.True(decision.SimpleRsi < 48m);
        Assert.True(decision.MomentumPercent > 0m);
    }

    [Fact]
    public void Evaluate_ConfirmsProfitablePeakAfterMomentumTurnsDown()
    {
        decimal[] prices =
        [
            100.00m, 100.10m, 100.20m, 100.30m, 100.40m,
            100.50m, 100.60m, 100.70m, 100.80m, 100.90m,
            101.00m, 100.98m, 101.00m, 100.99m, 101.00m,
            100.98m, 100.95m, 100.91m, 100.86m, 100.80m,
        ];
        IReadOnlyList<MarketQuote> quotes = Quotes(prices);
        var position = new StrategyPositionContext(5m, 100m, Start);

        StrategyDecision decision = new PriceActionSignalEngine().Evaluate(quotes, position);

        Assert.Equal(StrategySignalKind.Sell, decision.Signal);
        Assert.Equal("PEAK CONFIRMED", decision.State);
        Assert.True(decision.MomentumPercent < 0m);
    }

    [Fact]
    public void Evaluate_HoldsUntilIndicatorWindowIsWarm()
    {
        StrategyDecision decision = new PriceActionSignalEngine().Evaluate(
            Quotes([100m, 99.9m, 100m]),
            StrategyPositionContext.Flat);

        Assert.Equal(StrategySignalKind.Hold, decision.Signal);
        Assert.Equal("WARMING UP", decision.State);
    }

    internal static IReadOnlyList<MarketQuote> Quotes(IReadOnlyList<decimal> prices) =>
        [
            .. prices.Select((price, index) =>
            {
                DateTimeOffset timestamp = Start.AddSeconds(index * 5);
                return new MarketQuote(
                    Instrument,
                    timestamp,
                    timestamp,
                    price - 0.01m,
                    price + 0.01m,
                    price,
                    1_000m);
            }),
        ];
}
