using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
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
    private bool _isSimulationRunning;
    private bool _liveRiskAcknowledged;
    private string _statusMessage;

    public MainViewModel()
    {
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

        StartSimulationCommand = new RelayCommand(
            StartSimulation,
            () => SelectedMode is TradingMode.Simulation &&
                  EffectiveMode is TradingMode.Simulation &&
                  !IsSimulationRunning);
        StopSimulationCommand = new RelayCommand(StopSimulation, () => IsSimulationRunning);

        RebuildBufferSegments();
        AddActivity("Application started with operating mode OFF.");
        AddActivity("Stage 2 controls loaded; market and broker adapters are offline.");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SelectionOption<AmountBasis>> PositionSizeOptions { get; }
    public IReadOnlyList<SelectionOption<AmountBasis>> DailyLossOptions { get; }
    public IReadOnlyList<SelectionOption<StopLossBasis>> StopLossOptions { get; }

    public ObservableCollection<BufferSegmentViewModel> BufferSegments { get; } = [];
    public ObservableCollection<ActivityEntryViewModel> ActivityLog { get; } = [];

    public RelayCommand StartSimulationCommand { get; }
    public RelayCommand StopSimulationCommand { get; }

    public TradingMode SelectedMode => _modeState.SelectedMode;
    public TradingMode EffectiveMode => _modeState.EffectiveMode;
    public string SelectedModeLabel => SelectedMode.ToString().ToUpperInvariant();
    public string EffectiveModeLabel => EffectiveMode.ToString().ToUpperInvariant();
    public bool LiveArmed => _modeState.LiveArmed;
    public bool LiveRiskAcknowledged => _liveRiskAcknowledged;
    public string LiveStateLabel => LiveArmed ? "LIVE ARMED" : "LIVE DISARMED";
    public string MarketDataStatus => "ADAPTER OFFLINE";
    public string CurrentPrice => "--";
    public string SessionStateLabel => EffectiveMode is TradingMode.Off
        ? "OFF"
        : IsSimulationRunning ? "RUNNING" : "READY";
    public string SessionStateBackground => EffectiveMode is TradingMode.Off ? "#202B39" : "#123528";
    public string SessionStateBorder => EffectiveMode is TradingMode.Off ? "#3A4B61" : "#24684C";
    public string SessionStateForeground => EffectiveMode is TradingMode.Off ? "#A8B6C7" : "#5EE6B1";
    public string SymbolDisplay => string.IsNullOrWhiteSpace(Symbol) ? "—" : Symbol.Trim().ToUpperInvariant();
    public string BuyingPowerDisplay => StartingBalance.ToString("C", CultureInfo.CurrentCulture);
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
        set => SetField(ref _quotePollingSeconds, value);
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

    public bool IsSimulationRunning
    {
        get => _isSimulationRunning;
        private set
        {
            if (SetField(ref _isSimulationRunning, value))
            {
                OnPropertyChanged(nameof(SessionStateLabel));
                StartSimulationCommand.RaiseCanExecuteChanged();
                StopSimulationCommand.RaiseCanExecuteChanged();
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
        _modeState = mode is TradingMode.Live
            ? _modeState.Select(mode)
            : _modeState.ActivateSafeMode(mode);

        if (mode is not TradingMode.Live)
        {
            IsSimulationRunning = false;
            StatusMessage = mode switch
            {
                TradingMode.Off => "System is OFF. Choose Replay, Simulation, or LIVE to begin.",
                TradingMode.Replay => "Replay selected. Recorded-session loading will be added with the data engine.",
                TradingMode.Simulation => "Simulation selected. Configure the account and risk controls, then start.",
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
        IsSimulationRunning = false;
        StatusMessage = "LIVE risk acknowledged. Robinhood authorization is not connected in Stage 2; LIVE remains disarmed.";
        AddActivity("LIVE risk acknowledged; waiting for the future Robinhood authorization adapter.");
        OnPropertyChanged(nameof(LiveRiskAcknowledged));
        NotifyModeProperties();
    }

    private void StartSimulation()
    {
        SimulationSettings settings = CreateSettings();
        IReadOnlyList<string> errors = SimulationSettingsValidator.Validate(settings);

        if (errors.Count > 0)
        {
            StatusMessage = $"Cannot start simulation: {errors[0]}";
            AddActivity($"Configuration rejected: {errors[0]}");
            return;
        }

        Symbol = settings.Symbol.Trim().ToUpperInvariant();
        _modeState = _modeState.ActivateSafeMode(TradingMode.Simulation);
        NotifyModeProperties();
        IsSimulationRunning = true;
        StatusMessage = $"Simulation initialized for {Symbol}. Waiting for the market-data adapter.";
        AddActivity($"Simulation initialized for {Symbol} with {StartingBalance:C} starting equity.");
    }

    private void StopSimulation()
    {
        IsSimulationRunning = false;
        StatusMessage = "Simulation stopped. No market orders were sent.";
        AddActivity("Simulation session stopped.");
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

    private void AddActivity(string message) =>
        ActivityLog.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), message));

    private void NotifyModeProperties()
    {
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(EffectiveMode));
        OnPropertyChanged(nameof(SelectedModeLabel));
        OnPropertyChanged(nameof(EffectiveModeLabel));
        OnPropertyChanged(nameof(LiveArmed));
        OnPropertyChanged(nameof(LiveStateLabel));
        OnPropertyChanged(nameof(SessionStateLabel));
        OnPropertyChanged(nameof(SessionStateBackground));
        OnPropertyChanged(nameof(SessionStateBorder));
        OnPropertyChanged(nameof(SessionStateForeground));
        StartSimulationCommand.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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
