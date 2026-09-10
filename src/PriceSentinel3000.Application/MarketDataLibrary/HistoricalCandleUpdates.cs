namespace PriceSentinel3000.Application.MarketDataLibrary;

/// <summary>Applies provider corrections without erasing known values with empty placeholders.</summary>
public static class HistoricalCandleUpdates
{
    public static HistoricalCandle? Apply(HistoricalCandle incoming, HistoricalCandle? existing)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (incoming.StartsAtUtc.Offset != TimeSpan.Zero || incoming.EndsAtUtc.Offset != TimeSpan.Zero ||
            incoming.AvailableAtUtc.Offset != TimeSpan.Zero || incoming.EndsAtUtc <= incoming.StartsAtUtc ||
            incoming.AvailableAtUtc != incoming.EndsAtUtc)
            throw new InvalidDataException("Incoming candle timing is invalid; saved history was preserved.");
        if (existing is not null && (incoming.StartsAtUtc != existing.StartsAtUtc ||
            incoming.EndsAtUtc != existing.EndsAtUtc || incoming.AvailableAtUtc != existing.AvailableAtUtc))
            throw new InvalidDataException("Incoming candle times conflict with saved history; saved history was preserved.");
        if (incoming.Open < 0 || incoming.High < 0 || incoming.Low < 0 || incoming.Close < 0 || incoming.Volume is < 0)
            throw new InvalidDataException("Negative prices or volume cannot be stored as a genuine candle.");

        bool incompletePrices = incoming.Open == 0 || incoming.High == 0 || incoming.Low == 0 || incoming.Close == 0;
        if (existing is null && incompletePrices) return null;
        HistoricalCandle updated = incoming with
        {
            Open = incoming.Open == 0 ? existing!.Open : incoming.Open,
            High = incoming.High == 0 ? existing!.High : incoming.High,
            Low = incoming.Low == 0 ? existing!.Low : incoming.Low,
            Close = incoming.Close == 0 ? existing!.Close : incoming.Close,
            Volume = (incoming.Volume is null or 0) && existing is not null ? existing.Volume : incoming.Volume,
        };
        if (updated.High < Math.Max(updated.Open, updated.Close) || updated.Low > Math.Min(updated.Open, updated.Close))
        {
            if (!incompletePrices || existing is null)
                throw new InvalidDataException("Invalid OHLC cannot be stored as a genuine candle.");
            // A missing high/low may make field-wise fallback inconsistent. Preserve
            // the known price bar together, while still accepting a valid new volume.
            updated = updated with { Open = existing.Open, High = existing.High, Low = existing.Low, Close = existing.Close };
        }
        return updated;
    }
}
