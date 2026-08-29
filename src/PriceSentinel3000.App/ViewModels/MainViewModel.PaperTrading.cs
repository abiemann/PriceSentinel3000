using System.Globalization;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.PaperTrading;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private void ProcessPaperObservation(
        MarketQuote trigger,
        bool allowHistoricalSource = false)
    {
        if (_ringBuffer is null ||
            _paperTradingEngine is null ||
            _activeSession is null)
        {
            return;
        }

        if (!allowHistoricalSource && !IsFreshObservation(trigger))
        {
            _strategyStateLabel = "MARKET CLOSED";
            _strategyMessage = "The newest Robinhood venue timestamp is stale; paper decisions and fills are paused.";
            _strategyMetrics = "RSI --  |  MOM --  |  CONF --";
            NotifyStrategyProperties();
            return;
        }

        PaperTradeResult result = _paperTradingEngine.Process(_ringBuffer.Snapshot());
        _journal.AppendDecision(_activeSession.Id, result.Decision);
        UpdatePaperAccount(result.Account);
        _strategyStateLabel = result.Decision.State;
        _strategyMessage = result.Decision.Reasons.FirstOrDefault() ?? "Observing price action.";
        _strategyMetrics =
            $"RSI {(result.Decision.SimpleRsi is null ? "--" : result.Decision.SimpleRsi.Value.ToString("0.0", CultureInfo.InvariantCulture))}" +
            $"  |  MOM {result.Decision.MomentumPercent:+0.000;-0.000;0.000}%" +
            $"  |  CONF {result.Decision.Confidence:P0}";
        NotifyStrategyProperties();

        if (result.Order is null || result.Fill is null)
        {
            return;
        }

        _journal.AppendPaperFill(
            _activeSession.Id,
            _ringBuffer.Instrument,
            result.Order,
            result.Fill,
            result.Account);
        _tradeMarkers[result.Fill.FilledAtUtc] = result.Fill.Side is PaperOrderSide.Buy
            ? ChartTradeMarker.Buy
            : ChartTradeMarker.Sell;

        string profitLoss = result.Fill.Side is PaperOrderSide.Sell
            ? $"; realized {result.Fill.RealizedProfitLoss:+$0.00;-$0.00;$0.00}"
            : string.Empty;
        string settlement = result.Fill.ProceedsAvailableAtUtc is DateTimeOffset availableAtUtc
            ? $"; proceeds available {availableAtUtc.ToLocalTime():g}"
            : string.Empty;
        AddActivity(
            $"PAPER {result.Fill.Side.ToString().ToUpperInvariant()} filled {result.Fill.Quantity:0.######} {SymbolDisplay} @ {result.Fill.Price:C2}{profitLoss}{settlement}. " +
            $"Reason: {result.Decision.State}.");
    }

    private void UpdatePaperAccount(PaperAccountSnapshot account)
    {
        _paperBuyingPower = account.BuyingPower;
        _paperEquity = account.Equity;
        _paperPositionQuantity = account.PositionQuantity;
        _paperAveragePrice = account.AveragePrice;
        _paperRealizedProfitLoss = account.RealizedProfitLoss;
        _paperUnrealizedProfitLoss = account.UnrealizedProfitLoss;
        _paperEntries = account.EntriesToday;
        OnPropertyChanged(nameof(BuyingPowerDisplay));
        OnPropertyChanged(nameof(AccountEquityDisplay));
        OnPropertyChanged(nameof(PositionDisplay));
        OnPropertyChanged(nameof(ProfitLossDisplay));
        OnPropertyChanged(nameof(EntriesDisplay));
    }
}
