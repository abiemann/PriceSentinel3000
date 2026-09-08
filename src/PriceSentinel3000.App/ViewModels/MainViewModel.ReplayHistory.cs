using System.Text.Json;
using System.Windows.Threading;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private int? _historicalSourceIntervalSeconds;
    private string _historicalLibraryDescription = "";
    private LibraryReplayHistoryResult? _resolvedReplayHistory;
    private bool ReplayUsesLocalFiles => _resolvedReplayHistory?.Source is "local-library" or "pinned-library";
    private string ReplaySourceDescription => _resolvedReplayHistory?.Source == "local-and-provider-saved-library"
        ? "local files and Robinhood gap fills" : ReplayUsesLocalFiles ? "the local library" : "Robinhood";
    private int[] ReplayNativeIntervals => _resolvedReplayHistory?.Datasets.Select(item => item.SourceIntervalSeconds).Distinct().Order().ToArray() ?? [];

    private async Task<IReadOnlyList<MarketQuote>> LoadReplayHistoryAsync(
        Instrument instrument, DateTimeOffset from, DateTimeOffset through, CancellationToken token)
    {
        _resolvedReplayHistory = null;
        if (DataRetention is not { } retention)
        {
            SetMarketDataState("ROBINHOOD LOGIN", "AUTHORIZING", isConnected: false);
            await _marketDataSource.ConnectAsync(token);
            return await _marketDataSource.GetReplayHistoryAsync(instrument, from, through,
                _timeProvider.GetUtcNow(), token);
        }

        // Snapshot the root and choices before background disk work. A session's
        // recorded hashes stay fixed even if the next Replay uses other settings.
        IMarketDataLibrary library = retention.CreateLibrary();
        string[] pins = retention.ReplayPinnedHashes.Split([',', '\r', '\n', ' ', '\t'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool offline = retention.ReplayOfflineOnly;
        var query = new HistoricalDataQuery(instrument.Symbol, from.ToUniversalTime(), through.ToUniversalTime(),
            AdjustmentPolicy: "split", SessionBounds: retention.Collector.State.Settings.SessionBounds,
            PinnedHashes: pins,
            RevisionPolicy: retention.ReplayUseLatestRevision
                ? HistoricalRevisionPolicy.LatestFetched : HistoricalRevisionPolicy.CompatibleCoverage);
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var resolver = new LibraryReplayHistoryResolver(library, retention.Provider, cancellation =>
            dispatcher.InvokeAsync(() =>
            {
                SetMarketDataState("ROBINHOOD LOGIN", "AUTHORIZING", isConnected: false);
                return retention.PrepareConnectionAsync(cancellation);
            }).Task.Unwrap());
        SetMarketDataState("LOCAL LIBRARY", "READING HISTORY", isConnected: false);
        ReplayCheckContext current = CreateReplayCheckContext(ReplayDate);
        PreparedReplay? prepared = _preparedReplay;
        if (prepared is not null && prepared.Key == current.Key &&
            (prepared.Availability.HasData || _timeProvider.GetUtcNow() - prepared.CheckedAtUtc < TimeSpan.FromMinutes(5)) &&
            current.Query.Symbol == instrument.Symbol && current.Query.FromUtc == from && current.Query.ThroughUtc == through)
            _resolvedReplayHistory = await Task.Run(() => prepared.Service.LoadPreparedAsync(prepared.Availability, token), token);
        else
            _resolvedReplayHistory = await Task.Run(() => resolver.ResolveAsync(query, offline, token), token);
        foreach (MarketDataLibraryDiagnostic diagnostic in _resolvedReplayHistory.Diagnostics)
            AddActivity(diagnostic.Message, "WARNING");
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        return _resolvedReplayHistory.Candles.Select(candle => new MarketQuote(
            instrument, observedAt, candle.StartsAtUtc, 0m, 0m, candle.Close, candle.Volume ?? 0m,
            candle.Open, candle.High, candle.Low, candle.Close, _resolvedReplayHistory.SourceIntervalSeconds,
            HasKnownVolume: candle.Volume.HasValue)).ToArray();
    }

    private object ReplayHistoryProvenance(int sourceInterval) => new
    {
        SourceIntervalSeconds = sourceInterval,
        ReplayIntervalSeconds = sourceInterval,
        NativeSourceIntervals = ReplayNativeIntervals,
        IsFallback = sourceInterval > 15,
        Availability = "source-candle-close",
        ExecutionModel = "completed-source-candle-close",
        IntrabarPricesAvailable = false,
        Source = _resolvedReplayHistory?.Source ?? "provider",
        DatasetHashes = _resolvedReplayHistory?.Datasets.Select(item => item.DatasetHash).ToArray() ?? [],
        Datasets = _resolvedReplayHistory?.Datasets.Select(item => new
        {
            item.DatasetHash, item.Provider, item.InstrumentId, item.Symbol, item.TradingDate,
            item.SourceIntervalSeconds, item.AdjustmentPolicy, item.AdjustmentBasis,
            item.SessionBounds, item.FetchedAtUtc,
        }).ToArray(),
        Coverage = _resolvedReplayHistory?.Coverage,
        Diagnostics = _resolvedReplayHistory?.Diagnostics,
    };

    public string DataResolutionLabel => _historicalSourceIntervalSeconds is { } interval
        ? ReplayNativeIntervals.Length > 1 ? $"{interval} SEC REPLAY (COMBINED)" : $"{interval} SEC CANDLES"
        : _chartRingBuffer is null ? "--" : "SAMPLED QUOTES";

    public string DataResolutionDescription => _historicalSourceIntervalSeconds is { } interval
        ? $"Replay uses {interval}-second candles. Prices become available at each candle's close. Signals, risk checks, and simulated fills cannot see movements inside a replay candle.{_historicalLibraryDescription}"
        : "Paper and Live use sampled current quotes; historical warmup uses completed candles.";

    private void SetHistoricalSourceInterval(int? interval)
    {
        _historicalSourceIntervalSeconds = interval;
        _historicalLibraryDescription = interval is not null && _resolvedReplayHistory is { } result
            ? $" Source: {ReplaySourceDescription}. Native source intervals: {string.Join(", ", ReplayNativeIntervals)} seconds. " +
              $"Coverage: {result.Coverage.ActualCandleCount}/{result.Coverage.ExpectedCandleCount} candles ({(result.Coverage.Complete ? "complete" : "partial")}). " +
              $"Dataset hashes: {string.Join(", ", result.Datasets.Select(item => item.DatasetHash))}."
            : "";
        OnPropertyChanged(nameof(ChartCandleIntervalOptions));
        OnPropertyChanged(nameof(ChartCandleIntervalSeconds));
        OnPropertyChanged(nameof(DataResolutionLabel));
        OnPropertyChanged(nameof(DataResolutionDescription));
    }

    private static int ValidateReplaySourceInterval(IReadOnlyList<MarketQuote> history)
    {
        int interval = history[0].SourceIntervalSeconds;
        if (interval is not (15 or 30 or 60 or 120) ||
            history.Any(quote => quote.SourceIntervalSeconds != interval))
            throw new InvalidOperationException("Replay requires one consistent historical source interval of at most two minutes.");
        return interval;
    }

    // Read retained-session provenance, so a later configuration edit cannot relabel results.
    private JsonElement? AutomationReplayHistory
    {
        get
        {
            if (_automationSession is null) return null;
            using JsonDocument settings = JsonDocument.Parse(_automationSession.SettingsJson);
            return settings.RootElement.TryGetProperty("ReplayHistory", out JsonElement history)
                ? history.Clone() : null;
        }
    }
}
