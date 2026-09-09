using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class CollectionDayCoverageTests
{
    private static readonly DateOnly Day = new(2026, 9, 9);
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

        Assert.Equal(37.5m, CollectionDayCoverage.Calculate(Job, [partial]));
    }

    [Fact]
    public void OverlappingCompatibleSessionsAndDuplicateFilesCountEachCandleOnce()
    {
        HistoricalDatasetInfo extended = Piece(Day, 4, 20) with { SessionBounds = "extended" };
        HistoricalDatasetInfo regular = Piece(Day, 9.5, 16) with { SessionBounds = "regular" };
        HistoricalDatasetInfo overnight = Piece(Day, 0, 8);

        Assert.Equal(100m * 20 / 24, CollectionDayCoverage.Calculate(Job,
            [extended, regular, overnight, extended]));
        Assert.Equal(100m, CollectionDayCoverage.Calculate(Job with { SessionBounds = "regular" },
            [extended, overnight]));
    }

    [Theory]
    [InlineData(2026, 9, 11, 10)] // Friday has twenty active hours.
    [InlineData(2026, 9, 7, 2)] // Labor Day has only the next session's four evening hours.
    [InlineData(2026, 11, 27, 8.5)] // Early-close Friday has seventeen active hours.
    public void ClosedMarketHoursAreExcludedFromTheDenominator(int year, int month, int day, double hours)
    {
        DateOnly date = new(year, month, day);
        Assert.Equal(50m, CollectionDayCoverage.Calculate(Job with { SessionDate = date },
            [Piece(date, 0, hours)]));
    }

    [Fact]
    public void TodaysFutureHoursStayInTheDenominatorEvenWhenAllRequestedCandlesWereSaved()
    {
        HistoricalDatasetInfo morning = Piece(Day, 0, 6);
        CollectionJob current = Job with { RequestedThroughUtc = morning.Coverage.CoveredThroughUtc };

        Assert.True(morning.Coverage.Complete);
        Assert.Equal(25m, CollectionDayCoverage.Calculate(current, [morning]));
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

        Assert.Equal(25m, CollectionDayCoverage.Calculate(Job, datasets));
        Assert.Equal(0m, CollectionDayCoverage.Calculate(Job, datasets.Skip(1)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedSourceIdentityIsUnknownInsteadOfCombiningIncompatibleCoverage(bool differentProvider)
    {
        HistoricalDatasetInfo first = Piece(Day, 0, 12), second = Piece(Day, 12, 24);
        second = differentProvider ? second with { Provider = "Other broker" } :
            second with { InstrumentId = "different-instrument" };

        Assert.Null(CollectionDayCoverage.Calculate(Job with { ProviderInstrumentId = null }, [first, second]));
    }

    [Fact]
    public void UnsupportedSessionsClosedDatesAndMisalignedCandleBoundariesAreUnknown()
    {
        HistoricalDatasetInfo native = Piece(Day, 0, 6);
        HistoricalDatasetInfo offGrid = native with { Coverage = native.Coverage with
        {
            CoveredFromUtc = native.Coverage.CoveredFromUtc!.Value.AddSeconds(1),
        } };

        Assert.Null(CollectionDayCoverage.Calculate(Job with { SessionBounds = "unknown" }, []));
        Assert.Null(CollectionDayCoverage.Calculate(Job with { SourceIntervalSeconds = 60 }, [native]));
        Assert.Null(CollectionDayCoverage.Calculate(Job with { SessionDate = new(2026, 9, 12) }, []));
        Assert.Null(CollectionDayCoverage.Calculate(Job, [native with { SessionBounds = "unknown" }]));
        Assert.Null(CollectionDayCoverage.Calculate(Job, [offGrid]));
    }

    [Fact]
    public void NoSavedCandlesMeansZeroWhileTransferFailureDoesNotEraseExistingCoverage()
    {
        Assert.Equal(0m, CollectionDayCoverage.Calculate(Job, []));
        Assert.Equal(25m, CollectionDayCoverage.Calculate(Job with { Status = CollectionJobStatus.Failed },
            [Piece(Day, 0, 6)]));
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
