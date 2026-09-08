namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record ReplayHistorySource(
    int SourceIntervalSeconds,
    IReadOnlyList<HistoricalDatasetInfo> Datasets,
    IReadOnlyList<HistoricalCandle> Candles,
    HistoricalDownload? PendingDownload = null);

public sealed record ReplayHistoryComposition(
    int SourceIntervalSeconds,
    IReadOnlyList<HistoricalCandle> Candles,
    HistoricalCoverage Coverage,
    IReadOnlyList<ReplayHistorySource> Sources);

/// <summary>Combines genuine whole source bars into one uniform Replay interval without filling missing prices.</summary>
public static class ReplayHistoryComposer
{
    private static readonly int[] Intervals = [15, 30, 60, 120];

    public static ReplayHistoryComposition Compose(HistoricalDataQuery query, IReadOnlyList<ReplayHistorySource> sources)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sources);
        if (query.FromUtc.Offset != TimeSpan.Zero || query.ThroughUtc.Offset != TimeSpan.Zero ||
            query.ThroughUtc <= query.FromUtc)
            throw new ArgumentException("Replay requires a positive UTC time range.", nameof(query));

        var parts = new Dictionary<long, List<Part>>();
        for (int index = 0; index < sources.Count; index++)
        {
            ReplayHistorySource source = sources[index];
            if (!Intervals.Contains(source.SourceIntervalSeconds))
                throw new InvalidDataException("Replay sources must use genuine 15-, 30-, 60-, or 120-second candles.");
            long intervalTicks = source.SourceIntervalSeconds * TimeSpan.TicksPerSecond;
            var starts = new HashSet<long>();
            foreach (HistoricalCandle candle in source.Candles)
            {
                if (candle.StartsAtUtc.Offset != TimeSpan.Zero || candle.EndsAtUtc.Offset != TimeSpan.Zero ||
                    candle.EndsAtUtc.UtcTicks - candle.StartsAtUtc.UtcTicks != intervalTicks ||
                    candle.StartsAtUtc.UtcTicks % intervalTicks != 0 || candle.AvailableAtUtc != candle.EndsAtUtc ||
                    candle.Open <= 0 || candle.Close <= 0 || candle.Low <= 0 ||
                    candle.High < Math.Max(candle.Open, candle.Close) || candle.Low > Math.Min(candle.Open, candle.Close) ||
                    candle.Volume is < 0 || !starts.Add(candle.StartsAtUtc.UtcTicks))
                    throw new InvalidDataException("Replay source candles must be valid, unique, finalized, and aligned to their source interval.");
                if (candle.StartsAtUtc < query.FromUtc || candle.EndsAtUtc > query.ThroughUtc) continue;
                if (!parts.TryGetValue(candle.StartsAtUtc.UtcTicks, out List<Part>? at))
                    parts.Add(candle.StartsAtUtc.UtcTicks, at = []);
                at.Add(new(index, source.SourceIntervalSeconds, candle));
            }
        }
        foreach (List<Part> at in parts.Values)
        {
            if (query.IncludeCompatibleSessions && at.GroupBy(part => part.IntervalSeconds).Any(group =>
                group.Select(part => part.Candle).Distinct().Skip(1).Any() &&
                group.SelectMany(part => Identities(sources[part.SourceIndex]))
                    .Select(item => item.Identity.SessionBounds).Distinct().Skip(1).Any()))
                throw new InvalidDataException("Replay market sessions disagree on overlapping candle prices or volume. Select one saved revision.");
            at.Sort((left, right) => left.IntervalSeconds != right.IntervalSeconds
                ? left.IntervalSeconds.CompareTo(right.IntervalSeconds) : left.SourceIndex.CompareTo(right.SourceIndex));
        }

        ReplayHistoryComposition best = new(15, [], BuildCoverage(query, 15, []), []);
        long bestCoveredTicks = 0;
        foreach (int interval in Intervals)
        {
            long intervalTicks = interval * TimeSpan.TicksPerSecond;
            var candles = new List<HistoricalCandle>();
            var contributors = new HashSet<int>();
            foreach (long start in parts.Keys.Where(start => start % intervalTicks == 0 &&
                start <= query.ThroughUtc.UtcTicks - intervalTicks).Order())
            {
                var chosen = new List<Part>();
                if (!FillBucket(start, start + intervalTicks, parts, chosen, [])) continue;
                candles.Add(Aggregate(chosen));
                foreach (Part part in chosen) contributors.Add(part.SourceIndex);
            }
            ReplayHistorySource[] used = contributors.Order().Select(index => sources[index]).ToArray();
            ValidateProvenance(query, used);
            HistoricalCoverage coverage = BuildCoverage(query, interval, candles);
            var candidate = new ReplayHistoryComposition(interval, candles.ToArray(), coverage, used);
            if (coverage.Complete) return candidate;
            long coveredTicks = candles.Count * intervalTicks;
            if (coveredTicks > bestCoveredTicks)
            {
                best = candidate;
                bestCoveredTicks = coveredTicks;
            }
        }
        return best;
    }

    private static bool FillBucket(long cursor, long end, Dictionary<long, List<Part>> parts,
        List<Part> chosen, HashSet<long> failed)
    {
        if (cursor == end) return true;
        if (failed.Contains(cursor) || !parts.TryGetValue(cursor, out List<Part>? options)) return false;
        // At most eight 15-second pieces fit in the largest 120-second bucket.
        // Backtracking permits an intact coarse bar to replace an incomplete fine partition.
        foreach (Part part in options)
        {
            long next = part.Candle.EndsAtUtc.UtcTicks;
            if (next > end) continue;
            chosen.Add(part);
            if (FillBucket(next, end, parts, chosen, failed)) return true;
            chosen.RemoveAt(chosen.Count - 1);
        }
        failed.Add(cursor);
        return false;
    }

    private static HistoricalCandle Aggregate(IReadOnlyList<Part> parts)
    {
        HistoricalCandle first = parts[0].Candle, last = parts[^1].Candle;
        return new(first.StartsAtUtc, last.EndsAtUtc, last.EndsAtUtc,
            first.Open, parts.Max(part => part.Candle.High), parts.Min(part => part.Candle.Low), last.Close,
            parts.All(part => part.Candle.Volume.HasValue) ? parts.Sum(part => part.Candle.Volume!.Value) : null);
    }

    private static HistoricalCoverage BuildCoverage(HistoricalDataQuery query, int interval,
        IReadOnlyList<HistoricalCandle> candles)
    {
        var gaps = new List<HistoricalGap>();
        DateTimeOffset cursor = query.FromUtc;
        foreach (HistoricalCandle candle in candles)
        {
            if (candle.StartsAtUtc > cursor) gaps.Add(new(cursor, candle.StartsAtUtc));
            cursor = candle.EndsAtUtc;
        }
        if (cursor < query.ThroughUtc) gaps.Add(new(cursor, query.ThroughUtc));
        return new(query.FromUtc, query.ThroughUtc,
            candles.Count == 0 ? null : candles[0].StartsAtUtc,
            candles.Count == 0 ? null : candles[^1].EndsAtUtc,
            checked((int)Math.Ceiling((query.ThroughUtc - query.FromUtc).TotalSeconds / interval)),
            candles.Count, gaps.Count == 0, candles.Count > 0 && candles.All(candle => candle.Volume.HasValue), gaps);
    }

    private static void ValidateProvenance(HistoricalDataQuery query, IReadOnlyList<ReplayHistorySource> sources)
    {
        Identity? expected = null;
        foreach (ReplayHistorySource source in sources)
        {
            if (source.Datasets.Count == 0 && source.PendingDownload is null)
                throw new InvalidDataException("Contributing Replay history must identify its source provenance.");
            foreach ((Identity identity, int interval) in Identities(source))
            {
                if (interval != source.SourceIntervalSeconds || identity.Symbol != query.Symbol ||
                    (query.Provider is not null && identity.Provider != query.Provider) ||
                    (query.AdjustmentPolicy is not null && identity.AdjustmentPolicy != query.AdjustmentPolicy) ||
                    (query.AdjustmentBasis is not null && identity.AdjustmentBasis != query.AdjustmentBasis) ||
                    !query.MatchesSessionBounds(identity.SessionBounds) ||
                    (expected is not null && identity with { SessionBounds = query.SessionIdentity(identity.SessionBounds) } != expected))
                    throw new InvalidDataException("Replay cannot combine different providers, instruments, symbols, adjustments, or market sessions.");
                expected ??= identity with { SessionBounds = query.SessionIdentity(identity.SessionBounds) };
            }
        }
    }

    private static IEnumerable<(Identity Identity, int Interval)> Identities(ReplayHistorySource source)
    {
        foreach (HistoricalDatasetInfo dataset in source.Datasets)
            yield return (new(dataset.Provider, dataset.InstrumentId, dataset.Symbol,
                dataset.AdjustmentPolicy, dataset.AdjustmentBasis, dataset.SessionBounds), dataset.SourceIntervalSeconds);
        if (source.PendingDownload is { } download)
            yield return (new(download.Provider, download.InstrumentId, download.Symbol,
                download.AdjustmentPolicy, download.AdjustmentBasis, download.SessionBounds), download.SourceIntervalSeconds);
    }

    private sealed record Part(int SourceIndex, int IntervalSeconds, HistoricalCandle Candle);
    private sealed record Identity(string Provider, string InstrumentId, string Symbol,
        string AdjustmentPolicy, string AdjustmentBasis, string SessionBounds);
}
