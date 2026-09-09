using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class MetadataCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PriceSentinel-metadata-cache-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 13, 30, 0, TimeSpan.Zero);
    private string Root => Path.Combine(_directory, "library");

    [Fact]
    public void WarmScansAndIncrementalQueriesAndSavesDoNotReopenUnrelatedFiles()
    {
        var library = new JsonMarketDataLibrary(Root);
        library.Save(Download("AAPL", Bar(0)));
        string[] otherPaths = new[] { "AMD", "MSFT", "NFLX", "NVDA", "SPY" }
            .Select(symbol => Path.Combine(Root, Assert.Single(library.Save(Download(symbol, Bar(0)))).RelativePath)).ToArray();
        Assert.Equal(6, library.Scan().Datasets.Count);
        var held = new List<FileStream>();
        try
        {
            // Metadata remains readable, but reparsing any unrelated candle file
            // would fail and surface an invalid_dataset diagnostic.
            foreach (string path in otherPaths) held.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.None));
            Assert.NotEmpty(new JsonMarketDataLibrary(Root).Scan().Diagnostics);
            for (int next = 1; next <= 3; next++)
            {
                HistoricalDataQueryResult query = library.Query(new("AAPL", Start, Start.AddSeconds(60)));
                Assert.True(query.Succeeded);
                Assert.Empty(query.Diagnostics);
                Assert.Equal(next, query.Candles.Count);
                library.Save(Download("AAPL", Bar(next)));
                MarketDataLibraryScan scan = library.Scan();
                Assert.Empty(scan.Diagnostics);
                Assert.Equal(6, scan.Datasets.Count);
                Assert.Equal(next + 1, Assert.Single(scan.Datasets, item => item.Symbol == "AAPL").Coverage.ActualCandleCount);
            }
        }
        finally { foreach (FileStream stream in held) stream.Dispose(); }
        Assert.Equal(4, library.Query(new("AAPL", Start, Start.AddSeconds(60))).Candles.Count);
    }

    [Fact]
    public void ModifiedFilesAreRevalidatedAndRepairedFilesReturnToTheScan()
    {
        var library = new JsonMarketDataLibrary(Root);
        HistoricalDatasetInfo original = Assert.Single(library.Save(Download("AAPL", Bar(0))));
        string path = Path.Combine(Root, original.RelativePath);
        byte[] content = File.ReadAllBytes(path);
        DateTime lastWrite = File.GetLastWriteTimeUtc(path);
        Assert.Empty(library.Scan().Diagnostics);

        string corrupt = File.ReadAllText(path).Replace("\"80\"", "\"90\"", StringComparison.Ordinal);
        File.WriteAllText(path, corrupt);
        File.SetLastWriteTimeUtc(path, lastWrite.AddSeconds(1));
        Assert.Equal(content.Length, new FileInfo(path).Length);
        MarketDataLibraryScan damaged = library.Scan();
        Assert.Empty(damaged.Datasets);
        Assert.Contains(damaged.Diagnostics, item => item.Code == "invalid_dataset");
        Assert.Empty(library.Query(new("AAPL", Start, Start.AddSeconds(15))).Candles);

        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, lastWrite.AddSeconds(2));
        MarketDataLibraryScan repaired = library.Scan();
        Assert.Empty(repaired.Diagnostics);
        Assert.Equal(original.DatasetHash, Assert.Single(repaired.Datasets).DatasetHash);
        Assert.Equal(Bar(0), Assert.Single(library.Read(original.DatasetHash).Candles));
    }

    [Fact]
    public void AtomicReplacementWithSameLengthAndLastWriteUsesTheNewFileMetadata()
    {
        var library = new JsonMarketDataLibrary(Root);
        HistoricalDatasetInfo original = Assert.Single(library.Save(Download("AAPL", Bar(0))));
        string path = Path.Combine(Root, original.RelativePath);
        File.SetCreationTimeUtc(path, new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        DateTime lastWrite = File.GetLastWriteTimeUtc(path);
        long length = new FileInfo(path).Length;
        Assert.Equal(original.DatasetHash, Assert.Single(library.Scan().Datasets).DatasetHash);

        (HistoricalDatasetInfo replacement, byte[] bytes) = Source(Download("AAPL", Bar(0, 81m)));
        string temporary = path + ".replacement";
        File.WriteAllBytes(temporary, bytes);
        File.SetCreationTimeUtc(temporary, new(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(temporary, lastWrite);
        File.Move(temporary, path, overwrite: true);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));

        HistoricalDatasetInfo current = Assert.Single(library.Scan().Datasets);
        Assert.Equal(replacement.DatasetHash, current.DatasetHash);
        Assert.NotEqual(original.DatasetHash, current.DatasetHash);
        Assert.Equal(81m, Assert.Single(library.Query(new("AAPL", Start, Start.AddSeconds(15))).Candles).Close);
        Assert.Throws<InvalidDataException>(() => library.Read(original.DatasetHash));
    }

    [Fact]
    public void ImportedRenamedAndDeletedFilesAreObservedByTheSameInstance()
    {
        var library = new JsonMarketDataLibrary(Root);
        HistoricalDatasetInfo first = Assert.Single(library.Save(Download("AAPL", Bar(0))));
        Assert.Single(library.Scan().Datasets);
        (HistoricalDatasetInfo second, byte[] bytes) = Source(Download("MSFT", Bar(0)));
        string imported = Path.Combine(Root, "imported.json");
        File.WriteAllBytes(imported, bytes);
        Assert.Equal(2, library.Scan().Datasets.Count);
        Assert.Equal(second.DatasetHash, Assert.Single(library.Query(new("MSFT", Start, Start.AddSeconds(15))).Datasets).DatasetHash);

        string renamed = Path.Combine(Root, "renamed.json");
        File.Move(imported, renamed);
        Assert.Equal("renamed.json", Assert.Single(library.Scan().Datasets, item => item.Symbol == "MSFT").RelativePath);
        File.Delete(renamed);
        Assert.Equal(first.DatasetHash, Assert.Single(library.Scan().Datasets).DatasetHash);
        Assert.Empty(library.Query(new("MSFT", Start, Start.AddSeconds(15))).Candles);
        Assert.Throws<InvalidDataException>(() => library.Read(second.DatasetHash));
    }

    [Fact]
    public void ConsolidationAndLaterSaveInvalidateDescriptionsWithoutLosingExactHistory()
    {
        Directory.CreateDirectory(Root);
        (HistoricalDatasetInfo first, byte[] firstBytes) = Source(Download("AAPL", Bar(0)));
        var (_, secondBytes) = Source(Download("AAPL", Bar(2)));
        File.WriteAllBytes(Path.Combine(Root, "first.json"), firstBytes);
        File.WriteAllBytes(Path.Combine(Root, "second.json"), secondBytes);
        var library = new JsonMarketDataLibrary(Root);
        Assert.Equal(2, library.Scan().Datasets.Count);

        HistoricalDatasetInfo merged = Assert.Single(library.ConsolidateDailyFiles().Datasets);
        Assert.Equal(2, merged.Coverage.ActualCandleCount);
        Assert.Single(merged.Coverage.Gaps);
        Assert.Equal(merged.DatasetHash, Assert.Single(library.Scan().Datasets).DatasetHash);
        HistoricalDatasetInfo filled = Assert.Single(library.Save(Download("AAPL", Bar(1))));
        Assert.NotEqual(merged.DatasetHash, filled.DatasetHash);
        Assert.Equal(filled.DatasetHash, Assert.Single(library.Scan().Datasets).DatasetHash);
        Assert.Equal(new[] { Bar(0), Bar(1), Bar(2) }, library.Read(filled.DatasetHash).Candles);
        Assert.Equal(Bar(0), Assert.Single(library.Read(first.DatasetHash).Candles));
        Assert.Single(Directory.GetFiles(Root, "*.json", SearchOption.AllDirectories),
            path => !Path.GetRelativePath(Root, path).StartsWith(".archive" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public void CachedDescriptionsDoNotExposeMutableCoverageAndReadsStillValidateActualCandles()
    {
        var library = new JsonMarketDataLibrary(Root);
        HistoricalDatasetInfo original = Assert.Single(library.Save(Download("AAPL", Bar(0), Bar(2))));
        HistoricalDatasetInfo description = Assert.Single(library.Scan().Datasets);
        IList<HistoricalGap> gaps = Assert.IsAssignableFrom<IList<HistoricalGap>>(description.Coverage.Gaps);
        Assert.True(gaps.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => gaps.Clear());
        Assert.Single(Assert.Single(library.Scan().Datasets).Coverage.Gaps);

        string path = Path.Combine(Root, original.RelativePath);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Empty(library.Scan().Diagnostics);
        HistoricalDataQueryResult query = library.Query(new("AAPL", Start, Start.AddSeconds(45)));
        Assert.False(query.Succeeded);
        Assert.Contains(query.Diagnostics, item => item.Code == "dataset_read_failed");
    }

    private (HistoricalDatasetInfo Info, byte[] Bytes) Source(HistoricalDownload download)
    {
        string root = Path.Combine(_directory, "sources", Guid.NewGuid().ToString("N"));
        HistoricalDatasetInfo info = Assert.Single(new JsonMarketDataLibrary(root).Save(download));
        return (info, File.ReadAllBytes(Path.Combine(root, info.RelativePath)));
    }

    private static HistoricalDownload Download(string symbol, params HistoricalCandle[] candles) => new(
        "Robinhood", "instrument-" + symbol, symbol, 15, "split", "split", "regular", Start.AddDays(1),
        candles.Min(candle => candle.StartsAtUtc), candles.Max(candle => candle.EndsAtUtc), candles);
    private static HistoricalCandle Bar(int index, decimal price = 80m)
    {
        DateTimeOffset at = Start.AddSeconds(index * 15);
        return new(at, at.AddSeconds(15), at.AddSeconds(15), price, price + 1m, price - 1m, price, 100m);
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
