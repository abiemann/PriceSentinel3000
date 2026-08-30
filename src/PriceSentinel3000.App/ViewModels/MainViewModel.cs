using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PriceSentinel3000.Application.Configuration;
using PriceSentinel3000.Application.LiveTrading;
using PriceSentinel3000.Application.Sessions;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.PaperTrading;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IMarketDataSource _marketDataSource;
    private readonly ICachedAuthenticationMarketDataSource _cachedAuthentication;
    private readonly ILiveBrokerGateway _liveBrokerGateway;
    private readonly ITradingJournal _journal;
    private readonly IUserPreferencesStore _preferencesStore;
    private readonly TimeProvider _timeProvider;
    private readonly TradingSessionCoordinator _sessionCoordinator = new();
    private readonly RealtimeSessionRunner _realtimeSessionRunner;
    private readonly ReplaySessionRunner _replaySessionRunner;
    private readonly LiveOrderCoordinator _liveOrderCoordinator;
    private ModeState _modeState = ModeState.SafeDefault;
    private string _symbol;
    private decimal _startingBalance;
    private bool _tradesSettleImmediately;
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
    private int _reconciliationLookbackSeconds;
    private int _reconciliationCompletionDelaySeconds;
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
    private bool _showRsi;
    private bool _isChartManualScale;
    private int _chartScaleResetVersion;
    private int _chartCandleIntervalSeconds;
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
    private bool _isReplayPaused;
    private JournalSession? _activeSession;
    private PriceRingBuffer? _ringBuffer;
    private PriceRingBuffer? _chartRingBuffer;
    private MarketDataRequest? _marketDataRequest;
    private PaperTradingEngine? _paperTradingEngine;
    private LiveExecutionEngine? _liveExecutionEngine;
    private BrokerAccount? _liveAccount;
    private EquityTradability? _liveTradability;
    private readonly Dictionary<DateTimeOffset, ChartTradeMarker> _tradeMarkers = [];
    private Task? _shutdownTask;
    private bool _disposed;

    internal MainViewModel(
        IMarketDataSource marketDataSource,
        ICachedAuthenticationMarketDataSource cachedAuthentication,
        ILiveBrokerGateway liveBrokerGateway,
        ITradingJournal journal,
        IUserPreferencesStore preferencesStore,
        TimeProvider? timeProvider = null)
    {
        _marketDataSource = marketDataSource ??
            throw new ArgumentNullException(nameof(marketDataSource));
        _cachedAuthentication = cachedAuthentication ??
            throw new ArgumentNullException(nameof(cachedAuthentication));
        _liveBrokerGateway = liveBrokerGateway ??
            throw new ArgumentNullException(nameof(liveBrokerGateway));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _preferencesStore = preferencesStore ??
            throw new ArgumentNullException(nameof(preferencesStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _realtimeSessionRunner = new(_marketDataSource, _timeProvider);
        _replaySessionRunner = new(_timeProvider);
        _liveOrderCoordinator = new(_liveBrokerGateway, _journal, _timeProvider);
        _liveOrderCoordinator.Activity += activity =>
            AddActivity(activity.Message, activity.Level);
        _liveOrderCoordinator.DisarmRequested += DisarmLiveExecution;

        TradingSessionSettings defaults =
            _preferencesStore.Load() ?? TradingSessionSettings.Default;
        _symbol = defaults.Symbol;
        _startingBalance = defaults.StartingBalance;
        _tradesSettleImmediately = defaults.TradesSettleImmediately;
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
        _chartCandleIntervalSeconds =
            defaults.ChartCandleIntervalSeconds is 15 or 30 or 60 or 120
                ? defaults.ChartCandleIntervalSeconds
                : 15;
        _reconciliationSeconds = defaults.ReconciliationSeconds;
        _reconciliationLookbackSeconds = defaults.ReconciliationLookbackSeconds;
        _reconciliationCompletionDelaySeconds =
            defaults.ReconciliationCompletionDelaySeconds;
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
        ChartCandleIntervalOptions =
        [
            new("15 sec", 15),
            new("30 sec", 30),
            new("60 sec", 60),
            new("2 min", 120),
        ];

        StartSessionCommand = new AsyncRelayCommand(
            ExecutePrimarySessionActionAsync,
            () => IsReplayPaused ||
                  (SelectedMode is TradingMode.PaperTrader or TradingMode.Replay or TradingMode.Live &&
                   EffectiveMode == SelectedMode &&
                   !IsSessionRunning &&
                   !_isStartingSession),
            () => IsReplayPaused);
        StopSessionCommand = new AsyncRelayCommand(
            ExecuteSecondarySessionActionAsync,
            () => IsSessionRunning || _isStartingSession);
        ClearActivityLogCommand = new RelayCommand(ActivityLog.Clear);

        InitializeJournal();
        AddActivity("Application started with operating mode OFF.");
        AddActivity(
            _journalReady
                ? "Trading strategy loaded; SQLite journal is ready."
                : "Trading strategy loaded; SQLite journal could not be initialized.",
            _journalReady ? "INFO" : "ERROR");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SelectionOption<AmountBasis>> PositionSizeOptions { get; }
    public IReadOnlyList<SelectionOption<QuantityLimitMode>> QuantityLimitOptions { get; }
    public IReadOnlyList<SelectionOption<bool>> EntryLimitOptions { get; }
    public IReadOnlyList<SelectionOption<AmountBasis>> DailyLossOptions { get; }
    public IReadOnlyList<SelectionOption<StopLossBasis>> StopLossOptions { get; }
    public IReadOnlyList<SelectionOption<int>> ChartCandleIntervalOptions { get; }

    public ObservableCollection<ActivityEntryViewModel> ActivityLog { get; } = [];
    public ObservableCollection<PricePointViewModel> ChartPoints { get; } = [];

    public AsyncRelayCommand StartSessionCommand { get; }
    public AsyncRelayCommand StopSessionCommand { get; }
    public RelayCommand ClearActivityLogCommand { get; }

    public TradingMode SelectedMode => _modeState.SelectedMode;
    public TradingMode EffectiveMode => _modeState.EffectiveMode;
    public string SelectedModeLabel => FormatMode(SelectedMode);
    public string EffectiveModeLabel => FormatMode(EffectiveMode);
    public bool IsOffSelected => SelectedMode is TradingMode.Off;
    public bool IsReplaySelected => SelectedMode is TradingMode.Replay;
    public bool IsPaperTraderSelected => SelectedMode is TradingMode.PaperTrader;
    public bool IsLiveSelected => SelectedMode is TradingMode.Live;
    public bool IsLiveEffective => EffectiveMode is TradingMode.Live;
    public bool IsConfigurationPanelExpanded =>
        EffectiveMode is not TradingMode.Off;
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
        TradingMode.Live when LiveArmed => "#5EE6B1",
        TradingMode.Live => "#FF8A78",
        _ => "#8EA0B7",
    };
    public string AccountPanelCaption => IsLiveEffective ? "LIVE ACCOUNT" : "PAPER ACCOUNT";
    public string AccountBalanceCaption => IsLiveEffective ? "Account equity (API)" : "Starting balance";
    public string SessionEquityCaption => IsLiveEffective ? "Account equity" : "Paper equity";
    public bool IsStartingBalanceEditable => !IsLiveEffective;
    public decimal AccountBalanceValue
    {
        get => IsLiveEffective ? _paperEquity : StartingBalance;
        set
        {
            if (!IsLiveEffective)
            {
                StartingBalance = value;
            }
        }
    }
    public string MarketDataStatus => _marketDataStatus;
    public string MarketDataStateLabel => _marketDataStateLabel;
    public string CurrentPrice => _currentPrice;
    public string BidAskDisplay => _bidAskDisplay;
    public bool ShowRsi
    {
        get => _showRsi;
        set
        {
            if (SetField(ref _showRsi, value))
            {
                OnPropertyChanged(nameof(RsiToggleLabel));
            }
        }
    }
    public string RsiToggleLabel => ShowRsi ? "RSI: ON" : "RSI: OFF";
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
    public int ChartCandleIntervalSeconds
    {
        get => _chartCandleIntervalSeconds;
        set
        {
            if (value is not (15 or 30 or 60 or 120))
            {
                return;
            }

            if (SetPreferenceField(ref _chartCandleIntervalSeconds, value))
            {
                RefreshMarketView();
            }
        }
    }
    public int ChartScaleResetVersion => _chartScaleResetVersion;
    public string VersionDisplay { get; } =
        $"VERSION {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
    public bool HasMarketData => _hasMarketData;
    public string StrategyMessage => _strategyMessage;
    public string StrategyStateLabel => _strategyStateLabel;
    public string StrategyMetrics => _strategyMetrics;
    public string JournalStatus => _journalReady ? "SQLITE WAL" : "OFFLINE";
    public string PrimaryActionLabel => IsReplayPaused
        ? "RESUME"
        : SelectedMode switch
        {
            TradingMode.Replay => "START REPLAY",
            TradingMode.Live => "START LIVE TRADER",
            _ => "START PAPER TRADER",
        };
    public string SecondaryActionLabel =>
        EffectiveMode is TradingMode.Replay && IsSessionRunning && !IsReplayPaused
            ? "PAUSE"
            : "STOP";
    public string SessionStateLabel => EffectiveMode is TradingMode.Off
        ? "OFF"
        : EffectiveMode is TradingMode.Live && !LiveArmed ? "DISARMED"
        : IsReplayPaused ? "PAUSED"
        : IsSessionRunning ? "RUNNING" : "READY";
    public string SessionStateBackground => EffectiveMode is TradingMode.Live && !LiveArmed
        ? "#3B211E"
        : IsReplayPaused ? "#34251A"
        : EffectiveMode is TradingMode.Off ? "#202B39" : "#123528";
    public string SessionStateBorder => EffectiveMode is TradingMode.Live && !LiveArmed
        ? "#7F3C34"
        : IsReplayPaused ? "#7B5426"
        : EffectiveMode is TradingMode.Off ? "#3A4B61" : "#24684C";
    public string SessionStateForeground => EffectiveMode is TradingMode.Live && !LiveArmed
        ? "#FF9B8C"
        : IsReplayPaused ? "#F4B45E"
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

    public bool TradesSettleImmediately
    {
        get => _tradesSettleImmediately;
        set => SetPreferenceField(ref _tradesSettleImmediately, value);
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
        set => SetPreferenceField(ref _bufferMinutes, value);
    }

    public int QuotePollingSeconds
    {
        get => _quotePollingSeconds;
        set => SetPreferenceField(ref _quotePollingSeconds, value);
    }

    public int ReconciliationSeconds
    {
        get => _reconciliationSeconds;
        set => SetPreferenceField(ref _reconciliationSeconds, value);
    }

    public int ReconciliationLookbackSeconds
    {
        get => _reconciliationLookbackSeconds;
        set => SetPreferenceField(ref _reconciliationLookbackSeconds, value);
    }

    public int ReconciliationCompletionDelaySeconds
    {
        get => _reconciliationCompletionDelaySeconds;
        set => SetPreferenceField(ref _reconciliationCompletionDelaySeconds, value);
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
                OnPropertyChanged(nameof(PrimaryActionLabel));
                OnPropertyChanged(nameof(SecondaryActionLabel));
                StartSessionCommand.RaiseCanExecuteChanged();
                StopSessionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsReplayPaused
    {
        get => _isReplayPaused;
        private set
        {
            if (SetField(ref _isReplayPaused, value))
            {
                OnPropertyChanged(nameof(PrimaryActionLabel));
                OnPropertyChanged(nameof(SecondaryActionLabel));
                OnPropertyChanged(nameof(SessionStateLabel));
                OnPropertyChanged(nameof(SessionStateBackground));
                OnPropertyChanged(nameof(SessionStateBorder));
                OnPropertyChanged(nameof(SessionStateForeground));
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

    public bool RequestModeSelection(TradingMode mode)
    {
        if (IsSessionRunning || _isStartingSession)
        {
            if (EffectiveMode is TradingMode.Live && mode is TradingMode.Live)
            {
                StatusMessage =
                    "LIVE is already active. Choose STOP to reconcile any in-flight order before restarting.";
                AddActivity("LIVE mode is already active; use STOP before restarting it.", "WARNING");
            }
            else if (EffectiveMode is TradingMode.Live)
            {
                StatusMessage =
                    "Choose STOP before leaving an active LIVE session. PriceSentinel must reconcile any in-flight order before changing modes.";
                AddActivity("Mode change blocked until the active LIVE session is stopped safely.", "WARNING");
            }
            else
            {
                StatusMessage =
                    "Choose STOP before changing modes so the active data session can finish cleanly.";
                AddActivity("Mode change blocked until the active data session is stopped.", "WARNING");
            }

            return false;
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
        return true;
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
        if (LiveRiskAcknowledged && EffectiveMode is TradingMode.Live)
        {
            return;
        }

        _liveRiskAcknowledged = true;
        _modeState = _modeState.ActivateLiveDisarmed();
        CancellationToken cancellationToken = _sessionCoordinator.Begin();
        _isStartingSession = true;
        StatusMessage = "LIVE mode is effective and disarmed. Verifying the Robinhood connection...";
        AddActivity("LIVE risk acknowledged; verifying the startup Robinhood connection.");
        OnPropertyChanged(nameof(LiveRiskAcknowledged));
        NotifyModeProperties();

        try
        {
            SetMarketDataState("ROBINHOOD READY", "VERIFYING", isConnected: false);
            await _marketDataSource.ConnectAsync(cancellationToken);
            SetMarketDataState("ROBINHOOD READY", "CONNECTED");
            StatusMessage = "Robinhood is connected. Review the controls, then choose START LIVE TRADER to reconcile and arm execution.";
            AddActivity("Robinhood connection verified; LIVE execution awaits explicit START LIVE TRADER arming.");
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

        if (!_cachedAuthentication.HasCachedAuthentication)
        {
            return false;
        }

        StatusMessage = "Restoring the saved Robinhood connection...";
        SetMarketDataState("ROBINHOOD LOGIN", "RESTORING", isConnected: false);

        if (!await _cachedAuthentication.TryConnectUsingCachedAuthenticationAsync(
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

    public Task ShutdownAsync(bool forceUnresolvedLiveOrder = false)
    {
        if (_shutdownTask is not null)
        {
            return _shutdownTask;
        }

        _shutdownTask = ShutdownCoreAsync(forceUnresolvedLiveOrder);
        return _shutdownTask;
    }

    public async ValueTask DisposeAsync() =>
        await ShutdownAsync(forceUnresolvedLiveOrder: true);

    public async Task<bool> PrepareForShutdownAsync()
    {
        if (_disposed)
        {
            return true;
        }

        var failures = new List<Exception>();

        try
        {
            SavePreferences();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _sessionCoordinator.Cancel();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (StartSessionCommand.ExecutionTask is { IsCompleted: false } sessionTask)
        {
            try
            {
                await sessionTask;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (StopSessionCommand.ExecutionTask is { IsCompleted: false } stopTask)
        {
            try
            {
                await stopTask;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (_liveOrderCoordinator.HasActiveContext)
        {
            try
            {
                await CancelCoordinatedLiveOrderAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        bool hasUnresolvedLiveOrder = _liveOrderCoordinator.HasActiveContext;
        if (hasUnresolvedLiveOrder)
        {
            DisarmLiveExecution(
                "PriceSentinel could not confirm a final state for the active LIVE order. Verify it in Robinhood before exiting.");
            AddActivity(
                "Application close paused because a LIVE order remains unresolved. Verify Robinhood before exiting.",
                "ERROR");
        }
        else if (_activeSession is not null)
        {
            try
            {
                StopActiveSession(
                    "APPLICATION_CLOSED",
                    "Application closed; the data session was finalized.");
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        TraceShutdownFailures(failures);
        return !hasUnresolvedLiveOrder;
    }

    private async Task ShutdownCoreAsync(bool forceUnresolvedLiveOrder)
    {
        if (_disposed)
        {
            return;
        }

        bool safeToClose = await PrepareForShutdownAsync();
        if (!safeToClose && !forceUnresolvedLiveOrder)
        {
            throw new InvalidOperationException(
                "A LIVE order remains unresolved. Verify Robinhood or explicitly confirm that the application should exit anyway.");
        }

        _disposed = true;
        var failures = new List<Exception>();

        if (!safeToClose)
        {
            try
            {
                AddActivity(
                    "Application exit was confirmed while a LIVE order remained unresolved. Verify Robinhood immediately.",
                    "ERROR");
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (_activeSession is not null)
        {
            try
            {
                StopActiveSession(
                    safeToClose ? "APPLICATION_CLOSED" : "APPLICATION_CLOSED_ORDER_UNRESOLVED",
                    safeToClose
                        ? "Application closed; the data session was finalized."
                        : "Application closed with an unresolved LIVE order; verify Robinhood immediately.");
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            _sessionCoordinator.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _liveOrderCoordinator.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await _marketDataSource.DisposeAsync();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _journal.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        TraceShutdownFailures(failures);
    }

    private static void TraceShutdownFailures(IReadOnlyCollection<Exception> failures)
    {
        if (failures.Count > 0)
        {
            System.Diagnostics.Trace.TraceError(
                "PriceSentinel shutdown completed with {0} cleanup error(s): {1}",
                failures.Count,
                string.Join(" | ", failures.Select(exception => exception.Message)));
        }
    }

    public void SavePreferences() =>
        _preferencesStore.Save(CreateSettings());

    private TradingSessionSettings CreateSettings() => new()
    {
        Symbol = Symbol.Trim().ToUpperInvariant(),
        StartingBalance = StartingBalance,
        PositionSizeBasis = PositionSizeBasis,
        TradesSettleImmediately = TradesSettleImmediately,
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
        ChartCandleIntervalSeconds = ChartCandleIntervalSeconds,
        ReconciliationSeconds = ReconciliationSeconds,
        ReconciliationLookbackSeconds = ReconciliationLookbackSeconds,
        ReconciliationCompletionDelaySeconds =
            ReconciliationCompletionDelaySeconds,
        ReplayDate = ReplayDate,
        ReplayTime = ReplayTime,
        ReplayEndTime = ReplayEndTime,
        ReplaySpeed = ReplaySpeed,
    };

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

    private static bool IsFreshObservation(MarketQuote quote)
    {
        TimeSpan age = quote.ObservedAtUtc - quote.SourceTimestampUtc;
        return age >= TimeSpan.FromSeconds(-30) &&
               age <= TimeSpan.FromMinutes(2);
    }

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
