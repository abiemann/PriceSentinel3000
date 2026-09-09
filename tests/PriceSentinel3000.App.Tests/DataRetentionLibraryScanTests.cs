using System.IO;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task LibraryScan_LongDiagnosticsStayOutOfStatusAndAllNoticesRemainAuditable() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        string hash = new('a', 64);
        MarketDataLibraryDiagnostic[] notices = Enumerable.Range(1, 12).Select(index => new MarketDataLibraryDiagnostic(
            $"2026/09 - September/NFLX/2026-09-08.15s.{hash}.{index}.json", "revision_selection_required",
            $"Notice {index}: Conflicting saved revisions include {hash} and {new string('b', 64)}; inspect the original files.")).ToArray();
        var library = new ScanLibrary(fixture.LibraryRoot)
        {
            Result = new([ScanDataset("SOXL"), ScanDataset("NFLX")], notices),
        };
        await using DataRetentionViewModel vm = CreateScanViewModel(fixture, library);

        await vm.ScanLibraryAsync();

        Assert.Equal("Found 2 validated datasets. 12 scan notices. Open Library details.", vm.Status);
        Assert.DoesNotContain(hash, vm.Status);
        Assert.DoesNotContain("2026/", vm.Status);
        Assert.True(vm.HasLibraryDiagnostics);
        Assert.All(notices, notice =>
        {
            Assert.Contains(notice.RelativePath, vm.LibraryDiagnostics);
            Assert.Contains(notice.Code, vm.LibraryDiagnostics);
            Assert.Contains(notice.Message, vm.LibraryDiagnostics);
        });
        Assert.Equal(new[] { "NFLX", "SOXL" }, vm.Datasets.Select(dataset => dataset.Symbol));
        Assert.Equal(0, fixture.Provider.Calls);
    });

    [Fact]
    public Task LibraryScan_CleanRescanClearsOldNoticesAndNotifiesBothDetailsBindings() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        var library = new ScanLibrary(fixture.LibraryRoot)
        {
            Result = new([ScanDataset("NFLX")], [new("old.json", "invalid_file", "Previous scan notice.")]),
        };
        await using DataRetentionViewModel vm = CreateScanViewModel(fixture, library);
        await vm.ScanLibraryAsync();
        Assert.True(vm.HasLibraryDiagnostics);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        library.Result = new([], []);

        await vm.ScanLibraryAsync();

        Assert.Equal("", vm.LibraryDiagnostics);
        Assert.False(vm.HasLibraryDiagnostics);
        Assert.Empty(vm.Datasets);
        Assert.Equal("Found 0 validated datasets. 0 scan notices.", vm.Status);
        Assert.Contains(nameof(DataRetentionViewModel.LibraryDiagnostics), changed);
        Assert.Contains(nameof(DataRetentionViewModel.HasLibraryDiagnostics), changed);
        Assert.Equal(0, fixture.Provider.Calls);
    });

    [Fact]
    public Task LibraryScan_FailedRescanClearsStaleNoticesAndRetainsPreviouslyListedDatasets() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        HistoricalDatasetInfo original = ScanDataset("NFLX");
        var library = new ScanLibrary(fixture.LibraryRoot)
        {
            Result = new([original], [new("old.json", "invalid_file", "Previous scan notice.")]),
        };
        await using DataRetentionViewModel vm = CreateScanViewModel(fixture, library);
        await vm.ScanLibraryAsync();
        library.Failure = new IOException("Library scan failed.");

        await vm.ScanLibraryCommand.ExecuteAsync();

        Assert.Equal("", vm.LibraryDiagnostics);
        Assert.False(vm.HasLibraryDiagnostics);
        Assert.Equal("Library scan failed.", vm.Status);
        Assert.Same(original, Assert.Single(vm.Datasets));
        Assert.Equal(0, fixture.Provider.Calls);
    });

    private static DataRetentionViewModel CreateScanViewModel(RetentionFixture fixture, IMarketDataLibrary library) =>
        new(fixture.Collector, fixture.Provider, fixture.Provider, fixture.Provider, _ => library,
            _ => Task.CompletedTask, () => false, clock: fixture.Clock);

    private static HistoricalDatasetInfo ScanDataset(string symbol)
    {
        DateTimeOffset start = new(2026, 9, 8, 13, 30, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddSeconds(15);
        return new(new string(symbol == "NFLX" ? 'a' : 'b', 64), symbol + ".json", "test", symbol + "-id", symbol,
            new(2026, 9, 8), 15, "split", "robinhood-split-unversioned", "regular", end,
            new(start, end, start, end, 1, 1, true, true, []));
    }

    private sealed class ScanLibrary(string root) : IMarketDataLibrary
    {
        public string RootPath => root;
        public MarketDataLibraryScan Result { get; set; } = new([], []);
        public Exception? Failure { get; set; }
        public MarketDataLibraryScan Scan() => Failure is { } exception ? throw exception : Result;
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) => throw new NotSupportedException();
        public HistoricalDataset Read(string datasetHash) => throw new NotSupportedException();
        public HistoricalDataQueryResult Query(HistoricalDataQuery query) => throw new NotSupportedException();
    }
}
