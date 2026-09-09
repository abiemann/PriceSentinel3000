namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record ReplayHistoryAvailability(
    HistoricalDataQuery Query,
    bool OfflineOnly,
    string Source,
    int SourceIntervalSeconds,
    IReadOnlyList<HistoricalDatasetInfo> Datasets,
    IReadOnlyList<HistoricalCandle> Candles,
    HistoricalCoverage Coverage,
    IReadOnlyList<MarketDataLibraryDiagnostic> Diagnostics,
    HistoricalDownload? PendingDownload = null,
    IReadOnlyList<ReplayHistorySource>? Sources = null)
{
    public bool HasData => Candles.Count > 0;
    public bool Complete => HasData && Coverage.Complete;
    public bool IsLocal => Source is "local-library" or "pinned-library";
    public IReadOnlyList<int> NativeSourceIntervals => Sources?.Select(item => item.SourceIntervalSeconds).Distinct().Order().ToArray()
        ?? [SourceIntervalSeconds];
}

/// <summary>
/// Checks the exact dashboard range without changing the library. Provider checks
/// fetch genuine candles into memory; only START archives the selected result.
/// The provider must use its non-interactive history path: checking a date never signs in.
/// </summary>
public sealed class ReplayHistoryAvailabilityService(IMarketDataLibrary library, IMarketHistoryProvider provider)
{
    public async Task<ReplayHistoryAvailability> CheckAsync(
        HistoricalDataQuery query, bool offlineOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        query = query with { PinnedHashes = query.PinnedHashes?.ToArray() };
        if (query.PinnedHashes is { Count: > 0 } pins)
        {
            if (pins.Count > 366 || pins.Distinct(StringComparer.Ordinal).Count() != pins.Count)
                throw new InvalidOperationException("Pin at most one unique dataset hash per day.");
            int[] intervals = pins.Select(hash => library.Read(hash).SourceIntervalSeconds).Distinct().ToArray();
            if (intervals.Length != 1)
                throw new InvalidOperationException("Pinned Replay datasets must use one source interval; intervals are never mixed.");
            return WithDiagnostics(Local(query, offlineOnly, intervals[0], "pinned-library",
                ReadQuery(query with { SourceIntervalSeconds = intervals[0] })), []);
        }

        var sources = new List<ReplayHistorySource>();
        var diagnostics = new List<MarketDataLibraryDiagnostic>();
        // Read every supported local resolution before contacting the broker.
        foreach (int interval in new[] { 15, 30, 60, 120 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            HistoricalDataQuery current = query with { SourceIntervalSeconds = interval };
            HistoricalDataQueryResult local = ReadQuery(current);
            diagnostics.AddRange(local.Diagnostics);
            if (local.Candles.Count > 0)
                sources.Add(new(interval, local.Datasets.ToArray(), local.Candles.ToArray()));
            ReplayHistoryComposition localComposition = ReplayHistoryComposer.Compose(query, sources);
            if (localComposition.Coverage.Complete)
                return Composed(query, offlineOnly, localComposition, diagnostics);
        }

        ReplayHistoryComposition composition = ReplayHistoryComposer.Compose(query, sources);
        if (offlineOnly) return Composed(query, true, composition, diagnostics);
        // Robinhood has no native two-minute endpoint. Genuine local two-minute
        // files are supported above; broker requests use only supported intervals.
        foreach (int interval in new[] { 15, 30, 60 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            HistoricalGap first = composition.Coverage.Gaps[0], last = composition.Coverage.Gaps[^1];
            long ticks = TimeSpan.FromSeconds(interval).Ticks;
            // One bounded request spans the gaps, avoiding a request per missing bar.
            // Boundary expansion obtains whole coarse candles; composition never slices them.
            DateTimeOffset from = new(first.FromUtc.Ticks - first.FromUtc.Ticks % ticks, TimeSpan.Zero);
            DateTimeOffset through = new(checked((last.ThroughUtc.Ticks + ticks - 1) / ticks * ticks), TimeSpan.Zero);
            HistoricalDataQuery current = query with { FromUtc = from, ThroughUtc = through, SourceIntervalSeconds = interval };
            HistoricalDownload download = await provider.DownloadHistoryAsync(new(query.Symbol,
                from, through, interval, query.SessionBounds ?? "regular",
                query.AdjustmentPolicy ?? "split"), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Snapshot provider collections before validating/preparing so a later
            // provider request cannot change the candles promised by this check.
            ArgumentNullException.ThrowIfNull(download.Candles);
            download = download with { Candles = download.Candles.ToArray() };
            ValidateDownload(current, download);
            if (download.Candles.Count > 0) sources.Add(new(interval, [], download.Candles, download));
            composition = ReplayHistoryComposer.Compose(query, sources);
            if (composition.Coverage.Complete) break;
        }
        return Composed(query, false, composition, diagnostics);
    }

    /// <summary>Consumes exactly the checked source without another broker request or a silent revision change.</summary>
    public Task<LibraryReplayHistoryResult> LoadPreparedAsync(
        ReplayHistoryAvailability availability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(availability);
        cancellationToken.ThrowIfCancellationRequested();
        if (!availability.HasData)
            return Task.FromResult(new LibraryReplayHistoryResult(availability.Source,
                availability.SourceIntervalSeconds, [], [], availability.Coverage, availability.Diagnostics));

        if (availability.Sources is { } sources)
            return Task.FromResult(LoadComposition(availability, sources, cancellationToken));

        HistoricalDataQuery query = availability.Query with { SourceIntervalSeconds = availability.SourceIntervalSeconds };
        IReadOnlyList<HistoricalDatasetInfo> datasets = availability.Datasets;
        string source = availability.Source;
        if (availability.PendingDownload is { } download)
        {
            ValidateDownload(query, download);
            cancellationToken.ThrowIfCancellationRequested();
            datasets = library.Save(download);
            source = "provider-saved-library";
        }
        if (datasets.Count == 0)
            throw new InvalidOperationException("The checked Replay source has no immutable datasets.");
        HistoricalDataQueryResult loaded = ReadQuery(query with
        {
            PinnedHashes = datasets.Select(item => item.DatasetHash).ToArray(),
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (!loaded.Candles.SequenceEqual(availability.Candles))
            throw new InvalidOperationException("The checked Replay data changed. Check the date again before starting.");
        return Task.FromResult(new LibraryReplayHistoryResult(source, availability.SourceIntervalSeconds,
            loaded.Datasets, loaded.Candles, loaded.Coverage,
            availability.Diagnostics.Concat(loaded.Diagnostics).Distinct().ToArray()));
    }

    private HistoricalDataQueryResult ReadQuery(HistoricalDataQuery query)
    {
        HistoricalDataQueryResult result = library.Query(query);
        if (!result.Succeeded)
            throw new InvalidOperationException("Replay library selection failed: " + string.Join(" ",
                result.Diagnostics.Where(item => item.Code is not "conflicting_revisions")
                    .Select(item => item.Message).Distinct()));
        return result;
    }

    private static ReplayHistoryAvailability Local(HistoricalDataQuery query, bool offlineOnly, int interval,
        string source, HistoricalDataQueryResult result) =>
        new(query, offlineOnly, source, interval, result.Datasets, result.Candles, result.Coverage, result.Diagnostics);

    private static ReplayHistoryAvailability Composed(HistoricalDataQuery query, bool offlineOnly,
        ReplayHistoryComposition composition, IEnumerable<MarketDataLibraryDiagnostic> diagnostics)
    {
        bool broker = composition.Sources.Any(item => item.PendingDownload is not null);
        bool local = composition.Sources.Any(item => item.Datasets.Count > 0);
        string source = broker ? local ? "local-and-provider" : "provider" : "local-library";
        return WithDiagnostics(new(query, offlineOnly, source, composition.SourceIntervalSeconds,
            composition.Sources.SelectMany(item => item.Datasets).DistinctBy(item => item.DatasetHash).ToArray(),
            composition.Candles, composition.Coverage, [],
            composition.Sources.Select(item => item.PendingDownload).FirstOrDefault(item => item is not null),
            composition.Sources), diagnostics);
    }

    private LibraryReplayHistoryResult LoadComposition(ReplayHistoryAvailability availability,
        IReadOnlyList<ReplayHistorySource> sources, CancellationToken token)
    {
        var loaded = new List<ReplayHistorySource>();
        // Validate retained files before archiving any new source. Changed or missing
        // files fail the prepared selection rather than substituting another revision.
        foreach (ReplayHistorySource source in sources.Where(item => item.PendingDownload is null))
            _ = Reload(source, availability.Query.FromUtc, availability.Query.ThroughUtc);
        foreach (ReplayHistorySource source in sources)
        {
            token.ThrowIfCancellationRequested();
            ReplayHistorySource retained = source;
            DateTimeOffset from = availability.Query.FromUtc, through = availability.Query.ThroughUtc;
            if (source.PendingDownload is { } download)
            {
                from = download.RequestedFromUtc;
                through = download.RequestedThroughUtc;
                ValidateDownload(availability.Query with
                    { FromUtc = from, ThroughUtc = through, SourceIntervalSeconds = source.SourceIntervalSeconds }, download);
                retained = source with { Datasets = library.Save(download), PendingDownload = null };
            }
            loaded.Add(retained with { Candles = Reload(retained, from, through, source.PendingDownload is not null) });
        }
        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(availability.Query, loaded);
        token.ThrowIfCancellationRequested();
        if (result.SourceIntervalSeconds != availability.SourceIntervalSeconds || !result.Candles.SequenceEqual(availability.Candles))
            throw new InvalidOperationException("The checked Replay data changed. Check the date again before starting.");
        string origin = availability.Source switch
        {
            "provider" => "provider-saved-library",
            "local-and-provider" => "local-and-provider-saved-library",
            _ => availability.Source,
        };
        return new(origin, result.SourceIntervalSeconds,
            result.Sources.SelectMany(item => item.Datasets).DistinctBy(item => item.DatasetHash).ToArray(),
            result.Candles, result.Coverage, availability.Diagnostics);

        IReadOnlyList<HistoricalCandle> Reload(ReplayHistorySource source, DateTimeOffset from, DateTimeOffset through,
            bool allowAdditionalCandles = false)
        {
            token.ThrowIfCancellationRequested();
            HistoricalCandle[] candles;
            try
            {
                candles = source.Datasets.SelectMany(info => library.Read(info.DatasetHash).Candles)
                    .Where(item => item.StartsAtUtc >= from && item.EndsAtUtc <= through)
                    .Distinct().OrderBy(item => item.StartsAtUtc).ToArray();
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException("The checked Replay file is missing or changed. Check the date again before starting.", exception);
            }
            if (allowAdditionalCandles)
            {
                // A daily merge can retain other candles inside the broker's requested window.
                // Validate and replay only the exact candles promised by this source's check.
                HashSet<DateTimeOffset> promised = source.Candles.Select(item => item.StartsAtUtc).ToHashSet();
                candles = candles.Where(item => promised.Contains(item.StartsAtUtc)).ToArray();
            }
            if (!candles.SequenceEqual(source.Candles))
                throw new InvalidOperationException("The checked Replay data changed. Check the date again before starting.");
            return candles;
        }
    }

    private static ReplayHistoryAvailability WithDiagnostics(ReplayHistoryAvailability result,
        IEnumerable<MarketDataLibraryDiagnostic> previous)
    {
        var diagnostics = previous.Concat(result.Diagnostics).Distinct().ToList();
        if (result.NativeSourceIntervals.Count > 1)
            diagnostics.Add(new("", "composed_replay_history",
                $"Replay combines {string.Join(", ", result.NativeSourceIntervals)}-second source data into {result.SourceIntervalSeconds}-second candles. " +
                "Only complete bars are aggregated. Native source files are preserved; no finer candles are invented."));
        if (result.HasData && !result.Complete)
            diagnostics.Add(new("", "partial_replay_history",
                $"Only {result.Candles.Count} of {result.Coverage.ExpectedCandleCount} expected {result.SourceIntervalSeconds}-second candles are available. " +
                "Missing candles remain gaps; prices are never reconstructed."));
        if (result.HasData && !result.Coverage.HasCompleteVolume)
            diagnostics.Add(new("", "unknown_volume", "Some source candle volumes are unknown and remain null in the archive and source telemetry."));
        if (result.Datasets.Any(item => item.AdjustmentBasis.EndsWith("-unversioned", StringComparison.Ordinal)) ||
            result.PendingDownload?.AdjustmentBasis.EndsWith("-unversioned", StringComparison.Ordinal) == true)
            diagnostics.Add(new("", "unversioned_adjustment",
                "The provider does not identify its split-adjustment revision. Dataset hashes pin the exact fetched prices."));
        return result with { Diagnostics = diagnostics };
    }

    private static HistoricalCoverage ValidateDownload(HistoricalDataQuery query, HistoricalDownload download)
    {
        if (download.Symbol != query.Symbol || download.SourceIntervalSeconds != query.SourceIntervalSeconds ||
            (query.Provider is not null && download.Provider != query.Provider) ||
            download.AdjustmentPolicy != (query.AdjustmentPolicy ?? "split") ||
            (query.AdjustmentBasis is not null && download.AdjustmentBasis != query.AdjustmentBasis) ||
            download.SessionBounds != (query.SessionBounds ?? "regular") ||
            download.RequestedFromUtc != query.FromUtc || download.RequestedThroughUtc != query.ThroughUtc)
            throw new InvalidOperationException("Historical download does not match the requested Replay source and provenance.");
        foreach (string value in new[] { download.Provider, download.InstrumentId, download.AdjustmentPolicy,
                     download.AdjustmentBasis, download.SessionBounds })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
                throw new InvalidDataException("Historical download requires explicit, bounded source metadata.");
        if (download.FetchedAtUtc.Offset != TimeSpan.Zero || download.FetchedAtUtc.Year is < 1900 or > 9998 ||
            download.Candles.Count > 100_000)
            throw new InvalidDataException("Historical download has an invalid fetch time or candle count.");

        var gaps = new List<HistoricalGap>();
        DateTimeOffset cursor = query.FromUtc;
        foreach (HistoricalCandle candle in download.Candles)
        {
            if (candle is null || candle.StartsAtUtc.Offset != TimeSpan.Zero || candle.EndsAtUtc.Offset != TimeSpan.Zero ||
                candle.AvailableAtUtc.Offset != TimeSpan.Zero ||
                candle.EndsAtUtc - candle.StartsAtUtc != TimeSpan.FromSeconds(query.SourceIntervalSeconds) ||
                candle.StartsAtUtc.Ticks % TimeSpan.FromSeconds(query.SourceIntervalSeconds).Ticks != 0 ||
                candle.AvailableAtUtc != candle.EndsAtUtc || candle.AvailableAtUtc > download.FetchedAtUtc ||
                candle.StartsAtUtc < cursor || candle.EndsAtUtc > query.ThroughUtc ||
                candle.Open <= 0m || candle.Close <= 0m || candle.Low <= 0m ||
                candle.High < Math.Max(candle.Open, candle.Close) || candle.Low > Math.Min(candle.Open, candle.Close) ||
                candle.Volume is < 0m)
                throw new InvalidDataException("Historical download contains invalid, overlapping, unfinalized or out-of-range candles.");
            if (candle.StartsAtUtc > cursor) gaps.Add(new(cursor, candle.StartsAtUtc));
            cursor = candle.EndsAtUtc;
        }
        if (cursor < query.ThroughUtc) gaps.Add(new(cursor, query.ThroughUtc));
        return new(query.FromUtc, query.ThroughUtc,
            download.Candles.Count == 0 ? null : download.Candles[0].StartsAtUtc,
            download.Candles.Count == 0 ? null : download.Candles[^1].EndsAtUtc,
            checked((int)Math.Ceiling((query.ThroughUtc - query.FromUtc).TotalSeconds / query.SourceIntervalSeconds)),
            download.Candles.Count, gaps.Count == 0,
            download.Candles.Count > 0 && download.Candles.All(item => item.Volume.HasValue), gaps);
    }
}
