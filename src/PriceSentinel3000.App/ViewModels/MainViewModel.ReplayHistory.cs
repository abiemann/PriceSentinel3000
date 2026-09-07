using System.Text.Json;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private int? _historicalSourceIntervalSeconds;

    public string DataResolutionLabel => _historicalSourceIntervalSeconds is { } interval
        ? $"{interval} SEC CANDLES"
        : _chartRingBuffer is null ? "--" : "SAMPLED QUOTES";

    public string DataResolutionDescription => _historicalSourceIntervalSeconds is { } interval
        ? $"Historical data: {interval}-second candles. Prices become available at each candle's close. Signals, risk checks, and simulated fills cannot see movements inside a source candle."
        : "Paper and Live use sampled current quotes; historical warmup uses completed candles.";

    private void SetHistoricalSourceInterval(int? interval)
    {
        _historicalSourceIntervalSeconds = interval;
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
