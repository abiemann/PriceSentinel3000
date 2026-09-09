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
    public void LegacyMatchingSessionsUnionCandlesWithoutChangingNativeFiles(HistoricalRevisionPolicy policy)
    {
        HistoricalDatasetInfo regular = Assert.Single(SaveLegacy(Download("regular", 15, 30)));
        HistoricalDatasetInfo extended = Assert.Single(SaveLegacy(Download("extended", 0, 15)));
        HistoricalDatasetInfo overnight = Assert.Single(SaveLegacy(Download("24_5", 30, 45)));
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

    [Theory]
    [InlineData(HistoricalRevisionPolicy.RejectConflicts)]
    [InlineData(HistoricalRevisionPolicy.LatestFetched)]
    [InlineData(HistoricalRevisionPolicy.CompatibleCoverage)]
    public void MatchingSessionsSaveOneCanonicalDailyFileAndArchivePriorHashes(HistoricalRevisionPolicy policy)
    {
        HistoricalDatasetInfo regular = Assert.Single(Library.Save(Download("regular", 15, 30)));
        byte[] regularBytes = File.ReadAllBytes(Path.Combine(_root, regular.RelativePath));
        HistoricalDatasetInfo extended = Assert.Single(Library.Save(Download("extended", 0, 15)));
        byte[] extendedBytes = File.ReadAllBytes(Path.Combine(_root, extended.RelativePath));
        HistoricalDatasetInfo overnight = Assert.Single(Library.Save(Download("24_5", 30, 45)));

        HistoricalDataQueryResult result = Library.Query(Query with { RevisionPolicy = policy });

        Assert.True(result.Succeeded);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(new[] { 0d, 15d, 30d, 45d }, result.Candles.Select(c => (c.StartsAtUtc - Start).TotalSeconds));
        HistoricalDatasetInfo daily = Assert.Single(result.Datasets);
        Assert.Equal(overnight.DatasetHash, daily.DatasetHash);
        Assert.Equal("24_5", daily.SessionBounds);
        Assert.Equal("2026/09 - September/SOFI/2026-09-08.15s.json", daily.RelativePath.Replace('\\', '/'));
        Assert.Equal(daily.DatasetHash, Assert.Single(Library.Scan().Datasets).DatasetHash);
        Assert.Empty(Library.Scan().Diagnostics);
        Assert.Equal(regularBytes, File.ReadAllBytes(Path.Combine(_root, ".archive", regular.DatasetHash + ".json")));
        Assert.Equal(extendedBytes, File.ReadAllBytes(Path.Combine(_root, ".archive", extended.DatasetHash + ".json")));
        HistoricalDataset archivedRegular = Library.Read(regular.DatasetHash);
        HistoricalDataset archivedExtended = Library.Read(extended.DatasetHash);
        Assert.Equal("regular", archivedRegular.SessionBounds);
        Assert.Equal(2, archivedRegular.Candles.Count);
        Assert.Equal("extended", archivedExtended.SessionBounds);
        Assert.Equal(3, archivedExtended.Candles.Count);
    }

    [Fact]
    public void SessionReuseRequiresOptInAndNeverBroadensTheRequestedTimeRange()
    {
        SaveLegacy(Download("regular", 0, 15));
        SaveLegacy(Download("extended", 30, 45));

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
        SaveLegacy(Download("regular", 0));
        HistoricalDownload changed = Download("24_5", 0, 15);
        SaveLegacy(changed with { Candles = changed.Candles.Select(c => c with { Close = 10.5m }).ToArray() });

        HistoricalDataQueryResult result = Library.Query(Query with { RevisionPolicy = policy });

        Assert.False(result.Succeeded);
        Assert.Empty(result.Candles);
        Assert.Contains(result.Diagnostics, d => d.Code == "revision_selection_required");
    }

    [Fact]
    public void LatestFetchedSelectsOneRevisionForEachSessionBeforeUnion()
    {
        HistoricalDownload old = Download("regular", 0, 15);
        HistoricalDatasetInfo first = Assert.Single(SaveLegacy(old));
        HistoricalDatasetInfo latest = Assert.Single(SaveLegacy(old with
        {
            FetchedAtUtc = old.FetchedAtUtc.AddHours(1),
            Candles = old.Candles.Select(c => c with { Close = 10.5m }).ToArray(),
        }));
        HistoricalDatasetInfo extended = Assert.Single(SaveLegacy(Download("extended", 30, 45)));

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
        SaveLegacy(Download("24_5", 0, 15));
        HistoricalDownload other = Download("regular", 30, 45);
        other = field switch
        {
            "provider" => other with { Provider = "other" },
            "instrument" => other with { InstrumentId = "another-SOFI-id" },
            "policy" => other with { AdjustmentPolicy = "none" },
            _ => other with { AdjustmentBasis = "other" },
        };
        SaveLegacy(other);

        HistoricalDataQueryResult result = Library.Query(Query);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "incompatible_provenance");
    }

    [Fact]
    public void LegacyCoarseUnknownSessionNamesAreNeverTreatedAsCompatible()
    {
        static HistoricalDownload Coarse(string session, int offset)
        {
            HistoricalDownload source = Download(session, offset);
            return source with
            {
                SourceIntervalSeconds = 30,
                Candles = source.Candles.Select(c => c with
                {
                    EndsAtUtc = c.EndsAtUtc.AddSeconds(15), AvailableAtUtc = c.AvailableAtUtc.AddSeconds(15),
                }).ToArray(),
            };
        }
        HistoricalDatasetInfo known = Assert.Single(Library.Save(Coarse("regular", 0)));
        Library.Save(Coarse("custom-session", 30));
        HistoricalDataQuery query = Query with { SourceIntervalSeconds = 30 };

        HistoricalDataQueryResult result = Library.Query(query);

        Assert.True(result.Succeeded);
        Assert.False(result.Coverage.Complete);
        Assert.Equal(known.DatasetHash, Assert.Single(result.Datasets).DatasetHash);
        Assert.False(Library.Query(query with { SessionBounds = null }).Succeeded);
    }

    [Fact]
    public void PinsSelectOnlyTheirExactDatasetAndRemainOnePerDay()
    {
        HistoricalDatasetInfo regular = Assert.Single(Library.Save(Download("regular", 0, 15)));
        byte[] original = File.ReadAllBytes(Path.Combine(_root, regular.RelativePath));
        HistoricalDatasetInfo overnight = Assert.Single(Library.Save(Download("24_5", 0, 15, 30, 45)));
        HistoricalDataQuery pinned = Query with { PinnedHashes = [regular.DatasetHash], RevisionPolicy = HistoricalRevisionPolicy.LatestFetched };

        HistoricalDataQueryResult result = Library.Query(pinned);

        Assert.True(result.Succeeded);
        Assert.False(result.Coverage.Complete);
        Assert.Equal(2, result.Candles.Count);
        HistoricalDatasetInfo archived = Assert.Single(result.Datasets);
        Assert.Equal(regular.DatasetHash, archived.DatasetHash);
        Assert.Equal(Path.Combine(".archive", regular.DatasetHash + ".json"), archived.RelativePath);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, archived.RelativePath)));
        Assert.Equal(overnight.DatasetHash, Assert.Single(Library.Scan().Datasets).DatasetHash);
        Assert.False(Library.Query(pinned with { PinnedHashes = [regular.DatasetHash, overnight.DatasetHash] }).Succeeded);
        Assert.False(Library.Query(pinned with { PinnedHashes = [new string('a', 64)] }).Succeeded);
        Assert.False(Library.Query(pinned with { IncludeCompatibleSessions = false }).Succeeded);
    }

    private IReadOnlyList<HistoricalDatasetInfo> SaveLegacy(HistoricalDownload download)
    {
        string sourceRoot = _root + "-legacy-" + Guid.NewGuid().ToString("N");
        try
        {
            HistoricalDatasetInfo saved = Assert.Single(new JsonMarketDataLibrary(sourceRoot).Save(download));
            string relative = Path.ChangeExtension(saved.RelativePath, $"rev-{saved.DatasetHash}.json");
            string destination = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(sourceRoot, saved.RelativePath), destination);
            return [saved with { RelativePath = relative }];
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
        }
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
