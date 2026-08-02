using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;
using PriceSentinel3000.Infrastructure.MarketData;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan MaximumWarmStart = TimeSpan.FromMinutes(4);

    private readonly IMarketDataSource _marketDataSource;
    private readonly ITradingJournal _journal;
    private readonly JsonUserPreferencesStore _preferencesStore;
    private ModeState _modeState = ModeState.SafeDefault;
    private string _symbol;
    private decimal _startingBalance;
    private AmountBasis _positionSizeBasis;
    private decimal _positionSizeValue;
    private QuantityLimitMode _quantityLimitMode;
    private decimal _maximumQuantity;
    private bool _unlimitedEntries;
    private int _maximumEntriesPerDay;
    private AmountBasis _maximumDailyLossBasis;
    private decimal _maximumDailyLossValue;
    private StopLossBasis _stopLossBasis;
    private decimal _stopLossValue;
    private int _bufferMinutes;
    private int _quotePollingSeconds;
    private int _reconciliationSeconds;
    private int _reconciliationOverlapSeconds;
    private string _replayDate;
    private string _replayTime;
    private string _replayEndTime;
    private decimal _replaySpeed;
    private bool _isSessionRunning;
    private bool _isStartingSession;
    private bool _liveRiskAcknowledged;
    private bool _journalReady;
    private bool _hasMarketData;
    private bool _isMarketDataConnected;
    private bool _isChartManualScale;
    private string _statusMessage;
    private string _currentPrice = "--";
    private string _bidAskDisplay = "-- / --";
    private string _marketDataStatus = "ADAPTER OFFLINE";
    private string _marketDataStateLabel = "OFFLINE";
    private string _strategyMessage = "Select Paper Trader or Replay to start the data engine.";
    private string _strategyStateLabel = "IDLE";
    private string _strategyMetrics = "RSI --  |  MOM --  |  CONF --";
    private decimal _paperBuyingPower;
    private decimal _paperEquity;
    private decimal _paperPositionQuantity;
    private decimal _paperAveragePrice;
    private decimal _paperRealizedProfitLoss;
    private decimal _paperUnrealizedProfitLoss;
    private int _paperEntries;
    private CancellationTokenSource? _sessionCancellation;
    private JournalSession? _activeSession;
    private PriceRingBuffer? _ringBuffer;
    private MarketDataRequest? _marketDataRequest;
    private PaperTradingEngine? _paperTradingEngine;
    private readonly Dictionary<DateTimeOffset, ChartTradeMarker> _tradeMarkers = [];
    private bool _disposed;

    public MainViewModel()
        : this(
            RobinhoodMcpMarketDataSource.CreateDefault(),
            new SqliteTradingJournal(AppDataPaths.JournalDatabase),
            new JsonUserPreferencesStore(AppDataPaths.UserPreferences))
    {
    }

    internal MainViewModel(
        IMarketDataSource marketDataSource,
        ITradingJournal journal,
        JsonUserPreferencesStore preferencesStore)
    {
        _marketDataSource = marketDataSource;
        _journal = journal;
        _preferencesStore = preferencesStore;

        PaperTraderSettings defaults =
            _preferencesStore.Load() ?? PaperTraderSettings.Default;
        _symbol = defaults.Symbol;
        _startingBalance = defaults.StartingBalance;
        _positionSizeBasis = defaults.PositionSizeBasis;
        _positionSizeValue = defaults.PositionSizeValue;
        _quantityLimitMode = defaults.QuantityLimitMode;
        _maximumQuantity = defaults.MaximumQuantity;
        _unlimitedEntries = defaults.UnlimitedEntries;
        _maximumEntriesPerDay = defaults.MaximumEntriesPerDay;
        _maximumDailyLossBasis = defaults.MaximumDailyLossBasis;
        _maximumDailyLossValue = defaults.MaximumDailyLossValue;
        _stopLossBasis = defaults.StopLossBasis;
        _stopLossValue = defaults.StopLossValue;
        _bufferMinutes = defaults.BufferMinutes;
        _quotePollingSeconds = defaults.QuotePollingSeconds;
        _reconciliationSeconds = defaults.ReconciliationSeconds;
        _reconciliationOverlapSeconds = defaults.ReconciliationOverlapSeconds;
        _replayDate = defaults.ReplayDate;
        _replayTime = defaults.ReplayTime;
        _replayEndTime = defaults.ReplayEndTime;
        _replaySpeed = defaults.ReplaySpeed;
        _paperBuyingPower = defaults.StartingBalance;
        _paperEquity = defaults.StartingBalance;
        _statusMessage = "Choose Replay, Paper Trader, or LIVE on the rotary selector to begin.";

        PositionSizeOptions =
        [
            new("Fixed amount ($)", AmountBasis.FixedAmount),
            new("Account equity (%)", AmountBasis.AccountPercentage),
        ];
        DailyLossOptions =
        [
            new("Fixed amount ($)", AmountBasis.FixedAmount),
            new("Account equity (%)", AmountBasis.AccountPercentage),
        ];
        QuantityLimitOptions =
        [
            new("As many as possible", QuantityLimitMode.AsManyAsPossible),
            new("No more than", QuantityLimitMode.NoMoreThan),
        ];
        EntryLimitOptions =
        [
            new("As many as possible", true),
            new("No more than", false),
        ];
        StopLossOptions =
        [
            new(
                "Purchase price decline (%)",
                StopLossBasis.PurchasePriceDeclinePercentage),
            new(
                "Total position loss ($)",
                StopLossBasis.TotalPositionLossAmount),
        ];

        StartSessionCommand = new RelayCommand(
            StartSelectedSession,
            () => SelectedMode is TradingMode.PaperTrader or TradingMode.Replay &&
                  EffectiveMode == SelectedMode &&
                  !IsSessionRunning &&
                  !_isStartingSession);
        StopSessionCommand = new RelayCommand(StopSession, () => IsSessionRunning);

        RebuildBufferSegments();
        InitializeJournal();
        AddActivity("Application started with operating mode OFF.");
        AddActivity(
            _journalReady
                ? "Stage 5 paper strategy loaded; SQLite journal is ready."
                : "Stage 5 paper strategy loaded; SQLite journal could not be initialized.",
            _journalReady ? "INFO" : "ERROR");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SelectionOption<AmountBasis>> PositionSizeOptions { get; }
    public IReadOnlyList<SelectionOption<QuantityLimitMode>> QuantityLimitOptions { get; }
    public IReadOnlyList<SelectionOption<bool>> EntryLimitOptions { get; }
    public IReadOnlyList<SelectionOption<AmountBasis>> DailyLossOptions { get; }
    public IReadOnlyList<SelectionOption<StopLossBasis>> StopLossOptions { get; }

    public ObservableCollection<BufferSegmentViewModel> BufferSegments { get; } = [];
    public ObservableCollection<ActivityEntryViewModel> ActivityLog { get; } = [];
    public ObservableCollection<PricePointViewModel> ChartPoints { get; } = [];

    public RelayCommand StartSessionCommand { get; }
    public RelayCommand StopSessionCommand { get; }

    public TradingMode SelectedMode => _modeState.SelectedMode;
    public TradingMode EffectiveMode => _modeState.EffectiveMode;
    public string SelectedModeLabel => FormatMode(SelectedMode);
    public string EffectiveModeLabel => FormatMode(EffectiveMode);
    public bool IsOffSelected => SelectedMode is TradingMode.Off;
    public bool IsReplaySelected => SelectedMode is TradingMode.Replay;
    public bool IsPaperTraderSelected => SelectedMode is TradingMode.PaperTrader;
    public bool IsLiveSelected => SelectedMode is TradingMode.Live;
    public bool LiveArmed => _modeState.LiveArmed;
    public bool LiveRiskAcknowledged => _liveRiskAcknowledged;
    public string BrokerExecutionLabel => EffectiveMode switch
    {
        TradingMode.PaperTrader => "PAPER ONLY",
        TradingMode.Live when LiveArmed => "LIVE ARMED",
        TradingMode.Live => "LIVE DISARMED",
        _ => "DISABLED",
    };
    public string BrokerExecutionForeground => EffectiveMode switch
    {
        TradingMode.PaperTrader => "#5EE6B1",
        TradingMode.Live => "#FF8A78",
        _ => "#8EA0B7",
    };
    public string MarketDataStatus => _marketDataStatus;
    public string MarketDataStateLabel => _marketDataStateLabel;
    public string CurrentPrice => _currentPrice;
    public string BidAskDisplay => _bidAskDisplay;
    public bool IsChartManualScale
    {
        get => _isChartManualScale;
        set
        {
            if (SetField(ref _isChartManualScale, value))
            {
                OnPropertyChanged(nameof(ChartScaleLabel));
            }
        }
    }
    public string ChartScaleLabel => IsChartManualScale
        ? "SCALE: MANUAL"
        : "SCALE: AUTO";
    public bool HasMarketData => _hasMarketData;
    public string StrategyMessage => _strategyMessage;
    public string StrategyStateLabel => _strategyStateLabel;
    public string StrategyMetrics => _strategyMetrics;
    public string JournalStatus => _journalReady ? "SQLITE WAL" : "OFFLINE";
    public string PriceActionCaption => EffectiveMode is TradingMode.Replay
        ? "15 SECOND REPLAY CANDLES"
        : $"15 SECOND CANDLES · {QuotePollingSeconds} SECOND UPDATES";
    public string PrimaryActionLabel =>
        SelectedMode is TradingMode.Replay ? "START REPLAY" : "START PAPER TRADER";
    public string SessionStateLabel => EffectiveMode is TradingMode.Off
        ? "OFF"
        : EffectiveMode is TradingMode.Live && !LiveArmed ? "DISARMED"
        : IsSessionRunning ? "RUNNING" : "READY";
    public string SessionStateBackground => EffectiveMode is TradingMode.Live && !LiveArmed
        ? "#3B211E"
        : EffectiveMode is TradingMode.Off ? "#202B39" : "#123528";
    public string SessionStateBorder => EffectiveMode is TradingMode.Live && !LiveArmed
        ? "#7F3C34"
        : EffectiveMode is TradingMode.Off ? "#3A4B61" : "#24684C";
    public string SessionStateForeground => EffectiveMode is TradingMode.Live && !LiveArmed
        ? "#FF9B8C"
        : EffectiveMode is TradingMode.Off ? "#A8B6C7" : "#5EE6B1";
    public string MarketDataStatusBackground =>
        _isMarketDataConnected ? "#123528" : "#34251A";
    public string MarketDataStatusBorder =>
        _isMarketDataConnected ? "#24684C" : "#7B5426";
    public string MarketDataStatusForeground =>
        _isMarketDataConnected ? "#5EE6B1" : "#F4B45E";
    public string SymbolDisplay => string.IsNullOrWhiteSpace(Symbol)
        ? "—"
        : Symbol.Trim().ToUpperInvariant();
    public string BuyingPowerDisplay =>
        _paperBuyingPower.ToString("C", CultureInfo.CurrentCulture);
    public string AccountEquityDisplay =>
        _paperEquity.ToString("C", CultureInfo.CurrentCulture);
    public string PositionDisplay => _paperPositionQuantity <= 0m
        ? "FLAT"
        : $"{_paperPositionQuantity:0.######} @ {_paperAveragePrice:C2}";
    public string ProfitLossDisplay =>
        $"{_paperRealizedProfitLoss:+$0.00;-$0.00;$0.00} / {_paperUnrealizedProfitLoss:+$0.00;-$0.00;$0.00}";
    public string EntriesDisplay => _paperEntries.ToString(CultureInfo.InvariantCulture);
    public bool IsQuantityLimited => QuantityLimitMode is QuantityLimitMode.NoMoreThan;
    public bool IsEntryLimited => !UnlimitedEntries;
    public string BufferCaption => $"{BufferSegments.Count} × 1 MINUTE BLOCKS";

    public string Symbol
    {
        get => _symbol;
        set
        {
            string normalized = value?.ToUpperInvariant() ?? string.Empty;

            if (SetPreferenceField(ref _symbol, normalized))
            {
                OnPropertyChanged(nameof(SymbolDisplay));
            }
        }
    }

    public decimal StartingBalance
    {
        get => _startingBalance;
        set
        {
            if (SetPreferenceField(ref _startingBalance, value))
            {
                if (!IsSessionRunning)
                {
                    _paperBuyingPower = value;
                    _paperEquity = value;
                    OnPropertyChanged(nameof(AccountEquityDisplay));
                }

                OnPropertyChanged(nameof(BuyingPowerDisplay));
            }
        }
    }

    public AmountBasis PositionSizeBasis
    {
        get => _positionSizeBasis;
        set => SetPreferenceField(ref _positionSizeBasis, value);
    }

    public decimal PositionSizeValue
    {
        get => _positionSizeValue;
        set => SetPreferenceField(ref _positionSizeValue, value);
    }

    public QuantityLimitMode QuantityLimitMode
    {
        get => _quantityLimitMode;
        set
        {
            if (SetPreferenceField(ref _quantityLimitMode, value))
            {
                OnPropertyChanged(nameof(IsQuantityLimited));
            }
        }
    }

    public decimal MaximumQuantity
    {
        get => _maximumQuantity;
        set => SetPreferenceField(ref _maximumQuantity, value);
    }

    public bool UnlimitedEntries
    {
        get => _unlimitedEntries;
        set
        {
            if (SetPreferenceField(ref _unlimitedEntries, value))
            {
                OnPropertyChanged(nameof(IsEntryLimited));
            }
        }
    }

    public int MaximumEntriesPerDay
    {
        get => _maximumEntriesPerDay;
        set => SetPreferenceField(ref _maximumEntriesPerDay, value);
    }

    public AmountBasis MaximumDailyLossBasis
    {
        get => _maximumDailyLossBasis;
        set => SetPreferenceField(ref _maximumDailyLossBasis, value);
    }

    public decimal MaximumDailyLossValue
    {
        get => _maximumDailyLossValue;
        set => SetPreferenceField(ref _maximumDailyLossValue, value);
    }

    public StopLossBasis StopLossBasis
    {
        get => _stopLossBasis;
        set => SetPreferenceField(ref _stopLossBasis, value);
    }

    public decimal StopLossValue
    {
        get => _stopLossValue;
        set => SetPreferenceField(ref _stopLossValue, value);
    }

    public int BufferMinutes
    {
        get => _bufferMinutes;
        set
        {
            if (SetPreferenceField(ref _bufferMinutes, value))
            {
                RebuildBufferSegments();
            }
        }
    }

    public int QuotePollingSeconds
    {
        get => _quotePollingSeconds;
        set
        {
            if (SetPreferenceField(ref _quotePollingSeconds, value))
            {
                OnPropertyChanged(nameof(PriceActionCaption));
            }
        }
    }

    public int ReconciliationSeconds
    {
        get => _reconciliationSeconds;
        set => SetPreferenceField(ref _reconciliationSeconds, value);
    }

    public int ReconciliationOverlapSeconds
    {
        get => _reconciliationOverlapSeconds;
        set => SetPreferenceField(ref _reconciliationOverlapSeconds, value);
    }

    public string ReplayDate
    {
        get => _replayDate;
        set => SetPreferenceField(ref _replayDate, value);
    }

    public string ReplayTime
    {
        get => _replayTime;
        set => SetPreferenceField(ref _replayTime, value);
    }

    public string ReplayEndTime
    {
        get => _replayEndTime;
        set => SetPreferenceField(ref _replayEndTime, value);
    }

    public decimal ReplaySpeed
    {
        get => _replaySpeed;
        set => SetPreferenceField(ref _replaySpeed, value);
    }

    public bool IsSessionRunning
    {
        get => _isSessionRunning;
        private set
        {
            if (SetField(ref _isSessionRunning, value))
            {
                OnPropertyChanged(nameof(SessionStateLabel));
                StartSessionCommand.RaiseCanExecuteChanged();
                StopSessionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public void RequestModeSelection(TradingMode mode)
    {
        if (IsSessionRunning || _isStartingSession)
        {
            StopActiveSession("MODE_CHANGED", "The active data session was stopped before changing modes.");
        }

        _modeState = mode is TradingMode.Live
            ? _modeState.Select(mode)
            : _modeState.ActivateSafeMode(mode);

        if (mode is not TradingMode.Live)
        {
            StatusMessage = mode switch
            {
                TradingMode.Off => "System is OFF. Choose Replay, Paper Trader, or LIVE to begin.",
                TradingMode.Replay => "Replay selected. Choose a ticker and local date/time, then stream that history as new observations.",
                TradingMode.PaperTrader => "Paper Trader selected. Configure the paper account, then start the real Robinhood price feed.",
                _ => StatusMessage,
            };
            AddActivity($"{FormatMode(mode)} mode selected.");
        }

        NotifyModeProperties();
    }

    public void CancelModeSelection()
    {
        _modeState = _modeState.CancelSelection();
        StatusMessage = $"LIVE selection cancelled. Remaining in {EffectiveModeLabel}.";
        AddActivity("LIVE selection cancelled; effective mode was preserved.");
        NotifyModeProperties();
    }

    public async Task AcknowledgeLiveRiskAsync()
    {
        _liveRiskAcknowledged = true;
        _modeState = _modeState.ActivateLiveDisarmed();
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _sessionCancellation = new();
        _isStartingSession = true;
        StatusMessage = "LIVE mode is effective and disarmed. Verifying the Robinhood connection...";
        AddActivity("LIVE risk acknowledged; verifying the startup Robinhood connection.");
        OnPropertyChanged(nameof(LiveRiskAcknowledged));
        NotifyModeProperties();

        try
        {
            SetMarketDataState("ROBINHOOD READY", "VERIFYING", isConnected: false);
            await _marketDataSource.ConnectAsync(_sessionCancellation.Token);
            SetMarketDataState("ROBINHOOD READY", "CONNECTED");
            StatusMessage = "Robinhood remains connected. LIVE order execution is disarmed in this stage.";
            AddActivity("Robinhood connection verified; LIVE execution remains disarmed.");
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                StatusMessage = "Robinhood login was cancelled.";
                AddActivity("Robinhood login was cancelled.", "WARNING");
            }
        }
        catch (Exception exception)
        {
            SetMarketDataState("ADAPTER OFFLINE", "OFFLINE", isConnected: false);
            StatusMessage = $"Robinhood login failed: {exception.Message}";
            AddActivity($"Robinhood login failed: {exception.Message}", "ERROR");
        }
        finally
        {
            _isStartingSession = false;
            StartSessionCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task ConnectRobinhoodAtStartupAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StatusMessage = "Connecting to Robinhood before PriceSentinel starts...";
        SetMarketDataState("ROBINHOOD LOGIN", "AUTHORIZING", isConnected: false);

        try
        {
            await _marketDataSource.ConnectAsync(cancellationToken);
            SetMarketDataState("ROBINHOOD READY", "CONNECTED");
            StatusMessage = "Robinhood is connected. Choose Replay, Paper Trader, or LIVE to begin.";
            AddActivity("Robinhood connected at startup; operating mode remains OFF.");
        }
        catch
        {
            SetMarketDataState("ADAPTER OFFLINE", "OFFLINE", isConnected: false);
            StatusMessage = "Robinhood is required. Retry LOGIN or exit PriceSentinel.";
            throw;
        }
    }

    public async Task<bool> TryRestoreRobinhoodAtStartupAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_marketDataSource is not ICachedAuthenticationMarketDataSource cached ||
            !cached.HasCachedAuthentication)
        {
            return false;
        }

        StatusMessage = "Restoring the saved Robinhood connection...";
        SetMarketDataState("ROBINHOOD LOGIN", "RESTORING", isConnected: false);

        if (!await cached.TryConnectUsingCachedAuthenticationAsync(
                cancellationToken))
        {
            SetMarketDataState("ADAPTER OFFLINE", "OFFLINE", isConnected: false);
            StatusMessage = "The saved Robinhood connection needs authorization.";
            return false;
        }

        SetMarketDataState("ROBINHOOD READY", "CONNECTED");
        StatusMessage = "Robinhood is connected. Choose Replay, Paper Trader, or LIVE to begin.";
        AddActivity("Saved Robinhood connection restored; operating mode remains OFF.");
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SavePreferences();
        _sessionCancellation?.Cancel();

        if (_activeSession is not null)
        {
            StopActiveSession("APPLICATION_CLOSED", "Application closed; the data session was finalized.");
        }

        _sessionCancellation?.Dispose();
        _marketDataSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _journal.Dispose();
    }

    public void SavePreferences() =>
        _preferencesStore.Save(CreateSettings());

    private async void StartSelectedSession()
    {
        PaperTraderSettings settings = CreateSettings();
        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

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
        _modeState = _modeState.ActivateSafeMode(SelectedMode);
        NotifyModeProperties();
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _sessionCancellation = new();
        _isStartingSession = true;
        StartSessionCommand.RaiseCanExecuteChanged();

        try
        {
            if (EffectiveMode is TradingMode.Replay)
            {
                await StartReplayAsync(instrument, settings);
            }
            else
            {
                await StartPaperTraderAsync(instrument, settings);
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

    private async Task StartPaperTraderAsync(
        Instrument instrument,
        PaperTraderSettings settings)
    {
        CancellationToken token = _sessionCancellation!.Token;
        StatusMessage = "Connecting to Robinhood. Complete the secure browser login if it opens.";
        SetMarketDataState("ROBINHOOD LOGIN", "AUTHORIZING", isConnected: false);
        await _marketDataSource.ConnectAsync(token);
        PrepareDataSession(instrument, settings, TradingMode.PaperTrader);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan warmStart = TimeSpan.FromMinutes(
            Math.Min(settings.BufferMinutes, (int)MaximumWarmStart.TotalMinutes));
        _marketDataRequest = new(
            instrument,
            TimeSpan.FromSeconds(settings.QuotePollingSeconds),
            warmStart);
        IReadOnlyList<MarketQuote> history = await _marketDataSource.GetHistoryAsync(
            _marketDataRequest,
            now - warmStart,
            now,
            now,
            token);
        _journal.AppendQuotes(_activeSession!.Id, history, QuoteIngestionKind.WarmStart);
        QuoteMergeResult warmMerge = _ringBuffer!.Merge(history);
        MarketQuote current = await _marketDataSource.GetQuoteAsync(
            _marketDataRequest,
            DateTimeOffset.UtcNow,
            token);
        _journal.AppendQuotes(_activeSession.Id, [current], QuoteIngestionKind.Live);
        _ringBuffer.Merge([current]);
        SetQuoteMarketState(current);
        ProcessPaperObservation(current);
        RefreshMarketView();
        StatusMessage = $"Paper Trader is watching real {instrument.Symbol} prices every {settings.QuotePollingSeconds} seconds; order execution is paper-only.";
        AddActivity(
            $"Paper Trader started with {warmMerge.Added} real warm-start bars plus the current Robinhood quote; no real orders can be sent.");

        DateTimeOffset nextReconciliation =
            DateTimeOffset.UtcNow.AddSeconds(settings.ReconciliationSeconds);

        while (true)
        {
            await Task.Delay(_marketDataRequest.PollingInterval, token);
            DateTimeOffset observedAt = DateTimeOffset.UtcNow;
            MarketQuote quote = await _marketDataSource.GetQuoteAsync(
                _marketDataRequest,
                observedAt,
                token);
            _journal.AppendQuotes(_activeSession!.Id, [quote], QuoteIngestionKind.Live);
            _ringBuffer.Merge([quote]);
            SetQuoteMarketState(quote);

            if (observedAt >= nextReconciliation)
            {
                DateTimeOffset from =
                    observedAt.AddSeconds(
                        -(settings.ReconciliationSeconds +
                          settings.ReconciliationOverlapSeconds));
                IReadOnlyList<MarketQuote> verification = await _marketDataSource.GetHistoryAsync(
                    _marketDataRequest,
                    from,
                    observedAt,
                    observedAt,
                    token);
                _journal.AppendQuotes(
                    _activeSession.Id,
                    verification,
                    QuoteIngestionKind.Reconciliation);
                QuoteMergeResult merge = _ringBuffer.Merge(verification);
                AddActivity(
                    $"Robinhood history reconciled {verification.Count} bars: {merge.Duplicates} verified, {merge.Corrected} corrected, {merge.Added} gaps filled.");
                nextReconciliation =
                    observedAt.AddSeconds(settings.ReconciliationSeconds);
            }

            ProcessPaperObservation(quote);
            RefreshMarketView();
        }
    }

    private async Task StartReplayAsync(
        Instrument instrument,
        PaperTraderSettings settings)
    {
        CancellationToken token = _sessionCancellation!.Token;
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
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
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

        for (int index = 0; index < historicalQuotes.Count; index++)
        {
            if (index > 0)
            {
                await Task.Delay(CalculateReplayDelay(
                    historicalQuotes[index - 1].SourceTimestampUtc,
                    historicalQuotes[index].SourceTimestampUtc,
                    settings.ReplaySpeed), token);
            }

            MarketQuote replayed = historicalQuotes[index] with
            {
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };
            _journal.AppendQuotes(_activeSession!.Id, [replayed], QuoteIngestionKind.Replay);
            QuoteMergeResult merge = _ringBuffer!.Merge([replayed]);

            if (merge.Added + merge.Corrected == 0 && _ringBuffer.Count == 0)
            {
                throw new InvalidOperationException(
                    "Robinhood returned historical bars, but the replay buffer could not accept them.");
            }

            ProcessPaperObservation(replayed, allowHistoricalSource: true);
            RefreshMarketView();
            StatusMessage =
                $"Replaying {index + 1}/{historicalQuotes.Count} real {instrument.Symbol} observations from {firstSource.ToLocalTime():g} at {settings.ReplaySpeed:0.#}x speed.";
        }

        JournalSummary summary = _journal.GetSummary(_activeSession!.Id);
        AddActivity($"Historical Replay completed after {summary.QuoteCount} real observations.");
        StopActiveSession(
            "COMPLETED",
            $"Replay completed for {instrument.Symbol}. The chart remains available for inspection.");
    }

    private void PrepareDataSession(
        Instrument instrument,
        PaperTraderSettings settings,
        TradingMode mode)
    {
        _ringBuffer = new(instrument, TimeSpan.FromMinutes(settings.BufferMinutes));
        _paperTradingEngine = new(instrument, settings);
        _marketDataRequest = null;
        _tradeMarkers.Clear();
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

        foreach (BufferSegmentViewModel segment in BufferSegments)
        {
            segment.Update(new(
                segment.Number,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                0,
                null,
                null,
                null,
                null,
                null,
                PriceDirection.Empty));
        }

        string settingsJson = JsonSerializer.Serialize(settings);
        _activeSession = _journal.StartSession(
            instrument,
            mode,
            settings.StartingBalance,
            settingsJson,
            DateTimeOffset.UtcNow);
        IsSessionRunning = true;
    }

    private void StopSession() =>
        StopActiveSession("STOPPED_BY_USER", "Data session stopped. No market orders were sent.");

    private void StopActiveSession(string outcome, string statusMessage)
    {
        _sessionCancellation?.Cancel();

        if (_activeSession is not null)
        {
            AddActivity($"{_activeSession.Mode} session finalized: {outcome}.");
            _journal.CompleteSession(_activeSession.Id, DateTimeOffset.UtcNow, outcome);
            _activeSession = null;
        }

        IsSessionRunning = false;
        SetMarketDataState("ADAPTER OFFLINE", "OFFLINE", isConnected: false);
        _strategyStateLabel = "IDLE";
        _strategyMessage = ChartPoints.Count > 0
            ? "The captured chart remains visible; start another session when ready."
            : "Select Paper Trader or Replay to start the data engine.";
        NotifyStrategyProperties();
        StatusMessage = statusMessage;
    }

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

        ChartPoints.Clear();
        IReadOnlyList<PriceCandle> candles = PriceCandleAggregator.Aggregate(
            snapshot,
            TimeSpan.FromSeconds(15));

        foreach (PriceCandle candle in candles)
        {
            MarketQuote? markedQuote = snapshot.LastOrDefault(quote =>
                quote.SourceTimestampUtc >= candle.StartsAtUtc &&
                quote.SourceTimestampUtc < candle.EndsAtUtc &&
                _tradeMarkers.ContainsKey(quote.SourceTimestampUtc));
            ChartTradeMarker marker = markedQuote is null
                ? ChartTradeMarker.None
                : _tradeMarkers[markedQuote.SourceTimestampUtc];
            ChartPoints.Add(new(
                candle.StartsAtUtc,
                candle.Open,
                candle.High,
                candle.Low,
                candle.Close,
                marker,
                markedQuote?.Last));
        }

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

        IReadOnlyList<MinuteBlock> blocks = MinuteBlockAnalyzer.Analyze(
            snapshot,
            BufferSegments.Count,
            latest.SourceTimestampUtc);

        for (int index = 0; index < BufferSegments.Count; index++)
        {
            BufferSegments[index].Update(blocks[index]);
        }
    }

    private void SetMarketDataState(
        string headerStatus,
        string stateLabel,
        bool isConnected = true)
    {
        _marketDataStatus = headerStatus;
        _marketDataStateLabel = stateLabel;
        _isMarketDataConnected = isConnected;

        OnPropertyChanged(nameof(MarketDataStatus));
        OnPropertyChanged(nameof(MarketDataStateLabel));
        OnPropertyChanged(nameof(MarketDataStatusBackground));
        OnPropertyChanged(nameof(MarketDataStatusBorder));
        OnPropertyChanged(nameof(MarketDataStatusForeground));
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

    private static TimeSpan CalculateReplayDelay(
        DateTimeOffset previous,
        DateTimeOffset current,
        decimal speed)
    {
        double milliseconds =
            (current - previous).TotalMilliseconds / decimal.ToDouble(speed);
        return TimeSpan.FromMilliseconds(
            Math.Clamp(double.IsFinite(milliseconds) ? milliseconds : 20d,
                20d,
                2_000d));
    }

    private static string FormatMode(TradingMode mode) => mode switch
    {
        TradingMode.PaperTrader => "PAPER TRADER",
        _ => mode.ToString().ToUpperInvariant(),
    };

    private PaperTraderSettings CreateSettings() => new()
    {
        Symbol = Symbol.Trim().ToUpperInvariant(),
        StartingBalance = StartingBalance,
        PositionSizeBasis = PositionSizeBasis,
        PositionSizeValue = PositionSizeValue,
        QuantityLimitMode = QuantityLimitMode,
        MaximumQuantity = MaximumQuantity,
        UnlimitedEntries = UnlimitedEntries,
        MaximumEntriesPerDay = MaximumEntriesPerDay,
        MaximumDailyLossBasis = MaximumDailyLossBasis,
        MaximumDailyLossValue = MaximumDailyLossValue,
        StopLossBasis = StopLossBasis,
        StopLossValue = StopLossValue,
        BufferMinutes = BufferMinutes,
        QuotePollingSeconds = QuotePollingSeconds,
        ReconciliationSeconds = ReconciliationSeconds,
        ReconciliationOverlapSeconds = ReconciliationOverlapSeconds,
        ReplayDate = ReplayDate,
        ReplayTime = ReplayTime,
        ReplayEndTime = ReplayEndTime,
        ReplaySpeed = ReplaySpeed,
    };

    private void RebuildBufferSegments()
    {
        int segmentCount = Math.Clamp(BufferMinutes, 1, 15);
        BufferSegments.Clear();

        for (int number = 1; number <= segmentCount; number++)
        {
            BufferSegments.Add(new(number));
        }

        OnPropertyChanged(nameof(BufferCaption));
    }

    private void InitializeJournal()
    {
        try
        {
            _journal.Initialize();
            _journalReady = true;
        }
        catch
        {
            _journalReady = false;
        }

        OnPropertyChanged(nameof(JournalStatus));
    }

    private void AddActivity(string message, string level = "INFO")
    {
        DateTimeOffset now = DateTimeOffset.Now;
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
        OnPropertyChanged(nameof(LiveArmed));
        OnPropertyChanged(nameof(BrokerExecutionLabel));
        OnPropertyChanged(nameof(BrokerExecutionForeground));
        OnPropertyChanged(nameof(PriceActionCaption));
        OnPropertyChanged(nameof(PrimaryActionLabel));
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
        AddActivity(
            $"PAPER {result.Fill.Side.ToString().ToUpperInvariant()} filled {result.Fill.Quantity:0.######} {SymbolDisplay} @ {result.Fill.Price:C2}{profitLoss}. " +
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

    private static bool IsFreshObservation(MarketQuote quote) =>
        quote.ObservedAtUtc - quote.SourceTimestampUtc <= TimeSpan.FromMinutes(2);

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private bool SetPreferenceField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!SetField(ref field, value, propertyName))
        {
            return false;
        }

        SavePreferences();
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
