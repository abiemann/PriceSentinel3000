using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Infrastructure.MarketData;

public sealed class SyntheticMarketDataSource : IMarketDataSource
{
    public string Name => "DETERMINISTIC SYNTHETIC";
    public bool IsSynthetic => true;

    public IReadOnlyList<MarketQuote> GetHistory(
        MarketDataRequest request,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        DateTimeOffset observedAtUtc)
    {
        Validate(request);

        if (fromUtc > throughUtc)
        {
            throw new ArgumentException("History start must not be after its end.", nameof(fromUtc));
        }

        DateTimeOffset cursor = AlignUp(fromUtc.ToUniversalTime(), request.PollingInterval);
        DateTimeOffset end = throughUtc.ToUniversalTime();
        var quotes = new List<MarketQuote>();

        while (cursor <= end)
        {
            quotes.Add(CreateQuote(request, cursor, observedAtUtc.ToUniversalTime()));
            cursor += request.PollingInterval;
        }

        return quotes;
    }

    public MarketQuote GetQuote(
        MarketDataRequest request,
        DateTimeOffset sourceTimestampUtc)
    {
        Validate(request);
        DateTimeOffset timestamp = AlignDown(
            sourceTimestampUtc.ToUniversalTime(),
            request.PollingInterval);
        return CreateQuote(request, timestamp, DateTimeOffset.UtcNow);
    }

    private static MarketQuote CreateQuote(
        MarketDataRequest request,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset observedAtUtc)
    {
        uint symbolSeed = StableHash(request.Instrument.Symbol);
        long step = sourceTimestampUtc.UtcDateTime.Ticks / request.PollingInterval.Ticks;
        int phaseOffset = (int)(symbolSeed % 144);
        double cycle = PositiveModulo(step + phaseOffset, 144);
        double shape = cycle switch
        {
            < 34 => cycle * 0.014,
            < 58 => 0.476 - ((cycle - 34) * 0.038),
            < 72 => -0.436 + ((cycle - 58) * 0.004),
            < 108 => -0.38 + ((cycle - 72) * 0.027),
            _ => 0.592 - ((cycle - 108) * 0.0164),
        };
        double ripple =
            (0.07 * Math.Sin((step + symbolSeed % 31) * 1.71)) +
            (0.045 * Math.Sin((step + symbolSeed % 47) * 0.43));
        decimal basePrice = 40m + (symbolSeed % 11_000) / 100m;
        decimal last = decimal.Round(
            basePrice * (1m + (decimal)((shape + ripple) / 100d)),
            4,
            MidpointRounding.AwayFromZero);
        decimal halfSpread = Math.Max(0.005m, last * 0.0001m);
        decimal bid = decimal.Round(last - halfSpread, 4, MidpointRounding.AwayFromZero);
        decimal ask = decimal.Round(last + halfSpread, 4, MidpointRounding.AwayFromZero);
        decimal volume =
            100m + (decimal)PositiveModulo(step * 7919 + symbolSeed, 4900);

        return new(
            request.Instrument,
            observedAtUtc,
            sourceTimestampUtc,
            bid,
            ask,
            last,
            volume);
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval)
    {
        long ticks = value.UtcDateTime.Ticks;
        return new DateTimeOffset(ticks - ticks % interval.Ticks, TimeSpan.Zero);
    }

    private static DateTimeOffset AlignUp(DateTimeOffset value, TimeSpan interval)
    {
        DateTimeOffset aligned = AlignDown(value, interval);
        return aligned < value ? aligned + interval : aligned;
    }

    private static void Validate(MarketDataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Polling interval must be positive.");
        }
    }

    private static uint StableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        uint hash = offsetBasis;

        foreach (char character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }

    private static double PositiveModulo(long value, int divisor) =>
        ((value % divisor) + divisor) % divisor;
}
