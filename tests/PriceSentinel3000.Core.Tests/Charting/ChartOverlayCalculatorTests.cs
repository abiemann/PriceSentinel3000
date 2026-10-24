using PriceSentinel3000.Core.Charting;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Core.Tests.Charting;

public sealed class ChartOverlayCalculatorTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 23, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Ema_UsesObservedCloseSeedAndEachRequestedPeriod()
    {
        IReadOnlyList<ChartOverlayPoint> points = ChartOverlayCalculator.Calculate([
            Candle(Open, close: 100m), Candle(Open.AddSeconds(15), close: 151m),
            Candle(Open.AddSeconds(30), close: 100m),
        ]);

        Assert.Equal((100m, 100m, 100m), (points[0].Ema50, points[0].Ema100, points[0].Ema200));
        Assert.Equal(102m, points[1].Ema50);
        Assert.Equal(100m + 2m / 101m * 51m, points[1].Ema100);
        Assert.Equal(100m + 2m / 201m * 51m, points[1].Ema200);
        Assert.Equal(102m + 2m / 51m * (100m - 102m), points[2].Ema50);
    }

    [Fact]
    public void SyntheticGaps_DoNotSeedOrAdvanceEmaAndBreakVwap()
    {
        IReadOnlyList<ChartOverlayPoint> points = ChartOverlayCalculator.Calculate([
            Candle(Open.AddSeconds(-15), close: 900m) with { IsSynthetic = true },
            Candle(Open, close: 100m),
            Candle(Open.AddSeconds(15), close: 900m) with { IsSynthetic = true },
            Candle(Open.AddSeconds(30), close: 151m),
        ]);

        Assert.Null(points[0].Ema50);
        Assert.Equal(100m, points[1].Ema50);
        Assert.Null(points[2].Ema50);
        Assert.Null(points[2].Ema100);
        Assert.Null(points[2].Ema200);
        Assert.Equal(102m, points[3].Ema50);
        Assert.Equal(VwapStatus.IncompleteSession, points[3].VwapStatus);
        Assert.Null(points[3].Vwap);
    }

    [Fact]
    public void Vwap_WeightsTypicalPriceByVolumeAndIgnoresPremarket()
    {
        IReadOnlyList<ChartOverlayPoint> points = ChartOverlayCalculator.Calculate([
            Candle(Open.AddSeconds(-15), close: 900m, volume: 10000m),
            Candle(Open, close: 12m, volume: 100m) with { High = 15m, Low = 9m },
            Candle(Open.AddSeconds(15), close: 18m, volume: 300m) with { High = 21m, Low = 15m },
        ]);

        Assert.Equal(VwapStatus.OutsideSession, points[0].VwapStatus);
        Assert.Null(points[0].Vwap);
        Assert.Equal(12m, points[1].Vwap);
        Assert.Equal(16.5m, points[2].Vwap);
        Assert.Equal(VwapStatus.Available, points[2].VwapStatus);
    }

    [Fact]
    public void Vwap_KnownZeroVolumeWaitsThenCarriesTheWeightedValue()
    {
        IReadOnlyList<ChartOverlayPoint> points = ChartOverlayCalculator.Calculate([
            Candle(Open, close: 100m, volume: 0m),
            Candle(Open.AddSeconds(15), close: 120m, volume: 10m),
            Candle(Open.AddSeconds(30), close: 150m, volume: 0m),
        ]);

        Assert.Null(points[0].Vwap);
        Assert.Equal(VwapStatus.NoVolume, points[0].VwapStatus);
        Assert.Equal(120m, points[1].Vwap);
        Assert.Equal(120m, points[2].Vwap);
    }

    [Fact]
    public void Vwap_UnknownVolumeInvalidatesRemainderOfSessionAndRecoversNextDay()
    {
        DateTimeOffset nextOpen = Open.AddDays(3);
        IReadOnlyList<ChartOverlayPoint> points = ChartOverlayCalculator.Calculate([
            Candle(Open, close: 100m),
            Candle(Open.AddSeconds(15)) with { HasKnownVolume = false },
            Candle(Open.AddSeconds(30)), Candle(nextOpen, close: 200m),
        ]);

        Assert.Equal(100m, points[0].Vwap);
        Assert.Equal(VwapStatus.MissingVolume, points[1].VwapStatus);
        Assert.Equal(VwapStatus.MissingVolume, points[2].VwapStatus);
        Assert.Null(points[2].Vwap);
        Assert.Equal(200m, points[3].Vwap);
    }

    [Fact]
    public void Vwap_TruncatedHistoryDoesNotStartAnInventedSession()
    {
        IReadOnlyList<ChartOverlayPoint> points = ChartOverlayCalculator.Calculate([
            Candle(Open.AddMinutes(1)), Candle(Open.AddMinutes(1).AddSeconds(15)),
        ]);

        Assert.All(points, point =>
        {
            Assert.Null(point.Vwap);
            Assert.Equal(VwapStatus.IncompleteSession, point.VwapStatus);
        });
    }

    [Fact]
    public void Vwap_FormingFinalCandleWaitsAndRecoversWhenCompleted()
    {
        PriceCandle first = Candle(Open, close: 100m);
        PriceCandle second = Candle(Open.AddSeconds(15), close: 120m);
        IReadOnlyList<ChartOverlayPoint> forming = ChartOverlayCalculator.Calculate([
            first, second with { HasCompleteHistory = false },
        ]);
        IReadOnlyList<ChartOverlayPoint> completed = ChartOverlayCalculator.Calculate([first, second]);

        Assert.Equal(VwapStatus.Available, forming[0].VwapStatus);
        Assert.Equal(VwapStatus.IncompleteCandle, forming[1].VwapStatus);
        Assert.Null(forming[1].Vwap);
        Assert.Equal(VwapStatus.Available, completed[1].VwapStatus);
        Assert.Equal(110m, completed[1].Vwap);
    }

    [Fact]
    public void Vwap_RejectsUnrepresentedGapAndIncompleteChartBucket()
    {
        IReadOnlyList<ChartOverlayPoint> gap = ChartOverlayCalculator.Calculate([
            Candle(Open), Candle(Open.AddSeconds(30)),
        ]);
        IReadOnlyList<ChartOverlayPoint> incomplete = ChartOverlayCalculator.Calculate([
            Candle(Open) with { HasCompleteHistory = false }, Candle(Open.AddSeconds(15)),
        ]);

        Assert.Equal(VwapStatus.IncompleteSession, gap[1].VwapStatus);
        Assert.Null(gap[1].Vwap);
        Assert.All(incomplete, point => Assert.Equal(VwapStatus.IncompleteSession, point.VwapStatus));
    }

    [Theory]
    [InlineData(2026, 3, 6, 14)]
    [InlineData(2026, 3, 9, 13)]
    [InlineData(2026, 10, 30, 13)]
    [InlineData(2026, 11, 2, 14)]
    public void Vwap_RegularOpenFollowsNewYorkDaylightSavingTime(int year, int month, int day, int utcHour)
    {
        DateTimeOffset open = new(year, month, day, utcHour, 30, 0, TimeSpan.Zero);
        IReadOnlyList<ChartOverlayPoint> points = ChartOverlayCalculator.Calculate([
            Candle(open.AddSeconds(-15)), Candle(open),
        ]);

        Assert.Equal(VwapStatus.OutsideSession, points[0].VwapStatus);
        Assert.Equal(VwapStatus.Available, points[1].VwapStatus);
    }

    [Theory]
    [InlineData(2026, 10, 24, 13)]
    [InlineData(2026, 11, 26, 14)]
    public void Vwap_ExcludesWeekendsAndExchangeHolidays(int year, int month, int day, int utcHour)
    {
        ChartOverlayPoint point = Assert.Single(ChartOverlayCalculator.Calculate([
            Candle(new(year, month, day, utcHour, 30, 0, TimeSpan.Zero)),
        ]));

        Assert.Equal(VwapStatus.OutsideSession, point.VwapStatus);
        Assert.Null(point.Vwap);
    }

    [Theory]
    [InlineData(2026, 10, 23, 13, 30, 20)]
    [InlineData(2026, 11, 27, 14, 30, 18)]
    public void Vwap_StopsAtRegularOrEarlyClose(int year, int month, int day, int openHour, int openMinute, int closeHour)
    {
        DateTimeOffset open = new(year, month, day, openHour, openMinute, 0, TimeSpan.Zero);
        DateTimeOffset close = new(year, month, day, closeHour, 0, 0, TimeSpan.Zero);
        PriceCandle[] candles = Enumerable.Range(0, (int)((close - open).TotalSeconds / 15) + 1)
            .Select(index => Candle(open.AddSeconds(index * 15))).ToArray();

        IReadOnlyList<ChartOverlayPoint> points = ChartOverlayCalculator.Calculate(candles);

        Assert.Equal(VwapStatus.Available, points[^2].VwapStatus);
        Assert.Equal(VwapStatus.OutsideSession, points[^1].VwapStatus);
        Assert.Null(points[^1].Vwap);
    }

    [Fact]
    public void EmptyHistoryHasNoOverlayPoints() => Assert.Empty(ChartOverlayCalculator.Calculate([]));

    private static PriceCandle Candle(DateTimeOffset timestamp, decimal close = 100m, decimal volume = 10m) =>
        new(timestamp, timestamp.AddSeconds(15), 1, close, close, close, close, volume,
            HasCompleteHistory: true);
}
