using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketDataLibrary;

/// <summary>Stores collection observations in the same Eastern-dated JSON document as its candles.</summary>
public sealed class JsonCollectionGapIndex(string rootPath, string providerIdentity = "Robinhood") : ICollectionGapIndex
{
    private readonly JsonMarketDataLibrary _library = new(rootPath);
    public bool SupportsAttemptTracking => true;
    public void Initialize() { }

    public CollectionGapSnapshot Query(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc, DateTimeOffset nowUtc) =>
        _library.QueryCollection(key, providerIdentity, fromUtc, throughUtc, nowUtc);

    public void RecordDownloadAttempt(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc, DateTimeOffset checkedAtUtc) =>
        _library.RecordCollectionDownload(key, providerIdentity, fromUtc, throughUtc, checkedAtUtc);

    public void ResolveSavedRanges(CollectionGapKey key, IReadOnlyList<HistoricalGap> savedRanges) =>
        _library.ResolveCollectionSaved(key, providerIdentity, savedRanges);

    public void RecordAttempt(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc,
        IReadOnlyList<HistoricalGap> unavailableRanges, bool receivedCandles, DateTimeOffset checkedAtUtc, DateTimeOffset? retryAfterUtc) =>
        _library.RecordCollectionObservation(key, providerIdentity, fromUtc, throughUtc, unavailableRanges, receivedCandles, checkedAtUtc, retryAfterUtc);
}
