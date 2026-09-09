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

    private void RecordGapObservation(ICollectionGapIndex? index, CollectionJob job, HistoricalDownload download,
        DateTimeOffset requestedAt)
    {
        if (index is null) return;
        DateTimeOffset checkedAt = _clock.GetUtcNow();
        DateOnly requestDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(requestedAt, CollectionEastern).DateTime);
        // Only an empty successful response confirms absence. Holes in a partial
        // response still need their own check, and transport errors never reach here.
        HistoricalGap[] unavailable = download.Candles.Count == 0
            ? [new(download.RequestedFromUtc, download.RequestedThroughUtc)] : [];
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
