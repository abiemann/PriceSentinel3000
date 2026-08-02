using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Infrastructure.MarketData;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan MaximumWarmStart = TimeSpan.FromMinutes(4);

    private readonly IMarketDataSource _syntheticDataSource;
    private readonly ITradingJournal _journal;
    private ModeState _modeState = ModeState.SafeDefault;
    private string _symbol;
    private decimal _startingBalance;
    private AmountBasis _positionSizeBasis;
    private decimal _positionSizeValue;
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
    private bool _isSessionRunning;
    private bool _liveRiskAcknowledged;
    private bool _journalReady;
    private bool _hasMarketData;
    private bool _isMarketDataConnected;
    private string _statusMessage;
    private string _currentPrice = "--";
    private string _bidAskDisplay = "-- / --";
    private string _marketDataStatus = "ADAPTER OFFLINE";
    private string _marketDataStateLabel = "OFFLINE";
    private string _strategyMessage = "Select Simulation or Replay to start the data engine.";
    private string _strategyStateLabel = "IDLE";
    private CancellationTokenSource? _sessionCancellation;
    private JournalSession? _activeSession;
    private PriceRingBuffer? _ringBuffer;
    private MarketDataRequest? _marketDataRequest;
    private bool _disposed;

    public MainViewModel()
        : this(
            new SyntheticMarketDataSource(),
            new SqliteTradingJournal(AppDataPaths.JournalDatabase))
    {
    }

    internal MainViewModel(
        IMarketDataSource syntheticDataSource,
        ITradingJournal journal)
    {
        _syntheticDataSource = syntheticDataSource;
        _journal = journal;

        SimulationSettings defaults = SimulationSettings.Default;
        _symbol = defaults.Symbol;
        _startingBalance = defaults.StartingBalance;
        _positionSizeBasis = defaults.PositionSizeBasis;
        _positionSizeValue = defaults.PositionSizeValue;
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
        _statusMessage = "Choose Replay, Simulation, or LIVE on the rotary selector to begin.";

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
        StopLossOptions =
        [
            new("Position loss ($)", StopLossBasis.FixedAmount),
            new("Buy price (%)", StopLossBasis.BuyPercentage),
        ];

        StartSessionCommand = new RelayCommand(
            StartSelectedSession,
            () => SelectedMode is TradingMode.Simulation or TradingMode.Replay &&
                  EffectiveMode == SelectedMode &&
                  !IsSessionRunning);
        StopSessionCommand = new RelayCommand(StopSession, () => IsSessionRunning);

        RebuildBufferSegments();
        InitializeJournal();
        AddActivity("Application started with operating mode OFF.");
        AddActivity(
            _journalReady
                ? "Stage 3 data engine loaded; SQLite journal is ready."
                : "Stage 3 data engine loaded; SQLite journal could not be initialized.",
            _journalReady ? "INFO" : "ERROR");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SelectionOption<AmountBasis>> PositionSizeOptions { get; }
    public IReadOnlyList<SelectionOption<AmountBasis>> DailyLossOptions { get; }
    public IReadOnlyList<SelectionOption<StopLossBasis>> StopLossOptions { get; }

    public ObservableCollection<BufferSegmentViewModel> BufferSegments { get; } = [];
    public ObservableCollection<ActivityEntryViewModel> ActivityLog { get; } = [];
    public ObservableCollection<PricePointViewModel> ChartPoints { get; } = [];

    public RelayCommand StartSessionCommand { get; }
    public RelayCommand StopSessionCommand { get; }

    public TradingMode SelectedMode => _modeState.SelectedMode;
    public TradingMode EffectiveMode => _modeState.EffectiveMode;
    public string SelectedModeLabel => SelectedMode.ToString().ToUpperInvariant();
    public string EffectiveModeLabel => EffectiveMode.ToString().ToUpperInvariant();
    public bool IsOffSelected => SelectedMode is TradingMode.Off;
    public bool IsReplaySelected => SelectedMode is TradingMode.Replay;
    public bool IsSimulationSelected => SelectedMode is TradingMode.Simulation;
    public bool IsLiveSelected => SelectedMode is TradingMode.Live;
    public bool LiveArmed => _modeState.LiveArmed;
    public bool LiveRiskAcknowledged => _liveRiskAcknowledged;
    public string LiveStateLabel => LiveArmed ? "LIVE ARMED" : "LIVE DISARMED";
    public string MarketDataStatus => _marketDataStatus;
    public string MarketDataStateLabel => _marketDataStateLabel;
    public string CurrentPrice => _currentPrice;
    public string BidAskDisplay => _bidAskDisplay;
    public bool HasMarketData => _hasMarketData;
    public string StrategyMessage => _strategyMessage;
    public string StrategyStateLabel => _strategyStateLabel;
    public string JournalStatus => _journalReady ? "SQLITE WAL" : "OFFLINE";
    public string PriceActionCaption => $"{QuotePollingSeconds} SECOND PRICE ACTION";
    public string PrimaryActionLabel =>
        SelectedMode is TradingMode.Replay ? "START REPLAY" : "START SIMULATION";
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
        StartingBalance.ToString("C", CultureInfo.CurrentCulture);
    public string BufferCaption => $"{BufferSegments.Count} × 1 MINUTE BLOCKS";

    public string Symbol
    {
        get => _symbol;
        set
        {
            if (SetField(ref _symbol, value))
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
            if (SetField(ref _startingBalance, value))
            {
                OnPropertyChanged(nameof(BuyingPowerDisplay));
            }
        }
    }

    public AmountBasis PositionSizeBasis
    {
        get => _positionSizeBasis;
        set => SetField(ref _positionSizeBasis, value);
    }

    public decimal PositionSizeValue
    {
        get => _positionSizeValue;
        set => SetField(ref _positionSizeValue, value);
    }

    public bool UnlimitedEntries
    {
        get => _unlimitedEntries;
        set => SetField(ref _unlimitedEntries, value);
    }

    public int MaximumEntriesPerDay
    {
        get => _maximumEntriesPerDay;
        set => SetField(ref _maximumEntriesPerDay, value);
    }

    public AmountBasis MaximumDailyLossBasis
    {
        get => _maximumDailyLossBasis;
        set => SetField(ref _maximumDailyLossBasis, value);
    }

    public decimal MaximumDailyLossValue
    {
        get => _maximumDailyLossValue;
        set => SetField(ref _maximumDailyLossValue, value);
    }

    public StopLossBasis StopLossBasis
    {
        get => _stopLossBasis;
        set => SetField(ref _stopLossBasis, value);
    }

    public decimal StopLossValue
    {
        get => _stopLossValue;
        set => SetField(ref _stopLossValue, value);
    }

    public int BufferMinutes
    {
        get => _bufferMinutes;
        set
        {
            if (SetField(ref _bufferMinutes, value))
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
            if (SetField(ref _quotePollingSeconds, value))
            {
                OnPropertyChanged(nameof(PriceActionCaption));
            }
        }
    }

    public int ReconciliationSeconds
    {
        get => _reconciliationSeconds;
        set => SetField(ref _reconciliationSeconds, value);
    }

    public int ReconciliationOverlapSeconds
    {
        get => _reconciliationOverlapSeconds;
        set => SetField(ref _reconciliationOverlapSeconds, value);
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
        if (IsSessionRunning)
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
                TradingMode.Off => "System is OFF. Choose Replay, Simulation, or LIVE to begin.",
                TradingMode.Replay => "Replay selected. Start to play the latest SQLite-recorded simulation for this symbol.",
                TradingMode.Simulation => "Simulation selected. Configure the account and data timing, then start.",
                _ => StatusMessage,
            };
            AddActivity($"{mode} mode selected.");
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

    public void AcknowledgeLiveRisk()
    {
        _liveRiskAcknowledged = true;
        _modeState = _modeState.ActivateLiveDisarmed();
        StatusMessage = "LIVE mode is effective, but broker execution remains disarmed until Robinhood authorization is connected.";
        AddActivity("LIVE mode entered disarmed; waiting for the future Robinhood authorization adapter.");
        OnPropertyChanged(nameof(LiveRiskAcknowledged));
        NotifyModeProperties();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_activeSession is not null)
        {
            StopActiveSession("APPLICATION_CLOSED", "Application closed; the data session was finalized.");
        }

        _sessionCancellation?.Dispose();
        _journal.Dispose();
    }

    private async void StartSelectedSession()
    {
        SimulationSettings settings = CreateSettings();
        IReadOnlyList<string> errors = SimulationSettingsValidator.Validate(settings);

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

        try
        {
            if (EffectiveMode is TradingMode.Replay)
            {
                await StartReplayAsync(instrument, settings);
            }
            else
            {
                await StartSyntheticSimulationAsync(instrument, settings);
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
    }

    private async Task StartSyntheticSimulationAsync(
        Instrument instrument,
        SimulationSettings settings)
    {
        PrepareDataSession(instrument, settings, TradingMode.Simulation);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan warmStart = TimeSpan.FromMinutes(
            Math.Min(settings.BufferMinutes, (int)MaximumWarmStart.TotalMinutes));
        _marketDataRequest = new(
            instrument,
            TimeSpan.FromSeconds(settings.QuotePollingSeconds),
            warmStart);
        IReadOnlyList<MarketQuote> history = _syntheticDataSource.GetHistory(
            _marketDataRequest,
            now - warmStart,
            now,
            now);
        _journal.AppendQuotes(_activeSession!.Id, history, QuoteIngestionKind.WarmStart);
        QuoteMergeResult warmMerge = _ringBuffer!.Merge(history);
        SetMarketDataState("SYNTHETIC FEED", "SYNTHETIC");
        RefreshMarketView();
        _strategyStateLabel = "OBSERVING";
        _strategyMessage = "Stage 3 is measuring price action; automated buy/sell signals remain disabled.";
        NotifyStrategyProperties();
        StatusMessage = $"Simulation is running for {instrument.Symbol}. Four-minute warm start loaded; live points arrive every {settings.QuotePollingSeconds} seconds.";
        AddActivity(
            $"Synthetic simulation started with {warmMerge.Added} warm-start quotes; no orders can be sent.");

        DateTimeOffset nextReconciliation =
            DateTimeOffset.UtcNow.AddSeconds(settings.ReconciliationSeconds);
        CancellationToken token = _sessionCancellation!.Token;

        while (true)
        {
            await Task.Delay(_marketDataRequest.PollingInterval, token);
            DateTimeOffset observedAt = DateTimeOffset.UtcNow;
            MarketQuote quote = _syntheticDataSource.GetQuote(_marketDataRequest, observedAt);
            _journal.AppendQuotes(_activeSession!.Id, [quote], QuoteIngestionKind.Live);
            _ringBuffer.Merge([quote]);

            if (observedAt >= nextReconciliation)
            {
                DateTimeOffset from =
                    observedAt.AddSeconds(
                        -(settings.ReconciliationSeconds +
                          settings.ReconciliationOverlapSeconds));
                IReadOnlyList<MarketQuote> verification = _syntheticDataSource.GetHistory(
                    _marketDataRequest,
                    from,
                    observedAt,
                    observedAt);
                _journal.AppendQuotes(
                    _activeSession.Id,
                    verification,
                    QuoteIngestionKind.Reconciliation);
                QuoteMergeResult merge = _ringBuffer.Merge(verification);
                AddActivity(
                    $"Reconciled {verification.Count} overlapping quotes: {merge.Duplicates} verified, {merge.Corrected} corrected.");
                nextReconciliation =
                    observedAt.AddSeconds(settings.ReconciliationSeconds);
            }

            RefreshMarketView();
        }
    }

    private async Task StartReplayAsync(
        Instrument instrument,
        SimulationSettings settings)
    {
        ReplaySourceSession? source = _journal.FindLatestReplaySource(instrument);

        if (source is null)
        {
            StatusMessage = $"No recorded simulation exists for {instrument.Symbol}. Run Simulation first, then return to Replay.";
            AddActivity($"Replay could not start: no simulation data exists for {instrument.Symbol}.", "WARNING");
            return;
        }

        IReadOnlyList<MarketQuote> recordedQuotes =
            _journal.ReadSessionQuotes(source.Id, instrument);

        if (recordedQuotes.Count == 0)
        {
            StatusMessage = $"The latest {instrument.Symbol} session contains no replayable quotes.";
            AddActivity($"Replay source {source.Id:D} contained no quotes.", "WARNING");
            return;
        }

        PrepareDataSession(instrument, settings, TradingMode.Replay);
        SetMarketDataState("SQLITE REPLAY", "REPLAY");
        _strategyStateLabel = "REPLAYING";
        _strategyMessage = "Recorded observations are being replayed; strategy orders remain disabled.";
        NotifyStrategyProperties();
        TimeSpan replayDelay = TimeSpan.FromMilliseconds(
            Math.Clamp(settings.QuotePollingSeconds * 100, 100, 1_000));
        StatusMessage = $"Replaying {recordedQuotes.Count} distinct {instrument.Symbol} quotes from SQLite at 10× speed.";
        AddActivity(
            $"Replay started from simulation {source.Id:D} with {recordedQuotes.Count} distinct quotes.");
        CancellationToken token = _sessionCancellation!.Token;

        foreach (MarketQuote recorded in recordedQuotes)
        {
            await Task.Delay(replayDelay, token);
            MarketQuote replayed = recorded with { ObservedAtUtc = DateTimeOffset.UtcNow };
            _journal.AppendQuotes(_activeSession!.Id, [replayed], QuoteIngestionKind.Replay);
            _ringBuffer!.Merge([replayed]);
            RefreshMarketView();
        }

        JournalSummary summary = _journal.GetSummary(_activeSession!.Id);
        AddActivity($"Replay completed after {summary.QuoteCount} recorded observations.");
        StopActiveSession(
            "COMPLETED",
            $"Replay completed for {instrument.Symbol}. The chart remains available for inspection.");
    }

    private void PrepareDataSession(
        Instrument instrument,
        SimulationSettings settings,
        TradingMode mode)
    {
        _sessionCancellation?.Dispose();
        _sessionCancellation = new();
        _ringBuffer = new(instrument, TimeSpan.FromMinutes(settings.BufferMinutes));
        _marketDataRequest = null;
        ChartPoints.Clear();
        _hasMarketData = false;
        _currentPrice = "--";
        _bidAskDisplay = "-- / --";
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
            : "Select Simulation or Replay to start the data engine.";
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

        foreach (MarketQuote quote in snapshot)
        {
            ChartPoints.Add(new(quote.SourceTimestampUtc, quote.Last));
        }

        MarketQuote latest = snapshot[^1];
        _currentPrice = latest.Last.ToString("$0.00", CultureInfo.InvariantCulture);
        _bidAskDisplay =
            $"{latest.Bid.ToString("0.00", CultureInfo.InvariantCulture)} / {latest.Ask.ToString("0.00", CultureInfo.InvariantCulture)}";
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

    private SimulationSettings CreateSettings() => new()
    {
        Symbol = Symbol.Trim().ToUpperInvariant(),
        StartingBalance = StartingBalance,
        PositionSizeBasis = PositionSizeBasis,
        PositionSizeValue = PositionSizeValue,
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
        OnPropertyChanged(nameof(IsSimulationSelected));
        OnPropertyChanged(nameof(IsLiveSelected));
        OnPropertyChanged(nameof(LiveArmed));
        OnPropertyChanged(nameof(LiveStateLabel));
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
