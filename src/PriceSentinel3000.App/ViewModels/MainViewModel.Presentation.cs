using System.Globalization;
using PriceSentinel3000.Core.Charting;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private void RefreshMarketView()
    {
        if (_ringBuffer is null)
        {
            return;
        }

        IReadOnlyList<MarketQuote> snapshot = _ringBuffer.Snapshot();

        if (snapshot.Count == 0)
        {
            return;
        }

        IReadOnlyList<MarketQuote> retainedChartSnapshot =
            _chartRingBuffer?.Snapshot() ?? snapshot;
        IReadOnlyList<MarketQuote> chartSnapshot = SelectChartHistory(
            retainedChartSnapshot,
            BufferMinutes,
            ChartCandleIntervalSeconds);
        IReadOnlyList<PriceCandle> candles = PriceCandleAggregator.Aggregate(
            chartSnapshot,
            TimeSpan.FromSeconds(ChartCandleIntervalSeconds));
        var refreshedPoints = new List<PricePointViewModel>(candles.Count);

        foreach (PriceCandle candle in candles)
        {
            MarketQuote? markedQuote = chartSnapshot.LastOrDefault(quote =>
                quote.SourceTimestampUtc >= candle.StartsAtUtc &&
                quote.SourceTimestampUtc < candle.EndsAtUtc &&
                _tradeMarkers.ContainsKey(quote.SourceTimestampUtc));
            ChartTradeMarker marker = markedQuote is null
                ? ChartTradeMarker.None
                : _tradeMarkers[markedQuote.SourceTimestampUtc];
            refreshedPoints.Add(new(
                candle.StartsAtUtc,
                candle.Open,
                candle.High,
                candle.Low,
                candle.Close,
                marker,
                markedQuote?.Last,
                candle.IsSynthetic));
        }

        SynchronizeChartPoints(refreshedPoints);

        MarketQuote latest = snapshot[^1];
        _currentPrice = latest.Last.ToString("$0.00", CultureInfo.InvariantCulture);
        _bidAskDisplay = latest.HasTwoSidedMarket
            ? $"{latest.Bid.ToString("0.00", CultureInfo.InvariantCulture)} / {latest.Ask.ToString("0.00", CultureInfo.InvariantCulture)}"
            : "-- / --";
        _hasMarketData = true;
        OnPropertyChanged(nameof(CurrentPrice));
        OnPropertyChanged(nameof(BidAskDisplay));
        OnPropertyChanged(nameof(HasMarketData));
        OnPropertyChanged(nameof(MarketDataStatusBackground));
        OnPropertyChanged(nameof(MarketDataStatusBorder));
        OnPropertyChanged(nameof(MarketDataStatusForeground));
        RefreshTradableNowState();

    }

    private static IReadOnlyList<MarketQuote> SelectChartHistory(
        IReadOnlyList<MarketQuote> snapshot,
        int bufferMinutes,
        int candleIntervalSeconds)
    {
        TimeSpan historyDuration = TimeSpan.FromMinutes(bufferMinutes) +
            PriceChartHistoryCalculator.GetRsiLookback(candleIntervalSeconds);
        DateTimeOffset cutoff = snapshot[^1].SourceTimestampUtc - historyDuration;
        return
        [
            .. snapshot.Where(quote => quote.SourceTimestampUtc >= cutoff),
        ];
    }

    private TimeSpan GetMaximumChartHistoryDuration(int bufferMinutes)
    {
        int maximumCandleIntervalSeconds =
            ChartCandleIntervalOptions.Max(option => option.Value);
        return TimeSpan.FromMinutes(bufferMinutes) +
            PriceChartHistoryCalculator.GetRsiLookback(
                maximumCandleIntervalSeconds);
    }

    private void SynchronizeChartPoints(
        IReadOnlyList<PricePointViewModel> refreshedPoints)
    {
        int sharedCount = Math.Min(ChartPoints.Count, refreshedPoints.Count);

        for (int index = 0; index < sharedCount; index++)
        {
            if (ChartPoints[index] != refreshedPoints[index])
            {
                ChartPoints[index] = refreshedPoints[index];
            }
        }

        while (ChartPoints.Count > refreshedPoints.Count)
        {
            ChartPoints.RemoveAt(ChartPoints.Count - 1);
        }

        for (int index = sharedCount; index < refreshedPoints.Count; index++)
        {
            ChartPoints.Add(refreshedPoints[index]);
        }
    }

    private void SetMarketDataState(
        string headerStatus,
        string stateLabel,
        bool isConnected = true)
    {
        bool connectionChanged = _isMarketDataConnected != isConnected;
        _marketDataStatus = headerStatus;
        _marketDataStateLabel = stateLabel;
        _isMarketDataConnected = isConnected;

        OnPropertyChanged(nameof(MarketDataStatus));
        OnPropertyChanged(nameof(MarketDataStateLabel));
        OnPropertyChanged(nameof(MarketDataStatusBackground));
        OnPropertyChanged(nameof(MarketDataStatusBorder));
        OnPropertyChanged(nameof(MarketDataStatusForeground));

        if (!isConnected)
        {
            CancelSymbolTradabilityRefresh();
            CancelSymbolSuggestionRefresh();
            ClearSymbolSuggestions();
            _tradabilityAccount = null;
            ClearSymbolTradability();
            return;
        }

        if (!connectionChanged)
        {
            return;
        }

        if (_symbolTradability is null ||
            !string.Equals(
                _symbolTradability.Symbol,
                Symbol.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            ScheduleSymbolTradabilityRefresh();
        }
        else
        {
            RefreshTradableNowState();
        }
    }

    private void SetQuoteMarketState(MarketQuote quote)
    {
        if (!IsFreshObservation(quote))
        {
            SetMarketDataState("ROBINHOOD CONNECTED", "MARKET CLOSED");
            return;
        }

        SetMarketDataState("ROBINHOOD REAL PRICES", "REAL TIME");
    }

    private static string FormatMode(TradingMode mode) => mode switch
    {
        TradingMode.PaperTrader => "PAPER TRADER",
        _ => mode.ToString().ToUpperInvariant(),
    };

    private void AddActivity(string message, string level = "INFO")
    {
        DateTimeOffset now = _timeProvider.GetUtcNow().ToLocalTime();
        ActivityLog.Insert(0, new(now.ToString("HH:mm:ss"), message));

        if (!_journalReady)
        {
            return;
        }

        try
        {
            _journal.AppendActivity(
                _activeSession?.Id,
                now.ToUniversalTime(),
                level,
                message);
        }
        catch
        {
            _journalReady = false;
            OnPropertyChanged(nameof(JournalStatus));
        }
    }

    private void NotifyModeProperties()
    {
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(EffectiveMode));
        OnPropertyChanged(nameof(SelectedModeLabel));
        OnPropertyChanged(nameof(EffectiveModeLabel));
        OnPropertyChanged(nameof(IsOffSelected));
        OnPropertyChanged(nameof(IsReplaySelected));
        OnPropertyChanged(nameof(IsPaperTraderSelected));
        OnPropertyChanged(nameof(IsLiveSelected));
        OnPropertyChanged(nameof(IsLiveEffective));
        OnPropertyChanged(nameof(IsConfigurationPanelExpanded));
        OnPropertyChanged(nameof(LiveArmed));
        OnPropertyChanged(nameof(BrokerExecutionLabel));
        OnPropertyChanged(nameof(BrokerExecutionForeground));
        OnPropertyChanged(nameof(AccountPanelCaption));
        OnPropertyChanged(nameof(AccountBalanceCaption));
        OnPropertyChanged(nameof(SessionEquityCaption));
        OnPropertyChanged(nameof(IsStartingBalanceEditable));
        OnPropertyChanged(nameof(AccountBalanceValue));

        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(SecondaryActionLabel));
        OnPropertyChanged(nameof(SessionStateLabel));
        OnPropertyChanged(nameof(SessionStateBackground));
        OnPropertyChanged(nameof(SessionStateBorder));
        OnPropertyChanged(nameof(SessionStateForeground));
        StartSessionCommand.RaiseCanExecuteChanged();
    }

    private void NotifyStrategyProperties()
    {
        OnPropertyChanged(nameof(StrategyMessage));
        OnPropertyChanged(nameof(StrategyStateLabel));
        OnPropertyChanged(nameof(StrategyMetrics));
    }

    private void UpdateStrategyDecision(StrategyDecision decision)
    {
        _strategyStateLabel = decision.State;
        _strategyMessage = decision.Reasons.FirstOrDefault() ?? "Observing price action.";
        _strategyMetrics =
            $"RSI {(decision.SimpleRsi is null ? "--" : decision.SimpleRsi.Value.ToString("0.0", CultureInfo.InvariantCulture))}" +
            $"  |  MOM {decision.MomentumPercent:+0.000;-0.000;0.000}%" +
            $"  |  CONF {decision.Confidence:P0}";
        NotifyStrategyProperties();
    }
}
