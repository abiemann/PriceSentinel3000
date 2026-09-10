using System.Text.Json.Serialization;

namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record CollectionGapKey(string Symbol, string? InstrumentId, DateOnly SessionDate,
    string SessionBounds, string AdjustmentPolicy, string AdjustmentBasis, int SourceIntervalSeconds = 15);

public sealed record CollectionGapAttempt(HistoricalGap Gap, int Attempts);
public sealed record CollectionGapUnavailable(HistoricalGap Gap, DateTimeOffset? RetryAfterUtc);
public sealed record CollectionGapState(CollectionGapKey Key, string Provider, bool HasReturnedCandles,
    IReadOnlyList<CollectionGapAttempt> AttemptedRanges, IReadOnlyList<CollectionGapUnavailable> UnavailableRanges)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? BrokerHistoryUnavailableAtUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastBrokerCandlesAtUtc { get; init; }
}

public sealed record CollectionGapSnapshot(IReadOnlyList<HistoricalGap> UnavailableRanges, bool HasReturnedCandles)
{
    public IReadOnlyList<CollectionGapAttempt> AttemptedRanges { get; init; } = [];
    public DateTimeOffset? BrokerHistoryUnavailableAtUtc { get; init; }
}

/// <summary>Broker observations tracked separately from candle coverage; missing files alone are not unavailable-data evidence.</summary>
public interface ICollectionGapIndex
{
    bool SupportsAttemptTracking => false;
    void Initialize();
    CollectionGapSnapshot Query(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc, DateTimeOffset nowUtc);
    void RecordDownloadAttempt(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc, DateTimeOffset checkedAtUtc) { }
    void ResolveSavedRanges(CollectionGapKey key, IReadOnlyList<HistoricalGap> savedRanges) { }
    void RecordDiscoveryUnavailable(CollectionGapKey key, DateTimeOffset checkedAtUtc) { }
    void RecordAttempt(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc,
        IReadOnlyList<HistoricalGap> unavailableRanges, bool receivedCandles, DateTimeOffset checkedAtUtc,
        DateTimeOffset? retryAfterUtc);
}
