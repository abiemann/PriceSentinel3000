using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.LiveTrading;
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
    private readonly ILiveBrokerGateway? _liveBrokerGateway;
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
    private bool _showRsi;
    private bool _isChartManualScale;
    private int _chartScaleResetVersion;
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
    private TaskCompletionSource<bool>? _replayResumeSource;
    private CancellationTokenSource? _sessionCancellation;
    private JournalSession? _activeSession;
    private PriceRingBuffer? _ringBuffer;
    private PriceRingBuffer? _chartRingBuffer;
    private MarketDataRequest? _marketDataRequest;
    private PaperTradingEngine? _paperTradingEngine;
    private LiveExecutionEngine? _liveExecutionEngine;
    private BrokerAccount? _liveAccount;
    private EquityTradability? _liveTradability;
    private BrokerOrderSnapshot? _activeLiveOrder;
    private BrokerOrderIntent? _activeLiveIntent;
    private BrokerOrderReview? _activeLiveReview;
    private DateTimeOffset? _activeLiveTriggerTimestamp;
    private readonly SemaphoreSlim _liveOrderGate = new(1, 1);
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
        _liveBrokerGateway = marketDataSource as ILiveBrokerGateway;
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
            ExecutePrimarySessionAction,
            () => IsReplayPaused ||
                  (SelectedMode is TradingMode.PaperTrader or TradingMode.Replay or TradingMode.Live &&
                   EffectiveMode == SelectedMode &&
                   !IsSessionRunning &&
                   !_isStartingSession));
        StopSessionCommand = new RelayCommand(
            ExecuteSecondarySessionAction,
            () => IsSessionRunning);

        RebuildBufferSegments();
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
    public int ChartScaleResetVersion => _chartScaleResetVersion;
    public string VersionDisplay { get; } =
        $"VERSION {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
    public bool HasMarketData => _hasMarketData;
    public string StrategyMessage => _strategyMessage;
    public string StrategyStateLabel => _strategyStateLabel;
    public string StrategyMetrics => _strategyMetrics;
    public string JournalStatus => _journalReady ? "SQLITE WAL" : "OFFLINE";
    public string PriceActionCaption => EffectiveMode is TradingMode.Replay
        ? "15 SECOND REPLAY CANDLES"
        : $"15 SECOND CANDLES · {QuotePollingSeconds} SECOND UPDATES";
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

    public void RequestModeSelection(TradingMode mode)
    {
        if (EffectiveMode is TradingMode.Live &&
            (IsSessionRunning || _isStartingSession) &&
            mode is not TradingMode.Live)
        {
            StatusMessage =
                "Choose STOP before leaving an active LIVE session. PriceSentinel must reconcile any in-flight order before changing modes.";
            AddActivity("Mode change blocked until the active LIVE session is stopped safely.", "WARNING");
            return;
        }

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
        if (_activeLiveOrder?.IsOpen is true)
        {
            CancelActiveLiveOrderAsync().GetAwaiter().GetResult();
        }


        if (_activeSession is not null)
        {
            StopActiveSession("APPLICATION_CLOSED", "Application closed; the data session was finalized.");
        }

        _sessionCancellation?.Dispose();
        _marketDataSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _journal.Dispose();
        _liveOrderGate.Dispose();
    }

    public void SavePreferences() =>
        _preferencesStore.Save(CreateSettings());

    private void ExecutePrimarySessionAction()
    {
        if (IsReplayPaused)
        {
            ResumeReplay();
            return;
        }

        StartSelectedSession();
    }

    private async void ExecuteSecondarySessionAction()
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
                await StartRealtimeTraderAsync(instrument, settings, EffectiveMode);
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
        PaperTraderSettings settings,
        TradingMode mode)
    {
        CancellationToken token = _sessionCancellation!.Token;
        bool isLive = mode is TradingMode.Live;
        StatusMessage = "Connecting to Robinhood. Complete the secure browser login if it opens.";
        SetMarketDataState("ROBINHOOD LOGIN", "AUTHORIZING", isConnected: false);
        await _marketDataSource.ConnectAsync(token);
        PaperTraderSettings sessionSettings = settings;
        LiveBrokerSnapshot? initialBroker = null;

        if (isLive)
        {
            if (_liveBrokerGateway is null)
            {
                throw new InvalidOperationException(
                    "The connected market-data adapter cannot execute LIVE orders.");
            }

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
                DateTimeOffset.UtcNow);
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
        _chartRingBuffer!.Merge(history);
        MarketQuote current = await _marketDataSource.GetQuoteAsync(
            _marketDataRequest,
            DateTimeOffset.UtcNow,
            token);
        _journal.AppendQuotes(_activeSession.Id, [current], QuoteIngestionKind.Live);
        _ringBuffer.Merge([current]);
        _chartRingBuffer.Merge([current]);
        SetQuoteMarketState(current);
        if (isLive)
        {
            await ProcessLiveObservationAsync(current, token);
        }
        else
        {
            ProcessPaperObservation(current);
        }
        RefreshMarketView();
        StatusMessage = isLive
            ? $"LIVE Trader is armed and watching real {instrument.Symbol} prices every {settings.QuotePollingSeconds} seconds."
            : $"Paper Trader is watching real {instrument.Symbol} prices every {settings.QuotePollingSeconds} seconds; order execution is paper-only.";
        AddActivity(
            isLive
                ? $"LIVE Trader started with {warmMerge.Added} real warm-start bars plus the current Robinhood quote; confirmed strategy actions can submit reviewed Robinhood market orders."
                : $"Paper Trader started with {warmMerge.Added} real warm-start bars plus the current Robinhood quote; no real orders can be sent.");

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
            _chartRingBuffer!.Merge([quote]);
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
                _chartRingBuffer.Merge(verification);
                AddActivity(
                    $"Robinhood history reconciled {verification.Count} bars: {merge.Duplicates} verified, {merge.Corrected} corrected, {merge.Added} gaps filled.");
                nextReconciliation =
                    observedAt.AddSeconds(settings.ReconciliationSeconds);
            }

            if (isLive)
            {
                await ProcessLiveObservationAsync(quote, token);
            }
            else
            {
                ProcessPaperObservation(quote);
            }
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
                await DelayReplayAsync(CalculateReplayDelay(
                    historicalQuotes[index - 1].SourceTimestampUtc,
                    historicalQuotes[index].SourceTimestampUtc,
                    settings.ReplaySpeed), token);
            }
            else
            {
                await WaitWhileReplayPausedAsync(token);
            }

            MarketQuote replayed = historicalQuotes[index] with
            {
                ObservedAtUtc = DateTimeOffset.UtcNow,
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
                $"Replaying {index + 1}/{historicalQuotes.Count} real {instrument.Symbol} observations from {firstSource.ToLocalTime():g} at {settings.ReplaySpeed:0.#}x speed.";
        }

        JournalSummary summary = _journal.GetSummary(_activeSession!.Id);
        AddActivity($"Historical Replay completed after {summary.QuoteCount} real observations.");
        StopActiveSession(
            "COMPLETED",
            $"Replay completed for {instrument.Symbol}. The chart remains available for inspection.");
    }

    private async Task DelayReplayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = delay;
        TimeSpan maximumSlice = TimeSpan.FromMilliseconds(100);

        while (remaining > TimeSpan.Zero)
        {
            await WaitWhileReplayPausedAsync(cancellationToken);
            TimeSpan slice = remaining < maximumSlice ? remaining : maximumSlice;
            await Task.Delay(slice, cancellationToken);
            remaining -= slice;
        }

        await WaitWhileReplayPausedAsync(cancellationToken);
    }

    private async Task WaitWhileReplayPausedAsync(
        CancellationToken cancellationToken)
    {
        while (IsReplayPaused)
        {
            TaskCompletionSource<bool>? resumeSource = _replayResumeSource;

            if (resumeSource is null)
            {
                return;
            }

            await resumeSource.Task.WaitAsync(cancellationToken);
        }
    }

    private void PrepareDataSession(
        Instrument instrument,
        PaperTraderSettings settings,
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
        ClearActiveLiveOrderContext();

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

    private void PauseReplay()
    {
        if (EffectiveMode is not TradingMode.Replay ||
            !IsSessionRunning ||
            IsReplayPaused)
        {
            return;
        }

        _replayResumeSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
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

        TaskCompletionSource<bool>? resumeSource = _replayResumeSource;
        _replayResumeSource = null;
        IsReplayPaused = false;
        _strategyStateLabel = "REPLAYING";
        _strategyMessage =
            "Historical Robinhood prices are arriving as a new stream. Orders are simulated only.";
        NotifyStrategyProperties();
        StatusMessage = "Replay resumed from the next historical observation.";
        AddActivity("Historical Replay resumed.");
        resumeSource?.TrySetResult(true);
    }

    private void ReleaseReplayPause()
    {
        TaskCompletionSource<bool>? resumeSource = _replayResumeSource;
        _replayResumeSource = null;
        IsReplayPaused = false;
        resumeSource?.TrySetResult(true);
    }

    private async Task StopSessionAsync()
    {
        bool wasLive = EffectiveMode is TradingMode.Live;
        bool hadLiveOrderContext = _activeLiveIntent is not null ||
                                   _activeLiveOrder?.IsOpen is true;
        _sessionCancellation?.Cancel();
        bool orderStopHandled = await CancelActiveLiveOrderAsync();
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

    private async Task<bool> CancelActiveLiveOrderAsync()
    {
        BrokerOrderSnapshot? order = _activeLiveOrder;

        if (_liveBrokerGateway is null ||
            _liveAccount is null ||
            (_activeLiveIntent is null && order is null))
        {
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            if (order is null || string.IsNullOrWhiteSpace(order.BrokerOrderId))
            {
                order = await FindActiveLiveOrderByReferenceAsync(5, timeout.Token);
            }

            if (order is null)
            {
                AddActivity(
                    "STOP could not locate the in-flight order by its idempotent reference. Verify Robinhood immediately.",
                    "ERROR");
                return false;
            }

            _activeLiveOrder = order;
            if (order.IsTerminal)
            {
                RecordStoppedLiveOrderState(order, "TERMINAL");
                _liveExecutionEngine?.ObserveTerminalOrder(order);
                ClearActiveLiveOrderContext();
                AddActivity(
                    $"STOP found the Robinhood order already {order.State}; no cancellation request was sent. Verify the resulting position.",
                    order.State is BrokerOrderState.Filled ? "WARNING" : "INFO");
                return true;
            }

            await _liveBrokerGateway.CancelOrderAsync(
                _liveAccount.AccountNumber,
                order.BrokerOrderId,
                timeout.Token);
            RecordStoppedLiveOrderState(order, "CANCEL_REQUESTED");
            AddActivity(
                "STOP requested cancellation of the active Robinhood order; waiting briefly for its final broker state.",
                "WARNING");

            for (int attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750), timeout.Token);
                order = await _liveBrokerGateway.GetOrderAsync(
                    _liveAccount.AccountNumber,
                    order.BrokerOrderId,
                    timeout.Token);
                _activeLiveOrder = order;
                RecordStoppedLiveOrderState(
                    order,
                    order.IsTerminal ? "TERMINAL" : "CANCEL_RECONCILE");

                if (!order.IsTerminal)
                {
                    continue;
                }

                _liveExecutionEngine?.ObserveTerminalOrder(order);
                if (order.FilledQuantity > 0m)
                {
                    AddActivity(
                        $"STOP reconciliation found {order.FilledQuantity:0.######} shares filled before the order reached {order.State}. Check the resulting Robinhood position immediately.",
                        "ERROR");
                }
                else
                {
                    AddActivity($"Robinhood order reached final state {order.State} after STOP.");
                }

                ClearActiveLiveOrderContext();
                return true;
            }

            AddActivity(
                $"Robinhood accepted cancellation, but the order is still {order.State}. Verify its final state and any position in Robinhood.",
                "WARNING");
            return true;
        }
        catch (Exception exception)
        {
            AddActivity(
                $"STOP could not confirm Robinhood order cancellation: {exception.Message}. Verify Robinhood immediately.",
                "ERROR");
            return false;
        }
    }

    private void RecordStoppedLiveOrderState(
        BrokerOrderSnapshot order,
        string eventType)
    {
        if (_activeSession is null ||
            _ringBuffer is null ||
            _activeLiveIntent is null)
        {
            return;
        }

        _journal.AppendLiveOrderEvent(
            _activeSession.Id,
            _ringBuffer.Instrument,
            eventType,
            _activeLiveIntent,
            _activeLiveReview,
            order,
            DateTimeOffset.UtcNow);
    }

    private async Task<BrokerOrderSnapshot?> FindActiveLiveOrderByReferenceAsync(
        int attempts,
        CancellationToken cancellationToken)
    {
        if (_liveBrokerGateway is null ||
            _liveAccount is null ||
            _activeLiveIntent is null)
        {
            return null;
        }

        Instrument instrument = _ringBuffer?.Instrument ??
                                new Instrument(Symbol, AssetClass.Equity);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            BrokerOrderSnapshot? recovered =
                await _liveBrokerGateway.FindOrderByClientReferenceAsync(
                    _liveAccount.AccountNumber,
                    instrument,
                    _activeLiveIntent.ClientReferenceId,
                    cancellationToken);
            if (recovered is null)
            {
                IReadOnlyList<BrokerOrderSnapshot> recentOrders =
                    await _liveBrokerGateway.GetOrdersCreatedSinceAsync(
                        _liveAccount.AccountNumber,
                        _activeLiveIntent.CreatedAtUtc.AddSeconds(-5),
                        cancellationToken);
                BrokerOrderSnapshot[] exactMatches = recentOrders
                    .Where(order =>
                        string.Equals(order.Symbol, _activeLiveIntent.Symbol, StringComparison.OrdinalIgnoreCase) &&
                        order.Side == _activeLiveIntent.Side &&
                        order.RequestedQuantity == _activeLiveIntent.Quantity)
                    .GroupBy(order => order.BrokerOrderId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
                if (exactMatches.Length > 1)
                {
                    AddActivity(
                        "More than one recent Robinhood order matched the uncertain placement. Automatic recovery is blocked; verify Robinhood immediately.",
                        "ERROR");
                    return null;
                }

                recovered = exactMatches.SingleOrDefault();
            }
            if (recovered is not null)
            {
                recovered = recovered with
                {
                    ClientReferenceId = _activeLiveIntent.ClientReferenceId,
                };
                _activeLiveOrder = recovered;
                return recovered;
            }

            if (attempt + 1 < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        return null;
    }

    private void StopActiveSession(string outcome, string statusMessage)
    {
        _sessionCancellation?.Cancel();
        ReleaseReplayPause();

        if (_activeSession is not null)
        {
            AddActivity($"{_activeSession.Mode} session finalized: {outcome}.");
            _journal.CompleteSession(_activeSession.Id, DateTimeOffset.UtcNow, outcome);
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

        IReadOnlyList<MarketQuote> chartSnapshot =
            _chartRingBuffer?.Snapshot() ?? snapshot;
        IReadOnlyList<PriceCandle> candles = PriceCandleAggregator.Aggregate(
            chartSnapshot,
            TimeSpan.FromSeconds(15));
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

        IReadOnlyList<MinuteBlock> blocks = MinuteBlockAnalyzer.Analyze(
            snapshot,
            BufferSegments.Count,
            latest.SourceTimestampUtc);

        for (int index = 0; index < BufferSegments.Count; index++)
        {
            BufferSegments[index].Update(blocks[index]);
        }
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

        OnPropertyChanged(nameof(PriceActionCaption));
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

    private async Task<LiveBrokerSnapshot> InitializeLiveBrokerAsync(
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        ILiveBrokerGateway gateway = _liveBrokerGateway ??
            throw new InvalidOperationException("LIVE broker execution is unavailable.");
        _liveAccount = await gateway.GetAgenticAccountAsync(cancellationToken);
        BrokerPortfolio portfolio = await gateway.GetPortfolioAsync(
            _liveAccount.AccountNumber,
            cancellationToken);
        BrokerPosition position = await gateway.GetPositionAsync(
            _liveAccount.AccountNumber,
            instrument,
            cancellationToken);
        _liveTradability = await gateway.GetTradabilityAsync(
            _liveAccount.AccountNumber,
            _liveAccount.AccountType,
            instrument,
            cancellationToken);
        IReadOnlyList<BrokerOrderSnapshot> orders = await gateway.GetOpenOrdersAsync(
            _liveAccount.AccountNumber,
            instrument,
            cancellationToken);

        if (portfolio.TotalValue <= 0m)
        {
            throw new InvalidOperationException(
                "Robinhood returned an invalid account value; LIVE execution remains disarmed.");
        }

        if (!_liveTradability.Tradeable)
        {
            throw new InvalidOperationException(
                _liveTradability.Reason ??
                $"Robinhood reports {instrument.Symbol} is not tradeable.");
        }

        return new(
            _liveAccount,
            portfolio,
            position,
            _liveTradability,
            orders,
            DateTimeOffset.UtcNow);
    }

    private async Task<LiveBrokerSnapshot> CaptureLiveBrokerAsync(
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        ILiveBrokerGateway gateway = _liveBrokerGateway ??
            throw new InvalidOperationException("LIVE broker execution is unavailable.");
        BrokerAccount account = _liveAccount ??
            throw new InvalidOperationException("No agentic Robinhood account is selected.");
        EquityTradability tradability = _liveTradability ??
            throw new InvalidOperationException("Equity tradability was not verified.");
        BrokerPortfolio portfolio = await gateway.GetPortfolioAsync(
            account.AccountNumber,
            cancellationToken);
        BrokerPosition position = await gateway.GetPositionAsync(
            account.AccountNumber,
            instrument,
            cancellationToken);
        IReadOnlyList<BrokerOrderSnapshot> orders = await gateway.GetOpenOrdersAsync(
            account.AccountNumber,
            instrument,
            cancellationToken);
        return new(
            account,
            portfolio,
            position,
            tradability,
            orders,
            DateTimeOffset.UtcNow);
    }

    private async Task ProcessLiveObservationAsync(
        MarketQuote trigger,
        CancellationToken cancellationToken)
    {
        if (_ringBuffer is null ||
            _activeSession is null ||
            _liveExecutionEngine is null ||
            _liveBrokerGateway is null ||
            _liveAccount is null)
        {
            return;
        }

        if (!IsFreshObservation(trigger))
        {
            _strategyStateLabel = "MARKET CLOSED";
            _strategyMessage =
                "The newest Robinhood venue timestamp is stale; LIVE decisions and orders are paused.";
            _strategyMetrics = "RSI --  |  MOM --  |  CONF --";
            NotifyStrategyProperties();
            return;
        }
        if (await ReconcileActiveLiveOrderAsync(cancellationToken))
        {
            return;
        }


        LiveBrokerSnapshot broker = await CaptureLiveBrokerAsync(
            _ringBuffer.Instrument,
            cancellationToken);
        UpdateLiveAccount(broker, trigger.Last);
        LiveTradeEvaluation evaluation = _liveExecutionEngine.Evaluate(
            _ringBuffer.Snapshot(),
            broker);
        _journal.AppendDecision(_activeSession.Id, evaluation.Decision);
        UpdateStrategyDecision(evaluation.Decision);

        BrokerOrderIntent? intent = evaluation.Intent;
        if (intent is null)
        {
            return;
        }

        if (!LiveArmed)
        {
            AddActivity(
                $"LIVE {intent.Side.ToString().ToUpperInvariant()} signal ignored because broker execution is disarmed.",
                "WARNING");
            return;
        }

        if (!IsRegularEquityMarketHours(DateTimeOffset.UtcNow))
        {
            _strategyStateLabel = "MARKET HOURS ONLY";
            _strategyMessage =
                "A confirmed signal occurred outside regular equity hours; no LIVE order was submitted.";
            NotifyStrategyProperties();
            AddActivity(
                $"LIVE {intent.Side.ToString().ToUpperInvariant()} blocked outside 9:30 AM?4:00 PM ET regular equity hours.",
                "WARNING");
            return;
        }

        if (!broker.Tradability.FractionalTradeable &&
            intent.Quantity != Math.Floor(intent.Quantity))
        {
            AddActivity(
                $"LIVE order blocked because Robinhood does not allow fractional trading for {intent.Symbol}.",
                "WARNING");
            return;
        }

        if (!await _liveOrderGate.WaitAsync(0, cancellationToken))
        {
            AddActivity("A LIVE order workflow is already active; duplicate signal ignored.", "WARNING");
            return;
        }

        try
        {
            await ReviewPlaceAndReconcileOrderAsync(
                trigger,
                intent,
                cancellationToken);
        }
        finally
        {
            _liveOrderGate.Release();
        }
    }

    private async Task ReviewPlaceAndReconcileOrderAsync(
        MarketQuote trigger,
        BrokerOrderIntent intent,
        CancellationToken cancellationToken)
    {
        ILiveBrokerGateway gateway = _liveBrokerGateway!;
        BrokerAccount account = _liveAccount!;
        JournalSession session = _activeSession!;
        Instrument instrument = _ringBuffer!.Instrument;
        _journal.AppendLiveOrderEvent(
            session.Id,
            instrument,
            "INTENT_CREATED",
            intent,
            null,
            null,
            DateTimeOffset.UtcNow);
        AddActivity(
            $"Reviewing LIVE {intent.Side.ToString().ToUpperInvariant()} {intent.Quantity:0.######} {intent.Symbol} with Robinhood. Reason: {intent.Reason}.");

        BrokerOrderReview review = await gateway.ReviewOrderAsync(
            account.AccountNumber,
            intent,
            cancellationToken);
        _journal.AppendLiveOrderEvent(
            session.Id,
            instrument,
            review.Accepted ? "REVIEW_ACCEPTED" : "REVIEW_BLOCKED",
            intent,
            review,
            null,
            DateTimeOffset.UtcNow);

        if (!string.IsNullOrWhiteSpace(review.MarketDataDisclosure))
        {
            AddActivity(
                $"Robinhood market data for LIVE review: {review.MarketDataDisclosure}");
        }

        if (!review.Accepted)
        {
            string reason = review.Blockers.FirstOrDefault() ??
                            "Robinhood did not accept the order review.";
            AddActivity($"LIVE order review blocked: {reason}", "WARNING");
            return;
        }

        decimal triggerPrice = intent.Side is BrokerOrderSide.Buy
            ? trigger.HasTwoSidedMarket ? trigger.Ask : trigger.Last
            : trigger.HasTwoSidedMarket ? trigger.Bid : trigger.Last;
        decimal? reviewedPrice = intent.Side is BrokerOrderSide.Buy
            ? review.AskPrice ?? review.LastPrice
            : review.BidPrice ?? review.LastPrice;

        if (reviewedPrice is null or <= 0m || triggerPrice <= 0m)
        {
            AddActivity(
                "LIVE order blocked because Robinhood did not return a valid reviewed execution-side price.",
                "WARNING");
            return;
        }

        if (reviewedPrice is > 0m && triggerPrice > 0m &&
            Math.Abs(reviewedPrice.Value - triggerPrice) / triggerPrice > 0.005m)
        {
            AddActivity(
                $"LIVE order blocked because Robinhood's reviewed price moved more than 0.50% from the triggering quote.",
                "WARNING");
            return;
        }

        _activeLiveIntent = intent;
        _activeLiveReview = review;
        _activeLiveTriggerTimestamp = trigger.SourceTimestampUtc;
        BrokerOrderSnapshot placed;

        try
        {
            placed = await gateway.PlaceOrderAsync(
                account.AccountNumber,
                intent,
                cancellationToken);
        }
        catch (Exception firstException) when (firstException is not OperationCanceledException)
        {
            AddActivity(
                "Robinhood placement response was uncertain; retrying once with the same idempotent reference ID.",
                "WARNING");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            try
            {
                placed = await gateway.PlaceOrderAsync(
                    account.AccountNumber,
                    intent,
                    cancellationToken);
            }
            catch (Exception secondException) when (secondException is not OperationCanceledException)
            {
                BrokerOrderSnapshot? recovered =
                    await FindActiveLiveOrderByReferenceAsync(3, cancellationToken);
                if (recovered is null)
                {
                    _journal.AppendLiveOrderEvent(
                        session.Id,
                        instrument,
                        "PLACEMENT_UNCERTAIN",
                        intent,
                        review,
                        null,
                        DateTimeOffset.UtcNow);
                    DisarmLiveExecution(
                        "Robinhood did not confirm whether the LIVE order was accepted. Verify Robinhood immediately before restarting.");
                    throw new InvalidOperationException(
                        "Robinhood placement remained uncertain after an idempotent retry; verify Robinhood immediately.",
                        secondException);
                }

                placed = recovered;
                AddActivity("Recovered the Robinhood order by its idempotent reference after a lost placement response.", "WARNING");
            }
        }

        placed = placed with { ClientReferenceId = intent.ClientReferenceId };
        ValidatePlacedOrder(placed, intent);
        _activeLiveOrder = placed;
        _journal.AppendLiveOrderEvent(
            session.Id,
            instrument,
            "SUBMITTED",
            intent,
            review,
            placed,
            DateTimeOffset.UtcNow);
        AddActivity(
            $"LIVE {intent.Side.ToString().ToUpperInvariant()} submitted to Robinhood; state {placed.State.ToString().ToUpperInvariant()}.");

        BrokerOrderState priorState = placed.State;

        for (int attempt = 0; attempt < 30 && !placed.IsTerminal; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            placed = await gateway.GetOrderAsync(
                account.AccountNumber,
                placed.BrokerOrderId,
                cancellationToken);
            placed = placed with { ClientReferenceId = intent.ClientReferenceId };
            _activeLiveOrder = placed;

            if (placed.State == priorState && attempt % 5 != 4)
            {
                continue;
            }

            _journal.AppendLiveOrderEvent(
                session.Id,
                instrument,
                "BROKER_STATE",
                intent,
                review,
                placed,
                DateTimeOffset.UtcNow);
            priorState = placed.State;
        }

        if (!placed.IsTerminal)
        {
            AddActivity(
                $"Robinhood order remains {placed.State}; PriceSentinel will block duplicate orders and continue reconciliation.",
                "WARNING");
            return;
        }

        _journal.AppendLiveOrderEvent(
            session.Id,
            instrument,
            "TERMINAL",
            intent,
            review,
            placed,
            DateTimeOffset.UtcNow);
        ClearActiveLiveOrderContext();

        _liveExecutionEngine!.ObserveTerminalOrder(placed);
        if (placed.State is BrokerOrderState.Filled)
        {
            _tradeMarkers[trigger.SourceTimestampUtc] =
                intent.Side is BrokerOrderSide.Buy
                    ? ChartTradeMarker.Buy
                    : ChartTradeMarker.Sell;
            AddActivity(
                $"LIVE {intent.Side.ToString().ToUpperInvariant()} filled {placed.FilledQuantity:0.######} {intent.Symbol} @ {(placed.AveragePrice ?? reviewedPrice ?? triggerPrice):C2}.");
            LiveBrokerSnapshot reconciled = await CaptureLiveBrokerAsync(
                instrument,
                cancellationToken);
            UpdateLiveAccount(reconciled, trigger.Last);
            return;
        }

        if (placed.FilledQuantity > 0m)
        {
            LiveBrokerSnapshot partiallyFilled = await CaptureLiveBrokerAsync(
                instrument,
                cancellationToken);
            UpdateLiveAccount(partiallyFilled, trigger.Last);
        }

        AddActivity(
            $"LIVE order ended {placed.State}: {placed.RejectionReason ?? "no broker reason supplied"}. Execution has been disarmed.",
            "ERROR");
        DisarmLiveExecution("A Robinhood order did not fill successfully; inspect the journal before re-arming.");
    }

    private async Task<bool> ReconcileActiveLiveOrderAsync(
        CancellationToken cancellationToken)
    {
        if (_activeLiveOrder?.IsOpen is not true)
        {
            return false;
        }

        if (_liveBrokerGateway is null ||
            _liveAccount is null ||
            _activeSession is null ||
            _ringBuffer is null ||
            _activeLiveIntent is null ||
            _activeLiveReview is null)
        {
            DisarmLiveExecution(
                "An active LIVE order could not be reconciled from memory. Verify Robinhood before restarting PriceSentinel.");
            return true;
        }

        BrokerOrderSnapshot order = await _liveBrokerGateway.GetOrderAsync(
            _liveAccount.AccountNumber,
            _activeLiveOrder.BrokerOrderId,
            cancellationToken);
        order = order with
        {
            ClientReferenceId = _activeLiveIntent.ClientReferenceId,
        };
        _activeLiveOrder = order;
        _journal.AppendLiveOrderEvent(
            _activeSession.Id,
            _ringBuffer.Instrument,
            order.IsTerminal ? "TERMINAL" : "BROKER_STATE",
            _activeLiveIntent,
            _activeLiveReview,
            order,
            DateTimeOffset.UtcNow);

        if (!order.IsTerminal)
        {
            _strategyStateLabel = "ORDER PENDING";
            _strategyMessage =
                $"Robinhood order {order.State} is still active; all new LIVE orders are blocked.";
            NotifyStrategyProperties();
            return true;
        }

        BrokerOrderIntent intent = _activeLiveIntent;
        DateTimeOffset? triggerTimestamp = _activeLiveTriggerTimestamp;
        ClearActiveLiveOrderContext();

        _liveExecutionEngine!.ObserveTerminalOrder(order);
        if (order.State is BrokerOrderState.Filled)
        {
            if (triggerTimestamp is not null)
            {
                _tradeMarkers[triggerTimestamp.Value] = intent.Side is BrokerOrderSide.Buy
                    ? ChartTradeMarker.Buy
                    : ChartTradeMarker.Sell;
            }

            AddActivity(
                $"LIVE {intent.Side.ToString().ToUpperInvariant()} reached FILLED during reconciliation: {order.FilledQuantity:0.######} {intent.Symbol} @ {(order.AveragePrice ?? 0m):C2}.");
            LiveBrokerSnapshot reconciled = await CaptureLiveBrokerAsync(
                _ringBuffer.Instrument,
                cancellationToken);
            UpdateLiveAccount(reconciled, 0m);
            return true;
        }

        if (order.FilledQuantity > 0m)
        {
            LiveBrokerSnapshot partiallyFilled = await CaptureLiveBrokerAsync(
                _ringBuffer.Instrument,
                cancellationToken);
            UpdateLiveAccount(partiallyFilled, 0m);
        }

        AddActivity(
            $"LIVE order ended {order.State}: {order.RejectionReason ?? "no broker reason supplied"}. Execution has been disarmed.",
            "ERROR");
        DisarmLiveExecution(
            "A Robinhood order did not fill successfully; inspect the journal before re-arming.");
        return true;
    }

    private static void ValidatePlacedOrder(
        BrokerOrderSnapshot order,
        BrokerOrderIntent intent)
    {
        if (string.IsNullOrWhiteSpace(order.BrokerOrderId) ||
            order.State is BrokerOrderState.Unknown ||
            !string.Equals(order.Symbol, intent.Symbol, StringComparison.OrdinalIgnoreCase) ||
            order.Side != intent.Side ||
            order.RequestedQuantity != intent.Quantity)
        {
            throw new InvalidOperationException(
                "Robinhood returned an incomplete or mismatched order acknowledgement. LIVE execution is stopped; verify Robinhood immediately.");
        }
    }

    private void ClearActiveLiveOrderContext()
    {
        _activeLiveOrder = null;
        _activeLiveIntent = null;
        _activeLiveReview = null;
        _activeLiveTriggerTimestamp = null;
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

    private void UpdateLiveAccount(LiveBrokerSnapshot broker, decimal mark)
    {
        _paperBuyingPower = broker.Portfolio.BuyingPower;
        _paperEquity = broker.Portfolio.TotalValue;
        _paperPositionQuantity = broker.Position.Quantity;
        _paperAveragePrice = broker.Position.AverageBuyPrice;
        _paperUnrealizedProfitLoss = broker.Position.HasPosition && mark > 0m
            ? broker.Position.Quantity * (mark - broker.Position.AverageBuyPrice)
            : 0m;
        _paperEntries = _liveExecutionEngine?.EntriesToday ?? 0;
        OnPropertyChanged(nameof(AccountBalanceValue));
        OnPropertyChanged(nameof(BuyingPowerDisplay));
        OnPropertyChanged(nameof(AccountEquityDisplay));
        OnPropertyChanged(nameof(PositionDisplay));
        OnPropertyChanged(nameof(ProfitLossDisplay));
        OnPropertyChanged(nameof(EntriesDisplay));
    }

    private void DisarmLiveExecution(string reason)
    {
        if (SelectedMode is TradingMode.Live)
        {
            _modeState = _modeState.ActivateLiveDisarmed();
            NotifyModeProperties();
        }

        StatusMessage = reason;
    }

    private static DateTimeOffset GetEasternTradingDayStartUtc(DateTimeOffset nowUtc)
    {
        TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        DateTimeOffset easternNow = TimeZoneInfo.ConvertTime(nowUtc, eastern);
        DateTime easternDate = DateTime.SpecifyKind(easternNow.Date, DateTimeKind.Unspecified);
        TimeSpan offset = eastern.GetUtcOffset(easternDate);
        return new DateTimeOffset(easternDate, offset).ToUniversalTime();
    }

    private static bool IsRegularEquityMarketHours(DateTimeOffset nowUtc)
    {
        TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        DateTimeOffset easternNow = TimeZoneInfo.ConvertTime(nowUtc, eastern);
        TimeOnly time = TimeOnly.FromDateTime(easternNow.DateTime);
        return easternNow.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
               time >= new TimeOnly(9, 30) &&
               time < new TimeOnly(16, 0);
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
