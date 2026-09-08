using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class CompatibleSessionQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PriceSentinel-compatible-session-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-08T13:30:00Z");
    private JsonMarketDataLibrary Library => new(_root);
    private static HistoricalDataQuery Query => new("SOFI", Start, Start.AddSeconds(60), SessionBounds: "24_5",
        RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage, IncludeCompatibleSessions: true);

    [Theory]
    [InlineData(HistoricalRevisionPolicy.RejectConflicts)]
    [InlineData(HistoricalRevisionPolicy.LatestFetched)]
    [InlineData(HistoricalRevisionPolicy.CompatibleCoverage)]
    public void MatchingSessionsUnionCandlesWithoutChangingNativeFiles(HistoricalRevisionPolicy policy)
    {
        HistoricalDatasetInfo regular = Assert.Single(Library.Save(Download("regular", 15, 30)));
        HistoricalDatasetInfo extended = Assert.Single(Library.Save(Download("extended", 0, 15)));
        HistoricalDatasetInfo overnight = Assert.Single(Library.Save(Download("24_5", 30, 45)));
        HistoricalDatasetInfo[] saved = [regular, extended, overnight];
        byte[][] original = saved.Select(info => File.ReadAllBytes(Path.Combine(_root, info.RelativePath))).ToArray();

        HistoricalDataQueryResult result = Library.Query(Query with { RevisionPolicy = policy });

        Assert.True(result.Succeeded);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(4, result.Coverage.ActualCandleCount);
        Assert.Equal(new[] { 0d, 15d, 30d, 45d }, result.Candles.Select(c => (c.StartsAtUtc - Start).TotalSeconds));
        Assert.Equal(saved.Select(d => d.DatasetHash).Order(), result.Datasets.Select(d => d.DatasetHash).Order());
        Assert.Equal(new[] { "24_5", "extended", "regular" }, result.Datasets.Select(d => d.SessionBounds).Order());
        for (int i = 0; i < saved.Length; i++)
            Assert.Equal(original[i], File.ReadAllBytes(Path.Combine(_root, saved[i].RelativePath)));
    }

    [Fact]
    public void SessionReuseRequiresOptInAndNeverBroadensTheRequestedTimeRange()
    {
        Library.Save(Download("regular", 0, 15));
        Library.Save(Download("extended", 30, 45));

        Assert.Empty(Library.Query(Query with { IncludeCompatibleSessions = false }).Candles);
        Assert.False(Library.Query(Query with { SessionBounds = null, IncludeCompatibleSessions = false }).Succeeded);
        HistoricalDataQueryResult bounded = Library.Query(Query with { FromUtc = Start.AddSeconds(15), ThroughUtc = Start.AddSeconds(45) });
        Assert.True(bounded.Succeeded);
        Assert.True(bounded.Coverage.Complete);
        Assert.Equal(new[] { 15d, 30d }, bounded.Candles.Select(c => (c.StartsAtUtc - Start).TotalSeconds));
    }

    [Theory]
    [InlineData(HistoricalRevisionPolicy.RejectConflicts)]
    [InlineData(HistoricalRevisionPolicy.LatestFetched)]
    [InlineData(HistoricalRevisionPolicy.CompatibleCoverage)]
    public void ConflictingCrossSessionOverlapsRequireExplicitSelection(HistoricalRevisionPolicy policy)
    {
        Library.Save(Download("regular", 0));
        HistoricalDownload changed = Download("24_5", 0, 15);
        Library.Save(changed with { Candles = changed.Candles.Select(c => c with { Close = 10.5m }).ToArray() });

        HistoricalDataQueryResult result = Library.Query(Query with { RevisionPolicy = policy });

        Assert.False(result.Succeeded);
        Assert.Empty(result.Candles);
        Assert.Contains(result.Diagnostics, d => d.Code == "revision_selection_required");
    }

    [Fact]
    public void LatestFetchedSelectsOneRevisionForEachSessionBeforeUnion()
    {
        HistoricalDownload old = Download("regular", 0, 15);
        HistoricalDatasetInfo first = Assert.Single(Library.Save(old));
        HistoricalDatasetInfo latest = Assert.Single(Library.Save(old with
        {
            FetchedAtUtc = old.FetchedAtUtc.AddHours(1),
            Candles = old.Candles.Select(c => c with { Close = 10.5m }).ToArray(),
        }));
        HistoricalDatasetInfo extended = Assert.Single(Library.Save(Download("extended", 30, 45)));

        HistoricalDataQueryResult result = Library.Query(Query with { RevisionPolicy = HistoricalRevisionPolicy.LatestFetched });

        Assert.True(result.Succeeded);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(new[] { latest.DatasetHash, extended.DatasetHash }.Order(), result.Datasets.Select(d => d.DatasetHash).Order());
        Assert.DoesNotContain(result.Datasets, d => d.DatasetHash == first.DatasetHash);
        Assert.Equal(10.5m, result.Candles[0].Close);
        Assert.False(Library.Query(Query).Succeeded);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("instrument")]
    [InlineData("policy")]
    [InlineData("basis")]
    public void CompatibleSessionsDoNotRelaxOtherProvenance(string field)
    {
        Library.Save(Download("24_5", 0, 15));
        HistoricalDownload other = Download("regular", 30, 45);
        other = field switch
        {
            "provider" => other with { Provider = "other" },
            "instrument" => other with { InstrumentId = "another-SOFI-id" },
            "policy" => other with { AdjustmentPolicy = "none" },
            _ => other with { AdjustmentBasis = "other" },
        };
        Library.Save(other);

        HistoricalDataQueryResult result = Library.Query(Query);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "incompatible_provenance");
    }

    [Fact]
    public void UnknownSessionNamesAreNeverTreatedAsCompatible()
    {
        HistoricalDatasetInfo known = Assert.Single(Library.Save(Download("regular", 0, 15)));
        Library.Save(Download("custom-session", 30, 45));

        HistoricalDataQueryResult result = Library.Query(Query);

        Assert.True(result.Succeeded);
        Assert.False(result.Coverage.Complete);
        Assert.Equal(known.DatasetHash, Assert.Single(result.Datasets).DatasetHash);
        Assert.False(Library.Query(Query with { SessionBounds = null }).Succeeded);
    }

    [Fact]
    public void PinsSelectOnlyTheirExactDatasetAndRemainOnePerDay()
    {
        HistoricalDatasetInfo regular = Assert.Single(Library.Save(Download("regular", 0, 15)));
        HistoricalDatasetInfo overnight = Assert.Single(Library.Save(Download("24_5", 0, 15, 30, 45)));
        HistoricalDataQuery pinned = Query with { PinnedHashes = [regular.DatasetHash], RevisionPolicy = HistoricalRevisionPolicy.LatestFetched };

        HistoricalDataQueryResult result = Library.Query(pinned);

        Assert.True(result.Succeeded);
        Assert.False(result.Coverage.Complete);
        Assert.Equal(2, result.Candles.Count);
        Assert.Equal(regular.DatasetHash, Assert.Single(result.Datasets).DatasetHash);
        Assert.False(Library.Query(pinned with { PinnedHashes = [regular.DatasetHash, overnight.DatasetHash] }).Succeeded);
        Assert.False(Library.Query(pinned with { PinnedHashes = [new string('a', 64)] }).Succeeded);
        Assert.False(Library.Query(pinned with { IncludeCompatibleSessions = false }).Succeeded);
    }

    private static HistoricalDownload Download(string sessionBounds, params int[] offsets) =>
        new("test", "SOFI-id", "SOFI", 15, "split", "basis", sessionBounds, Start.AddHours(2),
            Start, Start.AddSeconds(60), offsets.Select(offset => new HistoricalCandle(Start.AddSeconds(offset),
                Start.AddSeconds(offset + 15), Start.AddSeconds(offset + 15), 10m, 11m, 9m, 10m, 100m)).ToArray());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
