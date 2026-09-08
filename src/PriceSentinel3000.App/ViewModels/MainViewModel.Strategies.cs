using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IStrategyCatalog _strategyCatalog;
    private string _selectedStrategyId;
    private int _scriptBarIntervalSeconds;
    private string _scriptDiagnostics = string.Empty;
    private IReadOnlyList<StrategyCatalogDiagnostic> _catalogDiagnostics = [];
    private PinnedStrategy? _pinnedStrategy;
    private ThinkScriptSignalEngine? _scriptSignalEngine;

    public ObservableCollection<StrategyDescriptor> AvailableStrategies { get; private set; } = [];
    public RelayCommand RefreshScriptsCommand { get; }
    public RelayCommand OpenScriptsFolderCommand { get; }
    public string ScriptsDirectory => _strategyCatalog.DirectoryPath;
    public string ScriptDiagnostics => _scriptDiagnostics;
    public bool HasScriptDiagnostics => _scriptDiagnostics.Length > 0;
    public bool IsExternalStrategySelected => SelectedStrategyId != StrategyDescriptor.BuiltInId;
    private StrategyDescriptor? SelectedScriptDescriptor =>
        !IsSessionConfigurationEditable && _pinnedStrategy?.Descriptor.Id == SelectedStrategyId
            ? _pinnedStrategy.Descriptor
            : AvailableStrategies.FirstOrDefault(s => s.Id == SelectedStrategyId);
    public int? TestedScriptIntervalSeconds => SelectedScriptDescriptor?.TestedCandleIntervalSeconds;
    public string TestedScriptIntervalLabel => TestedScriptIntervalSeconds is { } seconds
        ? $"Tested interval: {FormatScriptInterval(seconds)}"
        : "Tested interval not specified.";
    public bool HasScriptIntervalMismatch => IsExternalStrategySelected &&
        TestedScriptIntervalSeconds is { } seconds && seconds != ScriptBarIntervalSeconds;
    public string ScriptIntervalWarning => HasScriptIntervalMismatch
        ? "This changes the strategy's behavior; previous test results may not apply."
        : string.Empty;
    public IReadOnlyList<SelectionOption<int>> ScriptBarIntervalOptions { get; } =
        [new("15 sec", 15), new("30 sec", 30), new("1 min", 60), new("2 min", 120), new("5 min", 300)];

    public StrategyDescriptor? SelectedStrategy =>
        AvailableStrategies.FirstOrDefault(s => s.Id == SelectedStrategyId);

    public string SelectedStrategyId
    {
        get => _selectedStrategyId;
        set
        {
            // A WPF collection refresh can temporarily clear SelectedValue. Preserve the
            // persisted choice so a missing file cannot silently switch to Built-In.
            if (!string.IsNullOrWhiteSpace(value) && SetPreferenceField(ref _selectedStrategyId, value))
            {
                OnPropertyChanged(nameof(IsExternalStrategySelected));
                if (!_applyingAutomationConfiguration && TestedScriptIntervalSeconds is { } seconds)
                    ScriptBarIntervalSeconds = seconds;
                UpdateScriptDiagnostics();
            }
        }
    }

    public int ScriptBarIntervalSeconds
    {
        get => _scriptBarIntervalSeconds;
        set
        {
            if (SetPreferenceField(ref _scriptBarIntervalSeconds, value))
                NotifyTestedScriptInterval();
        }
    }

    internal Func<string, bool>? ExternalScriptApprovalPrompt { get; set; }

    public void RefreshScripts()
    {
        if (!IsSessionConfigurationEditable) return;
        try
        {
            StrategyCatalogSnapshot catalog = _strategyCatalog.Load();
            // A Clear/Add cycle can leave WPF's SelectedValue intact but its
            // SelectedItem empty. Replace the snapshot so selection is resolved once.
            AvailableStrategies = new(catalog.Strategies);
            OnPropertyChanged(nameof(AvailableStrategies));
            _catalogDiagnostics = catalog.Diagnostics;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _catalogDiagnostics = [new("Scripts folder", exception.Message)];
        }
        OnPropertyChanged(nameof(SelectedStrategyId));
        UpdateScriptDiagnostics();
    }

    private void UpdateScriptDiagnostics()
    {
        StrategyDescriptor? selected = AvailableStrategies.FirstOrDefault(s => s.Id == SelectedStrategyId);
        _scriptDiagnostics = string.Join(Environment.NewLine, _catalogDiagnostics
            .Where(d => d.IsError || d.FileName == selected?.FileName)
            .GroupBy(d => new { d.FileName, d.Message, d.IsError })
            .Select(group => $"{group.Key.FileName}: {(group.Key.IsError ? "Excluded" : "Note")} — {group.Key.Message}" +
                (group.Any(d => d.Line.HasValue) ? $" (lines {string.Join(", ", group.Where(d => d.Line.HasValue).Select(d => d.Line).Distinct())})" : "")));
        if (selected is null)
        {
            _scriptDiagnostics = $"Selected script is unavailable. Restore it or select another strategy.\n{_scriptDiagnostics}";
        }
        OnPropertyChanged(nameof(ScriptDiagnostics));
        OnPropertyChanged(nameof(HasScriptDiagnostics));
        OnPropertyChanged(nameof(SelectedStrategy));
        NotifyTestedScriptInterval();
    }

    private static string FormatScriptInterval(int seconds) => seconds switch
    {
        60 => "1 minute",
        120 => "2 minutes",
        300 => "5 minutes",
        _ => $"{seconds} seconds",
    };

    private void NotifyTestedScriptInterval()
    {
        OnPropertyChanged(nameof(TestedScriptIntervalSeconds));
        OnPropertyChanged(nameof(TestedScriptIntervalLabel));
        OnPropertyChanged(nameof(HasScriptIntervalMismatch));
        OnPropertyChanged(nameof(ScriptIntervalWarning));
    }

    private void SynchronizeStrategyDescriptor(StrategyDescriptor descriptor)
    {
        StrategyDescriptor? previous = AvailableStrategies.FirstOrDefault(s => s.Id == descriptor.Id);
        if (previous != descriptor)
        {
            // Replace the snapshot as RefreshScripts does so WPF resolves SelectedItem
            // even when the file was discovered or changed after the selector loaded.
            AvailableStrategies = new(previous is null
                ? AvailableStrategies.Append(descriptor)
                : AvailableStrategies.Select(s => s.Id == descriptor.Id ? descriptor : s));
            OnPropertyChanged(nameof(AvailableStrategies));
        }
        OnPropertyChanged(nameof(SelectedStrategyId));
        UpdateScriptDiagnostics();
    }

    private void OpenScriptsFolder()
    {
        if (!IsSessionConfigurationEditable || string.IsNullOrEmpty(ScriptsDirectory)) return;
        try
        {
            Directory.CreateDirectory(ScriptsDirectory);
            Process.Start(new ProcessStartInfo(ScriptsDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = $"Cannot open scripts folder: {exception.Message}";
        }
    }

    private void PinSelectedStrategy(TradingSessionSettings settings, TradingMode mode)
    {
        _pinnedStrategy = null;
        _scriptSignalEngine = null;
        PinnedStrategy pinned = _strategyCatalog.GetPinned(settings.StrategyId);
        if (pinned.Program is not null)
        {
            if ((long)(pinned.Program.RequiredWarmupBars + 2) * settings.ScriptBarIntervalSeconds > 86_400)
            {
                throw new InvalidOperationException("This script needs more than 24 hours of warm-up at the selected candle interval. Choose a shorter interval or reduce its lookbacks.");
            }
            if (mode is TradingMode.Live && ExternalScriptApprovalPrompt?.Invoke(
                    $"Run {pinned.Descriptor.Name} in LIVE using {settings.ScriptBarIntervalSeconds}-second completed candles?\n\n" +
                    $"File: {pinned.Descriptor.FileName}\nSHA-256: {pinned.Descriptor.SourceSha256}\n\n" +
                    "This script can propose real trades. Position sizing, entry limits, stop loss, and daily loss limits remain controlled by PriceSentinel. Review and test this version in Paper before approving it.") is not true)
            {
                throw new InvalidOperationException("The selected script version was not approved for this LIVE session.");
            }
            _scriptSignalEngine = new(pinned.Program, settings.ScriptBarIntervalSeconds);
        }
        _pinnedStrategy = pinned;
        SynchronizeStrategyDescriptor(pinned.Descriptor);
    }

    private IPriceActionSignalEngine CreateSessionSignalEngine(TradingSessionSettings settings) =>
        settings.StrategyId == StrategyDescriptor.BuiltInId
            ? new PriceActionSignalEngine(settings.BufferMinutes)
            : _scriptSignalEngine ?? throw new InvalidOperationException("The selected script has not been validated for this session.");

    private void AddStrategyProvenance(JsonNode settingsNode, TradingSessionSettings settings)
    {
        PinnedStrategy pinned = _pinnedStrategy ?? new(StrategyDescriptor.BuiltIn, null, null);
        settingsNode["Strategy"] = JsonSerializer.SerializeToNode(new
        {
            pinned.Descriptor.Id, pinned.Descriptor.Name, pinned.Descriptor.FileName,
            pinned.Descriptor.SourceSha256, pinned.Descriptor.RuntimeVersion, pinned.Source,
            HostVersion = typeof(MainViewModel).Assembly.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
            Inputs = pinned.Program?.DefaultInputs,
            CandleIntervalSeconds = pinned.Program is null ? (int?)null : settings.ScriptBarIntervalSeconds,
            pinned.Descriptor.TestedCandleIntervalSeconds,
            DataModel = pinned.Program is null ? "price-action-v1" : "completed-price-bars-v1",
        });
    }

    private void ObserveScriptQuote(MarketQuote quote, IReadOnlyList<MarketQuote>? warmStart = null)
    {
        if (_scriptSignalEngine is null || _ringBuffer?.IsValidQuote(quote) is not true || !IsFreshObservation(quote)) return;
        if (warmStart is not null)
        {
            _scriptSignalEngine.Bars.SeedHistory(warmStart.Where(_ringBuffer.IsValidQuote), quote.SourceTimestampUtc);
        }
        _scriptSignalEngine.Bars.ObserveQuote(quote);
    }

    private string ScriptMetrics => $"{_pinnedStrategy?.Descriptor.Name}  |  {ScriptBarIntervalSeconds}s candles  |  {_scriptSignalEngine?.Bars.Snapshot().Count} completed";

    private sealed class BuiltInOnlyStrategyCatalog : IStrategyCatalog
    {
        public string DirectoryPath => string.Empty;
        public StrategyCatalogSnapshot Load() => new([StrategyDescriptor.BuiltIn], []);
        public PinnedStrategy GetPinned(string id) => id == StrategyDescriptor.BuiltInId
            ? new(StrategyDescriptor.BuiltIn, null, null)
            : throw new InvalidOperationException("The selected script is unavailable.");
    }
}
