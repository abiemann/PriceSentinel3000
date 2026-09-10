using System.Text.Json.Nodes;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class JsonCollectionGapIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PriceSentinel-json-gaps-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset From = new(2026, 9, 9, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CheckedAt = From.AddHours(7);
    private static readonly CollectionGapKey Key = new("AAPL", "instrument-aapl", new(2026, 9, 9), "24_5", "split", "unversioned");
    private JsonCollectionGapIndex Index => new(_root);
    private JsonMarketDataLibrary Library => new(_root);

    [Fact]
    public void ReadsCreateNothingAndAttemptsPersistInTheDailyJsonWithoutASeparateDatabase()
    {
        Assert.True(Index.SupportsAttemptTracking);
        Index.Initialize();
        Assert.Empty(Query().AttemptedRanges);
        Assert.False(Directory.Exists(_root));
        Index.RecordDownloadAttempt(Key, From, From.AddHours(1), CheckedAt);
        Index.RecordDownloadAttempt(Key, From, From.AddHours(1), CheckedAt.AddMinutes(1));
        Index.RecordDownloadAttempt(Key, From, From.AddHours(1), CheckedAt.AddMinutes(2));

        CollectionGapAttempt attempted = Assert.Single(Query().AttemptedRanges);
        Assert.Equal(new(Gap(0, 60), 2), attempted);
        Assert.Empty(Query().UnavailableRanges);
        string file = Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        Assert.EndsWith(Path.Combine("AAPL", "2026-09-09.15s.json"), file);
        Assert.Contains("\"collection\"", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(_root, ".archive")));
    }

    [Fact]
    public void OverlappingAttemptsSplitCountsAndOnlySavedPiecesClearBothKindsOfEvidence()
    {
        Index.RecordDownloadAttempt(Key, From, From.AddMinutes(30), CheckedAt);
        Index.RecordDownloadAttempt(Key, From.AddMinutes(15), From.AddMinutes(45), CheckedAt);
        Assert.Equal(new[] { new CollectionGapAttempt(Gap(0, 15), 1), new(Gap(15, 30), 2), new(Gap(30, 45), 1) }, Query().AttemptedRanges);
        Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 60)], false, CheckedAt, null);
        Index.ResolveSavedRanges(Key, [Gap(20, 25), Gap(35, 40)]);

        Assert.Equal(new[] { new CollectionGapAttempt(Gap(0, 15), 1), new(Gap(15, 20), 2), new(Gap(25, 30), 2),
            new(Gap(30, 35), 1), new(Gap(40, 45), 1) }, Query().AttemptedRanges);
        Assert.Equal(new[] { Gap(0, 20), Gap(25, 35), Gap(40, 60) }, Query().UnavailableRanges);
        Assert.True(Query().HasReturnedCandles);
    }

    [Fact]
    public void ObservingEmptyOrPartialResponsesDoesNotResetAttemptCountsOrInferMissingFilled()
    {
        Index.RecordDownloadAttempt(Key, From, From.AddHours(1), CheckedAt);
        Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 60)], false, CheckedAt, null);
        Index.RecordAttempt(Key, From, From.AddHours(1), [], true, CheckedAt.AddMinutes(1), null);
        Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(15, 30)], true, CheckedAt.AddMinutes(2), null);

        Assert.Equal(new(Gap(0, 60), 1), Assert.Single(Query().AttemptedRanges));
        Assert.Equal(Gap(0, 60), Assert.Single(Query().UnavailableRanges));
    }

    [Fact]
    public void MetadataUpdatesKeepCandleHashAndPinnedReadsWithoutCreatingArchives()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(Bar(0))));
        Index.RecordDownloadAttempt(Key, From, From.AddMinutes(1), CheckedAt);
        Index.RecordAttempt(Key, From, From.AddMinutes(1), [new(From.AddSeconds(15), From.AddMinutes(1))], true, CheckedAt, null);

        HistoricalDatasetInfo current = Assert.Single(Library.Scan().Datasets);
        Assert.Equal(original.DatasetHash, current.DatasetHash);
        HistoricalDataset pinned = Library.Read(original.DatasetHash);
        Assert.Equal(Bar(0), Assert.Single(pinned.Candles));
        Assert.NotNull(pinned.Collection);
        Assert.False(Directory.Exists(Path.Combine(_root, ".archive")));
        Assert.Equal(new(new(From.AddSeconds(15), From.AddMinutes(1)), 1), Assert.Single(Query().AttemptedRanges));
    }

    [Fact]
    public void SaveClearsOnlyFilledCandlesAndPreservesRemainingMetadataAndArchivedHistory()
    {
        Index.RecordDownloadAttempt(Key, From, From.AddMinutes(1), CheckedAt);
        Index.RecordDownloadAttempt(Key, From, From.AddMinutes(1), CheckedAt);
        Index.RecordAttempt(Key, From, From.AddMinutes(1), [new(From, From.AddMinutes(1))], false, CheckedAt, null);

        HistoricalDatasetInfo saved = Assert.Single(Library.Save(Download(Bar(1))));

        Assert.Equal(new[] { new CollectionGapAttempt(new(From, From.AddSeconds(15)), 2),
            new(new(From.AddSeconds(30), From.AddMinutes(1)), 2) }, Query().AttemptedRanges);
        Assert.Equal(new[] { new HistoricalGap(From, From.AddSeconds(15)), new(From.AddSeconds(30), From.AddMinutes(1)) }, Query().UnavailableRanges);
        Assert.Equal(Bar(1), Assert.Single(Library.Read(saved.DatasetHash).Candles));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, ".archive"), "*.json"));
    }

    [Fact]
    public void UnknownInstrumentCarrierIsNotAReplaySourceAndPromotesOnlyWithSavedData()
    {
        CollectionGapKey unknown = Key with { InstrumentId = null };
        Index.RecordDownloadAttempt(unknown, From, From.AddMinutes(1), CheckedAt);
        Index.RecordAttempt(unknown, From, From.AddMinutes(1), [new(From, From.AddMinutes(1))], false, CheckedAt, null);
        HistoricalDataQueryResult before = Library.Query(new("AAPL", From, From.AddMinutes(1)));
        Assert.True(before.Succeeded);
        Assert.Empty(before.Datasets);

        HistoricalDatasetInfo saved = Assert.Single(Library.Save(Download(Bar(0))));
        Assert.Equal(Key.InstrumentId, saved.InstrumentId);
        Assert.Single(Library.Query(new("AAPL", From, From.AddMinutes(1))).Candles);
        CollectionGapState metadata = Assert.Single(Library.Read(saved.DatasetHash).Collection!);
        Assert.Equal(Key.InstrumentId, metadata.Key.InstrumentId);
        Assert.Equal(new(new(From.AddSeconds(15), From.AddMinutes(1)), 1), Assert.Single(Query().AttemptedRanges));
    }

    [Fact]
    public void CopyingOnlyTheDailyJsonPreservesCountsAndUnavailableRanges()
    {
        Index.RecordDownloadAttempt(Key, From, From.AddHours(1), CheckedAt);
        Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(15, 60)], false, CheckedAt, null);
        string original = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        string copiedRoot = Path.Combine(_root, "copied");
        string copied = Path.Combine(copiedRoot, Path.GetRelativePath(_root, original));
        Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
        File.Copy(original, copied);

        CollectionGapSnapshot snapshot = new JsonCollectionGapIndex(copiedRoot).Query(Key, From, From.AddHours(1), CheckedAt);
        Assert.Equal(Query().AttemptedRanges, snapshot.AttemptedRanges);
        Assert.Equal(Query().UnavailableRanges, snapshot.UnavailableRanges);
        Assert.Single(Directory.GetFiles(copiedRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ExpiredTodayCooldownDoesNotEraseHistoricalEvidenceOrAttemptCount()
    {
        Index.RecordDownloadAttempt(Key, From, From.AddHours(1), CheckedAt);
        Index.RecordAttempt(Key, From, From.AddHours(1), [Gap(0, 60)], false, CheckedAt, CheckedAt.AddMinutes(15));
        Assert.Empty(Query(CheckedAt.AddMinutes(15)).UnavailableRanges);
        CollectionGapSnapshot tomorrow = Query(CheckedAt.AddDays(1));
        Assert.Equal(Gap(0, 60), Assert.Single(tomorrow.UnavailableRanges));
        Assert.Equal(new(Gap(0, 60), 1), Assert.Single(tomorrow.AttemptedRanges));
    }

    [Fact]
    public void MetadataIsStrictlyValidatedEvenThoughItDoesNotChangeTheCandleHash()
    {
        Index.RecordDownloadAttempt(Key, From, From.AddHours(1), CheckedAt);
        string file = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        JsonNode json = JsonNode.Parse(File.ReadAllText(file))!;
        string hash = json["datasetHash"]!.GetValue<string>();
        json["collection"]![0]!["attemptedRanges"]![0]!["attempts"] = 3;
        File.WriteAllText(file, json.ToJsonString());

        Assert.Throws<InvalidDataException>(() => Library.Read(hash));
        Assert.Throws<InvalidDataException>(() => Query());
    }

    [Fact]
    public void DifferentInstrumentCannotOverwriteExistingDailyMetadataOrCandles()
    {
        Library.Save(Download(Bar(0)));
        Index.RecordDownloadAttempt(Key, From, From.AddHours(1), CheckedAt);
        string file = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        byte[] original = File.ReadAllBytes(file);

        Assert.Throws<InvalidDataException>(() => Index.RecordDownloadAttempt(Key with { InstrumentId = "other" }, From, From.AddHours(1), CheckedAt));
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Empty(Index.Query(Key with { InstrumentId = "other" }, From, From.AddHours(1), CheckedAt).AttemptedRanges);
    }

    [Fact]
    public void InvalidRangesAreRejectedBeforeWritingAnyFile()
    {
        Assert.Throws<ArgumentException>(() => Index.RecordDownloadAttempt(Key, From.AddSeconds(1), From.AddMinutes(1), CheckedAt));
        Assert.Throws<ArgumentException>(() => Index.RecordDownloadAttempt(Key, From.AddDays(-1), From, CheckedAt));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ConcurrentAttemptsAndSavesKeepOneValidDailyFileWithoutLosingCountsOrCandles()
    {
        Index.RecordDownloadAttempt(Key, From, From.AddMinutes(1), CheckedAt);
        await Task.WhenAll(
            Task.Run(() => Index.RecordDownloadAttempt(Key, From, From.AddMinutes(1), CheckedAt.AddSeconds(1))),
            Task.Run(() => Library.Save(Download(Bar(0)))));

        HistoricalDatasetInfo current = Assert.Single(Library.Scan().Datasets);
        Assert.Equal(Bar(0), Assert.Single(Library.Read(current.DatasetHash).Candles));
        Assert.Equal(new(new(From.AddSeconds(15), From.AddMinutes(1)), 2), Assert.Single(Query().AttemptedRanges));
        Assert.DoesNotContain(Library.Scan().Diagnostics, item => item.Code == "invalid_dataset");
    }

    [Fact]
    public void ConsolidatingCopiedDailyFilesPreservesTheHighestCountRatherThanAddingDuplicateAttempts()
    {
        for (int count = 1; count <= 2; count++)
        {
            string sourceRoot = Path.Combine(_root, "source-" + count);
            var sourceIndex = new JsonCollectionGapIndex(sourceRoot);
            for (int attempt = 0; attempt < count; attempt++)
                sourceIndex.RecordDownloadAttempt(Key, From, From.AddMinutes(1), CheckedAt.AddMinutes(count));
            sourceIndex.RecordAttempt(Key, From, From.AddMinutes(1), [new(From, From.AddMinutes(1))], false, CheckedAt.AddMinutes(count), null);
            HistoricalDatasetInfo source = Assert.Single(new JsonMarketDataLibrary(sourceRoot).Scan().Datasets);
            string destination = Path.Combine(_root, "2026", "09 - September", "AAPL", "2026-09-09.15s.rev-" + source.DatasetHash + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(sourceRoot, source.RelativePath), destination);
            Directory.Delete(sourceRoot, recursive: true);
        }

        MarketDataLibraryScan merged = Library.ConsolidateDailyFiles();

        Assert.Empty(merged.Diagnostics);
        Assert.Single(merged.Datasets);
        Assert.Equal(new(new(From, From.AddMinutes(1)), 2), Assert.Single(Query().AttemptedRanges));
        Assert.Equal(new(From, From.AddMinutes(1)), Assert.Single(Query().UnavailableRanges));
    }

    private CollectionGapSnapshot Query(DateTimeOffset? now = null) => Index.Query(Key, From, From.AddHours(1), now ?? CheckedAt);
    private static HistoricalGap Gap(int from, int through) => new(From.AddMinutes(from), From.AddMinutes(through));
    private static HistoricalCandle Bar(int index)
    {
        DateTimeOffset start = From.AddSeconds(index * 15);
        return new(start, start.AddSeconds(15), start.AddSeconds(15), 80m, 81m, 79m, 80m, 100m);
    }
    private static HistoricalDownload Download(params HistoricalCandle[] candles) => new("Robinhood", "instrument-aapl", "AAPL", 15,
        "split", "unversioned", "24_5", CheckedAt, From, From.AddMinutes(1), candles);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
