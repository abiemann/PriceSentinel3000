namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record LibraryReplayHistoryResult(
    string Source,
    int SourceIntervalSeconds,
    IReadOnlyList<HistoricalDatasetInfo> Datasets,
    IReadOnlyList<HistoricalCandle> Candles,
    HistoricalCoverage Coverage,
    IReadOnlyList<MarketDataLibraryDiagnostic> Diagnostics);

/// <summary>Selects one genuine source interval and retains the exact files used by Replay.</summary>
public sealed class LibraryReplayHistoryResolver(
    IMarketDataLibrary library,
    IMarketHistoryProvider provider,
    Func<CancellationToken, Task> prepareProviderConnection)
{
    public async Task<LibraryReplayHistoryResult> ResolveAsync(
        HistoricalDataQuery query,
        bool offlineOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.PinnedHashes is { Count: > 0 } pins)
        {
            if (pins.Count > 366 || pins.Distinct(StringComparer.Ordinal).Count() != pins.Count)
                throw new InvalidOperationException("Pin at most one unique dataset hash per day.");
            int[] intervals = pins.Select(hash => library.Read(hash).SourceIntervalSeconds).Distinct().ToArray();
            if (intervals.Length != 1)
                throw new InvalidOperationException("Pinned Replay datasets must use one source interval; intervals are never mixed.");
            HistoricalDataQueryResult pinned = Query(query with { SourceIntervalSeconds = intervals[0] });
            return Result("pinned-library", intervals[0], pinned);
        }

        bool connected = false;
        var diagnostics = new List<MarketDataLibraryDiagnostic>();
        HistoricalDataQueryResult? last = null;
        foreach (int interval in new[] { 15, 30, 60, 120 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            HistoricalDataQuery currentQuery = query with { SourceIntervalSeconds = interval };
            last = Query(currentQuery);
            diagnostics.AddRange(last.Diagnostics);
            // Sparse fine history remains useful. Do not silently replace it with
            // coarse bars or authorize a provider merely to fill local gaps.
            if (last.Candles.Count > 0)
                return Result("local-library", interval, last, diagnostics);
            if (offlineOnly || interval == 120) continue;

            if (!connected)
            {
                await prepareProviderConnection(cancellationToken).ConfigureAwait(false);
                connected = true;
            }
            HistoricalDownload download = await provider.DownloadHistoryAsync(new(
                query.Symbol, query.FromUtc, query.ThroughUtc, interval,
                query.SessionBounds ?? "regular", query.AdjustmentPolicy ?? "split"), cancellationToken).ConfigureAwait(false);
            if (download.SourceIntervalSeconds != interval || download.Symbol != query.Symbol ||
                (query.Provider is not null && download.Provider != query.Provider) ||
                (query.AdjustmentPolicy is not null && download.AdjustmentPolicy != query.AdjustmentPolicy) ||
                (query.AdjustmentBasis is not null && download.AdjustmentBasis != query.AdjustmentBasis) ||
                (query.SessionBounds is not null && download.SessionBounds != query.SessionBounds) ||
                download.RequestedFromUtc != query.FromUtc || download.RequestedThroughUtc != query.ThroughUtc)
                throw new InvalidOperationException("Historical download does not match the requested Replay source and provenance.");
            if (download.Candles.Count == 0) continue;

            // Saving validates all prices/timestamps. Read the precise saved
            // revision back so the journal hashes identify the data actually used.
            IReadOnlyList<HistoricalDatasetInfo> saved = library.Save(download);
            HistoricalDataQueryResult fetched = Query(currentQuery with
            {
                PinnedHashes = saved.Select(item => item.DatasetHash).ToArray(),
            });
            return Result("provider-saved-library", interval, fetched, diagnostics);
        }
        return Result(connected ? "provider" : "local-library", 120, last!, diagnostics);
    }

    private HistoricalDataQueryResult Query(HistoricalDataQuery query)
    {
        HistoricalDataQueryResult result = library.Query(query);
        if (!result.Succeeded)
            throw new InvalidOperationException("Replay library selection failed: " + string.Join(" ",
                result.Diagnostics.Where(item => item.Code is not "conflicting_revisions")
                    .Select(item => item.Message).Distinct()));
        return result;
    }

    private static LibraryReplayHistoryResult Result(string source, int interval,
        HistoricalDataQueryResult result, IEnumerable<MarketDataLibraryDiagnostic>? previous = null)
    {
        var diagnostics = (previous ?? []).Concat(result.Diagnostics).Distinct().ToList();
        if (!result.Coverage.Complete && result.Candles.Count > 0)
            diagnostics.Add(new("", "partial_replay_history",
                $"Replay uses {result.Candles.Count} of {result.Coverage.ExpectedCandleCount} expected {interval}-second candles. " +
                "Missing source candles remain gaps; prices are never reconstructed or replaced with coarser history."));
        if (!result.Coverage.HasCompleteVolume && result.Candles.Count > 0)
            diagnostics.Add(new("", "unknown_volume", "Some source candle volumes are unknown and remain null in the archive and source telemetry."));
        if (result.Datasets.Any(item => item.AdjustmentBasis.EndsWith("-unversioned", StringComparison.Ordinal)))
            diagnostics.Add(new("", "unversioned_adjustment",
                "The provider does not identify its split-adjustment revision. Dataset hashes pin the exact fetched prices."));
        return new(source, interval, result.Datasets, result.Candles, result.Coverage, diagnostics);
    }
}
