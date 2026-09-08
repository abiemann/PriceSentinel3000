using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests.MarketDataLibrary;

public sealed class CompatibleSessionComposerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-08T13:30:00Z");
    private static HistoricalDataQuery Query => new("SOFI", Start, Start.AddSeconds(30), SessionBounds: "24_5", IncludeCompatibleSessions: true);

    [Theory]
    [InlineData("regular")]
    [InlineData("extended")]
    public void SavedSessionsComposeWithAllHoursGapFillsWithoutRelabeling(string sessionBounds)
    {
        ReplayHistorySource local = Source(sessionBounds, [Bar(0)]);
        ReplayHistorySource broker = Source("24_5", [Bar(0), Bar(15)], pending: true);

        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(Query, [local, broker]);

        Assert.True(result.Coverage.Complete);
        Assert.Equal(2, result.Candles.Count);
        Assert.Equal(sessionBounds, Assert.Single(result.Sources[0].Datasets).SessionBounds);
        Assert.Equal("24_5", result.Sources[1].PendingDownload!.SessionBounds);
        Assert.Throws<InvalidDataException>(() => ReplayHistoryComposer.Compose(Query with { IncludeCompatibleSessions = false }, [local, broker]));
    }

    [Fact]
    public void UnequalCrossSessionOverlapsAreNotSilentlyReplaced()
    {
        ReplayHistorySource local = Source("regular", [Bar(0)]);
        ReplayHistorySource broker = Source("24_5", [Bar(0) with { Close = 10.5m }, Bar(15)], pending: true);

        Assert.Throws<InvalidDataException>(() => ReplayHistoryComposer.Compose(Query, [local, broker]));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("instrument")]
    [InlineData("symbol")]
    [InlineData("policy")]
    [InlineData("basis")]
    [InlineData("session")]
    public void CompatibilityDoesNotRelaxOtherContributorIdentity(string field)
    {
        ReplayHistorySource local = Source("regular", [Bar(0)]);
        ReplayHistorySource broker = Source("24_5", [Bar(15)], pending: true);
        HistoricalDownload download = broker.PendingDownload!;
        broker = broker with { PendingDownload = field switch
        {
            "provider" => download with { Provider = "other" },
            "instrument" => download with { InstrumentId = "another-SOFI-id" },
            "symbol" => download with { Symbol = "MSFT" },
            "policy" => download with { AdjustmentPolicy = "none" },
            "basis" => download with { AdjustmentBasis = "other" },
            _ => download with { SessionBounds = "custom-session" },
        } };

        Assert.Throws<InvalidDataException>(() => ReplayHistoryComposer.Compose(Query, [local, broker]));
    }

    private static HistoricalCandle Bar(int offset) => new(Start.AddSeconds(offset), Start.AddSeconds(offset + 15),
        Start.AddSeconds(offset + 15), 10m, 11m, 9m, 10m, 100m);

    private static ReplayHistorySource Source(string sessionBounds, HistoricalCandle[] candles, bool pending = false)
    {
        var download = new HistoricalDownload("test", "SOFI-id", "SOFI", 15, "split", "basis", sessionBounds,
            Start.AddHours(1), Start, Start.AddSeconds(30), candles);
        if (pending) return new(15, [], candles, download);
        var info = new HistoricalDatasetInfo(new string('a', 64), "test.json", "test", "SOFI-id", "SOFI",
            new(2026, 9, 8), 15, "split", "basis", sessionBounds, download.FetchedAtUtc,
            new(Start, Start.AddSeconds(30), candles[0].StartsAtUtc, candles[^1].EndsAtUtc, 2, candles.Length,
                candles.Length == 2, true, []));
        return new(15, [info], candles);
    }
}
