using System.Text.Json.Nodes;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class JsonBrokerHistoryBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PriceSentinel-broker-boundary-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset From = new(2026, 9, 8, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CheckedAt = From.AddDays(1).AddHours(7);
    private static readonly CollectionGapKey Key = new("AAPL", "instrument-aapl", new(2026, 9, 8), "24_5", "split", "unversioned");
    private JsonCollectionGapIndex Index => new(_root);
    private JsonMarketDataLibrary Library => new(_root);

    [Fact]
    public void BoundaryPersistsAlongsideExistingCandlesWithoutChangingTheirHashOrCreatingAnArchive()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(From.AddHours(7), Bar(0))));
        Assert.DoesNotContain("brokerHistoryUnavailableAtUtc", File.ReadAllText(Path.Combine(_root, original.RelativePath)));

        Index.RecordDiscoveryUnavailable(Key, CheckedAt);
        Index.ResolveSavedRanges(Key, [new(From, From.AddSeconds(15))]);

        Assert.Equal(CheckedAt, Query().BrokerHistoryUnavailableAtUtc);
        HistoricalDatasetInfo current = Assert.Single(Library.Scan().Datasets);
        Assert.Equal(original.DatasetHash, current.DatasetHash);
        HistoricalDataset dataset = Library.Read(original.DatasetHash);
        Assert.Equal(Bar(0), Assert.Single(dataset.Candles));
        Assert.Equal(CheckedAt, Assert.Single(dataset.Collection!).BrokerHistoryUnavailableAtUtc);
        Assert.False(Directory.Exists(Path.Combine(_root, ".archive")));
    }

    [Fact]
    public void CopyingTheDailyJsonPreservesTheBoundaryWithoutAnExternalDatabase()
    {
        Index.RecordDiscoveryUnavailable(Key with { InstrumentId = null }, CheckedAt);
        string file = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        string destinationRoot = Path.Combine(_root, "copied");
        string destination = Path.Combine(destinationRoot, Path.GetRelativePath(_root, file));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination);

        var copied = new JsonCollectionGapIndex(destinationRoot);
        Assert.Equal(CheckedAt, copied.Query(Key with { InstrumentId = null }, From, From.AddMinutes(1), CheckedAt).BrokerHistoryUnavailableAtUtc);
        Assert.Single(Directory.GetFiles(destinationRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void OnlyAPositiveProviderObservationClearsTheBoundaryWithoutNewCandles()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(From.AddHours(7), Bar(0))));
        Index.RecordDiscoveryUnavailable(Key, CheckedAt);
        Index.RecordDownloadAttempt(Key, From, From.AddMinutes(1), CheckedAt.AddMinutes(1));
        Index.RecordAttempt(Key, From, From.AddMinutes(1), [new(From.AddSeconds(15), From.AddMinutes(1))], false, CheckedAt.AddMinutes(1), null);
        Assert.Equal(CheckedAt, Query().BrokerHistoryUnavailableAtUtc);

        Index.RecordAttempt(Key, From, From.AddMinutes(1), [], true, CheckedAt.AddMinutes(2), null);

        Assert.Null(Query().BrokerHistoryUnavailableAtUtc);
        Assert.Equal(original.DatasetHash, Assert.Single(Library.Scan().Datasets).DatasetHash);
        Assert.DoesNotContain("brokerHistoryUnavailableAtUtc", File.ReadAllText(Path.Combine(_root, original.RelativePath)));
    }

    [Fact]
    public void OldImportsAndNewerEmptyDownloadsKeepTheBoundaryButNewerCandlesClearIt()
    {
        Library.Save(Download(From.AddHours(7), Bar(0)));
        Index.RecordDiscoveryUnavailable(Key, CheckedAt);
        Library.Save(Download(CheckedAt.AddMinutes(-1), Bar(1)));
        Assert.Equal(CheckedAt, Query().BrokerHistoryUnavailableAtUtc);
        Library.Save(Download(CheckedAt.AddMinutes(1)) with { RequestedThroughUtc = From.AddMinutes(2) });
        Assert.Equal(CheckedAt, Query().BrokerHistoryUnavailableAtUtc);
        // A later read/merge of this wider empty result must not mistake the old
        // candles it contains for fresh provider availability.
        Library.Save(Download(CheckedAt.AddMinutes(1)) with { RequestedThroughUtc = From.AddMinutes(3) });
        Assert.Equal(CheckedAt, Query().BrokerHistoryUnavailableAtUtc);

        Library.Save(Download(CheckedAt.AddMinutes(2), Bar(2)));

        Assert.Null(Query().BrokerHistoryUnavailableAtUtc);
        Assert.Equal(new[] { Bar(0), Bar(1), Bar(2) }, Library.Read(Assert.Single(Library.Scan().Datasets).DatasetHash).Candles);
    }

    [Fact]
    public void ConsolidatingAnOlderSnapshotCannotRestoreABoundaryDisprovedByNewerCandles()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(From.AddHours(7), Bar(0))));
        Index.RecordDiscoveryUnavailable(Key, CheckedAt);
        Library.Save(Download(CheckedAt.AddMinutes(1), Bar(1)));
        string archive = Path.Combine(_root, ".archive", original.DatasetHash + ".json");
        Assert.True(File.Exists(archive));
        string duplicate = Path.Combine(_root, "copied-boundary.json");
        File.Copy(archive, duplicate);

        MarketDataLibraryScan consolidated = Library.ConsolidateDailyFiles();

        Assert.Empty(consolidated.Diagnostics);
        Assert.Single(consolidated.Datasets);
        Assert.Null(Query().BrokerHistoryUnavailableAtUtc);
        Assert.Equal(new[] { Bar(0), Bar(1) }, Library.Read(consolidated.Datasets[0].DatasetHash).Candles);
    }

    [Fact]
    public void CopiedOldBoundaryCannotOverrideANewerPositiveObservationWithUnchangedCandles()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(From.AddHours(7), Bar(0))));
        Index.RecordDiscoveryUnavailable(Key, CheckedAt);
        string originalFile = Path.Combine(_root, original.RelativePath);
        string oldBoundary = File.ReadAllText(originalFile);
        Index.RecordAttempt(Key, From, From.AddMinutes(1), [], true, CheckedAt.AddMinutes(1), null);
        // No candle changed: both files deliberately retain the same candle hash.
        Assert.Equal(original.DatasetHash, Assert.Single(Library.Scan().Datasets).DatasetHash);
        File.WriteAllText(Path.Combine(_root, "copied-boundary.json"), oldBoundary);

        MarketDataLibraryScan consolidated = Library.ConsolidateDailyFiles();

        Assert.Empty(consolidated.Diagnostics);
        Assert.Null(Query().BrokerHistoryUnavailableAtUtc);
        CollectionGapState metadata = Assert.Single(Library.Read(Assert.Single(consolidated.Datasets).DatasetHash).Collection!);
        Assert.Equal(CheckedAt.AddMinutes(1), metadata.LastBrokerCandlesAtUtc);
        Assert.Equal(Bar(0), Assert.Single(Library.Read(original.DatasetHash).Candles));
    }

    [Fact]
    public void BoundaryRemainsScopedToProviderInstrumentAndCollectionSession()
    {
        Index.RecordDiscoveryUnavailable(Key, CheckedAt);
        Assert.Null(Index.Query(Key with { InstrumentId = "other" }, From, From.AddMinutes(1), CheckedAt).BrokerHistoryUnavailableAtUtc);
        Assert.Null(Index.Query(Key with { SessionBounds = "regular" }, From, From.AddMinutes(1), CheckedAt).BrokerHistoryUnavailableAtUtc);
        Assert.Null(new JsonCollectionGapIndex(_root, "Other").Query(Key, From, From.AddMinutes(1), CheckedAt).BrokerHistoryUnavailableAtUtc);
    }

    [Fact]
    public void TodaysDateCannotBeRecordedAsAnOlderHistoryBoundary()
    {
        Assert.Throws<ArgumentException>(() => Index.RecordDiscoveryUnavailable(Key, From.AddHours(1)));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void InvalidBoundaryMetadataFailsValidationEvenThoughCandleHashIsUnchanged()
    {
        Index.RecordDiscoveryUnavailable(Key, CheckedAt);
        string file = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        JsonNode document = JsonNode.Parse(File.ReadAllText(file))!;
        document["collection"]![0]!["brokerHistoryUnavailableAtUtc"] = From.ToString("O");
        File.WriteAllText(file, document.ToJsonString());

        Assert.Throws<InvalidDataException>(() => Query());
    }

    private CollectionGapSnapshot Query() => Index.Query(Key, From, From.AddMinutes(1), CheckedAt);
    private static HistoricalCandle Bar(int index)
    {
        DateTimeOffset start = From.AddSeconds(index * 15);
        return new(start, start.AddSeconds(15), start.AddSeconds(15), 80m, 81m, 79m, 80m, 100m);
    }
    private static HistoricalDownload Download(DateTimeOffset fetched, params HistoricalCandle[] candles) => new("Robinhood", "instrument-aapl", "AAPL", 15,
        "split", "unversioned", "24_5", fetched, From, From.AddMinutes(1), candles);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}