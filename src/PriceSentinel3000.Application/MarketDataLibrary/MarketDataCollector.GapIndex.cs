namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
    private readonly Dictionary<string, ICollectionGapIndex> _gapIndexes = new(StringComparer.OrdinalIgnoreCase);

    private ICollectionGapIndex? GetGapIndex(string root)
    {
        if (_gapIndexFactory is null) return null;
        string path = Path.GetFullPath(root);
        if (!_gapIndexes.TryGetValue(path, out ICollectionGapIndex? index))
        {
            index = _gapIndexFactory(path);
            _gapIndexes.Add(path, index);
        }
        index.Initialize();
        return index;
    }

    private static CollectionGapKey GapKey(CollectionJob job) => new(job.Symbol, job.ProviderInstrumentId,
        job.SessionDate, job.SessionBounds, job.AdjustmentPolicy, job.AdjustmentBasis, job.SourceIntervalSeconds);

    private HistoricalGap[] AttemptLimitedRanges(CollectionJob job, ICollectionGapIndex? index, CollectionGapSnapshot known)
    {
        if (job.IgnoreKnownGaps) return [];
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), CollectionEastern).DateTime);
        return index?.SupportsAttemptTracking == true && job.SessionDate < today
            ? known.AttemptedRanges.Where(item => item.Attempts >= 2).Select(item => item.Gap).ToArray()
            : known.UnavailableRanges.ToArray();
    }

    private static HistoricalGap[] SavedCandleRanges(IEnumerable<HistoricalCandle> candles,
        DateTimeOffset from, DateTimeOffset through)
    {
        var ranges = new List<HistoricalGap>();
        foreach (HistoricalCandle candle in candles.Where(candle => candle.StartsAtUtc >= from && candle.EndsAtUtc <= through)
                     .OrderBy(candle => candle.StartsAtUtc))
        {
            if (ranges.Count > 0 && ranges[^1].ThroughUtc >= candle.StartsAtUtc)
                ranges[^1] = ranges[^1] with { ThroughUtc = ranges[^1].ThroughUtc > candle.EndsAtUtc ? ranges[^1].ThroughUtc : candle.EndsAtUtc };
            else ranges.Add(new(candle.StartsAtUtc, candle.EndsAtUtc));
        }
        return ranges.ToArray();
    }

    private void RecordGapObservation(ICollectionGapIndex? index, CollectionJob job, HistoricalDownload download,
        DateTimeOffset requestedAt, IReadOnlyList<HistoricalCandle>? previouslySaved = null)
    {
        if (index is null) return;
        DateTimeOffset checkedAt = _clock.GetUtcNow();
        DateOnly requestDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(requestedAt, CollectionEastern).DateTime);
        HistoricalGap[] unavailable;
        if (index.SupportsAttemptTracking)
        {
            HistoricalGap[] covered = SavedCandleRanges((previouslySaved ?? []).Concat(download.Candles),
                download.RequestedFromUtc, download.RequestedThroughUtc);
            index.ResolveSavedRanges(GapKey(job), covered);
            // Every unresolved part of a completed request stays with this day's JSON,
            // including holes inside a partial response. Saved parts lose their attempts.
            unavailable = ExcludeKnownGaps([new(download.RequestedFromUtc, download.RequestedThroughUtc)], covered);
        }
        else
        {
            // Legacy indexes retain their successful-wholly-empty observation policy.
            unavailable = download.Candles.Count == 0
                ? [new(download.RequestedFromUtc, download.RequestedThroughUtc)] : [];
        }
        index.RecordAttempt(GapKey(job), download.RequestedFromUtc, download.RequestedThroughUtc,
            unavailable, download.Candles.Count > 0, checkedAt,
            job.SessionDate == requestDay ? checkedAt.AddMinutes(15) : null);
    }

    private static HistoricalGap[] ExcludeKnownGaps(IReadOnlyList<HistoricalGap> missing, IReadOnlyList<HistoricalGap> known)
    {
        var result = new List<HistoricalGap>();
        int first = 0;
        foreach (HistoricalGap gap in missing)
        {
            DateTimeOffset cursor = gap.FromUtc;
            while (first < known.Count && known[first].ThroughUtc <= cursor) first++;
            for (int index = first; index < known.Count && known[index].FromUtc < gap.ThroughUtc; index++)
            {
                HistoricalGap blocked = known[index];
                if (blocked.FromUtc > cursor) result.Add(new(cursor, blocked.FromUtc));
                if (blocked.ThroughUtc > cursor) cursor = blocked.ThroughUtc;
                if (cursor >= gap.ThroughUtc) break;
            }
            if (cursor < gap.ThroughUtc) result.Add(new(cursor, gap.ThroughUtc));
        }
        return result.ToArray();
    }
}
