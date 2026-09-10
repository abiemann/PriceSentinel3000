using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed partial class DailyFileConsolidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Consolidate_EmptyLibraryProgressCompletesWithoutCreatingData(bool exists)
    {
        if (exists) Directory.CreateDirectory(Root);
        var progress = new RecordedProgress();

        MarketDataLibraryScan result = Library.ConsolidateDailyFiles(progress);

        Assert.Empty(result.Datasets);
        Assert.Equal(0, result.TotalFileBytes);
        Assert.Equal(0, Library.Scan().TotalFileBytes);
        Assert.Empty(result.Diagnostics);
        AssertCompletedProgress(progress);
        Assert.Equal(exists, Directory.Exists(Root));
    }

    [Fact]
    public void Consolidate_ProgressTracksFileScansAndMergedDaysBeforeCompleting()
    {
        Seed(Download(Bar(0)));
        Seed(Download(Bar(1)));
        Seed(Download(Bar(0)) with { Symbol = "AAPL", InstrumentId = "instrument-aapl" });
        Seed(Download(Bar(1)) with { Symbol = "AAPL", InstrumentId = "instrument-aapl" });
        int filesAfterFirstScan = 0;
        int filesAfterFirstMerge = 0;
        int filesAtCompletion = 0;
        var progress = new RecordedProgress(percent =>
        {
            if (percent == 25) filesAfterFirstScan = ActiveJsonFiles().Length;
            if (percent is > 25 and < 90) filesAfterFirstMerge = ActiveJsonFiles().Length;
            if (percent == 100)
            {
                filesAtCompletion = ActiveJsonFiles().Length;
                Assert.True(File.Exists(Path.Combine(Root, "README.md")));
            }
        });

        MarketDataLibraryScan result = Library.ConsolidateDailyFiles(progress);

        AssertCompletedProgress(progress);
        Assert.Contains(progress.Values, percent => percent is > 0 and < 25);
        Assert.Contains(progress.Values, percent => percent is > 90 and < 100);
        Assert.Equal(4, filesAfterFirstScan);
        Assert.Equal(3, filesAfterFirstMerge);
        Assert.Equal(2, filesAtCompletion);
        Assert.Equal(2, result.Datasets.Count);
        Assert.Equal(result.Datasets.Sum(item => new FileInfo(Path.Combine(Root, item.RelativePath)).Length), result.TotalFileBytes);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Datasets, item => Assert.Equal(2, item.Coverage.ActualCandleCount));

        var unchangedProgress = new RecordedProgress();
        MarketDataLibraryScan unchanged = Library.ConsolidateDailyFiles(unchangedProgress);
        AssertCompletedProgress(unchangedProgress);
        Assert.Contains(unchangedProgress.Values, percent => percent is > 25 and < 90);
        Assert.Equal(result.Datasets.Select(item => item.DatasetHash), unchanged.Datasets.Select(item => item.DatasetHash));
        Assert.Equal(result.TotalFileBytes, unchanged.TotalFileBytes);
    }

    [Fact]
    public void Consolidate_ProgressCompletesWithMergeAndValidationNoticesWithoutChangingOriginals()
    {
        Seed(Download(Bar(0)));
        Seed(Conflicting(Download(Bar(0)), "provider"));
        File.WriteAllText(Path.Combine(Root, "invalid.json"), "broken original");
        File.WriteAllText(Path.Combine(Root, "pending.json.tmp-test"), "unfinished");
        Dictionary<string, string> originalFiles = JsonSnapshot();
        var progress = new RecordedProgress();

        MarketDataLibraryScan result = Library.ConsolidateDailyFiles(progress);

        AssertCompletedProgress(progress);
        Assert.Equal(2, result.Datasets.Count);
        Assert.Equal(result.Datasets.Sum(item => new FileInfo(Path.Combine(Root, item.RelativePath)).Length), result.TotalFileBytes);
        Assert.Contains(result.Diagnostics, item => item.Code == "daily_merge_blocked");
        Assert.Contains(result.Diagnostics, item => item.Code == "invalid_dataset");
        Assert.Contains(result.Diagnostics, item => item.Code == "interrupted_write");
        AssertSnapshotUnchanged(originalFiles);
        Assert.Equal("unfinished", File.ReadAllText(Path.Combine(Root, "pending.json.tmp-test")));
    }

    private static void AssertCompletedProgress(RecordedProgress progress)
    {
        Assert.Equal(0, progress.Values[0]);
        Assert.Equal(100, progress.Values[^1]);
        Assert.All(progress.Values, value => Assert.InRange(value, 0, 100));
        Assert.All(progress.Values.Zip(progress.Values.Skip(1)), pair => Assert.True(pair.First < pair.Second));
        Assert.Single(progress.Values, value => value == 100);
    }

    private sealed class RecordedProgress(Action<int>? observed = null) : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value)
        {
            Values.Add(value);
            observed?.Invoke(value);
        }
    }
}
