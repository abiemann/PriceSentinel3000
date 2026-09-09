using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class CollectionAvailabilityTests
{
    [Fact]
    public async Task DiscoveryFindsOlderThanRetryHorizonAndStopsAtFirstUnavailableRegularDay()
    {
        using var fixture = new Fixture();
        fixture.Provider.Oldest["SOFI"] = new(2026, 8, 24);
        fixture.Provider.Empty.Add(("SOFI", new(2026, 8, 27)));
        await fixture.Collector.QueueAvailableAsync();
        CollectionJob initial = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(new DateOnly(2026, 9, 8), initial.SessionDate);
        Assert.Equal(initial.SessionDate, initial.DiscoveryAsOfDate);
        await fixture.Drain();

        Assert.Contains(fixture.Library.Scan().Datasets, d => d.TradingDate == new DateOnly(2026, 8, 28));
        Assert.DoesNotContain(fixture.Library.Scan().Datasets, d => d.TradingDate < new DateOnly(2026, 8, 28));
        Assert.Equal(new DateOnly(2026, 8, 27), fixture.Provider.Requests.Min(Date));
        Assert.All(fixture.Provider.Requests, r => Assert.Equal(15, r.SourceIntervalSeconds));
        Assert.All(fixture.Collector.State.Jobs, j => Assert.True(j.IsAvailabilityProbe));
        Assert.DoesNotContain(fixture.Collector.State.Jobs, j => j.DiscoveryEmptySessions is not null);
        int requests = fixture.Provider.Requests.Count;
        fixture.Restart();
        await fixture.Collector.TickAsync(true);
        Assert.Equal(requests, fixture.Provider.Requests.Count);
    }

    [Fact]
    public async Task EachIncludedEquityHasAnIndependentBoundaryAndDuplicateListsDoNotDuplicateWork()
    {
        using var fixture = new Fixture("SOFI", "NVDA");
        fixture.Provider.Oldest["SOFI"] = new(2026, 8, 31);
        fixture.Provider.Oldest["NVDA"] = new(2026, 8, 24);
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.Equal(new DateOnly(2026, 8, 28), fixture.Provider.Requests.Where(r => r.Symbol == "SOFI").Min(Date));
        Assert.Equal(new DateOnly(2026, 8, 21), fixture.Provider.Requests.Where(r => r.Symbol == "NVDA").Min(Date));
        Assert.Equal(fixture.Provider.Requests.Count, fixture.Provider.Requests.Select(r => (r.Symbol, r.FromUtc, r.ThroughUtc)).Distinct().Count());
        Assert.DoesNotContain(fixture.Provider.Requests, r => r.Symbol is "EXCLUDED" or "DISABLED");
    }

    [Fact]
    public async Task FailedFrontierSurvivesRestartAndNewDiscoveryWithoutEstablishingAnEmptyBoundary()
    {
        using var fixture = new Fixture();
        fixture.Provider.Oldest["SOFI"] = new(2026, 8, 24);
        fixture.Provider.FailingDate = new(2026, 8, 31);
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        CollectionJob failed = Assert.Single(fixture.Collector.State.Jobs, j => j.Status == CollectionJobStatus.Failed);
        Assert.NotNull(failed.DiscoveryEmptySessions);
        Assert.DoesNotContain(fixture.Provider.Requests, r => Date(r) < fixture.Provider.FailingDate);
        fixture.Restart();
        fixture.Provider.FailingDate = null;
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.Contains(fixture.Library.Scan().Datasets, d => d.TradingDate == new DateOnly(2026, 8, 24));
    }

    [Fact]
    public async Task CompleteFilesSkipNetworkAndStaleCompleteJobsAreCheckedAgainstDiskAgain()
    {
        using var fixture = new Fixture();
        fixture.Provider.Oldest["SOFI"] = new(2026, 9, 3);
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        DateOnly day = new(2026, 9, 4);
        HistoricalDatasetInfo saved = fixture.Library.Scan().Datasets.First(d => d.TradingDate == day);
        int initial = fixture.Provider.Requests.Count(r => Date(r) == day);
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.Equal(initial, fixture.Provider.Requests.Count(r => Date(r) == day));
        File.Delete(Path.Combine(fixture.Library.RootPath, saved.RelativePath));
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        // Deleting the canonical file removes the whole active day, rather than one request-sized section.
        Assert.Equal(initial * 2, fixture.Provider.Requests.Count(r => Date(r) == day));
        Assert.True(Assert.Single(fixture.Library.Scan().Datasets, d => d.TradingDate == day).Coverage.Complete);
    }

    [Theory]
    [InlineData("2026-09-09T04:00:00Z", "2026-09-08")]
    [InlineData("2026-09-09T04:00:14Z", "2026-09-08")]
    [InlineData("2026-09-09T04:00:15Z", "2026-09-09")]
    public async Task MidnightSeedUsesYesterdayUntilTodaysFirstCandleCompletes(string nowText, string expectedDateText)
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = DateTimeOffset.Parse(nowText);

        await fixture.Collector.QueueAvailableAsync();

        CollectionJob queued = Assert.Single(fixture.Collector.State.Jobs);
        DateOnly today = new(2026, 9, 9), expectedDate = DateOnly.Parse(expectedDateText);
        Assert.Equal(expectedDate, queued.SessionDate);
        Assert.Equal(today, queued.DiscoveryAsOfDate);
        Assert.Equal(expectedDate < today, queued.AvailabilityCheckPending);
        if (expectedDate < today) Assert.Null(queued.RequestedThroughUtc);
        else Assert.Equal(fixture.Clock.Now, queued.RequestedThroughUtc);
        Assert.Empty(fixture.Provider.Requests);
    }
    [Fact]
    public async Task CurrentSessionCollectsOnlyCompletedCandlesThenExtendsFromSavedCoverage()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-08T14:00:07Z");
        fixture.Provider.Oldest["SOFI"] = new(2026, 9, 3);
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        DateOnly day = new(2026, 9, 8);
        CollectionJob first = Assert.Single(fixture.Collector.State.Jobs, j => j.SessionDate == day);
        Assert.Equal(CollectionJobStatus.Complete, first.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T14:00:00Z"), first.RequestedThroughUtc);
        Assert.Contains(day, CollectionBackfillPlanner.Plan(new("SOFI"), fixture.Library.Scan().Datasets, day,
            "24_5", fixture.Clock.Now).MissingSessions);

        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-08T14:05:22Z");
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        HistoricalDataRequest extended = fixture.Provider.Requests.Last(r => Date(r) == day);
        Assert.Equal(first.RequestedThroughUtc, extended.FromUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T14:05:15Z"), extended.ThroughUtc);

        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-09T04:16:00Z");
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        CollectionJob finalized = Assert.Single(fixture.Collector.State.Jobs, j => j.SessionDate == day);
        Assert.Null(finalized.RequestedThroughUtc);
        Assert.Equal(CollectionJobStatus.Complete, finalized.Status);
        Assert.DoesNotContain(day, CollectionBackfillPlanner.Plan(new("SOFI"), fixture.Library.Scan().Datasets, day,
            "24_5", fixture.Clock.Now).MissingSessions);
    }

    [Fact]
    public async Task PendingDiscoveryRestartsWithoutDuplicatingCompletedDownloads()
    {
        using var fixture = new Fixture();
        fixture.Provider.Oldest["SOFI"] = new(2026, 8, 24);
        await fixture.Collector.QueueAvailableAsync();
        for (int i = 0; i < 4; i++) await fixture.Collector.TickAsync(true);
        Assert.Contains(fixture.Collector.State.Jobs, j => j.Status == CollectionJobStatus.Pending && j.DiscoveryEmptySessions is not null);
        fixture.Restart();
        await fixture.Drain();
        Assert.Equal(fixture.Provider.Requests.Count, fixture.Provider.Requests.Select(r => (r.Symbol, r.FromUtc, r.ThroughUtc)).Distinct().Count());
        Assert.Contains(fixture.Library.Scan().Datasets, d => d.TradingDate == new DateOnly(2026, 8, 24));
    }

    [Fact]
    public async Task CrashAfterPositiveProbeSaveBeforeCursorCommitPreservesAvailabilityEvidence()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-09T04:00:15Z");
        DateOnly boundaryDay = new(2026, 9, 8), olderDay = new(2026, 9, 4);
        fixture.Provider.Oldest["SOFI"] = olderDay;
        CollectionState? crashState = null;
        HistoricalCandle[] savedCandles = [];
        fixture.AfterSave = download =>
        {
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(download.RequestedFromUtc,
                TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime) != boundaryDay || crashState is not null) return;
            // Capture durable state after the real daily file was written, while the
            // collector has not returned from Save to commit its advanced cursor.
            crashState = fixture.Store.Load();
            savedCandles = download.Candles.ToArray();
        };
        await fixture.Collector.QueueAvailableAsync();
        for (int tick = 0; tick < 20 && crashState is null; tick++) await fixture.Collector.TickAsync(true);
        CollectionState interruptedState = Assert.IsType<CollectionState>(crashState);
        CollectionJob interrupted = Assert.Single(interruptedState.Jobs, job => job.SessionDate == boundaryDay);
        Assert.Equal(CollectionJobStatus.Downloading, interrupted.Status);
        Assert.Null(interrupted.NextGapFromUtc);
        Assert.True(interrupted.ReceivedCandlesThisRun);
        Assert.NotEmpty(savedCandles);

        fixture.AfterSave = null;
        fixture.Provider.Empty.Add(("SOFI", boundaryDay));
        fixture.Store.Save(interruptedState);
        fixture.Restart();
        await fixture.Drain();

        CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(boundaryDay, "24_5");
        HistoricalDataQueryResult retained = fixture.Library.Query(new("SOFI", window.FromUtc, window.ThroughUtc,
            SessionBounds: "24_5", RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage));
        Assert.Equal(savedCandles, retained.Candles);
        Assert.False(retained.Coverage.Complete);
        Assert.Single(fixture.Library.Scan().Datasets, dataset => dataset.TradingDate == boundaryDay);
        Assert.Contains(fixture.Library.Scan().Datasets, dataset => dataset.TradingDate == olderDay);
        Assert.Equal(CollectionJobStatus.Partial,
            Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == boundaryDay).Status);
    }
    [Fact]
    public async Task ExpectedEmptyProbesDoNotCreateHistoricalContinuityGapsOnScheduledRuns()
    {
        using var fixture = new Fixture();
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.Contains(fixture.Collector.State.Jobs, j => j.IsAvailabilityProbe && j.Status == CollectionJobStatus.Unavailable);
        await fixture.Collector.SaveSettingsAsync(fixture.Collector.State.Settings with { AutomaticDownloadsEnabled = true });
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        await fixture.Collector.TickAsync(false);
        Assert.Empty(fixture.Collector.State.ContinuityGaps);
    }

    [Fact]
    public async Task ScheduledRetryRestartsAtNewestDateBeforeReachingFailedDiscoveryFrontier()
    {
        using var fixture = new Fixture();
        fixture.Provider.Oldest["SOFI"] = new(2026, 9, 2);
        fixture.Provider.FailingDate = new(2026, 9, 2);
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        CollectionJob failed = Assert.Single(fixture.Collector.State.Jobs, j => j.Status == CollectionJobStatus.Failed);
        Assert.NotNull(failed.DiscoveryEmptySessions);
        await fixture.Collector.SaveSettingsAsync(fixture.Collector.State.Settings with
        {
            AutomaticDownloadsEnabled = true, TimeZoneId = "UTC", DailyDownloadTime = new(22, 0),
        });
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-08T22:00:01Z");
        await fixture.Collector.TickAsync(false);
        CollectionJob unchanged = Assert.Single(fixture.Collector.State.Jobs, j => j.Id == failed.Id);
        Assert.Equal(CollectionJobStatus.Failed, unchanged.Status);
        Assert.Equal(failed.LastAttemptAtUtc, unchanged.LastAttemptAtUtc);
        CollectionJob queued = Assert.Single(fixture.Collector.State.Jobs, j => j.Status == CollectionJobStatus.Pending);
        Assert.Equal(new DateOnly(2026, 9, 8), queued.SessionDate);
        Assert.Equal(queued.SessionDate, queued.DiscoveryAsOfDate);
        Assert.True(queued.IsAvailabilityProbe);

        fixture.Provider.FailingDate = null;
        await fixture.Drain();
        CollectionJob repaired = Assert.Single(fixture.Collector.State.Jobs, j => j.Id == failed.Id);
        Assert.Equal(CollectionJobStatus.Complete, repaired.Status);
        Assert.Null(repaired.DiscoveryEmptySessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadNowRepairsOldKnownGapsOnlyWhenDiscoveryReachesThem(bool discoveryReachesOldJobs)
    {
        using var fixture = new Fixture();
        DateOnly partialDay = new(2026, 8, 24), failedDay = new(2026, 8, 25), unavailableDay = new(2026, 8, 26);
        HistoricalDatasetInfo prefix = fixture.SavePrefix(partialDay, 5);
        CollectionJob[] oldJobs =
        [
            fixture.OldJob(partialDay, CollectionJobStatus.Partial) with { DatasetHashes = [prefix.DatasetHash], ActualSourceIntervalSeconds = 15 },
            fixture.OldJob(failedDay, CollectionJobStatus.Failed),
            fixture.OldJob(unavailableDay, CollectionJobStatus.Unavailable),
        ];
        fixture.Store.Save(fixture.Store.Load() with { Jobs = oldJobs });
        fixture.Restart();
        if (discoveryReachesOldJobs) fixture.Provider.Oldest["SOFI"] = partialDay;
        fixture.Provider.AvailableDays.UnionWith(oldJobs.Select(j => (j.Symbol, j.SessionDate)));

        await fixture.Collector.QueueAvailableAsync();
        Assert.All(oldJobs, old => Assert.Equal(old.Status,
            Assert.Single(fixture.Collector.State.Jobs, j => j.Id == old.Id).Status));
        await fixture.Drain();
        if (!discoveryReachesOldJobs)
        {
            Assert.All(oldJobs, old => Assert.Equal(JsonSerializer.Serialize(old),
                JsonSerializer.Serialize(Assert.Single(fixture.Collector.State.Jobs, j => j.Id == old.Id))));
            Assert.DoesNotContain(fixture.Provider.Requests, r => oldJobs.Any(j => j.SessionDate == Date(r)));
            Assert.Equal(prefix.DatasetHash, Assert.Single(fixture.Library.Scan().Datasets,
                d => d.TradingDate == partialDay).DatasetHash);
            return;
        }
        Assert.All(oldJobs, old => Assert.Equal(CollectionJobStatus.Complete,
            Assert.Single(fixture.Collector.State.Jobs, j => j.Id == old.Id).Status));
        Assert.All(oldJobs, old => Assert.Contains(fixture.Provider.Requests, r => Date(r) == old.SessionDate));
        HistoricalDataRequest repair = fixture.Provider.Requests.First(r => Date(r) == partialDay);
        CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(partialDay, "24_5");
        Assert.Equal(window.FromUtc.AddSeconds(75), repair.FromUtc);
        HistoricalDataQueryResult recovered = fixture.Library.Query(new("SOFI", window.FromUtc, window.ThroughUtc,
            SessionBounds: "24_5", RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage));
        Assert.True(recovered.Coverage.Complete);
        Assert.Equal(5760, recovered.Candles.Count);
        Assert.Equal(fixture.Library.Read(prefix.DatasetHash).Candles, recovered.Candles.Take(5));

        int oldDayRequests = fixture.Provider.Requests.Count(r => oldJobs.Any(j => j.SessionDate == Date(r)));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.All(oldJobs, old => Assert.Contains(fixture.Provider.Requests, r => Date(r) == old.SessionDate));
        Assert.Equal(oldDayRequests, fixture.Provider.Requests.Count(r => oldJobs.Any(j => j.SessionDate == Date(r))));
        Assert.All(fixture.Provider.Requests, r => Assert.Equal(15, r.SourceIntervalSeconds));
    }

    [Fact]
    public async Task ExplicitRetryOfDiscoveredJobsSurvivesStartingNewAvailabilityDiscovery()
    {
        using var fixture = new Fixture();
        CollectionJob[] history =
        [
            fixture.OldJob(new(2026, 8, 24), CollectionJobStatus.Partial) with { IsAvailabilityProbe = true },
            fixture.OldJob(new(2026, 8, 25), CollectionJobStatus.Unavailable) with { IsAvailabilityProbe = true },
        ];
        fixture.Store.Save(fixture.Store.Load() with { Jobs = history });
        fixture.Restart();
        await fixture.Collector.RetryMissingAsync(history.Select(job => job.Id).ToArray());
        CollectionJob[] retries = fixture.Collector.State.Jobs.ToArray();

        await fixture.Collector.QueueAvailableAsync();

        Assert.All(retries, retry =>
        {
            CollectionJob retained = Assert.Single(fixture.Collector.State.Jobs, job => job.Id == retry.Id);
            Assert.Equal(CollectionJobStatus.Pending, retained.Status);
            Assert.False(retained.IsAvailabilityProbe);
            Assert.Null(retained.DiscoveryAsOfDate);
            Assert.Equal(JsonSerializer.Serialize(retry), JsonSerializer.Serialize(retained));
        });
        Assert.Single(fixture.Collector.State.Jobs, job => job.DiscoveryAsOfDate is not null);
        Assert.Empty(fixture.Provider.Requests);
    }
    [Fact]
    public async Task DownloadNowDoesNotRetryOldFailuresOutsideSavedListFolderOrSessionScope()
    {
        using var fixture = new Fixture();
        DateOnly day = new(2026, 9, 4);
        CollectionJob[] unrelated =
        [
            fixture.OldJob(day, CollectionJobStatus.Failed) with { Symbol = "EXCLUDED" },
            fixture.OldJob(day, CollectionJobStatus.Failed) with { Symbol = "DISABLED" },
            fixture.OldJob(day, CollectionJobStatus.Failed) with { LibraryRootPath = fixture.Library.RootPath + "-other" },
            fixture.OldJob(day, CollectionJobStatus.Failed) with { SessionBounds = "unsupported" },
            fixture.OldJob(day, CollectionJobStatus.Failed) with { ProviderInstrumentId = "different-SOFI-id" },
        ];
        CollectionState saved = fixture.Store.Load();
        fixture.Store.Save(saved with
        {
            Jobs = unrelated,
            Settings = saved.Settings with
            {
                Lists = saved.Settings.Lists.Select(list => list with
                {
                    Members = list.Members.Select(member => member.Symbol == "SOFI"
                        ? member with { ProviderInstrumentId = "SOFI-id" } : member).ToArray(),
                }).ToArray(),
            },
        });
        fixture.Restart();
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.All(unrelated, old =>
        {
            CollectionJob unchanged = Assert.Single(fixture.Collector.State.Jobs, j => j.Id == old.Id);
            Assert.Equal(old.Status, unchanged.Status);
            Assert.Equal(old.LastAttemptAtUtc, unchanged.LastAttemptAtUtc);
            Assert.Equal(old.QueuedAtUtc, unchanged.QueuedAtUtc);
        });
        Assert.DoesNotContain(fixture.Provider.Requests, r => r.Symbol is "EXCLUDED" or "DISABLED");
        Assert.Contains(fixture.Provider.Requests, r => Date(r) == day && r.Symbol == "SOFI" && r.InstrumentId == "SOFI-id");
    }

    [Fact]
    public async Task DownloadNowRepairsAnImportedReachablePartialDayWithoutAnOperationalJob()
    {
        using var fixture = new Fixture();
        DateOnly day = new(2026, 9, 3);
        fixture.SavePrefix(day, 4);
        fixture.Provider.AvailableDays.Add(("SOFI", day));
        Assert.Empty(fixture.Collector.State.Jobs);
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        CollectionJob repaired = Assert.Single(fixture.Collector.State.Jobs, j => j.SessionDate == day);
        Assert.Equal(CollectionJobStatus.Complete, repaired.Status);
        HistoricalDataRequest request = fixture.Provider.Requests.First(r => Date(r) == day);
        Assert.Equal(CollectionSchedule.GetSessionWindow(day, "24_5").FromUtc.AddSeconds(60), request.FromUtc);
    }

    [Fact]
    public async Task ImportedPartialRepairDoesNotReuseAnOldJobForADifferentKnownInstrument()
    {
        using var fixture = new Fixture();
        DateOnly day = new(2026, 9, 3);
        fixture.SavePrefix(day, 4);
        fixture.Provider.AvailableDays.Add(("SOFI", day));
        CollectionJob differentInstrument = fixture.OldJob(day, CollectionJobStatus.Failed) with
        {
            ProviderInstrumentId = "different-SOFI-id",
        };
        CollectionState saved = fixture.Store.Load();
        fixture.Store.Save(saved with
        {
            Jobs = [differentInstrument],
            Settings = saved.Settings with
            {
                Lists = saved.Settings.Lists.Select(list => list with
                {
                    Members = list.Members.Select(member => member.Symbol == "SOFI"
                        ? member with { ProviderInstrumentId = "SOFI-id" } : member).ToArray(),
                }).ToArray(),
            },
        });
        fixture.Restart();

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        CollectionJob preserved = Assert.Single(fixture.Collector.State.Jobs, j => j.Id == differentInstrument.Id);
        Assert.Equal(CollectionJobStatus.Failed, preserved.Status);
        Assert.Equal(differentInstrument.ProviderInstrumentId, preserved.ProviderInstrumentId);
        Assert.Equal(differentInstrument.LastAttemptAtUtc, preserved.LastAttemptAtUtc);
        CollectionJob repaired = Assert.Single(fixture.Collector.State.Jobs,
            j => j.SessionDate == day && j.ProviderInstrumentId == "SOFI-id");
        Assert.NotEqual(differentInstrument.Id, repaired.Id);
        Assert.Equal(CollectionJobStatus.Complete, repaired.Status);
        HistoricalDataRequest request = fixture.Provider.Requests.First(r => Date(r) == day);
        Assert.Equal("SOFI-id", request.InstrumentId);
        Assert.Equal(CollectionSchedule.GetSessionWindow(day, "24_5").FromUtc.AddSeconds(60), request.FromUtc);
    }

    private static DateOnly Date(HistoricalDataRequest request) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.FromUtc, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);

    private sealed class Fixture : IDisposable
    {
        public Fixture(params string[] symbols)
        {
            if (symbols.Length == 0) symbols = ["SOFI"];
            Library = new(Path.Combine(Path.GetTempPath(), "PriceSentinel-availability-" + Guid.NewGuid().ToString("N")));
            Store.Save(new() { Settings = new()
            {
                LibraryRootPath = Library.RootPath,
                Lists = [new(Guid.NewGuid(), "Included", true,
                    [.. symbols.Select(s => new DownloadListMember(s)), new("EXCLUDED", IsIncluded: false)]),
                    new(Guid.NewGuid(), "Duplicate", true, [new(symbols[0])]),
                    new(Guid.NewGuid(), "Disabled", false, [new("DISABLED")])],
            } });
            Provider = new(Clock);
            Restart();
        }
        public Clock Clock { get; } = new();
        public MemoryStore Store { get; } = new();
        public Provider Provider { get; }
        public JsonMarketDataLibrary Library { get; }
        public Action<HistoricalDownload>? AfterSave { get; set; }
        public MarketDataCollector Collector { get; private set; } = null!;
        public CollectionJob OldJob(DateOnly day, CollectionJobStatus status) => new()
        {
            Symbol = "SOFI", SessionDate = day, Status = status, LibraryRootPath = Library.RootPath, SessionBounds = "24_5",
            QueuedAtUtc = Clock.Now.AddDays(-1), LastAttemptAtUtc = Clock.Now.AddDays(-1),
            Error = "Previous download needs attention.",
        };
        public HistoricalDatasetInfo SavePrefix(DateOnly day, int candleCount)
        {
            CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(day, "24_5");
            return Assert.Single(Library.Save(new("test", "SOFI-id", "SOFI", 15, "split", "robinhood-split-unversioned",
                "24_5", Clock.Now.AddDays(-1), window.FromUtc, window.ThroughUtc,
                Enumerable.Range(0, candleCount).Select(index =>
                {
                    DateTimeOffset at = window.FromUtc.AddSeconds(index * 15);
                    return new HistoricalCandle(at, at.AddSeconds(15), at.AddSeconds(15), 10, 11, 9, 10, 100);
                }).ToArray())));
        }
        public void Restart() => Collector = new(Store, Provider, _ => new ObservedLibrary(Library, download => AfterSave?.Invoke(download)), Clock, new()
        {
            MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero, MaximumRequestsPerTick = 2,
        });
        public async Task Drain()
        {
            for (int i = 0; i < 300 && Collector.State.Jobs.Any(j => j.Status == CollectionJobStatus.Pending); i++)
                await Collector.TickAsync(true);
            Assert.DoesNotContain(Collector.State.Jobs, j => j.Status == CollectionJobStatus.Pending);
        }
        public void Dispose()
        {
            if (Directory.Exists(Library.RootPath)) Directory.Delete(Library.RootPath, recursive: true);
        }
    }

    private sealed class ObservedLibrary(JsonMarketDataLibrary inner, Action<HistoricalDownload> afterSave) : IMarketDataLibrary
    {
        public string RootPath => inner.RootPath;
        public MarketDataLibraryScan Scan() => inner.Scan();
        public HistoricalDataset Read(string hash) => inner.Read(hash);
        public HistoricalDataQueryResult Query(HistoricalDataQuery query) => inner.Query(query);
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download)
        {
            IReadOnlyList<HistoricalDatasetInfo> saved = inner.Save(download);
            afterSave(download);
            return saved;
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-08T21:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class MemoryStore : ICollectionStateStore
    {
        private string _json = JsonSerializer.Serialize(new CollectionState());
        public CollectionState Load() => JsonSerializer.Deserialize<CollectionState>(_json)!;
        public void Save(CollectionState state) => _json = JsonSerializer.Serialize(state);
    }
    private sealed class Provider(Clock clock) : IMarketHistoryProvider
    {
        public List<HistoricalDataRequest> Requests { get; } = [];
        public Dictionary<string, DateOnly> Oldest { get; } = [];
        public HashSet<(string Symbol, DateOnly Day)> Empty { get; } = [];
        public HashSet<(string Symbol, DateOnly Day)> AvailableDays { get; } = [];
        public DateOnly? FailingDate { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Date(request) == FailingDate) throw new HttpRequestException("Temporary failure.");
            var candles = new List<HistoricalCandle>();
            if ((Date(request) >= Oldest.GetValueOrDefault(request.Symbol, new(2026, 9, 3)) ||
                AvailableDays.Contains((request.Symbol, Date(request)))) && !Empty.Contains((request.Symbol, Date(request))))
                for (DateTimeOffset at = request.FromUtc; at < request.ThroughUtc; at = at.AddSeconds(15))
                    candles.Add(new(at, at.AddSeconds(15), at.AddSeconds(15), 10, 11, 9, 10, 100));
            return Task.FromResult(new HistoricalDownload("test", request.Symbol + "-id", request.Symbol, 15,
                request.AdjustmentPolicy, "robinhood-split-unversioned", request.SessionBounds, clock.Now,
                request.FromUtc, request.ThroughUtc, candles));
        }
    }
}
