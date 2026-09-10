using System.IO;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    private static readonly DateOnly CoverageLoadDay = new(2026, 9, 9);

    [Theory]
    [InlineData(true, 24)]
    [InlineData(false, 16)]
    public Task LibraryCoverageLoad_ProviderEligibilityControlsEasternRangeWithoutDownloading(
        bool overnight, int hours) => host.RunAsync(async () =>
    {
        await using var fixture = new CoverageLoadFixture();
        fixture.Provider.Eligible = overnight;

        LibraryCoverageTimeline result = await fixture.ViewModel.LoadLibraryCoverageAsync(
            CoverageLoadRow(), CancellationToken.None);

        Assert.Equal(hours, (result.ThroughUtc - result.FromUtc).TotalHours);
        DateTimeOffset expectedFrom = new(2026, 9, 9, overnight ? 4 : 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedFrom, result.FromUtc);
        Assert.Equal(TimeZoneInfo.ConvertTime(expectedFrom, TimeZoneInfo.Local).ToString("HH:mm"), result.Ticks[0].Label);
        Assert.Equal(CoverageLoadDay, result.Date);
        Assert.Equal(1, fixture.Provider.EligibilityCalls);
        Assert.Equal("AAPL", fixture.Provider.RequestedSymbol);
        Assert.Null(result.Notice);
        fixture.AssertNoDataOrAuthenticationCalls();
    });

    [Fact]
    public Task LibraryCoverageLoad_ClipsAdjacentMetadataToTheSelectedEasternDay() => host.RunAsync(async () =>
    {
        await using var fixture = new CoverageLoadFixture();
        fixture.Provider.Eligible = true;
        TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        DateTimeOffset first = CollectionSchedule.ResolveDailyOccurrence(
            CoverageLoadDay.AddDays(-1), TimeOnly.MinValue, eastern.Id);
        for (int index = 0; index < 3; index++)
        {
            DateTimeOffset from = first.AddDays(index), through = first.AddDays(index + 1);
            fixture.ViewModel.Datasets.Add(new($"hash-{index}", $"day-{index}.json", "Robinhood", "id-AAPL", "AAPL",
                CoverageLoadDay.AddDays(index - 1), 15, "split", "robinhood-split-unversioned", "24_5",
                through.AddHours(1), new(from, through, from, through, 5760, 5760, true, true, [])));
        }
        // A different stock must not participate in coverage or compatibility checks.
        fixture.ViewModel.Datasets.Add(fixture.ViewModel.Datasets[1] with
        {
            Symbol = "MSFT", InstrumentId = "id-MSFT", Provider = "another broker",
        });

        LibraryCoverageTimeline result = await fixture.ViewModel.LoadLibraryCoverageAsync(
            CoverageLoadRow(), CancellationToken.None);

        DateTimeOffset expectedFrom = CollectionSchedule.ResolveDailyOccurrence(
            CoverageLoadDay, TimeOnly.MinValue, eastern.Id);
        Assert.Equal(expectedFrom, result.FromUtc);
        Assert.Equal(expectedFrom.AddDays(1), result.ThroughUtc);
        Assert.Equal(5760, result.Blocks.Sum(block => block.SavedCandleCount));
        Assert.All(result.Blocks, block => Assert.Equal(LibraryCoverageBlockState.Complete, block.State));
        fixture.AssertNoDataOrAuthenticationCalls();
    });

    [Fact]
    public Task LibraryCoverageLoad_EligibilityFailureFallsBackToUnknownFullDay() => host.RunAsync(async () =>
    {
        await using var fixture = new CoverageLoadFixture();
        fixture.Provider.EligibilityError = new IOException("The connection dropped.");

        LibraryCoverageTimeline result = await fixture.ViewModel.LoadLibraryCoverageAsync(
            CoverageLoadRow(), CancellationToken.None);

        Assert.Equal(24, (result.ThroughUtc - result.FromUtc).TotalHours);
        Assert.Contains("eligibility is unknown", result.Notice);
        Assert.Equal(1, fixture.Provider.EligibilityCalls);
        fixture.AssertNoDataOrAuthenticationCalls();
    });

    [Fact]
    public Task LibraryCoverageLoad_DisconnectedDoesNotReconnectOrQueryEligibility() => host.RunAsync(async () =>
    {
        await using var fixture = new CoverageLoadFixture { Connected = false };
        fixture.Provider.Eligible = false;

        LibraryCoverageTimeline result = await fixture.ViewModel.LoadLibraryCoverageAsync(
            CoverageLoadRow(), CancellationToken.None);

        Assert.Equal(24, (result.ThroughUtc - result.FromUtc).TotalHours);
        Assert.Contains("eligibility is unknown", result.Notice);
        Assert.Equal(0, fixture.Provider.EligibilityCalls);
        fixture.AssertNoDataOrAuthenticationCalls();
    });

    [Fact]
    public Task LibraryCoverageLoad_ExplicitCancellationPropagatesWhileEligibilityIsPending() => host.RunAsync(async () =>
    {
        await using var fixture = new CoverageLoadFixture();
        fixture.Provider.HoldEligibility = true;
        using var cancellation = new CancellationTokenSource();
        Task<LibraryCoverageTimeline> loading = fixture.ViewModel.LoadLibraryCoverageAsync(
            CoverageLoadRow(), cancellation.Token);
        await fixture.Provider.EligibilityStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loading);
        Assert.True(fixture.Provider.ReceivedToken.IsCancellationRequested);
        fixture.AssertNoDataOrAuthenticationCalls();
    });

    [Fact]
    public Task LibraryCoverageLoad_DisposalCancelsPendingEligibility() => host.RunAsync(async () =>
    {
        await using var fixture = new CoverageLoadFixture();
        fixture.Provider.HoldEligibility = true;
        Task<LibraryCoverageTimeline> loading = fixture.ViewModel.LoadLibraryCoverageAsync(
            CoverageLoadRow(), CancellationToken.None);
        await fixture.Provider.EligibilityStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.ViewModel.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loading);
        Assert.True(fixture.Provider.ReceivedToken.IsCancellationRequested);
        fixture.AssertNoDataOrAuthenticationCalls();
    });

    private static LibraryDaySummary CoverageLoadRow() =>
        new("AAPL", CoverageLoadDay, "15", "Robinhood", 0, 0, "");

    private sealed class CoverageLoadFixture : IAsyncDisposable
    {
        public CoverageLoadProvider Provider { get; } = new();
        public DataRetentionViewModel ViewModel { get; }
        public bool Connected { get; set; } = true;
        private int _libraryCalls;
        private int _connectionCalls;

        public CoverageLoadFixture()
        {
            var clock = new TestClock { Now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero) };
            var store = new MemoryCollectionStore(Path.Combine(Path.GetTempPath(), "pricesentinel-coverage-unused"));
            IMarketDataLibrary UnexpectedLibrary(string root)
            {
                _libraryCalls++;
                throw new InvalidOperationException("Coverage should use the inventory, without scanning disk.");
            }
            Task UnexpectedConnection(CancellationToken token)
            {
                _connectionCalls++;
                throw new InvalidOperationException("Coverage must not request authentication.");
            }
            var collector = new MarketDataCollector(store, Provider, UnexpectedLibrary, clock);
            ViewModel = new(collector, Provider, Provider, Provider, UnexpectedLibrary,
                UnexpectedConnection, () => Connected, reconnect: UnexpectedConnection, clock: clock);
        }

        public void AssertNoDataOrAuthenticationCalls()
        {
            Assert.Equal(0, _libraryCalls);
            Assert.Equal(0, _connectionCalls);
            Assert.Equal(0, Provider.HistoryCalls);
            Assert.Equal(0, Provider.UnrelatedCalls);
        }

        public ValueTask DisposeAsync() => ViewModel.DisposeAsync();
    }

    private sealed class CoverageLoadProvider :
        IMarketHistoryProvider, IPersonalWatchlistSource, IEquityCatalogSource, IEquityMarketHoursSource
    {
        public bool Eligible { get; set; }
        public bool HoldEligibility { get; set; }
        public Exception? EligibilityError { get; set; }
        public int EligibilityCalls { get; private set; }
        public int HistoryCalls { get; private set; }
        public int UnrelatedCalls { get; private set; }
        public string? RequestedSymbol { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }
        public TaskCompletionSource EligibilityStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> IsTwentyFourHourEligibleAsync(string symbol, CancellationToken cancellationToken)
        {
            EligibilityCalls++;
            RequestedSymbol = symbol;
            ReceivedToken = cancellationToken;
            EligibilityStarted.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (HoldEligibility) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (EligibilityError is not null) throw EligibilityError;
            return Eligible;
        }

        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            HistoryCalls++;
            throw new InvalidOperationException("Opening coverage must never download candles.");
        }

        public Task<IReadOnlyList<PersonalWatchlist>> GetWatchlistsAsync(CancellationToken cancellationToken)
        {
            UnrelatedCalls++;
            throw new InvalidOperationException("Opening coverage must not load personal watchlists.");
        }

        public Task<PersonalWatchlistMembers> GetWatchlistMembersAsync(
            PersonalWatchlist watchlist, CancellationToken cancellationToken)
        {
            UnrelatedCalls++;
            throw new InvalidOperationException("Opening coverage must not load personal watchlist members.");
        }

        public Task<IReadOnlyList<EquityResolution>> ResolveEquitiesAsync(
            IReadOnlyList<string> symbols, CancellationToken cancellationToken)
        {
            UnrelatedCalls++;
            throw new InvalidOperationException("Opening coverage must not resolve equities.");
        }
    }
}
