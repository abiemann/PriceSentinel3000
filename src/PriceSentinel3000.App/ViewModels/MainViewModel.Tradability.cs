using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly TimeSpan SymbolTradabilityDebounce =
        TimeSpan.FromMilliseconds(500);

    private void ScheduleSymbolTradabilityRefresh()
    {
        CancelSymbolTradabilityRefresh();
        ClearSymbolTradability();

        string symbol = Symbol.Trim().ToUpperInvariant();
        if (!_isMarketDataConnected || string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _symbolTradabilityCancellation = cancellation;
        _ = RefreshSymbolTradabilityAsync(symbol, cancellation);
    }

    private async Task RefreshSymbolTradabilityAsync(
        string symbol,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(SymbolTradabilityDebounce, cancellation.Token);
            BrokerAccount account = _tradabilityAccount ??
                await _liveBrokerGateway.GetAgenticAccountAsync(cancellation.Token);
            EquityTradability tradability =
                await _liveBrokerGateway.GetTradabilityAsync(
                    account.AccountNumber,
                    account.AccountType,
                    new Instrument(symbol, AssetClass.Equity),
                    cancellation.Token);

            if (cancellation.IsCancellationRequested ||
                !string.Equals(symbol, Symbol.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _tradabilityAccount = account;
            SetSymbolTradability(tradability);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ReferenceEquals(_symbolTradabilityCancellation, cancellation) &&
                string.Equals(
                    symbol,
                    Symbol.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                ClearSymbolTradability();
            }
        }
        finally
        {
            if (ReferenceEquals(_symbolTradabilityCancellation, cancellation))
            {
                _symbolTradabilityCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void SetSymbolTradability(EquityTradability tradability)
    {
        _symbolTradability = tradability;
        OnPropertyChanged(nameof(HasTradabilityResult));
        SetField(
            ref _isTwentyFourHourEligible,
            tradability.OvernightTradeable,
            nameof(IsTwentyFourHourEligible));
        RefreshTradableNowState();
        OnPropertyChanged(nameof(TradableNowText));
    }

    private void ClearSymbolTradability()
    {
        bool hadResult = _symbolTradability is not null;
        _symbolTradability = null;
        SetField(
            ref _isTwentyFourHourEligible,
            false,
            nameof(IsTwentyFourHourEligible));
        SetTradableNow(false);

        if (hadResult)
        {
            OnPropertyChanged(nameof(HasTradabilityResult));
            OnPropertyChanged(nameof(TradableNowText));
        }
    }

    private void RefreshTradableNowState()
    {
        bool tradableNow = _symbolTradability is { Tradeable: true } tradability &&
                           _marketSessionEvaluator.IsTradableNow(
                               tradability.ExtendedHoursTradeable,
                               tradability.OvernightTradeable);
        SetTradableNow(tradableNow);
    }

    private void HandleTradabilityRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_isMarketDataConnected &&
            _symbolTradability is null &&
            _symbolTradabilityCancellation is null)
        {
            ScheduleSymbolTradabilityRefresh();
            return;
        }

        RefreshTradableNowState();
    }

    private void SetTradableNow(bool value)
    {
        if (SetField(ref _isTradableNow, value, nameof(IsTradableNow)))
        {
            OnPropertyChanged(nameof(TradableNowText));
        }
    }

    private void CancelSymbolTradabilityRefresh()
    {
        CancellationTokenSource? cancellation = _symbolTradabilityCancellation;
        _symbolTradabilityCancellation = null;
        cancellation?.Cancel();
    }
}
