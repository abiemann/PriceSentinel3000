using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class CollectionDayCoverageTests
{
    private static readonly DateOnly Day = new(2026, 9, 9);
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly CollectionJob Job = new()
    {
        Symbol = "AAPL", ProviderInstrumentId = "id-AAPL", SessionDate = Day, SessionBounds = "24_5",
    };

    [Fact]
    public void GapsAndUncoveredHoursRemainMissingFromTheFullDay()
    {
        HistoricalDatasetInfo partial = Piece(Day, 0, 12);
        DateTimeOffset start = partial.Coverage.RequestedFromUtc;
        partial = partial with { Coverage = partial.Coverage with
        {
            Complete = false, ActualCandleCount = 9 * 240,
            Gaps = [new(start.AddHours(8), start.AddHours(10)), new(start.AddHours(2), start.AddHours(3))],
        } };

        Assert.Equal(37.5m, CollectionDayCoverage.Calculate(Job, [partial], Now));
    }

    [Fact]
    public void OverlappingCompatibleSessionsAndDuplicateFilesCountEachCandleOnce()
    {
        HistoricalDatasetInfo extended = Piece(Day, 4, 20) with { SessionBounds = "extended" };
        HistoricalDatasetInfo regular = Piece(Day, 9.5, 16) with { SessionBounds = "regular" };
        HistoricalDatasetInfo overnight = Piece(Day, 0, 8);

        Assert.Equal(100m * 20 / 24, CollectionDayCoverage.Calculate(Job,
            [extended, regular, overnight, extended], Now));
        Assert.Equal(100m, CollectionDayCoverage.Calculate(Job with { SessionBounds = "regular" },
            [extended, overnight], Now));
    }

    [Theory]
    [InlineData(2026, 9, 11, 10)] // Friday has twenty active hours.
    [InlineData(2026, 9, 7, 2)] // Labor Day has only the next session's four evening hours.
    [InlineData(2026, 11, 27, 8.5)] // Early-close Friday has seventeen active hours.
    public void ClosedMarketHoursAreExcludedFromTheDenominator(int year, int month, int day, double hours)
    {
        DateOnly date = new(year, month, day);
        DateTimeOffset now = month == 11 ? new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero) : Now;
        Assert.Equal(50m, CollectionDayCoverage.Calculate(Job with { SessionDate = date },
            [Piece(date, 0, hours)], now));
    }

    [Fact]
    public void DailyDownloadCutoffDoesNotFreezeTheCompletedCandleDenominator()
    {
        HistoricalDatasetInfo morning = Piece(Day, 0, 6);
        CollectionJob current = Job with { RequestedThroughUtc = morning.Coverage.CoveredThroughUtc };
        DateTimeOffset start = morning.Coverage.RequestedFromUtc;

        Assert.True(morning.Coverage.Complete);
        Assert.Equal(100m, CollectionDayCoverage.Calculate(current, [morning], start.AddHours(6)));
        Assert.Equal(50m, CollectionDayCoverage.Calculate(current, [morning], start.AddHours(12)));
        Assert.Equal(25m, CollectionDayCoverage.Calculate(current, [morning], Now));
    }

    [Fact]
    public void IntradayCoverageMatchesSaved3471Of4343CompletedCandlesAndExcludesTheFormingCandle()
    {
        HistoricalDatasetInfo saved = Piece(Day, 0, 24);
        DateTimeOffset start = saved.Coverage.RequestedFromUtc;
        DateTimeOffset savedThrough = start.AddSeconds(3471 * 15);
        saved = saved with { Coverage = saved.Coverage with
        {
            CoveredThroughUtc = savedThrough, ActualCandleCount = 3471, Complete = false,
            Gaps = [new(savedThrough, saved.Coverage.RequestedThroughUtc)],
        } };
        DateTimeOffset now = start.AddSeconds(4343 * 15 + 14);

        Assert.Equal(100m * 3471 / 4343, CollectionDayCoverage.Calculate(Job, [saved], now));
        Assert.Equal(100m * 3471 / 4344, CollectionDayCoverage.Calculate(Job, [saved], now.AddSeconds(1)));
    }

    [Fact]
    public void StoredFutureCandlesDoNotIncreaseCurrentCoverage()
    {
        DateTimeOffset now = Piece(Day, 0, 10).Coverage.RequestedThroughUtc;
        HistoricalDatasetInfo future = Piece(Day, 12, 24);

        Assert.Equal(0m, CollectionDayCoverage.Calculate(Job, [future], now));
        Assert.Equal(60m, CollectionDayCoverage.Calculate(Job, [Piece(Day, 0, 6), future], now));
        Assert.Equal(100m, CollectionDayCoverage.Calculate(Job, [Piece(Day, 0, 24)], now));
    }

    [Fact]
    public void NoCompletedTradingSlotsMeansUnknownUntilTheFirstCandleEnds()
    {
        CollectionJob regular = Job with { SessionBounds = "regular" };
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(Day, "regular");
        HistoricalDatasetInfo saved = Piece(Day, 9.5, 16);

        Assert.Null(CollectionDayCoverage.Calculate(regular, [saved], session.FromUtc.AddMinutes(-1)));
        Assert.Null(CollectionDayCoverage.Calculate(regular, [saved], session.FromUtc.AddSeconds(14)));
        Assert.Equal(0m, CollectionDayCoverage.Calculate(regular, [], session.FromUtc.AddSeconds(15)));
        Assert.Null(CollectionDayCoverage.Calculate(Job with { SessionDate = Day.AddDays(1) }, [], session.ThroughUtc));
    }

    [Fact]
    public void ExplicitSelectedRangeKeepsItsBoundsAndExcludesFutureCandles()
    {
        HistoricalDatasetInfo saved = Piece(Day, 4, 6);
        DateTimeOffset start = CollectionSchedule.GetSessionWindow(Day, "24_5").FromUtc;
        CollectionJob range = Job with { RequestedFromUtc = start.AddHours(4), RequestedThroughUtc = start.AddHours(8) };

        Assert.Null(CollectionDayCoverage.Calculate(range, [saved], start.AddHours(4).AddSeconds(14)));
        Assert.Equal(100m, CollectionDayCoverage.Calculate(range, [saved], start.AddHours(5)));
        Assert.Equal(100m * 2 / 3, CollectionDayCoverage.Calculate(range, [saved], start.AddHours(7)));
        Assert.Equal(50m, CollectionDayCoverage.Calculate(range, [saved], Now));
    }

    [Fact]
    public void OnlyMatchingNativeInstrumentAndAdjustmentDataContributes()
    {
        HistoricalDatasetInfo native = Piece(Day, 0, 6);
        HistoricalDatasetInfo other = Piece(Day, 0, 24);
        HistoricalDatasetInfo[] datasets =
        [
            native with { Symbol = "aapl" },
            other with { Symbol = "MSFT" },
            other with { TradingDate = Day.AddDays(-1) },
            other with { InstrumentId = "another-AAPL-listing" },
            other with { AdjustmentPolicy = "none" },
            other with { AdjustmentBasis = "another-price-basis" },
            other with { SourceIntervalSeconds = 60 },
        ];

        Assert.Equal(25m, CollectionDayCoverage.Calculate(Job, datasets, Now));
        Assert.Equal(0m, CollectionDayCoverage.Calculate(Job, datasets.Skip(1), Now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedSourceIdentityIsUnknownInsteadOfCombiningIncompatibleCoverage(bool differentProvider)
    {
        HistoricalDatasetInfo first = Piece(Day, 0, 12), second = Piece(Day, 12, 24);
        second = differentProvider ? second with { Provider = "Other broker" } :
            second with { InstrumentId = "different-instrument" };

        Assert.Null(CollectionDayCoverage.Calculate(Job with { ProviderInstrumentId = null }, [first, second], Now));
    }

    [Fact]
    public void UnsupportedSessionsClosedDatesAndMisalignedCandleBoundariesAreUnknown()
    {
        HistoricalDatasetInfo native = Piece(Day, 0, 6);
        HistoricalDatasetInfo offGrid = native with { Coverage = native.Coverage with
        {
            CoveredFromUtc = native.Coverage.CoveredFromUtc!.Value.AddSeconds(1),
        } };

        Assert.Null(CollectionDayCoverage.Calculate(Job with { SessionBounds = "unknown" }, [], Now));
        Assert.Null(CollectionDayCoverage.Calculate(Job with { SourceIntervalSeconds = 60 }, [native], Now));
        Assert.Null(CollectionDayCoverage.Calculate(Job with { SessionDate = new(2026, 9, 12) }, [], Now));
        Assert.Null(CollectionDayCoverage.Calculate(Job, [native with { SessionBounds = "unknown" }], Now));
        Assert.Null(CollectionDayCoverage.Calculate(Job, [offGrid], Now));
    }

    [Fact]
    public void NoSavedCandlesMeansZeroWhileTransferFailureDoesNotEraseExistingCoverage()
    {
        Assert.Equal(0m, CollectionDayCoverage.Calculate(Job, [], Now));
        Assert.Equal(25m, CollectionDayCoverage.Calculate(Job with { Status = CollectionJobStatus.Failed },
            [Piece(Day, 0, 6)], Now));
    }

    private static HistoricalDatasetInfo Piece(DateOnly day, double fromHours, double throughHours)
    {
        CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(day, "24_5");
        DateTimeOffset from = window.FromUtc.AddHours(fromHours), through = window.FromUtc.AddHours(throughHours);
        int count = (int)((through - from).TotalSeconds / 15);
        return new($"piece-{fromHours}-{throughHours}", "fragment.json", "Robinhood", "id-AAPL", "AAPL", day,
            15, "split", "robinhood-split-unversioned", "24_5", through.AddHours(1),
            new(from, through, from, through, count, count, true, true, []));
    }
}
