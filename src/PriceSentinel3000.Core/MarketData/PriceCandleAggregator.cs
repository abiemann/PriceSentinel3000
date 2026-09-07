namespace PriceSentinel3000.Core.MarketData;

public sealed record PriceCandle(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int QuoteCount,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    bool IsSynthetic = false);

public static class PriceCandleAggregator
{
    public static IReadOnlyList<PriceCandle> Aggregate(
        IReadOnlyList<MarketQuote> quotes,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(quotes);

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "Candle interval must be positive.");
        }

        foreach (MarketQuote quote in quotes)
        {
            // Live samples have no candle span and may arrive at any timestamp.
            if (quote.OpenPrice is null && quote.HighPrice is null &&
                quote.LowPrice is null && quote.ClosePrice is null)
                continue;
            if (quote.SourceIntervalSeconds is not (15 or 30 or 60 or 120))
                throw new InvalidOperationException("Historical source interval must be 15, 30, 60, or 120 seconds.");
            TimeSpan sourceInterval = TimeSpan.FromSeconds(quote.SourceIntervalSeconds);
            if (interval.Ticks % sourceInterval.Ticks != 0)
                throw new InvalidOperationException("Chart candle interval must be an exact multiple of the historical source interval.");
            if (AlignToInterval(quote.SourceTimestampUtc, sourceInterval) != quote.SourceTimestampUtc)
                throw new InvalidOperationException("Historical bars must start on a source interval boundary.");
        }

        PriceCandle[] observedCandles =
        [
            .. quotes
                .OrderBy(quote => quote.SourceTimestampUtc)
                .GroupBy(quote => AlignToInterval(quote.SourceTimestampUtc, interval))
                .Select(group => CreateCandle(group.Key, interval, group.ToArray())),
        ];

        if (observedCandles.Length < 2)
        {
            return observedCandles;
        }

        var candles = new List<PriceCandle>(observedCandles.Length);
        candles.Add(observedCandles[0]);

        for (int index = 1; index < observedCandles.Length; index++)
        {
            PriceCandle nextObserved = observedCandles[index];
            PriceCandle previous = candles[^1];
            DateTimeOffset missingStart = previous.EndsAtUtc;

            while (missingStart < nextObserved.StartsAtUtc)
            {
                decimal carriedClose = previous.Close;
                previous = new(
                    missingStart,
                    missingStart + interval,
                    0,
                    carriedClose,
                    carriedClose,
                    carriedClose,
                    carriedClose,
                    0m,
                    IsSynthetic: true);
                candles.Add(previous);
                missingStart = previous.EndsAtUtc;
            }

            candles.Add(nextObserved);
        }

        return candles;
    }

    public static DateTimeOffset AlignToInterval(
        DateTimeOffset timestamp,
        TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        long utcTicks = timestamp.ToUniversalTime().Ticks;
        long alignedTicks = utcTicks - utcTicks % interval.Ticks;
        return new(alignedTicks, TimeSpan.Zero);
    }

    private static PriceCandle CreateCandle(
        DateTimeOffset startsAtUtc,
        TimeSpan interval,
        IReadOnlyList<MarketQuote> members)
    {
        MarketQuote first = members[0];
        MarketQuote last = members[^1];
        return new(
            startsAtUtc,
            startsAtUtc + interval,
            members.Count,
            first.CandleOpen,
            members.Max(quote => quote.CandleHigh),
            members.Min(quote => quote.CandleLow),
            last.CandleClose,
            members.Sum(quote => quote.Volume));
    }
}
