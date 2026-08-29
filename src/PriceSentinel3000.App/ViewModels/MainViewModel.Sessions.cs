using System.Text.Json;
using PriceSentinel3000.Application.LiveTrading;
using PriceSentinel3000.Application.Sessions;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private Task ExecutePrimarySessionActionAsync()
    {
        if (IsReplayPaused)
        {
            ResumeReplay();
            return Task.CompletedTask;
        }

        return StartSelectedSessionAsync();
    }

    private async Task ExecuteSecondarySessionActionAsync()
    {
        if (EffectiveMode is TradingMode.Replay &&
            IsSessionRunning &&
            !IsReplayPaused)
        {
            PauseReplay();
            return;
        }

        await StopSessionAsync();
    }

    private async Task StartSelectedSessionAsync()
    {
        TradingSessionSettings settings = CreateSettings();
        IReadOnlyList<string> errors = TradingSessionSettingsValidator.Validate(settings);

        if (errors.Count > 0)
        {
            StatusMessage = $"Cannot start: {errors[0]}";
            AddActivity($"Configuration rejected: {errors[0]}", "WARNING");
            return;
        }

        if (!_journalReady)
        {
            InitializeJournal();

            if (!_journalReady)
            {
                StatusMessage = "Cannot start: the SQLite journal is unavailable.";
                AddActivity("Data session rejected because the SQLite journal is unavailable.", "ERROR");
                return;
            }
        }

        Symbol = settings.Symbol.Trim().ToUpperInvariant();
        var instrument = new Instrument(Symbol, AssetClass.Equity);

        if (SelectedMode is TradingMode.Live)
        {
            if (EffectiveMode is not TradingMode.Live || !LiveRiskAcknowledged)
            {
                StatusMessage = "Cannot start LIVE Trader until the LIVE warning is acknowledged.";
                AddActivity("LIVE Trader start rejected because LIVE mode is not authorized.", "WARNING");
                return;
            }
        }
        else
        {
            _modeState = _modeState.ActivateSafeMode(SelectedMode);
        }

        NotifyModeProperties();
        CancellationToken cancellationToken = _sessionCoordinator.Begin();
        _isStartingSession = true;
        StartSessionCommand.RaiseCanExecuteChanged();

        try
        {
            if (EffectiveMode is TradingMode.Replay)
            {
                await StartReplayAsync(instrument, settings, cancellationToken);
            }
            else
            {
                await StartRealtimeTraderAsync(
                    instrument,
                    settings,
                    EffectiveMode,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AddActivity($"Data engine error: {exception.Message}", "ERROR");
            StopActiveSession("ERROR", $"Data engine stopped: {exception.Message}");
        }
        finally
        {
            _isStartingSession = false;
            StartSessionCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task StartRealtimeTraderAsync(
        Instrument instrument,
        TradingSessionSettings settings,
        TradingMode mode,
        CancellationToken token)
    {
        bool isLive = mode is TradingMode.Live;
        StatusMessage = "Connecting to Robinhood. Complete the secure browser login if it opens.";
        SetMarketDataState("ROBINHOOD LOGIN", "AUTHORIZING", isConnected: false);
        await _marketDataSource.ConnectAsync(token);
        TradingSessionSettings sessionSettings = settings;
        LiveBrokerSnapshot? initialBroker = null;

        if (isLive)
        {
            StatusMessage = "Reconciling the Robinhood agentic account before arming LIVE execution...";
            initialBroker = await InitializeLiveBrokerAsync(instrument, token);
            sessionSettings = settings with
            {
                StartingBalance = initialBroker.Portfolio.TotalValue,
            };

            if (initialBroker.HasOpenOrder)
            {
                throw new InvalidOperationException(
                    "An open Robinhood order already exists for this symbol. Resolve it before starting LIVE Trader.");
            }

            if (initialBroker.Position.HasPosition)
            {
                throw new InvalidOperationException(
                    $"Robinhood already holds {initialBroker.Position.Quantity:0.######} {instrument.Symbol} shares. This v1 starts LIVE only from a flat position; manage the existing position in Robinhood first.");
            }

            DateTimeOffset tradingDayStartUtc = GetEasternTradingDayStartUtc(
                _timeProvider.GetUtcNow());
            IReadOnlyList<BrokerOrderSnapshot> ordersToday =
                await _liveBrokerGateway.GetOrdersCreatedSinceAsync(
                    _liveAccount!.AccountNumber,
                    tradingDayStartUtc,
                    token);
            int initialEntriesToday = ordersToday
                .Where(order => order.Side is BrokerOrderSide.Buy && order.FilledQuantity > 0m)
                .Select(order => string.IsNullOrWhiteSpace(order.BrokerOrderId)
                    ? order.ClientReferenceId.ToString("D")
                    : order.BrokerOrderId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            BrokerOrderSnapshot? latestSell = ordersToday
                .Where(order =>
                    order.Side is BrokerOrderSide.Sell &&
                    order.FilledQuantity > 0m &&
                    string.Equals(
                        order.Symbol,
                        instrument.Symbol,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(order => order.UpdatedAtUtc)
                .FirstOrDefault();
            if (latestSell is not null && latestSell.EffectiveAveragePrice is not > 0m)
            {
                throw new InvalidOperationException(
                    "The most recent LIVE sell has no usable fill price, so the re-entry price gate cannot be restored; execution remains disarmed.");
            }
            decimal dailyStartingEquity =
                _journal.GetLiveStartingBalanceSince(tradingDayStartUtc) ??
                initialBroker.Portfolio.TotalValue;
            if (dailyStartingEquity <= 0m)
            {
                throw new InvalidOperationException("The saved LIVE daily-equity baseline is invalid; execution remains disarmed.");
            }

            _liveExecutionEngine = new(
                sessionSettings,
                dailyStartingEquity,
                initialEntriesToday: initialEntriesToday,
                initialLastExitUtc: latestSell?.UpdatedAtUtc,
                initialLastExitPrice: latestSell?.EffectiveAveragePrice);
        }

        PrepareDataSession(instrument, sessionSettings, mode);

        if (initialBroker is not null)
        {
            UpdateLiveAccount(initialBroker, 0m);
            _modeState = _modeState.ArmLive();
            NotifyModeProperties();
            AddActivity(
                $"LIVE execution armed for agentic account {_liveAccount!.MaskedNumber}; buying power {initialBroker.Portfolio.BuyingPower:C2}. No order exists at startup.");
        }
        TimeSpan warmStart = TimeSpan.FromMinutes(
            Math.Min(settings.BufferMinutes, (int)MaximumWarmStart.TotalMinutes));
        _marketDataRequest = new(
            instrument,
            TimeSpan.FromSeconds(settings.QuotePollingSeconds),
            warmStart);
        bool isFirstUpdate = true;

        await foreach (RealtimeSessionUpdate update in _realtimeSessionRunner.RunAsync(
                           _marketDataRequest,
                           settings.ReconciliationSeconds,
                           settings.ReconciliationLookbackSeconds,
                           settings.ReconciliationCompletionDelaySeconds,
                           token))
        {
            int warmStartAdded = 0;
            if (update.WarmStart.Count > 0)
            {
                _journal.AppendQuotes(
                    _activeSession!.Id,
                    update.WarmStart,
                    QuoteIngestionKind.WarmStart);
                warmStartAdded = _ringBuffer!.Merge(update.WarmStart).Added;
                _chartRingBuffer!.Merge(update.WarmStart);
            }

            _journal.AppendQuotes(
                _activeSession!.Id,
                [update.Quote],
                QuoteIngestionKind.Live);
            _ringBuffer!.Merge([update.Quote]);
            _chartRingBuffer!.Merge([update.Quote]);

            if (update.Reconciliation.Count > 0)
            {
                _journal.AppendQuotes(
                    _activeSession.Id,
                    update.Reconciliation,
                    QuoteIngestionKind.Reconciliation);
                _ringBuffer.Merge(update.Reconciliation);
                _chartRingBuffer.Merge(update.Reconciliation);
            }

            SetQuoteMarketState(update.Quote);
            if (isLive)
            {
                await ProcessLiveObservationAsync(update.Quote, token);
            }
            else
            {
                ProcessPaperObservation(update.Quote);
            }

            RefreshMarketView();

            if (!isFirstUpdate)
            {
                continue;
            }

            isFirstUpdate = false;
            StatusMessage = isLive
                ? $"LIVE Trader is armed and watching real {instrument.Symbol} prices every {settings.QuotePollingSeconds} seconds."
                : $"Paper Trader is watching real {instrument.Symbol} prices every {settings.QuotePollingSeconds} seconds; order execution is paper-only.";
            AddActivity(
                isLive
                    ? $"LIVE Trader started with {warmStartAdded} real warm-start bars plus the current Robinhood quote; confirmed strategy actions can submit reviewed Robinhood market orders."
                    : $"Paper Trader started with {warmStartAdded} real warm-start bars plus the current Robinhood quote; no real orders can be sent.");
        }
    }

    private async Task StartReplayAsync(
        Instrument instrument,
        TradingSessionSettings settings,
        CancellationToken token)
    {
        if (!ReplaySchedule.TryParseLocalRange(
                settings.ReplayDate,
                settings.ReplayTime,
                settings.ReplayEndTime,
                out DateTimeOffset replayStart,
                out DateTimeOffset replayEnd))
        {
            throw new InvalidOperationException("The Replay date, start, or end time is invalid.");
        }

        StatusMessage = $"Loading real 15-second {instrument.Symbol} history from {replayStart:g}...";
        SetMarketDataState("ROBINHOOD LOGIN", "AUTHORIZING", isConnected: false);
        await _marketDataSource.ConnectAsync(token);
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        IReadOnlyList<MarketQuote> historicalQuotes =
            await _marketDataSource.GetReplayHistoryAsync(
                instrument,
                replayStart,
                replayEnd,
                observedAt,
                token);

        if (historicalQuotes.Count == 0)
        {
            SetMarketDataState("ROBINHOOD READY", "NO HISTORY");
            StatusMessage = $"Robinhood returned no {instrument.Symbol} trades from {replayStart:g} through {replayEnd:t}.";
            AddActivity($"Replay found no historical {instrument.Symbol} observations in the requested window.", "WARNING");
            return;
        }

        PrepareDataSession(instrument, settings, TradingMode.Replay);
        SetMarketDataState("ROBINHOOD HISTORY", "REPLAY");
        _strategyStateLabel = "REPLAYING";
        _strategyMessage = "Historical Robinhood prices are arriving as a new stream. Orders are simulated only.";
        NotifyStrategyProperties();
        DateTimeOffset firstSource = historicalQuotes[0].SourceTimestampUtc;
        DateTimeOffset lastSource = historicalQuotes[^1].SourceTimestampUtc;
        StatusMessage = $"Replaying {historicalQuotes.Count} real {instrument.Symbol} observations from {firstSource.ToLocalTime():g} at {settings.ReplaySpeed:0.#}x speed.";
        AddActivity(
            $"Historical Replay loaded {historicalQuotes.Count} Robinhood observations for the requested {replayStart:g} start through {lastSource.ToLocalTime():g}.");

        await foreach (ReplaySessionUpdate update in _replaySessionRunner.RunAsync(
                           historicalQuotes,
                           settings.ReplaySpeed,
                           token))
        {
            MarketQuote replayed = update.Quote with
            {
                ObservedAtUtc = _timeProvider.GetUtcNow(),
            };
            _journal.AppendQuotes(_activeSession!.Id, [replayed], QuoteIngestionKind.Replay);
            QuoteMergeResult merge = _ringBuffer!.Merge([replayed]);
            _chartRingBuffer!.Merge([replayed]);

            if (merge.Added + merge.Corrected == 0 && _ringBuffer.Count == 0)
            {
                throw new InvalidOperationException(
                    "Robinhood returned historical bars, but the replay buffer could not accept them.");
            }

            ProcessPaperObservation(replayed, allowHistoricalSource: true);
            RefreshMarketView();
            StatusMessage =
                $"Replaying {update.Index + 1}/{update.Total} real {instrument.Symbol} observations from {firstSource.ToLocalTime():g} at {settings.ReplaySpeed:0.#}x speed.";
        }

        JournalSummary summary = _journal.GetSummary(_activeSession!.Id);
        AddActivity($"Historical Replay completed after {summary.QuoteCount} real observations.");
        StopActiveSession(
            "COMPLETED",
            $"Replay completed for {instrument.Symbol}. The chart remains available for inspection.");
    }

    private void PrepareDataSession(
        Instrument instrument,
        TradingSessionSettings settings,
        TradingMode mode)
    {
        ReleaseReplayPause();
        _ringBuffer = new(instrument, TimeSpan.FromMinutes(settings.BufferMinutes));
        _chartRingBuffer = new(
            instrument,
            TimeSpan.FromMinutes(settings.BufferMinutes) + MaximumWarmStart);
        _paperTradingEngine = mode is TradingMode.Live
            ? null
            : new(instrument, settings);
        _liveOrderCoordinator.Reset();

        if (mode is not TradingMode.Live)
        {
            _liveExecutionEngine = null;
        }
        _marketDataRequest = null;
        _tradeMarkers.Clear();
        _chartScaleResetVersion++;
        OnPropertyChanged(nameof(ChartScaleResetVersion));
        ChartPoints.Clear();
        _hasMarketData = false;
        _currentPrice = "--";
        _bidAskDisplay = "-- / --";
        UpdatePaperAccount(new(
            settings.StartingBalance,
            settings.StartingBalance,
            settings.StartingBalance,
            0m,
            0m,
            0m,
            0m,
            0m,
            0,
            false));
        OnPropertyChanged(nameof(HasMarketData));
        OnPropertyChanged(nameof(CurrentPrice));
        OnPropertyChanged(nameof(BidAskDisplay));

        string settingsJson = JsonSerializer.Serialize(settings);
        _activeSession = _journal.StartSession(
            instrument,
            mode,
            settings.StartingBalance,
            settingsJson,
            _timeProvider.GetUtcNow());
        IsSessionRunning = true;
    }

    private void PauseReplay()
    {
        if (EffectiveMode is not TradingMode.Replay ||
            !IsSessionRunning ||
            IsReplayPaused)
        {
            return;
        }

        if (!_replaySessionRunner.Pause())
        {
            return;
        }

        IsReplayPaused = true;
        _strategyStateLabel = "PAUSED";
        _strategyMessage =
            "Replay is paused; the chart, buffer, and paper account are preserved.";
        NotifyStrategyProperties();
        StatusMessage =
            "Replay paused. Resume continues with the next historical observation.";
        AddActivity("Historical Replay paused.");
    }

    private void ResumeReplay()
    {
        if (!IsReplayPaused)
        {
            return;
        }

        _replaySessionRunner.Resume();
        IsReplayPaused = false;
        _strategyStateLabel = "REPLAYING";
        _strategyMessage =
            "Historical Robinhood prices are arriving as a new stream. Orders are simulated only.";
        NotifyStrategyProperties();
        StatusMessage = "Replay resumed from the next historical observation.";
        AddActivity("Historical Replay resumed.");
    }

    private void ReleaseReplayPause()
    {
        _replaySessionRunner.Resume();
        IsReplayPaused = false;
    }

    private async Task StopSessionAsync()
    {
        bool wasLive = EffectiveMode is TradingMode.Live;
        bool hadLiveOrderContext = _liveOrderCoordinator.HasActiveContext;
        _sessionCoordinator.Cancel();

        if (StartSessionCommand.ExecutionTask is { IsCompleted: false } sessionTask)
        {
            await sessionTask;
        }

        bool orderStopHandled = await CancelCoordinatedLiveOrderAsync();
        StopActiveSession(
            "STOPPED_BY_USER",
            orderStopHandled
                ? "LIVE session stopped; the active order was reconciled or cancellation was requested. Confirm its final state in Robinhood."
                : wasLive && hadLiveOrderContext
                    ? "LIVE session stopped, but order cancellation was not confirmed. Verify Robinhood immediately."
                    : wasLive
                        ? "LIVE Trader stopped and disarmed; no active PriceSentinel order was found."
                        : "Data session stopped.");
    }

    private async Task<bool> CancelCoordinatedLiveOrderAsync()
    {
        Instrument instrument = _ringBuffer?.Instrument ??
                                new Instrument(Symbol, AssetClass.Equity);
        LiveOrderOperationResult cancellation =
            await _liveOrderCoordinator.CancelActiveAsync(
                _liveAccount,
                _activeSession?.Id,
                instrument);
        if (cancellation.TerminalOrder is not null)
        {
            _liveExecutionEngine?.ObserveTerminalOrder(cancellation.TerminalOrder);
        }

        return cancellation.Handled;
    }

    private void StopActiveSession(string outcome, string statusMessage)
    {
        _sessionCoordinator.Cancel();
        ReleaseReplayPause();

        if (_activeSession is not null)
        {
            AddActivity($"{_activeSession.Mode} session finalized: {outcome}.");
            _journal.CompleteSession(
                _activeSession.Id,
                _timeProvider.GetUtcNow(),
                outcome);
            _activeSession = null;
        }

        IsSessionRunning = false;
        if (EffectiveMode is TradingMode.Live && SelectedMode is TradingMode.Live)
        {
            _modeState = _modeState.ActivateLiveDisarmed();
            NotifyModeProperties();
        }

        SetMarketDataState("ADAPTER OFFLINE", "OFFLINE", isConnected: false);
        _strategyStateLabel = "IDLE";
        _strategyMessage = ChartPoints.Count > 0
            ? "The captured chart remains visible; start another session when ready."
            : "Select Paper Trader or Replay to start the data engine.";
        NotifyStrategyProperties();
        StatusMessage = statusMessage;
    }
}
