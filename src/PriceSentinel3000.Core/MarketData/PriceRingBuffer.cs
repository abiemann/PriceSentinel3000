namespace PriceSentinel3000.Core.MarketData;

public sealed class PriceRingBuffer
{
    private readonly SortedDictionary<DateTimeOffset, MarketQuote> _quotes = [];

    public PriceRingBuffer(Instrument instrument, TimeSpan retention)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        }

        Instrument = instrument;
        Retention = retention;
    }

    public Instrument Instrument { get; }
    public TimeSpan Retention { get; }
    public int Count => _quotes.Count;

    public QuoteMergeResult Merge(IEnumerable<MarketQuote> quotes)
    {
        ArgumentNullException.ThrowIfNull(quotes);

        int added = 0;
        int corrected = 0;
        int duplicates = 0;
        int rejected = 0;

        foreach (MarketQuote quote in quotes.OrderBy(item => item.SourceTimestampUtc))
        {
            if (quote.Instrument != Instrument ||
                quote.Last <= 0m ||
                !HasValidMarketPrices(quote))
            {
                rejected++;
                continue;
            }

            if (!_quotes.TryGetValue(quote.SourceTimestampUtc, out MarketQuote? existing))
            {
                _quotes.Add(quote.SourceTimestampUtc, quote);
                added++;
                continue;
            }

            if (HasSameMarketValues(existing, quote))
            {
                duplicates++;
                continue;
            }

            if (quote.ObservedAtUtc >= existing.ObservedAtUtc)
            {
                _quotes[quote.SourceTimestampUtc] = quote;
                corrected++;
            }
            else
            {
                rejected++;
            }
        }

        TrimExpired();
        return new(added, corrected, duplicates, rejected, _quotes.Count);
    }

    public IReadOnlyList<MarketQuote> Snapshot() => [.. _quotes.Values];

    private static bool HasSameMarketValues(MarketQuote left, MarketQuote right) =>
        left.Bid == right.Bid &&
        left.Ask == right.Ask &&
        left.Last == right.Last &&
        left.Volume == right.Volume &&
        left.OpenPrice == right.OpenPrice &&
        left.HighPrice == right.HighPrice &&
        left.LowPrice == right.LowPrice &&
        left.ClosePrice == right.ClosePrice;

    private static bool HasValidMarketPrices(MarketQuote quote) =>
        (quote.HasTwoSidedMarket || quote.Bid == 0m && quote.Ask == 0m) &&
        HasValidCandle(quote);

    private static bool HasValidCandle(MarketQuote quote)
    {
        bool hasAnyCandleValue =
            quote.OpenPrice.HasValue ||
            quote.HighPrice.HasValue ||
            quote.LowPrice.HasValue ||
            quote.ClosePrice.HasValue;

        if (!hasAnyCandleValue)
        {
            return true;
        }

        if (!quote.OpenPrice.HasValue ||
            !quote.HighPrice.HasValue ||
            !quote.LowPrice.HasValue ||
            !quote.ClosePrice.HasValue)
        {
            return false;
        }

        decimal bodyHigh = Math.Max(quote.OpenPrice.Value, quote.ClosePrice.Value);
        decimal bodyLow = Math.Min(quote.OpenPrice.Value, quote.ClosePrice.Value);
        return quote.LowPrice.Value > 0m &&
               quote.LowPrice.Value <= bodyLow &&
               quote.HighPrice.Value >= bodyHigh;
    }

    private void TrimExpired()
    {
        if (_quotes.Count == 0)
        {
            return;
        }

        DateTimeOffset cutoff = _quotes.Keys.Last() - Retention;
        DateTimeOffset[] expired = [.. _quotes.Keys.TakeWhile(timestamp => timestamp < cutoff)];

        foreach (DateTimeOffset timestamp in expired)
        {
            _quotes.Remove(timestamp);
        }
    }
}

public sealed record QuoteMergeResult(
    int Added,
    int Corrected,
    int Duplicates,
    int Rejected,
    int BufferCount);
