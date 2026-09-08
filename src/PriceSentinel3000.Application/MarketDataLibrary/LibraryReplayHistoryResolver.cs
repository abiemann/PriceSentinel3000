namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record LibraryReplayHistoryResult(
    string Source,
    int SourceIntervalSeconds,
    IReadOnlyList<HistoricalDatasetInfo> Datasets,
    IReadOnlyList<HistoricalCandle> Candles,
    HistoricalCoverage Coverage,
    IReadOnlyList<MarketDataLibraryDiagnostic> Diagnostics);

/// <summary>START uses the same disk-first composition as the availability check.</summary>
public sealed class LibraryReplayHistoryResolver(
    IMarketDataLibrary library,
    IMarketHistoryProvider provider,
    Func<CancellationToken, Task> prepareProviderConnection)
{
    public async Task<LibraryReplayHistoryResult> ResolveAsync(
        HistoricalDataQuery query,
        bool offlineOnly,
        CancellationToken cancellationToken)
    {
        var connectedProvider = new ConnectingProvider(provider, prepareProviderConnection);
        var service = new ReplayHistoryAvailabilityService(library, connectedProvider);
        ReplayHistoryAvailability prepared = await service.CheckAsync(query, offlineOnly, cancellationToken).ConfigureAwait(false);
        return await service.LoadPreparedAsync(prepared, cancellationToken).ConfigureAwait(false);
    }

    private sealed class ConnectingProvider(IMarketHistoryProvider inner, Func<CancellationToken, Task> connect) : IMarketHistoryProvider
    {
        private bool _connected;
        public async Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_connected)
            {
                await connect(token).ConfigureAwait(false);
                _connected = true;
            }
            return await inner.DownloadHistoryAsync(request, token).ConfigureAwait(false);
        }
    }
}
