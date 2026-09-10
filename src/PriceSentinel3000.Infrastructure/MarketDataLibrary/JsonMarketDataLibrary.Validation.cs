using System.Text.Json;
using System.Text.RegularExpressions;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketDataLibrary;

public sealed partial class JsonMarketDataLibrary
{
    private static void ValidateDownload(HistoricalDownload download, bool allowZeroPrices = false)
    {
        ValidateMetadata(download.Provider, download.InstrumentId, download.Symbol,
            download.AdjustmentPolicy, download.AdjustmentBasis, download.SessionBounds);
        ValidateRange(download.RequestedFromUtc, download.RequestedThroughUtc);
        ValidateInterval(download.SourceIntervalSeconds);
        ValidateUtc(download.FetchedAtUtc);
        ArgumentNullException.ThrowIfNull(download.Candles);
        if (download.Candles.Count > 100_000) throw new InvalidDataException("One download may contain at most 100,000 candles.");
        if (download.Candles.Any(item => item is null)) throw new InvalidDataException("A candle entry cannot be null.");
        ValidateCandles(download.Candles.OrderBy(item => item.StartsAtUtc).ToArray(), download.SourceIntervalSeconds,
            download.RequestedFromUtc, download.RequestedThroughUtc, download.FetchedAtUtc, allowZeroPrices);
    }

    private static void ValidateDataset(HistoricalDataset dataset)
    {
        if (dataset.SchemaVersion != SchemaVersion || dataset.GroupingTimeZone != GroupingZone)
            throw new InvalidDataException("Unsupported candle schema version or grouping time zone.");
        ValidateHash(dataset.DatasetHash);
        ValidateMetadata(dataset.Provider, dataset.InstrumentId, dataset.Symbol,
            dataset.AdjustmentPolicy, dataset.AdjustmentBasis, dataset.SessionBounds);
        ValidateInterval(dataset.SourceIntervalSeconds);
        ValidateUtc(dataset.FetchedAtUtc);
        ArgumentNullException.ThrowIfNull(dataset.Coverage);
        ArgumentNullException.ThrowIfNull(dataset.Coverage.Gaps);
        ArgumentNullException.ThrowIfNull(dataset.Candles);
        ValidateCollection(dataset);
        if (dataset.Candles.Count > 10_000) throw new InvalidDataException("A daily document contains too many candles.");
        DateTimeOffset from = dataset.Coverage.RequestedFromUtc;
        DateTimeOffset through = dataset.Coverage.RequestedThroughUtc;
        ValidateRange(from, through);
        if (from < DayStart(dataset.TradingDate) || through > DayStart(dataset.TradingDate.AddDays(1)))
            throw new InvalidDataException("Daily requested coverage must stay within the Eastern calendar date.");
        ValidateCandles(dataset.Candles, dataset.SourceIntervalSeconds, from, through, dataset.FetchedAtUtc);
        if (dataset.Candles.Any(item => EasternDate(item.StartsAtUtc) != dataset.TradingDate))
            throw new InvalidDataException("A candle's Eastern start date does not match its daily document.");
        HistoricalCoverage actual = Coverage(from, through, dataset.SourceIntervalSeconds, dataset.Candles);
        if (JsonSerializer.Serialize(actual, JsonOptions) != JsonSerializer.Serialize(dataset.Coverage, JsonOptions))
            throw new InvalidDataException("Stored coverage and gaps do not match the candle data.");
    }

    private static void ValidateCandles(IReadOnlyList<HistoricalCandle> candles, int seconds,
        DateTimeOffset from, DateTimeOffset through, DateTimeOffset fetchedAt, bool allowZeroPrices = false)
    {
        DateTimeOffset? previousEnd = null;
        foreach (HistoricalCandle candle in candles)
        {
            ArgumentNullException.ThrowIfNull(candle);
            ValidateUtc(candle.StartsAtUtc);
            ValidateUtc(candle.EndsAtUtc);
            ValidateUtc(candle.AvailableAtUtc);
            if (candle.EndsAtUtc - candle.StartsAtUtc != TimeSpan.FromSeconds(seconds) ||
                candle.StartsAtUtc.Ticks % TimeSpan.FromSeconds(seconds).Ticks != 0)
                throw new InvalidDataException("Candle boundaries must match and align to the actual source interval.");
            if (candle.AvailableAtUtc != candle.EndsAtUtc)
                throw new InvalidDataException("Schema 1 supports finalized candles available at source close only; fetch time is separate.");
            if (candle.StartsAtUtc < from || candle.EndsAtUtc > through || candle.AvailableAtUtc > fetchedAt)
                throw new InvalidDataException("A candle is outside the requested or finalized range.");
            if (previousEnd is { } previous && candle.StartsAtUtc < previous)
                throw new InvalidDataException("Candles must be ordered without duplicate or overlapping intervals.");
            bool hasEmptyPrice = candle.Open == 0m || candle.High == 0m || candle.Low == 0m || candle.Close == 0m;
            if (candle.Open < 0m || candle.High < 0m || candle.Low < 0m || candle.Close < 0m || candle.Volume is < 0m ||
                (hasEmptyPrice ? !allowZeroPrices :
                    candle.High < Math.Max(candle.Open, candle.Close) || candle.Low > Math.Min(candle.Open, candle.Close)))
                throw new InvalidDataException("Invalid OHLC or volume cannot be stored as a genuine candle.");
            previousEnd = candle.EndsAtUtc;
        }
    }

    private static HistoricalCoverage Coverage(DateTimeOffset from, DateTimeOffset through, int seconds,
        IReadOnlyList<HistoricalCandle> candles)
    {
        var gaps = new List<HistoricalGap>();
        DateTimeOffset cursor = from;
        foreach (HistoricalCandle candle in candles)
        {
            if (candle.StartsAtUtc > cursor) gaps.Add(new(cursor, candle.StartsAtUtc));
            cursor = Max(cursor, candle.EndsAtUtc);
        }
        if (cursor < through) gaps.Add(new(cursor, through));
        return new(from, through, candles.Count == 0 ? null : candles[0].StartsAtUtc,
            candles.Count == 0 ? null : candles[^1].EndsAtUtc,
            checked((int)Math.Ceiling((through - from).TotalSeconds / seconds)), candles.Count,
            gaps.Count == 0, candles.Count > 0 && candles.All(item => item.Volume.HasValue), gaps);
    }

    private static void ValidateMetadata(string provider, string instrumentId, string symbol,
        string adjustmentPolicy, string adjustmentBasis, string sessionBounds)
    {
        ValidateSymbol(symbol);
        foreach (string value in new[] { provider, instrumentId, adjustmentPolicy, adjustmentBasis, sessionBounds })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
                throw new InvalidDataException("Provider, instrument, adjustment and session metadata must be explicit, bounded text.");
    }
    private static void ValidateSymbol(string symbol)
    {
        if (symbol is null || !Regex.IsMatch(symbol, "^[A-Z][A-Z0-9.-]{0,14}$", RegexOptions.CultureInvariant) ||
            symbol.EndsWith('.') || symbol.Contains("..", StringComparison.Ordinal) ||
            Regex.IsMatch(symbol, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\\.)", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Use a normalized, path-safe equity symbol of at most 15 characters.");
    }
    private static void ValidateHash(string hash)
    {
        if (hash is null || !Regex.IsMatch(hash, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("A dataset hash must be 64 lowercase hexadecimal characters.");
    }
    private static void ValidateInterval(int interval)
    {
        if (interval is not (15 or 30 or 60 or 120))
            throw new InvalidDataException("Supported real source intervals are 15, 30, 60, and 120 seconds.");
    }
    private static void ValidateRange(DateTimeOffset from, DateTimeOffset through)
    {
        ValidateUtc(from);
        ValidateUtc(through);
        if (through <= from || through - from > TimeSpan.FromDays(366))
            throw new InvalidDataException("A requested UTC range must be positive and at most 366 days.");
    }
    private static void ValidateUtc(DateTimeOffset timestamp)
    {
        if (timestamp.Offset != TimeSpan.Zero || timestamp.Year is < 1900 or > 9998)
            throw new InvalidDataException("Candle timestamps must be UTC within the supported calendar range.");
    }
    private static DateOnly EasternDate(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Eastern).DateTime);
    private static DateTimeOffset DayStart(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Eastern), TimeSpan.Zero);
    private static IEnumerable<DateOnly> Dates(DateTimeOffset from, DateTimeOffset through)
    {
        DateOnly last = EasternDate(through.AddTicks(-1));
        for (DateOnly day = EasternDate(from); day <= last; day = day.AddDays(1)) yield return day;
    }
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
