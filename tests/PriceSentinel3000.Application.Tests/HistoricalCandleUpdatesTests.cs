using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class HistoricalCandleUpdatesTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 17, 43, 30, TimeSpan.Zero);
    private static readonly HistoricalCandle Saved = new(Start, Start.AddSeconds(15), Start.AddSeconds(15),
        80.315m, 80.315m, 80.315m, 80.315m, 108);

    [Fact]
    public void PositiveRevisionReplacesAllValuesIncludingLowerVolume()
    {
        HistoricalCandle revised = Saved with { Open = 80.31m, High = 80.31m, Low = 80.31m, Close = 80.31m, Volume = 100 };
        Assert.Equal(revised, HistoricalCandleUpdates.Apply(revised, Saved));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void MissingVolumeKeepsSavedVolumeWhileAcceptingPrices(int? volume)
    {
        HistoricalCandle incoming = Saved with { High = 81, Close = 81, Volume = volume };
        Assert.Equal(incoming with { Volume = Saved.Volume }, HistoricalCandleUpdates.Apply(incoming, Saved));
    }

    [Fact]
    public void ZeroPricesKeepKnownFieldsWhileOtherValidFieldsUpdate()
    {
        HistoricalCandle incoming = Saved with { Open = 0, High = 81, Low = 0, Close = 80.5m, Volume = 0 };
        Assert.Equal(Saved with { High = 81, Close = 80.5m }, HistoricalCandleUpdates.Apply(incoming, Saved));
    }

    [Fact]
    public void InconsistentPlaceholderFallbackKeepsKnownPriceBarTogether()
    {
        HistoricalCandle incoming = Saved with { High = 0, Close = 82, Volume = 200 };
        Assert.Equal(Saved with { Volume = 200 }, HistoricalCandleUpdates.Apply(incoming, Saved));
    }

    [Fact]
    public void WhollyZeroUpdateCannotEraseKnownCandle()
    {
        Assert.Equal(Saved, HistoricalCandleUpdates.Apply(Saved with { Open = 0, High = 0, Low = 0, Close = 0, Volume = 0 }, Saved));
    }

    [Fact]
    public void IncompleteNewCandleIsSkippedWithoutInventingCoverage()
    {
        Assert.Null(HistoricalCandleUpdates.Apply(Saved with { Open = 0 }, null));
        Assert.Equal(Saved with { Volume = null }, HistoricalCandleUpdates.Apply(Saved with { Volume = null }, null));
    }

    [Fact]
    public void InvalidNumbersAndTimingStillReject()
    {
        Assert.Throws<InvalidDataException>(() => HistoricalCandleUpdates.Apply(Saved with { Volume = -1 }, Saved));
        Assert.Throws<InvalidDataException>(() => HistoricalCandleUpdates.Apply(Saved with { Low = -1, Open = 0 }, null));
        Assert.Throws<InvalidDataException>(() => HistoricalCandleUpdates.Apply(Saved with { High = 79 }, Saved));
        Assert.Throws<InvalidDataException>(() => HistoricalCandleUpdates.Apply(Saved with { AvailableAtUtc = Start }, Saved));
        Assert.Throws<InvalidDataException>(() => HistoricalCandleUpdates.Apply(Saved with
        {
            EndsAtUtc = Start.AddSeconds(30), AvailableAtUtc = Start.AddSeconds(30), Open = 0,
        }, Saved));
    }
}
