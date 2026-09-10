using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class ReplayHistoryAvailabilityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PriceSentinel-availability-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 13, 30, 0, TimeSpan.Zero);
    private readonly Provider _provider = new();
    private JsonMarketDataLibrary Library => new(_root);
    private HistoricalDataQuery Query => new("MSFT", Start, Start.AddMinutes(2), AdjustmentPolicy: "split", SessionBounds: "regular");
    private ReplayHistoryAvailabilityService Service => new(Library, _provider);

    [Fact]
    public async Task CompleteLocalFine_IsReadyWithoutNetworkOrArchiveChanges()
    {
        string hash = Assert.Single(Library.Save(Download(15))).DatasetHash;
        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.True(result.Complete);
        Assert.True(result.IsLocal);
        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.Equal(hash, Assert.Single(result.Datasets).DatasetHash);
        Assert.Null(result.PendingDownload);
        Assert.Empty(_provider.Requests);
        Assert.Equal(hash, Assert.Single(Library.Scan().Datasets).DatasetHash);
    }

    [Fact]
    public async Task QueryWindow_NotDailyFileLabel_DeterminesCompleteCoverage()
    {
        Library.Save(Download(15) with { Candles = Download(15).Candles.Take(4).ToArray() });
        ReplayHistoryAvailability complete = await Service.CheckAsync(Query with { ThroughUtc = Start.AddMinutes(1) }, true, default);
        ReplayHistoryAvailability partial = await Service.CheckAsync(Query, true, default);

        Assert.True(complete.Complete);
        Assert.False(partial.Complete);
        Assert.True(partial.HasData);
        Assert.Equal(4, partial.Candles.Count);
        Assert.Equal(8, partial.Coverage.ExpectedCandleCount);
        Assert.Contains(partial.Diagnostics, item => item.Code == "partial_replay_history");
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task ProviderFine_ComplementsIncompleteLocalForPreparedChoice_WithoutWriting()
    {
        string partialHash = Assert.Single(Library.Save(Download(15) with { Candles = [Download(15).Candles[1]] })).DatasetHash;
        _provider.Downloads[15] = Download(15);

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.True(result.Complete);
        Assert.False(result.IsLocal);
        Assert.Equal("local-and-provider", result.Source);
        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.NotNull(result.PendingDownload);
        Assert.Equal(partialHash, Assert.Single(Library.Scan().Datasets).DatasetHash);
        Assert.Equal(new[] { 15 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
    }

    [Fact]
    public async Task ProviderCheck_KeepsDataInMemoryUntilStart_AndStartNeverDownloadsAgain()
    {
        _provider.Downloads[15] = Download(15) with
        {
            Candles = Download(15).Candles.Select(item => item with { Volume = null, Close = 100.123456789m }).ToArray(),
        };
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        Assert.False(Directory.Exists(_root));
        _provider.Error = new HttpRequestException("Connection disappeared after checking");

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal("provider-saved-library", loaded.Source);
        Assert.Single(_provider.Requests);
        Assert.Equal(prepared.Candles, loaded.Candles);
        string hash = Assert.Single(loaded.Datasets).DatasetHash;
        Assert.Equal(prepared.Candles, Library.Read(hash).Candles);
        Assert.All(loaded.Candles, item => Assert.Null(item.Volume));
        Assert.Contains(loaded.Diagnostics, item => item.Code == "unknown_volume");
    }

    [Fact]
    public async Task LocalStart_PinsCheckedSnapshot_EvenWhenDailyFileGainsCandles()
    {
        HistoricalCandle[] prefix = Download(15).Candles.Take(4).ToArray();
        string checkedHash = Assert.Single(Library.Save(Download(15) with { Candles = prefix })).DatasetHash;
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, true, default);
        Library.Save(Download(15));

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal(checkedHash, Assert.Single(loaded.Datasets).DatasetHash);
        Assert.Equal(prefix, loaded.Candles);
        Assert.DoesNotContain(Library.Scan().Datasets, item => item.DatasetHash == checkedHash);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task ProviderCollectionChanges_DoNotChangePreparedCandles()
    {
        HistoricalCandle[] mutable = Download(15).Candles.ToArray();
        _provider.Downloads[15] = Download(15) with { Candles = mutable };
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        mutable[0] = mutable[0] with { Close = 100.5m };

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal(100m, loaded.Candles[0].Close);
        Assert.Single(_provider.Requests);
    }

    [Fact]
    public async Task RemovedPreparedFile_FailsBeforeStartWithoutSubstitution()
    {
        Library.Save(Download(15));
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        File.Delete(Path.Combine(_root, Assert.Single(prepared.Datasets).RelativePath));
        _provider.Downloads[15] = Download(15);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.LoadPreparedAsync(prepared, default));
        Assert.Empty(_provider.Requests);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public async Task CompleteLocalCoarse_IsPreferredToIncompleteFineDuringExplicitPreflight(int interval)
    {
        Library.Save(Download(15) with { Candles = [Download(15).Candles[0]] });
        Library.Save(Download(interval));

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, true, default);

        Assert.True(result.Complete);
        Assert.True(result.IsLocal);
        Assert.Equal(interval, result.SourceIntervalSeconds);
        Assert.Empty(_provider.Requests);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public async Task BrokerFallback_ProbesGenuineIntervalsInOrderAndPreservesActualResolution(int interval)
    {
        _provider.Downloads[15] = Download(15) with { Candles = [Download(15).Candles[0]] };
        _provider.Downloads[interval] = Download(interval);

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.True(result.Complete);
        Assert.False(result.IsLocal);
        Assert.Equal(interval, result.SourceIntervalSeconds);
        Assert.Equal(new[] { 15, 30, 60 }.Where(item => item <= interval), _provider.Requests.Select(item => item.SourceIntervalSeconds));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CompleteLocalTwoMinute_PreventsBrokerRequests()
    {
        Library.Save(Download(120));

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.True(result.Complete);
        Assert.Equal(120, result.SourceIntervalSeconds);
        Assert.True(result.IsLocal);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task NoCompleteSource_ReturnsBestCoveredPartialAndItsExactGaps()
    {
        Library.Save(Download(15) with { Candles = [Download(15).Candles[0]] });
        _provider.Downloads[15] = Download(15) with { Candles = Download(15).Candles.Take(3).ToArray() };
        _provider.Downloads[30] = Download(30) with { Candles = Download(30).Candles.Take(3).ToArray() };

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.False(result.Complete);
        Assert.True(result.HasData);
        Assert.Equal(30, result.SourceIntervalSeconds);
        Assert.Equal(3, result.Candles.Count);
        Assert.Equal(new HistoricalGap(Start.AddSeconds(90), Start.AddMinutes(2)), Assert.Single(result.Coverage.Gaps));
        Assert.Contains(result.Diagnostics, item => item.Code == "partial_replay_history");
    }

    [Fact]
    public async Task NoData_IsDistinctFromPartialAndDoesNotCreateArchiveFiles()
    {
        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);
        Assert.False(result.HasData);
        Assert.False(result.Complete);
        Assert.Single(result.Coverage.Gaps);
        Assert.False(Directory.Exists(_root));
        Assert.Empty((await Service.LoadPreparedAsync(result, default)).Candles);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ExplicitCoarsePins_DoNotProbeFineBrokerOrSelectFineLocal()
    {
        string pinned = Assert.Single(Library.Save(Download(60))).DatasetHash;
        Library.Save(Download(15));

        ReplayHistoryAvailability result = await Service.CheckAsync(Query with { PinnedHashes = [pinned] }, false, default);

        Assert.Equal("pinned-library", result.Source);
        Assert.Equal(60, result.SourceIntervalSeconds);
        Assert.Equal(pinned, Assert.Single(result.Datasets).DatasetHash);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task PinnedPartialFine_RemainsPartialEvenWhenBrokerHasCompleteData()
    {
        string pinned = Assert.Single(Library.Save(Download(15) with { Candles = [Download(15).Candles[1]] })).DatasetHash;
        _provider.Downloads[15] = Download(15);

        ReplayHistoryAvailability result = await Service.CheckAsync(Query with { PinnedHashes = [pinned] }, false, default);

        Assert.True(result.HasData);
        Assert.False(result.Complete);
        Assert.Equal(pinned, Assert.Single(result.Datasets).DatasetHash);
        Assert.Contains(result.Diagnostics, item => item.Code == "partial_replay_history");
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task ConflictingRevisions_RequireExplicitPolicyInsteadOfBrokerSubstitution()
    {
        Library.Save(Download(30));
        string newer = Assert.Single(Library.Save(Download(30) with
        {
            FetchedAtUtc = Start.AddDays(2),
            Candles = Download(30).Candles.Select(item => item with { Close = 100.5m }).ToArray(),
        })).DatasetHash;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CheckAsync(Query, false, default));
        ReplayHistoryAvailability result = await Service.CheckAsync(Query with { RevisionPolicy = HistoricalRevisionPolicy.LatestFetched }, false, default);

        Assert.Equal(newer, Assert.Single(result.Datasets).DatasetHash);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task BrokerFailure_IsUnknown_NotProofThatOnlyCoarseHistoryExists()
    {
        Library.Save(Download(60) with { Candles = [Download(60).Candles[0]] });
        _provider.Error = new MarketDataConnectionUnavailableException("Not connected");
        await Assert.ThrowsAsync<MarketDataConnectionUnavailableException>(() => Service.CheckAsync(Query, false, default));
        Assert.Equal(new[] { 15 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("interval")]
    [InlineData("range")]
    [InlineData("bounds")]
    [InlineData("adjustment")]
    [InlineData("provider")]
    public async Task WrongBrokerProvenance_CannotProduceAvailability(string field)
    {
        HistoricalDownload wrong = field switch
        {
            "symbol" => Download(15) with { Symbol = "SOXL" },
            "interval" => Download(30),
            "range" => Download(15) with { RequestedFromUtc = Start.AddMinutes(-1) },
            "bounds" => Download(15) with { SessionBounds = "extended" },
            "adjustment" => Download(15) with { AdjustmentPolicy = "none" },
            _ => Download(15) with { Provider = "other-provider" },
        };
        _provider.Downloads[15] = wrong;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CheckAsync(Query with { Provider = "Robinhood" }, false, default));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("price")]
    [InlineData("unfinalized")]
    [InlineData("future")]
    public async Task InvalidBrokerCandles_CannotTurnDateGreen(string kind)
    {
        HistoricalCandle[] candles = Download(15).Candles.ToArray();
        candles[1] = kind switch
        {
            "duplicate" => candles[0],
            "price" => candles[1] with { Close = -1m },
            "unfinalized" => candles[1] with { AvailableAtUtc = candles[1].EndsAtUtc.AddSeconds(1) },
            _ => candles[1] with { EndsAtUtc = Start.AddDays(2) },
        };
        _provider.Downloads[15] = Download(15) with { Candles = candles };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.CheckAsync(Query, false, default));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CancelledCheckAndStart_DoNotRequestOrWrite()
    {
        _provider.Downloads[15] = Download(15);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.CheckAsync(Query, false, cancellation.Token));
        Assert.Empty(_provider.Requests);
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.LoadPreparedAsync(prepared, cancellation.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task FineLocalPrefix_RequestsOnlyMissingTail_AndPreparedStartIsReproducible()
    {
        HistoricalCandle[] prefix = Download(15).Candles.Take(4).ToArray();
        string localHash = Assert.Single(Library.Save(Download(15) with { Candles = prefix })).DatasetHash;
        _provider.Downloads[15] = Download(15);

        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);

        HistoricalDataRequest request = Assert.Single(_provider.Requests);
        Assert.Equal(Start.AddMinutes(1), request.FromUtc);
        Assert.Equal(Start.AddMinutes(2), request.ThroughUtc);
        Assert.Equal(15, request.SourceIntervalSeconds);
        Assert.Equal("local-and-provider", prepared.Source);
        Assert.True(prepared.Complete);
        Assert.Equal(Download(15).Candles, prepared.Candles);
        Assert.Equal(localHash, Assert.Single(Library.Scan().Datasets).DatasetHash);

        LibraryReplayHistoryResult first = await Service.LoadPreparedAsync(prepared, default);
        LibraryReplayHistoryResult second = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal("local-and-provider-saved-library", first.Source);
        Assert.Equal(prepared.Candles, first.Candles);
        Assert.Equal(first.Candles, second.Candles);
        Assert.Equal(first.Datasets.Select(item => item.DatasetHash), second.Datasets.Select(item => item.DatasetHash));
        Assert.Equal(prefix, Library.Read(localHash).Candles);
        Assert.Single(_provider.Requests);

        ReplayHistoryAvailability retained = await Service.CheckAsync(Query with
        {
            RevisionPolicy = HistoricalRevisionPolicy.CompatibleCoverage,
        }, false, default);

        Assert.True(retained.IsLocal);
        Assert.True(retained.Complete);
        Assert.Equal(prepared.Candles, retained.Candles);
        Assert.Single(_provider.Requests);
        Assert.Equal(retained.Candles, (await Service.LoadPreparedAsync(retained, default)).Candles);
    }

    [Fact]
    public async Task FineLocalPrefix_AndMinuteBrokerTail_AggregateWithoutChangingNativeFiles()
    {
        HistoricalCandle[] prefix = Download(15).Candles.Take(4).Select((candle, index) => candle with
        {
            Open = 100m + index, High = 105m + index, Low = 90m - index, Close = 101m + index,
            Volume = index == 1 ? null : 1000m,
        }).ToArray();
        string localHash = Assert.Single(Library.Save(Download(15) with { Candles = prefix })).DatasetHash;
        _provider.Downloads[60] = Download(60);

        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);

        Assert.True(prepared.Complete);
        Assert.Equal(60, prepared.SourceIntervalSeconds);
        Assert.Equal(new[] { 15, 60 }, prepared.NativeSourceIntervals);
        Assert.Equal(new[] { 15, 30, 60 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
        Assert.All(_provider.Requests, request =>
        {
            Assert.Equal(Start.AddMinutes(1), request.FromUtc);
            Assert.Equal(Start.AddMinutes(2), request.ThroughUtc);
        });
        Assert.Equal(new HistoricalCandle(Start, Start.AddMinutes(1), Start.AddMinutes(1),
            100m, 108m, 87m, 104m, null), prepared.Candles[0]);
        Assert.Equal(Download(60).Candles[1], prepared.Candles[1]);
        Assert.Contains(prepared.Diagnostics, item => item.Code == "composed_replay_history");
        Assert.Equal(localHash, Assert.Single(Library.Scan().Datasets).DatasetHash);

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal(prepared.Candles, loaded.Candles);
        Assert.Equal(2, loaded.Datasets.Count);
        Assert.Contains(loaded.Datasets, item => item.DatasetHash == localHash && item.SourceIntervalSeconds == 15);
        HistoricalDatasetInfo brokerFile = Assert.Single(loaded.Datasets, item => item.SourceIntervalSeconds == 60);
        Assert.Equal(new[] { Download(60).Candles[1] }, Library.Read(brokerFile.DatasetHash).Candles);
        Assert.Equal(prefix, Library.Read(localHash).Candles);
        Assert.Equal(3, _provider.Requests.Count);
    }

    [Fact]
    public async Task CoarseBrokerCandle_ReplacesWholeOverlappingBucket_WithoutSlicingOrLookahead()
    {
        HistoricalCandle[] fine = Download(15).Candles.Where((_, index) => index != 5).ToArray();
        string localHash = Assert.Single(Library.Save(Download(15) with { Candles = fine })).DatasetHash;
        HistoricalCandle coarse = Download(60).Candles[1] with
        {
            Open = 200m, High = 220m, Low = 190m, Close = 210m, Volume = 999m,
        };
        _provider.Downloads[60] = Download(60) with { Candles = [coarse] };

        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);

        Assert.True(prepared.Complete);
        Assert.Equal(60, prepared.SourceIntervalSeconds);
        Assert.Equal(new[] { Start.AddSeconds(75), Start.AddSeconds(60), Start.AddMinutes(1) },
            _provider.Requests.Select(item => item.FromUtc));
        Assert.Equal(new[] { Start.AddSeconds(90), Start.AddSeconds(90), Start.AddMinutes(2) },
            _provider.Requests.Select(item => item.ThroughUtc));
        Assert.Equal(2, prepared.Candles.Count);
        Assert.Equal(coarse, prepared.Candles[1]);
        Assert.All(prepared.Candles, candle =>
        {
            Assert.Equal(TimeSpan.FromMinutes(1), candle.EndsAtUtc - candle.StartsAtUtc);
            Assert.Equal(candle.EndsAtUtc, candle.AvailableAtUtc);
        });

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);
        Assert.Equal(prepared.Candles, loaded.Candles);
        Assert.Equal(fine, Library.Read(localHash).Candles);
        Assert.Equal(3, _provider.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedPreparedStart_CancellationOrMissingLocalFile_FailsBeforeSavingBrokerData(bool removeFile)
    {
        Library.Save(Download(15) with { Candles = Download(15).Candles.Take(4).ToArray() });
        _provider.Downloads[60] = Download(60);
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        HistoricalDatasetInfo local = Assert.Single(prepared.Datasets);
        using var cancellation = new CancellationTokenSource();
        if (removeFile)
        {
            File.Delete(Path.Combine(_root, local.RelativePath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service.LoadPreparedAsync(prepared, default));
        }
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.LoadPreparedAsync(prepared, cancellation.Token));
        }

        Assert.DoesNotContain(Library.Scan().Datasets, item => item.SourceIntervalSeconds == 60);
        Assert.Equal(removeFile ? 0 : 1, Library.Scan().Datasets.Count);
        Assert.Equal(3, _provider.Requests.Count);
    }

    [Fact]
    public async Task PendingGapFill_MergedDailySupersetPreservesExactlyTheCheckedComposition()
    {
        HistoricalCandle[] all = Download(15).Candles.ToArray();
        HistoricalCandle[] local = [all[2], all[5]];
        string localHash = Assert.Single(Library.Save(Download(15) with { Candles = local })).DatasetHash;
        _provider.Downloads[15] = Download(15) with
        {
            Candles = all.Where(item => !local.Contains(item)).ToArray(),
        };
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        Assert.Equal(all, prepared.Candles);
        Assert.Equal(Start, Assert.Single(_provider.Requests).FromUtc);
        Assert.Equal(Start.AddMinutes(2), _provider.Requests[0].ThroughUtc);

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);
        LibraryReplayHistoryResult repeated = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal(prepared.Candles, loaded.Candles);
        Assert.Equal(loaded.Candles, repeated.Candles);
        Assert.Equal(all, Library.Read(Assert.Single(Library.Scan().Datasets).DatasetHash).Candles);
        Assert.Equal(local, Library.Read(localHash).Candles);
        Assert.Single(_provider.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRevision_PreservesUnavailableFields_AndCheckedLocalSnapshot(bool nullVolume)
    {
        HistoricalCandle[] all = Download(15).Candles.ToArray();
        HistoricalCandle saved = all[2];
        string originalHash = Assert.Single(Library.Save(Download(15) with { Candles = [saved] })).DatasetHash;
        all[2] = saved with { Open = 0m, Close = 100.5m, Volume = nullVolume ? null : 0m };
        _provider.Downloads[15] = Download(15) with { Candles = all };

        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);

        Assert.True(prepared.Complete);
        Assert.Equal(saved, prepared.Candles[2]);
        HistoricalCandle expectedRevision = saved with { Close = 100.5m };
        Assert.Equal(expectedRevision, prepared.PendingDownload!.Candles[2]);
        Assert.Equal(originalHash, Assert.Single(Library.Scan().Datasets).DatasetHash);

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);
        LibraryReplayHistoryResult repeated = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal(prepared.Candles, loaded.Candles);
        Assert.Equal(loaded.Candles, repeated.Candles);
        Assert.Equal(saved, Assert.Single(Library.Read(originalHash).Candles));
        Assert.Equal(expectedRevision, Library.Read(Assert.Single(Library.Scan().Datasets).DatasetHash).Candles[2]);
        Assert.Single(_provider.Requests);
    }

    [Fact]
    public async Task NullEntries_AreIgnoredWhileValidProviderCandlesRemainPrepared()
    {
        HistoricalCandle valid = Download(15).Candles[1];
        _provider.Downloads[15] = Download(15) with { Candles = [null!, valid, null!] };

        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);

        Assert.False(prepared.Complete);
        Assert.Equal(valid, Assert.Single(prepared.Candles));
        Assert.Equal(valid, Assert.Single(prepared.PendingDownload!.Candles));
        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);
        Assert.Equal(prepared.Candles, loaded.Candles);
    }

    [Fact]
    public async Task AllNullProviderCandles_PreserveLocalSnapshotAndMissingGaps()
    {
        HistoricalCandle saved = Download(15).Candles[2];
        string hash = Assert.Single(Library.Save(Download(15) with { Candles = [saved] })).DatasetHash;
        _provider.Downloads[15] = Download(15) with { Candles = [null!, null!] };

        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);

        Assert.False(prepared.Complete);
        Assert.True(prepared.IsLocal);
        Assert.Equal(saved, Assert.Single(prepared.Candles));
        Assert.Null(prepared.PendingDownload);
        Assert.Equal(2, prepared.Coverage.Gaps.Count);
        Assert.Equal(hash, Assert.Single(Library.Scan().Datasets).DatasetHash);
        Assert.Equal(prepared.Candles, (await Service.LoadPreparedAsync(prepared, default)).Candles);
    }

    [Fact]
    public async Task UnavailableNewPrice_RemainsGapAndIsNotPromisedForStart()
    {
        HistoricalCandle[] all = Download(15).Candles.ToArray();
        all[2] = all[2] with { Open = 0m };
        _provider.Downloads[15] = Download(15) with { Candles = all };

        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);

        Assert.False(prepared.Complete);
        Assert.Equal(7, prepared.Candles.Count);
        Assert.DoesNotContain(prepared.Candles, candle => candle.StartsAtUtc == all[2].StartsAtUtc);
        Assert.False(Directory.Exists(_root));
        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);
        Assert.Equal(prepared.Candles, loaded.Candles);
        Assert.Equal(7, Library.Read(Assert.Single(Library.Scan().Datasets).DatasetHash).Candles.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingDownload_MissingOrChangedPromisedCandleFailsPreparedStart(bool removeCandle)
    {
        _provider.Downloads[15] = Download(15);
        var service = new ReplayHistoryAvailabilityService(new ChangedSaveLibrary(Library, removeCandle), _provider);
        ReplayHistoryAvailability prepared = await service.CheckAsync(Query, false, default);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoadPreparedAsync(prepared, default));

        Assert.Contains("checked Replay data changed", error.Message);
        Assert.Single(_provider.Requests);
    }

    private sealed class ChangedSaveLibrary(IMarketDataLibrary inner, bool removeCandle) : IMarketDataLibrary
    {
        public string RootPath => inner.RootPath;
        public MarketDataLibraryScan Scan() => inner.Scan();
        public HistoricalDataset Read(string datasetHash) => inner.Read(datasetHash);
        public HistoricalDataQueryResult Query(HistoricalDataQuery query) => inner.Query(query);
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) => inner.Save(download with
        {
            Candles = removeCandle ? download.Candles.Skip(1).ToArray() : download.Candles
                .Select((item, index) => index == 0 ? item with { Close = 100.5m } : item).ToArray(),
        });
    }

    private static HistoricalDownload Download(int interval) => new("Robinhood", "test-msft", "MSFT", interval,
        "split", "robinhood-split-unversioned", "regular", Start.AddDays(1), Start, Start.AddMinutes(2),
        Enumerable.Range(0, 120 / interval).Select(index =>
        {
            DateTimeOffset at = Start.AddSeconds(index * interval);
            return new HistoricalCandle(at, at.AddSeconds(interval), at.AddSeconds(interval), 100m, 101m, 99m, 100m, 1000m);
        }).ToArray());

    private sealed class Provider : IMarketHistoryProvider
    {
        public Dictionary<int, HistoricalDownload> Downloads { get; } = [];
        public List<HistoricalDataRequest> Requests { get; } = [];
        public Exception? Error { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Error is not null) throw Error;
            HistoricalDownload download = Downloads.GetValueOrDefault(request.SourceIntervalSeconds) ??
                Download(request.SourceIntervalSeconds) with { Candles = [] };
            // Ordinary fixtures represent the original dashboard range. Narrow them
            // for gap requests, while preserving intentionally invalid provenance.
            if (download.RequestedFromUtc == Start && download.RequestedThroughUtc == Start.AddMinutes(2) &&
                request.FromUtc >= Start && request.ThroughUtc <= Start.AddMinutes(2) &&
                (request.FromUtc != Start || request.ThroughUtc != Start.AddMinutes(2)))
                download = download with
                {
                    RequestedFromUtc = request.FromUtc, RequestedThroughUtc = request.ThroughUtc,
                    Candles = download.Candles.Where(item => item.StartsAtUtc >= request.FromUtc && item.EndsAtUtc <= request.ThroughUtc).ToArray(),
                };
            return Task.FromResult(download);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
