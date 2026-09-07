namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed class MarketDataConnectionUnavailableException(string message) : InvalidOperationException(message);

public sealed record HistoricalDataRequest(
    string Symbol,
    DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc,
    int SourceIntervalSeconds = 15,
    string SessionBounds = "regular",
    string AdjustmentPolicy = "split",
    string? InstrumentId = null);

public interface IMarketHistoryProvider
{
    Task<HistoricalDownload> DownloadHistoryAsync(
        HistoricalDataRequest request,
        CancellationToken cancellationToken);
}

public sealed record PersonalWatchlist(string Id, string Name, int ItemCount);

public sealed record PersonalWatchlistMember(
    string ObjectType,
    string? Symbol,
    string? ProviderInstrumentId);

public sealed record PersonalWatchlistMembers(
    PersonalWatchlist Watchlist,
    IReadOnlyList<PersonalWatchlistMember> Members);

public interface IPersonalWatchlistSource
{
    Task<IReadOnlyList<PersonalWatchlist>> GetWatchlistsAsync(CancellationToken cancellationToken);

    Task<PersonalWatchlistMembers> GetWatchlistMembersAsync(
        PersonalWatchlist watchlist,
        CancellationToken cancellationToken);
}

public sealed record EquityResolution(
    string RequestedSymbol,
    string Symbol,
    string? CompanyName,
    string? ProviderInstrumentId,
    bool IsSupported,
    string? Message = null);

public interface IEquityCatalogSource
{
    Task<IReadOnlyList<EquityResolution>> ResolveEquitiesAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken);
}
