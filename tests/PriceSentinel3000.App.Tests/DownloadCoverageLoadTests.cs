using System.IO;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task DownloadCoverageLoad_ScansPinnedFolderWithoutLibraryTabAndUsesFreshSavedMetadata() =>
        host.RunAsync(async () =>
    {
        await using var fixture = new DownloadCoverageLoadFixture();
        Assert.Empty(fixture.ViewModel.Datasets);

        LibraryCoverageTimeline empty = await fixture.ViewModel.LoadDownloadCoverageAsync(
            fixture.Job, CancellationToken.None);

        Assert.Equal(0, empty.Blocks.Sum(block => block.SavedCandleCount));
        Assert.All(empty.Blocks, block => Assert.Equal(LibraryCoverageBlockState.Missing, block.State));
        Assert.Empty(fixture.ViewModel.Datasets);
        Assert.Single(fixture.OpenedRoots);

        // The currently selected folder's inventory must not replace the queued job's folder.
        fixture.ViewModel.Datasets.Add(DownloadCoverageMetadata(24));
        fixture.Library.Datasets = [DownloadCoverageMetadata(6)];

        LibraryCoverageTimeline saved = await fixture.ViewModel.LoadDownloadCoverageAsync(
            fixture.Job, CancellationToken.None);

        Assert.Equal(1440, saved.Blocks.Sum(block => block.SavedCandleCount));
        Assert.Equal(24, saved.Blocks.Count(block => block.State == LibraryCoverageBlockState.Complete));
        Assert.Equal(72, saved.Blocks.Count(block => block.State == LibraryCoverageBlockState.Missing));
        Assert.Equal(5760, Assert.Single(fixture.ViewModel.Datasets).Coverage.ActualCandleCount);
        Assert.Equal(2, fixture.OpenedRoots.Count);
        Assert.All(fixture.OpenedRoots, root => Assert.Equal(fixture.Job.LibraryRootPath, root));
        Assert.NotEqual(fixture.ViewModel.LibraryRootPath, fixture.Job.LibraryRootPath);
        fixture.AssertReadOnly();
    });

    [Fact]
    public Task DownloadCoverageLoad_ScanNoticesDoNotDiscardValidatedSavedMetadata() => host.RunAsync(async () =>
    {
        await using var fixture = new DownloadCoverageLoadFixture();
        fixture.Library.Datasets = [DownloadCoverageMetadata(6)];
        fixture.Library.Diagnostics = [new("MSFT-broken.json", "invalid", "Invalid saved candle file.")];

        LibraryCoverageTimeline result = await fixture.ViewModel.LoadDownloadCoverageAsync(
            fixture.Job, CancellationToken.None);

        Assert.Equal(1440, result.Blocks.Sum(block => block.SavedCandleCount));
        Assert.Equal(1, fixture.Provider.EligibilityCalls);
        fixture.AssertReadOnly();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DownloadCoverageLoad_CancellationOrDisposalStopsPendingEligibility(bool dispose) =>
        host.RunAsync(async () =>
    {
        await using var fixture = new DownloadCoverageLoadFixture();
        fixture.Provider.HoldEligibility = true;
        using var cancellation = new CancellationTokenSource();
        Task<LibraryCoverageTimeline> loading = fixture.ViewModel.LoadDownloadCoverageAsync(
            fixture.Job, cancellation.Token);
        await fixture.Provider.EligibilityStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (dispose) await fixture.ViewModel.DisposeAsync();
        else await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loading);
        Assert.True(fixture.Provider.ReceivedToken.IsCancellationRequested);
        fixture.AssertReadOnly();
    });

    [Fact]
    public Task DownloadCoverageLoad_SelectedMissingRangeKeepsOriginalFolderAfterSettingsChange() =>
        host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        DataRetentionViewModel vm = fixture.ViewModel;
        LibraryCoverageTimeline timeline = DownloadSelectionTimeline();
        string selectedRoot = Path.Combine(fixture.Root, "new-library");
        vm.LibraryRootPath = selectedRoot;
        await vm.SaveScheduleAsync();

        await vm.DownloadCoverageAsync(timeline, timeline.Blocks[2], fixture.LibraryRoot);

        CollectionJob job = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(fixture.LibraryRoot, job.LibraryRootPath);
        Assert.Equal(selectedRoot, fixture.Collector.State.Settings.LibraryRootPath);
        Assert.Equal(CollectionJobStatus.Complete, job.Status);
        Assert.Single(fixture.Provider.Requests);
        Assert.Empty(new JsonMarketDataLibrary(selectedRoot).Scan().Datasets);
        Assert.Empty(vm.Datasets);

        LibraryCoverageTimeline refreshed = await vm.LoadDownloadCoverageAsync(job, CancellationToken.None);

        Assert.Equal(180, refreshed.Blocks.Sum(block => block.SavedCandleCount));
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(180, Assert.Single(new JsonMarketDataLibrary(fixture.LibraryRoot).Scan().Datasets)
            .Coverage.ActualCandleCount);
    });

    private static HistoricalDatasetInfo DownloadCoverageMetadata(int hours)
    {
        DateTimeOffset from = new(2026, 9, 9, 4, 0, 0, TimeSpan.Zero);
        DateTimeOffset through = from.AddHours(hours);
        return new($"download-coverage-{hours}", $"coverage-{hours}.json", "Robinhood", "id-AAPL", "AAPL",
            CoverageLoadDay, 15, "split", "robinhood-split-unversioned", "24_5", through,
            new(from, through, from, through, hours * 240, hours * 240, true, true, []));
    }

    private sealed class DownloadCoverageLoadFixture : IAsyncDisposable
    {
        public CoverageLoadProvider Provider { get; } = new() { Eligible = true };
        public DownloadCoverageScanLibrary Library { get; }
        public DataRetentionViewModel ViewModel { get; }
        public CollectionJob Job { get; }
        public List<string> OpenedRoots { get; } = [];
        private int _connectionCalls;

        public DownloadCoverageLoadFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "pricesentinel-download-coverage", Guid.NewGuid().ToString("N"));
            Library = new(Path.Combine(root, "original-library"));
            Job = new()
            {
                Symbol = "AAPL", SessionDate = CoverageLoadDay, SessionBounds = "24_5",
                LibraryRootPath = Library.RootPath, Status = CollectionJobStatus.Partial,
            };
            var store = new MemoryCollectionStore(Path.Combine(root, "current-library"));
            var clock = new TestClock { Now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero) };
            IMarketDataLibrary OpenLibrary(string requestedRoot)
            {
                OpenedRoots.Add(requestedRoot);
                Assert.Equal(Library.RootPath, requestedRoot);
                return Library;
            }
            Task UnexpectedConnection(CancellationToken token)
            {
                _connectionCalls++;
                throw new InvalidOperationException("Opening queued coverage must not authenticate.");
            }
            var collector = new MarketDataCollector(store, Provider, OpenLibrary, clock);
            ViewModel = new(collector, Provider, Provider, Provider, OpenLibrary,
                UnexpectedConnection, () => true, reconnect: UnexpectedConnection, clock: clock);
        }

        public void AssertReadOnly()
        {
            Assert.Equal(0, _connectionCalls);
            Assert.Equal(0, Provider.HistoryCalls);
            Assert.Equal(0, Provider.UnrelatedCalls);
        }

        public ValueTask DisposeAsync() => ViewModel.DisposeAsync();
    }

    private sealed class DownloadCoverageScanLibrary(string root) : IMarketDataLibrary
    {
        public string RootPath => root;
        public IReadOnlyList<HistoricalDatasetInfo> Datasets { get; set; } = [];
        public IReadOnlyList<MarketDataLibraryDiagnostic> Diagnostics { get; set; } = [];
        public MarketDataLibraryScan Scan() => new(Datasets, Diagnostics);
        public MarketDataLibraryScan ConsolidateDailyFiles() =>
            throw new InvalidOperationException("Opening queued coverage must not rewrite the library.");
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) =>
            throw new InvalidOperationException("Opening queued coverage must not save candles.");
        public HistoricalDataset Read(string datasetHash) =>
            throw new InvalidOperationException("Coverage uses scanned metadata.");
        public HistoricalDataQueryResult Query(HistoricalDataQuery query) =>
            throw new InvalidOperationException("Coverage uses scanned metadata.");
    }
}
