namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record HistoricalCandle(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    DateTimeOffset AvailableAtUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal? Volume);

public sealed record HistoricalDownload(
    string Provider,
    string InstrumentId,
    string Symbol,
    int SourceIntervalSeconds,
    string AdjustmentPolicy,
    string AdjustmentBasis,
    string SessionBounds,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset RequestedFromUtc,
    DateTimeOffset RequestedThroughUtc,
    IReadOnlyList<HistoricalCandle> Candles);

public sealed record HistoricalGap(DateTimeOffset FromUtc, DateTimeOffset ThroughUtc);

public sealed record HistoricalCoverage(
    DateTimeOffset RequestedFromUtc,
    DateTimeOffset RequestedThroughUtc,
    DateTimeOffset? CoveredFromUtc,
    DateTimeOffset? CoveredThroughUtc,
    int ExpectedCandleCount,
    int ActualCandleCount,
    bool Complete,
    bool HasCompleteVolume,
    IReadOnlyList<HistoricalGap> Gaps);

public sealed record HistoricalDataset(
    int SchemaVersion,
    string GroupingTimeZone,
    string DatasetHash,
    string Provider,
    string InstrumentId,
    string Symbol,
    DateOnly TradingDate,
    int SourceIntervalSeconds,
    string AdjustmentPolicy,
    string AdjustmentBasis,
    string SessionBounds,
    DateTimeOffset FetchedAtUtc,
    HistoricalCoverage Coverage,
    IReadOnlyList<HistoricalCandle> Candles);

public sealed record HistoricalDatasetInfo(
    string DatasetHash,
    string RelativePath,
    string Provider,
    string InstrumentId,
    string Symbol,
    DateOnly TradingDate,
    int SourceIntervalSeconds,
    string AdjustmentPolicy,
    string AdjustmentBasis,
    string SessionBounds,
    DateTimeOffset FetchedAtUtc,
    HistoricalCoverage Coverage);

public sealed record MarketDataLibraryDiagnostic(string RelativePath, string Code, string Message);

public sealed record MarketDataLibraryScan(
    IReadOnlyList<HistoricalDatasetInfo> Datasets,
    IReadOnlyList<MarketDataLibraryDiagnostic> Diagnostics);

public enum HistoricalRevisionPolicy { RejectConflicts, LatestFetched, CompatibleCoverage }

public sealed record HistoricalDataQuery(
    string Symbol,
    DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc,
    int SourceIntervalSeconds = 15,
    string? Provider = null,
    string? AdjustmentPolicy = null,
    string? AdjustmentBasis = null,
    string? SessionBounds = null,
    IReadOnlyList<string>? PinnedHashes = null,
    HistoricalRevisionPolicy RevisionPolicy = HistoricalRevisionPolicy.RejectConflicts,
    bool IncludeCompatibleSessions = false)
{
    public bool MatchesSessionBounds(string sessionBounds) => SessionBounds is null ||
        SessionIdentity(sessionBounds) == SessionIdentity(SessionBounds);

    // This key is only for comparison; native dataset session metadata stays intact.
    public string SessionIdentity(string sessionBounds) => IncludeCompatibleSessions &&
        sessionBounds is "regular" or "extended" or "24_5" ? "24_5" : sessionBounds;
}

public sealed record HistoricalDataQueryResult(
    bool Succeeded,
    IReadOnlyList<HistoricalDatasetInfo> Datasets,
    IReadOnlyList<HistoricalCandle> Candles,
    HistoricalCoverage Coverage,
    IReadOnlyList<MarketDataLibraryDiagnostic> Diagnostics);

/// <summary>Portable daily candle files with exact-hash history. Call disk operations off the UI thread.</summary>
public interface IMarketDataLibrary
{
    string RootPath { get; }
    MarketDataLibraryScan Scan();
    MarketDataLibraryScan ConsolidateDailyFiles() => Scan();
    MarketDataLibraryScan ConsolidateDailyFiles(IProgress<int> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(0);
        MarketDataLibraryScan result = ConsolidateDailyFiles();
        progress.Report(100);
        return result;
    }
    IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download);
    HistoricalDataset Read(string datasetHash);
    HistoricalDataQueryResult Query(HistoricalDataQuery query);
}
