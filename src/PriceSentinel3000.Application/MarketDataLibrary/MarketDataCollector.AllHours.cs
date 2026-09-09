namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
    // Smaller than a normal regular session's 1,560 bars, which the provider already supports.
    private static readonly TimeSpan AllHoursRequestSpan = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaximumSavedSpanBetweenGaps = TimeSpan.FromMinutes(5);

    private async Task<(int Requests, bool ConnectionLost)> CollectAllHoursAsync(
        CollectionJob job, int requestBudget, CancellationToken cancellationToken)
    {
        int requests = 0;
        CollectionJob active = job;
        try
        {
            CollectionSessionWindow day = CollectionSchedule.GetSessionWindow(job.SessionDate, job.SessionBounds);
            DateTimeOffset through = job.RequestedThroughUtc ?? day.ThroughUtc;
            if (through <= day.FromUtc || through > day.ThroughUtc ||
                through.UtcTicks % (15 * TimeSpan.TicksPerSecond) != 0)
                throw new InvalidDataException("The requested collection cutoff is outside the market session.");
            IMarketDataLibrary library = GetCollectionLibrary(job.LibraryRootPath);
            SetActivity("CheckingLocalHistory", job);
            var query = new HistoricalDataQuery(job.Symbol, day.FromUtc, through, 15,
                AdjustmentPolicy: job.AdjustmentPolicy, AdjustmentBasis: job.AdjustmentBasis,
                SessionBounds: job.SessionBounds, RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage,
                IncludeCompatibleSessions: true);
            HistoricalDataQueryResult saved = library.Query(query);
            if (!saved.Succeeded || job.ProviderInstrumentId is not null &&
                saved.Datasets.Any(d => d.InstrumentId != job.ProviderInstrumentId))
                throw new InvalidDataException("Saved history could not be validated for gap recovery. Existing files were preserved.");
            job = job with { SavedCoveragePercent = CollectionDayCoverage.Calculate(job, saved.Datasets) };
            active = job;
            IReadOnlyList<CollectionSessionWindow> windows = CollectionSchedule.GetSessionWindows(job.SessionDate, job.SessionBounds);
            HistoricalGap[] missing = windows
                .SelectMany(window => saved.Coverage.Gaps.Select(gap => new HistoricalGap(
                    gap.FromUtc > window.FromUtc ? gap.FromUtc : window.FromUtc,
                    gap.ThroughUtc < window.ThroughUtc ? gap.ThroughUtc : window.ThroughUtc)))
                .Where(gap => gap.FromUtc < gap.ThroughUtc).OrderBy(gap => gap.FromUtc).ToArray();
            ICollectionGapIndex? gapIndex = GetGapIndex(job.LibraryRootPath);
            CollectionGapSnapshot known = gapIndex?.Query(GapKey(job), day.FromUtc, through, _clock.GetUtcNow()) ?? new([], false);
            HistoricalGap[] requestable = job.IgnoreKnownGaps ? missing : ExcludeKnownGaps(missing, known.UnavailableRanges);
            HistoricalGap? next = requestable.FirstOrDefault(gap =>
                job.NextGapFromUtc is null || gap.ThroughUtc > job.NextGapFromUtc);
            if (next is null)
            {
                bool reusedCheck = job.NextGapFromUtc is null && missing.Length > 0 && requestable.Length == 0;
                // Cached gaps in a previously productive day must not cut off the
                // older search simply because no broker call was needed this run.
                bool receivedCandles = job.ReceivedCandlesThisRun || reusedCheck && known.HasReturnedCandles;
                FinishCollection(job with
                {
                    Status = missing.Length == 0 ? CollectionJobStatus.Complete :
                        saved.Candles.Count > 0 ? CollectionJobStatus.Partial : CollectionJobStatus.Unavailable,
                    ActualSourceIntervalSeconds = saved.Candles.Count > 0 ? 15 : null,
                    DatasetHashes = saved.Datasets.Select(d => d.DatasetHash).ToArray(),
                    Error = missing.Length == 0 ? null : reusedCheck
                        ? "Known empty ranges were skipped using the gap index; saved candles were preserved."
                        : "All missing sections were checked. Confirmed empty ranges are remembered for later downloads.",
                }, emptySession: saved.Candles.Count == 0 && !receivedCandles,
                    receivedCandles: receivedCandles);
                return (0, false);
            }
            if (requestBudget < 1) return (0, false);
            DateTimeOffset from = job.NextGapFromUtc is { } cursor && cursor > next.FromUtc ? cursor : next.FromUtc;
            // Batch nearby holes across short saved spans. The durable cursor checks each
            // batch once per run; a partial response never proves its omitted candles absent.
            DateTimeOffset end = GroupedGapEnd(requestable, from, windows,
                job.AvailabilityCheckPending ? TimeSpan.FromHours(1) : AllHoursRequestSpan,
                job.IgnoreKnownGaps ? [] : known.UnavailableRanges);
            active = job with
            {
                Status = CollectionJobStatus.Downloading, Attempts = job.Attempts + 1,
                LastAttemptAtUtc = _clock.GetUtcNow(), Error = null, RetryAfterUtc = null,
            };
            Update(active);
            if (_lastRequestAt is { } last)
            {
                TimeSpan delay = last + _options.MinimumRequestInterval - _clock.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    SetActivity("WaitingForRateLimit", job, from, end);
                    await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset requestedAt = _clock.GetUtcNow();
            _lastRequestAt = requestedAt;
            requests++;
            SetActivity(job.AvailabilityCheckPending ? "CheckingBrokerAvailability" : "Downloading", job, from, end);
            HistoricalDownload downloaded = await _provider.DownloadHistoryAsync(
                new(job.Symbol, from, end, 15, job.SessionBounds, job.AdjustmentPolicy, job.ProviderInstrumentId),
                cancellationToken).ConfigureAwait(false);
            if (downloaded.Symbol != job.Symbol || downloaded.SourceIntervalSeconds != 15 ||
                downloaded.SessionBounds != job.SessionBounds || downloaded.AdjustmentPolicy != job.AdjustmentPolicy ||
                downloaded.AdjustmentBasis != job.AdjustmentBasis ||
                (job.ProviderInstrumentId is not null && downloaded.InstrumentId != job.ProviderInstrumentId) ||
                downloaded.RequestedFromUtc != from || downloaded.RequestedThroughUtc != end ||
                downloaded.Candles.Any(c => c.StartsAtUtc < from || c.EndsAtUtc > end))
                throw new InvalidDataException("The provider returned mismatched 15-second history provenance.");
            if (saved.Datasets.Any(d => d.Provider != downloaded.Provider || d.InstrumentId != downloaded.InstrumentId ||
                d.AdjustmentPolicy != downloaded.AdjustmentPolicy || d.AdjustmentBasis != downloaded.AdjustmentBasis))
                throw new InvalidDataException("Downloaded and saved history have different provenance; existing files were preserved.");
            var savedByStart = saved.Candles.ToDictionary(candle => candle.StartsAtUtc);
            var returnedStarts = new HashSet<DateTimeOffset>();
            bool hasNewCandles = false;
            foreach (HistoricalCandle candle in downloaded.Candles)
            {
                if (!returnedStarts.Add(candle.StartsAtUtc))
                    throw new InvalidDataException("The provider returned duplicate candles; existing files were preserved.");
                if (savedByStart.TryGetValue(candle.StartsAtUtc, out HistoricalCandle? existing))
                {
                    if (existing != candle)
                        throw new InvalidDataException("Downloaded candles conflict with saved history; existing files were preserved.");
                }
                else hasNewCandles = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (job.AvailabilityCheckPending && downloaded.Candles.Count > 0)
            {
                // Persist availability before the daily file: a restart must not lose this
                // evidence when the saved probe candles no longer appear in the gaps.
                active = active with { ReceivedCandlesThisRun = true, AvailabilityCheckPending = false };
                Update(active);
            }
            IReadOnlyList<HistoricalDatasetInfo> added = [];
            if (hasNewCandles)
            {
                SetActivity("Saving", job, from, end);
                // Keep the native response and its provenance. Compatible overlap is deduplicated on read;
                // responses containing only already-saved candles do not create redundant revisions.
                added = library.Save(downloaded);
            }
            // A durable observation precedes the cursor commit, so a restart can
            // skip an empty response even if the process closed between these writes.
            RecordGapObservation(gapIndex, job, downloaded, requestedAt);
            Update(active with
            {
                Status = CollectionJobStatus.Pending, Attempts = 0, NextGapFromUtc = end,
                ReceivedCandlesThisRun = job.ReceivedCandlesThisRun || downloaded.Candles.Count > 0,
                AvailabilityCheckPending = job.AvailabilityCheckPending && downloaded.Candles.Count == 0,
                ActualSourceIntervalSeconds = saved.Candles.Count > 0 || downloaded.Candles.Count > 0 ? 15 : null,
                DatasetHashes = saved.Datasets.Concat(added).Select(d => d.DatasetHash).Distinct().ToArray(),
                SavedCoveragePercent = CollectionDayCoverage.Calculate(job, saved.Datasets.Concat(added)),
            });
        }
        catch (MarketDataConnectionUnavailableException exception)
        {
            Update(active with { Status = CollectionJobStatus.Pending, Attempts = job.Attempts,
                Error = exception.Message, RetryAfterUtc = null });
            return (requests, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Update(active with { Status = CollectionJobStatus.Pending, Attempts = job.Attempts,
                Error = "Download interrupted; saved sections will be reused on resume.", RetryAfterUtc = null });
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            bool retry = active.Attempts < _options.MaximumTransientAttempts;
            Update(active with { Status = retry ? CollectionJobStatus.Pending : CollectionJobStatus.Failed,
                Error = exception.Message, RetryAfterUtc = retry ? _clock.GetUtcNow() + _options.RetryDelay : null });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Update(active with { Status = CollectionJobStatus.Failed, Error = exception.Message, RetryAfterUtc = null });
        }
        return (requests, false);
    }

    private static DateTimeOffset GroupedGapEnd(IReadOnlyList<HistoricalGap> missing, DateTimeOffset from,
        IReadOnlyList<CollectionSessionWindow> windows, TimeSpan maximumSpan, IReadOnlyList<HistoricalGap> unavailable)
    {
        DateTimeOffset sessionEnd = windows.First(window => window.FromUtc <= from && window.ThroughUtc > from).ThroughUtc;
        DateTimeOffset limit = sessionEnd < from + maximumSpan ? sessionEnd : from + maximumSpan;
        HistoricalGap? barrier = unavailable.FirstOrDefault(gap => gap.FromUtc >= from && gap.FromUtc < limit);
        if (barrier is not null) limit = barrier.FromUtc;
        DateTimeOffset end = from;
        foreach (HistoricalGap gap in missing)
        {
            if (gap.ThroughUtc <= from) continue;
            if (gap.FromUtc >= limit || gap.FromUtc > end + MaximumSavedSpanBetweenGaps) break;
            end = gap.ThroughUtc < limit ? gap.ThroughUtc : limit;
            if (end == limit) break;
        }
        return end;
    }
}
