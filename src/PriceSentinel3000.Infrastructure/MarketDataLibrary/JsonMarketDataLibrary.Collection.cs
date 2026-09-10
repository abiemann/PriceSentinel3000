using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketDataLibrary;

public sealed partial class JsonMarketDataLibrary
{
    private const string UnresolvedInstrument = "pricesentinel-unresolved-instrument";
    private static readonly long CollectionIntervalTicks = TimeSpan.FromSeconds(15).Ticks;

    internal CollectionGapSnapshot QueryCollection(CollectionGapKey key, string provider,
        DateTimeOffset from, DateTimeOffset through, DateTimeOffset now)
    {
        ValidateCollectionRange(key, from, through);
        string path = CollectionPath(key);
        if (!File.Exists(path)) return new([], false);
        HistoricalDataset dataset = Load(path);
        if (!CollectionIdentityMatches(dataset, key, provider)) return new([], false);
        CollectionGapKey effective = CollectionKey(dataset, key);
        CollectionGapState? scope = dataset.Collection?.SingleOrDefault(item => item.Key == effective && item.Provider == provider);
        if (scope is null) return new([], false);
        HistoricalGap[] unavailable = scope.UnavailableRanges
            .Where(item => key.SessionDate < EasternDate(now) || item.RetryAfterUtc is null || item.RetryAfterUtc > now)
            .Select(item => ClipCollectionGap(item.Gap, from, through)).OfType<HistoricalGap>().ToArray();
        var counts = scope.AttemptedRanges.Select(item => new { Gap = ClipCollectionGap(item.Gap, from, through), item.Attempts })
            .Where(item => item.Gap is not null).Select(item => new CollectionGapAttempt(item.Gap!, item.Attempts)).ToArray();
        return new(UnionCollectionGaps(unavailable), scope.HasReturnedCandles) { AttemptedRanges = counts };
    }

    internal void RecordCollectionDownload(CollectionGapKey key, string provider,
        DateTimeOffset from, DateTimeOffset through, DateTimeOffset checkedAt)
    {
        ValidateCollectionRange(key, from, through);
        MutateCollection(key, provider, checkedAt, scope =>
        {
            Dictionary<long, int> counts = AttemptSlots(scope.AttemptedRanges);
            foreach (long slot in CollectionSlots(new(from, through)))
                counts[slot] = Math.Min(2, counts.GetValueOrDefault(slot) + 1);
            return scope with { AttemptedRanges = PackAttempts(counts) };
        });
    }

    internal void ResolveCollectionSaved(CollectionGapKey key, string provider, IReadOnlyList<HistoricalGap> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        foreach (HistoricalGap gap in saved)
        {
            ArgumentNullException.ThrowIfNull(gap);
            ValidateCollectionRange(key, gap.FromUtc, gap.ThroughUtc);
        }
        if (saved.Count == 0 || !File.Exists(CollectionPath(key))) return;
        MutateCollection(key, provider, DateTimeOffset.UtcNow, scope => ClearCollectionSaved(scope, saved), createScope: false);
    }

    internal void RecordCollectionObservation(CollectionGapKey key, string provider, DateTimeOffset from, DateTimeOffset through,
        IReadOnlyList<HistoricalGap> unavailable, bool received, DateTimeOffset checkedAt, DateTimeOffset? retryAfter)
    {
        ValidateCollectionRange(key, from, through);
        ArgumentNullException.ThrowIfNull(unavailable);
        foreach (HistoricalGap gap in unavailable)
        {
            ArgumentNullException.ThrowIfNull(gap);
            ValidateCollectionRange(key, gap.FromUtc, gap.ThroughUtc);
            if (gap.FromUtc < from || gap.ThroughUtc > through)
                throw new ArgumentException("Unavailable intervals must stay within the completed request.", nameof(unavailable));
        }
        if (retryAfter <= checkedAt) throw new ArgumentOutOfRangeException(nameof(retryAfter));
        MutateCollection(key, provider, checkedAt, scope =>
        {
            Dictionary<long, DateTimeOffset?> slots = UnavailableSlots(scope.UnavailableRanges);
            // Observations do not clear omitted spans. Only successfully saved candles do.
            foreach (HistoricalGap gap in unavailable)
                foreach (long slot in CollectionSlots(gap)) slots[slot] = retryAfter?.ToUniversalTime();
            return scope with { HasReturnedCandles = scope.HasReturnedCandles || received, UnavailableRanges = PackUnavailable(slots) };
        });
    }

    private void MutateCollection(CollectionGapKey key, string provider, DateTimeOffset checkedAt,
        Func<CollectionGapState, CollectionGapState> change, bool createScope = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        WithLibraryWriteLock(() =>
        {
            string path = CollectionPath(key);
            HistoricalDataset dataset;
            if (File.Exists(path))
            {
                dataset = Load(path);
                if (!CollectionIdentityMatches(dataset, key, provider))
                    throw new InvalidDataException("Collection observations cannot replace a different provider, instrument or adjustment identity.");
            }
            else
            {
                DateTimeOffset from = DayStart(key.SessionDate), through = DayStart(key.SessionDate.AddDays(1));
                dataset = new(SchemaVersion, GroupingZone, "", provider, key.InstrumentId ?? UnresolvedInstrument,
                    key.Symbol, key.SessionDate, 15, key.AdjustmentPolicy, key.AdjustmentBasis, key.SessionBounds,
                    checkedAt.ToUniversalTime(), Coverage(from, through, 15, []), []);
                dataset = dataset with { DatasetHash = Hash(dataset) };
            }
            CollectionGapKey effective = CollectionKey(dataset, key);
            var scopes = dataset.Collection?.ToList() ?? [];
            int index = scopes.FindIndex(item => item.Provider == provider && item.Key == effective);
            if (index < 0 && !createScope) return 0;
            CollectionGapState current = index < 0 ? new(effective, provider, false, [], []) : scopes[index];
            CollectionGapState updated = change(current);
            updated = ClearCollectionSaved(updated, dataset.Candles.Select(item => new HistoricalGap(item.StartsAtUtc, item.EndsAtUtc)).ToArray());
            if (index < 0) scopes.Add(updated); else scopes[index] = updated;
            HistoricalDataset result = dataset with { Collection = scopes.OrderBy(item => item.Key.SessionBounds, StringComparer.Ordinal).ToArray() };
            ValidateDataset(result);
            if (File.Exists(path) && JsonSerializer.Serialize(dataset.Collection, JsonOptions) ==
                JsonSerializer.Serialize(result.Collection, JsonOptions)) return 0;
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
            if (content.Length > MaximumFileBytes) throw new InvalidDataException("The daily file exceeds the 8 MiB limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EnsureNoReparsePoint(path);
            WriteAtomic(path, content, overwrite: true);
            return 0;
        });
    }

    private string CollectionPath(CollectionGapKey key)
    {
        ValidateSymbol(key.Symbol);
        return Resolve(Path.Combine(key.SessionDate.Year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
            key.SessionDate.ToString("MM - MMMM", System.Globalization.CultureInfo.InvariantCulture), key.Symbol,
            key.SessionDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".15s.json"));
    }

    private static bool CollectionIdentityMatches(HistoricalDataset dataset, CollectionGapKey key, string provider) =>
        dataset.Provider == provider && dataset.Symbol == key.Symbol && dataset.TradingDate == key.SessionDate &&
        dataset.SourceIntervalSeconds == key.SourceIntervalSeconds && dataset.AdjustmentPolicy == key.AdjustmentPolicy &&
        dataset.AdjustmentBasis == key.AdjustmentBasis &&
        (key.InstrumentId is null || dataset.InstrumentId == key.InstrumentId ||
            (dataset.InstrumentId == UnresolvedInstrument && dataset.Candles.Count == 0));

    private static CollectionGapKey CollectionKey(HistoricalDataset dataset, CollectionGapKey key) =>
        key with { InstrumentId = dataset.InstrumentId == UnresolvedInstrument ? null : dataset.InstrumentId };

    private static void ValidateCollectionRange(CollectionGapKey key, DateTimeOffset from, DateTimeOffset through)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateMetadata("collection", key.InstrumentId ?? UnresolvedInstrument, key.Symbol,
            key.AdjustmentPolicy, key.AdjustmentBasis, key.SessionBounds);
        if (key.SourceIntervalSeconds != 15 || key.SessionBounds is not ("regular" or "extended" or "24_5") ||
            from >= through || from < DayStart(key.SessionDate) || through > DayStart(key.SessionDate.AddDays(1)) ||
            from.UtcTicks % CollectionIntervalTicks != 0 || through.UtcTicks % CollectionIntervalTicks != 0)
            throw new ArgumentException("Collection ranges must align to 15 seconds within the Eastern calendar date.");
    }

    private static void ValidateCollection(HistoricalDataset dataset)
    {
        if (dataset.InstrumentId == UnresolvedInstrument && dataset.Candles.Count != 0)
            throw new InvalidDataException("An unresolved collection record cannot contain candles.");
        if (dataset.Collection is null) return;
        if (dataset.SourceIntervalSeconds != 15 || dataset.Collection.Count > 16)
            throw new InvalidDataException("Invalid daily collection metadata.");
        var identities = new HashSet<CollectionGapKey>();
        foreach (CollectionGapState scope in dataset.Collection)
        {
            if (scope is null || scope.Key is null || scope.Provider != dataset.Provider ||
                !identities.Add(scope.Key) || !CollectionIdentityMatches(dataset, scope.Key, scope.Provider) ||
                scope.Key.InstrumentId != (dataset.InstrumentId == UnresolvedInstrument ? null : dataset.InstrumentId) ||
                scope.AttemptedRanges is null || scope.UnavailableRanges is null ||
                scope.AttemptedRanges.Count > 6000 || scope.UnavailableRanges.Count > 6000)
                throw new InvalidDataException("Collection metadata must match its daily candle identity.");
            long lastEnd = 0;
            foreach (CollectionGapAttempt attempt in scope.AttemptedRanges)
            {
                if (attempt is null || attempt.Gap is null || attempt.Attempts is < 1 or > 2)
                    throw new InvalidDataException("Invalid collection attempt count.");
                ValidateCollectionRange(scope.Key, attempt.Gap.FromUtc, attempt.Gap.ThroughUtc);
                if (attempt.Gap.FromUtc.UtcTicks < lastEnd) throw new InvalidDataException("Collection attempt ranges overlap or are out of order.");
                lastEnd = attempt.Gap.ThroughUtc.UtcTicks;
            }
            lastEnd = 0;
            foreach (CollectionGapUnavailable unavailable in scope.UnavailableRanges)
            {
                if (unavailable is null || unavailable.Gap is null) throw new InvalidDataException("Invalid unavailable collection range.");
                ValidateCollectionRange(scope.Key, unavailable.Gap.FromUtc, unavailable.Gap.ThroughUtc);
                if (unavailable.RetryAfterUtc is { } retry) ValidateUtc(retry);
                if (unavailable.Gap.FromUtc.UtcTicks < lastEnd) throw new InvalidDataException("Unavailable ranges overlap or are out of order.");
                lastEnd = unavailable.Gap.ThroughUtc.UtcTicks;
            }
        }
    }

    private static IReadOnlyList<CollectionGapState>? MergeCollection(IReadOnlyList<HistoricalDataset> sources,
        string instrumentId, IReadOnlyList<HistoricalCandle> candles)
    {
        CollectionGapState[] scopes = sources.SelectMany(item => item.Collection ?? [])
            .Select(item => item with { Key = item.Key with { InstrumentId = instrumentId == UnresolvedInstrument ? null : instrumentId } }).ToArray();
        if (scopes.Length == 0) return null;
        var merged = new List<CollectionGapState>();
        HistoricalGap[] saved = candles.Select(item => new HistoricalGap(item.StartsAtUtc, item.EndsAtUtc)).ToArray();
        foreach (var group in scopes.GroupBy(item => (item.Provider, item.Key)))
        {
            var counts = new Dictionary<long, int>();
            var unavailable = new Dictionary<long, DateTimeOffset?>();
            foreach (CollectionGapState scope in group)
            {
                foreach ((long slot, int count) in AttemptSlots(scope.AttemptedRanges))
                    counts[slot] = Math.Max(counts.GetValueOrDefault(slot), count);
                foreach ((long slot, DateTimeOffset? retry) in UnavailableSlots(scope.UnavailableRanges))
                    unavailable[slot] = unavailable.TryGetValue(slot, out DateTimeOffset? previous)
                        ? previous is null || retry is null ? null : previous > retry ? previous : retry
                        : retry;
            }
            merged.Add(ClearCollectionSaved(new(group.Key.Key, group.Key.Provider, group.Any(item => item.HasReturnedCandles),
                PackAttempts(counts), PackUnavailable(unavailable)), saved));
        }
        return merged.OrderBy(item => item.Key.SessionBounds, StringComparer.Ordinal).ToArray();
    }

    private static CollectionGapState ClearCollectionSaved(CollectionGapState scope, IReadOnlyList<HistoricalGap> saved)
    {
        var attempts = AttemptSlots(scope.AttemptedRanges);
        var unavailable = UnavailableSlots(scope.UnavailableRanges);
        foreach (HistoricalGap gap in saved)
            foreach (long slot in CollectionSlots(gap))
            {
                attempts.Remove(slot);
                unavailable.Remove(slot);
            }
        return scope with { HasReturnedCandles = scope.HasReturnedCandles || saved.Count > 0,
            AttemptedRanges = PackAttempts(attempts), UnavailableRanges = PackUnavailable(unavailable) };
    }

    private static Dictionary<long, int> AttemptSlots(IReadOnlyList<CollectionGapAttempt> ranges)
    {
        var slots = new Dictionary<long, int>();
        foreach (CollectionGapAttempt item in ranges)
            foreach (long slot in CollectionSlots(item.Gap)) slots[slot] = item.Attempts;
        return slots;
    }

    private static Dictionary<long, DateTimeOffset?> UnavailableSlots(IReadOnlyList<CollectionGapUnavailable> ranges)
    {
        var slots = new Dictionary<long, DateTimeOffset?>();
        foreach (CollectionGapUnavailable item in ranges)
            foreach (long slot in CollectionSlots(item.Gap)) slots[slot] = item.RetryAfterUtc;
        return slots;
    }

    private static IEnumerable<long> CollectionSlots(HistoricalGap gap)
    {
        for (long slot = gap.FromUtc.UtcTicks; slot < gap.ThroughUtc.UtcTicks; slot += CollectionIntervalTicks) yield return slot;
    }

    private static CollectionGapAttempt[] PackAttempts(Dictionary<long, int> slots)
    {
        var packed = new List<CollectionGapAttempt>();
        foreach ((long start, int count) in slots.OrderBy(item => item.Key))
        {
            if (packed.Count > 0 && packed[^1].Attempts == count && packed[^1].Gap.ThroughUtc.UtcTicks == start)
                packed[^1] = packed[^1] with { Gap = packed[^1].Gap with { ThroughUtc = new(start + CollectionIntervalTicks, TimeSpan.Zero) } };
            else packed.Add(new(new(new(start, TimeSpan.Zero), new(start + CollectionIntervalTicks, TimeSpan.Zero)), count));
        }
        return packed.ToArray();
    }

    private static CollectionGapUnavailable[] PackUnavailable(Dictionary<long, DateTimeOffset?> slots)
    {
        var packed = new List<CollectionGapUnavailable>();
        foreach ((long start, DateTimeOffset? retry) in slots.OrderBy(item => item.Key))
        {
            if (packed.Count > 0 && packed[^1].RetryAfterUtc == retry && packed[^1].Gap.ThroughUtc.UtcTicks == start)
                packed[^1] = packed[^1] with { Gap = packed[^1].Gap with { ThroughUtc = new(start + CollectionIntervalTicks, TimeSpan.Zero) } };
            else packed.Add(new(new(new(start, TimeSpan.Zero), new(start + CollectionIntervalTicks, TimeSpan.Zero)), retry));
        }
        return packed.ToArray();
    }

    private static HistoricalGap? ClipCollectionGap(HistoricalGap gap, DateTimeOffset from, DateTimeOffset through) =>
        gap.FromUtc < through && gap.ThroughUtc > from ? new(Max(gap.FromUtc, from), Min(gap.ThroughUtc, through)) : null;

    private static HistoricalGap[] UnionCollectionGaps(IEnumerable<HistoricalGap> gaps)
    {
        var result = new List<HistoricalGap>();
        foreach (HistoricalGap gap in gaps.OrderBy(item => item.FromUtc))
        {
            if (result.Count > 0 && result[^1].ThroughUtc >= gap.FromUtc)
                result[^1] = result[^1] with { ThroughUtc = Max(result[^1].ThroughUtc, gap.ThroughUtc) };
            else result.Add(gap);
        }
        return result.ToArray();
    }
}
