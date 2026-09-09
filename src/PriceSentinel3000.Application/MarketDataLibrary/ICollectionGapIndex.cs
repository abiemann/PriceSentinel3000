namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record CollectionGapKey(string Symbol, string? InstrumentId, DateOnly SessionDate,
    string SessionBounds, string AdjustmentPolicy, string AdjustmentBasis, int SourceIntervalSeconds = 15);

public sealed record CollectionGapSnapshot(IReadOnlyList<HistoricalGap> UnavailableRanges, bool HasReturnedCandles);

/// <summary>Broker observations separate from portable candles; missing files alone are not unavailable-data evidence.</summary>
public interface ICollectionGapIndex
{
    void Initialize();
    CollectionGapSnapshot Query(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc, DateTimeOffset nowUtc);
    void RecordAttempt(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc,
        IReadOnlyList<HistoricalGap> unavailableRanges, bool receivedCandles, DateTimeOffset checkedAtUtc,
        DateTimeOffset? retryAfterUtc);
}
