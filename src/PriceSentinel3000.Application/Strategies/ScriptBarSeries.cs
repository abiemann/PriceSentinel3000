using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Scripting;

namespace PriceSentinel3000.Application.Strategies;

/// <summary>Freezes observed strategy bars; chart corrections never enter this series.</summary>
public sealed class ScriptBarSeries
{
    private readonly TimeSpan _interval;
    private readonly int _capacity;
    private readonly List<StrategyBar> _bars = [];
    private StrategyBar? _forming;
    private DateTimeOffset? _lastObservation;
    private bool _formingComplete;
    private bool _hasLiveObservation;
    private DateTimeOffset? _nextHistoricalStart;

    public ScriptBarSeries(int intervalSeconds, int capacity)
    {
        if (intervalSeconds is not (15 or 30 or 60 or 120 or 300))
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
        if (capacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _interval = TimeSpan.FromSeconds(intervalSeconds);
        _capacity = capacity;
    }

    public long Version { get; private set; }
    public long CompletedBarCount { get; private set; }
    public long ContinuityVersion { get; private set; }
    public IReadOnlyList<StrategyBar> Snapshot() => _bars.ToArray();

    public void SeedHistory(IEnumerable<MarketQuote> history, DateTimeOffset availableAtUtc)
    {
        if (_lastObservation is not null || _bars.Count > 0)
            throw new InvalidOperationException("Strategy history can only be seeded before observation starts.");
        foreach (MarketQuote quote in history.OrderBy(item => item.SourceTimestampUtc))
        {
            ValidateHistoricalInterval(quote);
            if (quote.SourceEndsAtUtc <= availableAtUtc)
                ObserveHistoricalBar(quote);
        }
        // Retain only history already available at the first quote. Any remaining
        // forming candle can continue with subsequent sampled live observations.
        _nextHistoricalStart = null;
    }

    public void ObserveQuote(MarketQuote quote)
    {
        Validate(quote);
        DateTimeOffset at = quote.SourceTimestampUtc;
        if (_lastObservation is not null &&
            (at < _lastObservation || (_hasLiveObservation && at == _lastObservation)))
            return;
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(at, _interval);
        if (_forming is not null && start >= _forming.EndsAtUtc)
        {
            bool adjacent = start == _forming.EndsAtUtc;
            if (adjacent)
            {
                if (_formingComplete)
                    Complete(_forming);
            }
            else
                ResetCompleted();
            _forming = null;
            _formingComplete = adjacent || at == start;
        }
        if (_forming is null)
        {
            if (_bars.Count > 0 && _bars[^1].EndsAtUtc != start)
                ResetCompleted();
            // A startup quote partway through a candle is not a complete observation
            // of that candle. The following candle can be observed in full.
            if (_lastObservation is null ||
                PriceCandleAggregator.AlignToInterval(_lastObservation.Value, _interval) == start)
                _formingComplete = at == start;
            _forming = new(start, start + _interval, quote.Last, quote.Last, quote.Last, quote.Last, 0m,
                HasKnownVolume: false);
        }
        else
        {
            _forming = _forming with
            {
                High = Math.Max(_forming.High, quote.Last),
                Low = Math.Min(_forming.Low, quote.Last),
                Close = quote.Last,
                HasKnownVolume = false,
            };
        }
        _lastObservation = at;
        _hasLiveObservation = true;
    }

    public void ObserveHistoricalBar(MarketQuote quote)
    {
        Validate(quote);
        ValidateHistoricalInterval(quote);
        DateTimeOffset at = quote.SourceTimestampUtc;
        if (_lastObservation is not null && at < _lastObservation)
            return;
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(at, _interval);
        if (_nextHistoricalStart is not null && at != _nextHistoricalStart)
        {
            ResetCompleted();
            _forming = null;
        }
        if (_forming is null || _forming.StartsAtUtc != start)
        {
            if (_forming is not null || (_bars.Count > 0 && _bars[^1].EndsAtUtc != start))
                ResetCompleted();
            _formingComplete = at == start;
            _forming = new(start, start + _interval, quote.CandleOpen, quote.CandleHigh,
                quote.CandleLow, quote.CandleClose, quote.Volume, quote.HasKnownVolume);
        }
        else
        {
            _forming = _forming with
            {
                High = Math.Max(_forming.High, quote.CandleHigh),
                Low = Math.Min(_forming.Low, quote.CandleLow),
                Close = quote.CandleClose,
                Volume = _forming.Volume + quote.Volume,
                HasKnownVolume = _forming.HasKnownVolume && quote.HasKnownVolume,
            };
        }
        _nextHistoricalStart = quote.SourceEndsAtUtc;
        _lastObservation = _nextHistoricalStart;
        if (_nextHistoricalStart == _forming.EndsAtUtc)
        {
            if (_formingComplete)
                Complete(_forming);
            _forming = null;
        }
    }

    private void Complete(StrategyBar bar)
    {
        CompletedBarCount++;
        _bars.Add(bar);
        if (_bars.Count > _capacity)
            _bars.RemoveAt(0);
        Version++;
    }

    private void ResetCompleted()
    {
        _bars.Clear();
        ContinuityVersion++;
        Version++;
    }

    private void ValidateHistoricalInterval(MarketQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.SourceIntervalSeconds is not (15 or 30 or 60 or 120))
            throw new InvalidOperationException("Historical source interval must be 15, 30, 60, or 120 seconds.");
        TimeSpan sourceInterval = TimeSpan.FromSeconds(quote.SourceIntervalSeconds);
        if (_interval.Ticks % sourceInterval.Ticks != 0)
            throw new InvalidOperationException("Strategy candle interval must be an exact multiple of the historical source interval.");
        if (PriceCandleAggregator.AlignToInterval(quote.SourceTimestampUtc, sourceInterval) != quote.SourceTimestampUtc)
            throw new InvalidOperationException("Historical bars must start on a source interval boundary.");
    }

    private static void Validate(MarketQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.Last <= 0m || quote.CandleOpen <= 0m || quote.CandleLow <= 0m ||
            quote.CandleHigh < Math.Max(quote.CandleOpen, quote.CandleClose) ||
            quote.CandleLow > Math.Min(quote.CandleOpen, quote.CandleClose))
            throw new InvalidOperationException("Invalid prices cannot become strategy bars.");
    }
}
