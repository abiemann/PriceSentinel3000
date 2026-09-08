using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private const int AutomationResultLimit = 200;
    private readonly Dispatcher _automationDispatcher = Dispatcher.CurrentDispatcher;
    private readonly Queue<StrategyDecision> _automationDecisions = new();
    private readonly Queue<PaperFill> _automationFills = new();
    private bool _automationMutationInProgress;
    private bool _applyingAutomationConfiguration;
    private bool _automationStartRequested;
    private Guid? _automationOperationId;
    private Guid? _automationOperationSessionId;
    private bool _automationStopRequested;
    private string _automationOperationState = "idle";
    private string? _automationOperationError;
    private JournalSession? _automationSession;
    private string? _automationOutcome;
    private PaperAccountSnapshot? _automationAccount;
    private int _automationProcessedObservations;
    private int _automationTotalObservations;
    private int? _automationPauseAtObservation;
    private long? _automationPauseAtStrategyBar;
    private TaskCompletionSource? _automationStepCompletion;

    internal bool AutomationClosing { get; set; }

    /// <summary>Called only on the dispatcher that owns this running view model.</summary>
    internal async Task<AutomationResponse> HandleAutomationAsync(AutomationRequest request)
    {
        _automationDispatcher.VerifyAccess();
        try
        {
            if (_disposed) return AutomationResponse.Fail("closed", "The application is closed.");
            string command = request.Command;
            bool readOnly = command is "status" or "results" or "candles" or "indicators" or "events" or "capture_chart" or "library_datasets" or "library_candles" ||
                            command == "strategies" && !ReadArguments<StrategyArguments>(request).Refresh;
            if (!readOnly)
            {
                if (AutomationClosing || _shutdownTask is not null)
                    return AutomationResponse.Fail("closing", "The application is closing.");
                if (HasAutomationLiveContext)
                    return AutomationResponse.Fail("live_forbidden", "Automation only controls Replay and Paper Trader. Leave LIVE using the application.");
                if (_automationMutationInProgress)
                    return AutomationResponse.Fail("busy", "A session control operation is still finishing.");
            }

            switch (command)
            {
                case "status":
                    ReadArguments<EmptyArguments>(request);
                    return AutomationResponse.Ok(AutomationStatus());
                case "results":
                    ReadArguments<EmptyArguments>(request);
                    return AutomationResponse.Ok(AutomationResults());
                case "candles":
                    return ReadAutomationCandles(ReadArguments<CandleArguments>(request));
                case "indicators":
                    return ReadAutomationIndicators(ReadArguments<ResearchArguments>(request));
                case "events":
                    return ReadAutomationEvents(ReadArguments<ResearchPageArguments>(request));
                case "capture_chart":
                    return CaptureAutomationChart(request.Arguments);
                case "library_datasets":
                    return await ReadAutomationLibraryDatasetsAsync(ReadArguments<LibraryDatasetArguments>(request));
                case "library_candles":
                    return await ReadAutomationLibraryCandlesAsync(ReadArguments<LibraryCandleArguments>(request));
                case "strategies":
                    StrategyArguments scripts = ReadArguments<StrategyArguments>(request);
                    if (scripts.Refresh)
                    {
                        if (!IsSessionConfigurationEditable)
                            return AutomationResponse.Fail("busy", "Stop the session before refreshing strategies.");
                        RefreshScripts();
                    }
                    return AutomationResponse.Ok(new { strategies = AvailableStrategies.ToArray(), diagnostics = _catalogDiagnostics });
                case "configure":
                    return ConfigureAutomation(ReadArguments<ConfigureArguments>(request));
                case "start":
                    return StartAutomation(ReadArguments<ReplayArguments>(request));
                case "pause":
                    ReadArguments<EmptyArguments>(request);
                    RequireReplay(running: true);
                    PauseReplay();
                    return AutomationResponse.Ok(AutomationStatus());
                case "resume":
                    ReplayArguments resume = ReadArguments<ReplayArguments>(request);
                    RequireReplay(running: true, paused: true);
                    SetAutomationReplayControls(resume);
                    ResumeReplay();
                    return AutomationResponse.Ok(AutomationStatus());
                case "step":
                    ReadArguments<EmptyArguments>(request);
                    RequireReplay(running: true, paused: true);
                    _automationMutationInProgress = true;
                    _automationStepCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    bool previousFast = _replaySessionRunner.Fast;
                    try
                    {
                        SetAutomationReplayControls(new(PauseAfterObservations: 1, Fast: true));
                        ResumeReplay();
                        await _automationStepCompletion.Task;
                    }
                    finally
                    {
                        _replaySessionRunner.Fast = previousFast;
                        _automationStepCompletion = null;
                        _automationMutationInProgress = false;
                    }
                    return AutomationResponse.Ok(AutomationStatus());
                case "run_to_end":
                    ReadArguments<EmptyArguments>(request);
                    RequireReplay(running: true);
                    SetAutomationReplayControls(new(Fast: true));
                    ResumeReplay();
                    return AutomationResponse.Ok(AutomationStatus());
                case "stop":
                    ReadArguments<EmptyArguments>(request);
                    if (!IsSessionRunning && !_isStartingSession)
                        return AutomationResponse.Ok(AutomationStatus());
                    _automationMutationInProgress = true;
                    _automationStopRequested = true;
                    try { await StopSessionAsync(); }
                    finally { _automationMutationInProgress = false; }
                    return AutomationResponse.Ok(AutomationStatus());
                default:
                    return AutomationResponse.Fail("unknown_command", "Unknown automation command.");
            }
        }
        catch (JsonException exception)
        {
            return AutomationResponse.Fail("invalid_arguments", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return AutomationResponse.Fail("invalid_arguments", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return AutomationResponse.Fail("invalid_state", exception.Message);
        }
        catch (Exception exception)
        {
            return AutomationResponse.Fail("operation_failed", exception.Message);
        }
    }

    private bool HasAutomationLiveContext => SelectedMode is TradingMode.Live ||
        EffectiveMode is TradingMode.Live || _activeSession?.Mode is TradingMode.Live ||
        _liveOrderCoordinator.HasActiveContext;

    private AutomationResponse ConfigureAutomation(ConfigureArguments arguments)
    {
        if (!IsSessionConfigurationEditable)
            return AutomationResponse.Fail("busy", "Stop the session before changing its configuration.");
        TradingMode mode = arguments.Mode ?? SelectedMode;
        if (mode is not (TradingMode.Replay or TradingMode.PaperTrader))
            throw new ArgumentException("Mode must be Replay or PaperTrader.");

        JsonObject merged = JsonSerializer.SerializeToNode(CreateSettings(), AutomationProtocol.JsonOptions)!.AsObject();
        if (arguments.Settings is { } patch)
        {
            if (patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("Settings must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in patch.EnumerateObject())
            {
                if (!merged.ContainsKey(property.Name) || !names.Add(property.Name) || property.Value.ValueKind is JsonValueKind.Null)
                    throw new ArgumentException($"Unknown, duplicate, or null setting: {property.Name}.");
                merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }
        TradingSessionSettings candidate = merged.Deserialize<TradingSessionSettings>(AutomationProtocol.JsonOptions)!;
        candidate = candidate with { Symbol = candidate.Symbol.Trim().ToUpperInvariant() };
        IReadOnlyList<string> errors = TradingSessionSettingsValidator.Validate(candidate);
        if (errors.Count > 0) return AutomationResponse.Fail("invalid_configuration", string.Join(" ", errors));
        ValidateAutomationStrategy(candidate);

        // Validate the complete candidate before any setters or preference writes run.
        _applyingAutomationConfiguration = true;
        try
        {
            Symbol = candidate.Symbol;
            StartingBalance = candidate.StartingBalance;
            TradesSettleImmediately = candidate.TradesSettleImmediately;
            PositionSizeBasis = candidate.PositionSizeBasis;
            PositionSizeValue = candidate.PositionSizeValue;
            QuantityLimitMode = candidate.QuantityLimitMode;
            MaximumQuantity = candidate.MaximumQuantity;
            UnlimitedEntries = candidate.UnlimitedEntries;
            MaximumEntriesPerDay = candidate.MaximumEntriesPerDay;
            MaximumDailyLossBasis = candidate.MaximumDailyLossBasis;
            MaximumDailyLossValue = candidate.MaximumDailyLossValue;
            StopLossBasis = candidate.StopLossBasis;
            StopLossValue = candidate.StopLossValue;
            BufferMinutes = candidate.BufferMinutes;
            QuotePollingSeconds = candidate.QuotePollingSeconds;
            SelectedStrategyId = candidate.StrategyId;
            ScriptBarIntervalSeconds = candidate.ScriptBarIntervalSeconds;
            ChartCandleIntervalSeconds = candidate.ChartCandleIntervalSeconds;
            ReconciliationSeconds = candidate.ReconciliationSeconds;
            ReconciliationLookbackSeconds = candidate.ReconciliationLookbackSeconds;
            ReconciliationCompletionDelaySeconds = candidate.ReconciliationCompletionDelaySeconds;
            ReplayDate = candidate.ReplayDate;
            ReplayTime = candidate.ReplayTime;
            ReplayEndTime = candidate.ReplayEndTime;
            ReplaySpeed = candidate.ReplaySpeed;
            RequestModeSelection(mode);
        }
        finally { _applyingAutomationConfiguration = false; }
        SavePreferences();
        return AutomationResponse.Ok(AutomationStatus());
    }

    private void ValidateAutomationStrategy(TradingSessionSettings settings)
    {
        PinnedStrategy pinned = _strategyCatalog.GetPinned(settings.StrategyId);
        if (pinned.Program is not null &&
            (long)(pinned.Program.RequiredWarmupBars + 2) * settings.ScriptBarIntervalSeconds > 86_400)
            throw new ArgumentException("The selected script requires more than 24 hours of warm-up.");
    }

    private AutomationResponse StartAutomation(ReplayArguments arguments)
    {
        if (SelectedMode is not (TradingMode.Replay or TradingMode.PaperTrader))
            throw new InvalidOperationException("Configure Replay or PaperTrader before starting.");
        if (IsSessionRunning || _isStartingSession || !StartSessionCommand.CanExecute(null))
            throw new InvalidOperationException("The session cannot start in its current state.");
        ValidateReplayArguments(arguments, SelectedMode);
        ValidateAutomationStrategy(CreateSettings());
        _automationOperationId = Guid.NewGuid();
        _automationOperationState = "starting";
        _automationOperationError = null;
        _automationOperationSessionId = null;
        _automationStopRequested = false;
        _automationProcessedObservations = 0;
        _automationTotalObservations = 0;
        _automationPauseAtObservation = arguments.PauseAfterObservations;
        _automationPauseAtStrategyBar = arguments.PauseAfterStrategyBars;
        _replaySessionRunner.Fast = arguments.Fast ?? false;
        Guid operationId = _automationOperationId.Value;
        _automationStartRequested = true;
        Task session;
        try { session = StartSessionCommand.ExecuteAsync(); }
        finally { _automationStartRequested = false; }
        _ = ObserveAutomationSessionAsync(session, operationId);
        if (_automationOperationState == "failed")
            return AutomationResponse.Fail("start_failed", _automationOperationError ?? StatusMessage);
        return AutomationResponse.Ok(AutomationStatus());
    }

    private async Task ObserveAutomationSessionAsync(Task task, Guid operationId)
    {
        try
        {
            await task;
            if (_automationOperationId != operationId) return;
            if (_automationStopRequested || AutomationClosing) _automationOperationState = "stopped";
            else if (_automationOperationSessionId.HasValue && _automationOutcome is "COMPLETED") _automationOperationState = "completed";
            else if (_automationOperationSessionId.HasValue && _automationOutcome is "STOPPED_BY_USER" or "APPLICATION_CLOSED") _automationOperationState = "stopped";
            else if (!_automationOperationSessionId.HasValue || _automationOutcome is "ERROR" || !IsSessionRunning)
            {
                _automationOperationState = "failed";
                _automationOperationError = StatusMessage;
            }
        }
        catch (Exception exception)
        {
            if (_automationOperationId != operationId) return;
            _automationOperationState = "failed";
            _automationOperationError = exception.Message;
        }
    }

    private void RequireReplay(bool running, bool paused = false)
    {
        if (EffectiveMode is not TradingMode.Replay || (running && !IsSessionRunning) ||
            (paused && !IsReplayPaused))
            throw new InvalidOperationException(paused ? "Replay must be paused." : "Replay must be running.");
    }

    private void ValidateReplayArguments(ReplayArguments arguments, TradingMode mode)
    {
        if (mode is not TradingMode.Replay &&
            (arguments.Fast.HasValue || arguments.PauseAfterObservations.HasValue || arguments.PauseAfterStrategyBars.HasValue))
            throw new ArgumentException("Playback controls only apply to Replay; Paper Trader follows real time.");
        if (arguments.PauseAfterObservations is <= 0 || arguments.PauseAfterStrategyBars is <= 0 ||
            arguments.PauseAfterObservations.HasValue && arguments.PauseAfterStrategyBars.HasValue)
            throw new ArgumentException("Specify one positive observation or strategy-bar pause limit.");
        if (arguments.PauseAfterStrategyBars.HasValue && SelectedStrategyId == StrategyDescriptor.BuiltInId)
            throw new ArgumentException("A strategy-bar pause limit requires a scripted strategy.");
    }

    private void SetAutomationReplayControls(ReplayArguments arguments)
    {
        ValidateReplayArguments(arguments, TradingMode.Replay);
        _automationPauseAtObservation = arguments.PauseAfterObservations is { } observations
            ? checked(_automationProcessedObservations + observations) : null;
        _automationPauseAtStrategyBar = arguments.PauseAfterStrategyBars is { } bars
            ? (_scriptSignalEngine?.Bars.CompletedBarCount ?? 0) + bars : null;
        if (arguments.Fast.HasValue) _replaySessionRunner.Fast = arguments.Fast.Value;
    }

    private object AutomationStatus() => new
    {
        protocolVersion = 1,
        appVersion = PriceSentinel3000.Application.BuildVersion.Display(typeof(MainViewModel).Assembly),
        processId = Environment.ProcessId,
        operationId = _automationOperationId,
        operationState = _automationOperationState,
        operationError = _automationOperationError,
        sessionId = HasAutomationLiveContext ? null : _activeSession?.Id ?? _automationOperationSessionId,
        selectedMode = SelectedMode,
        effectiveMode = EffectiveMode,
        running = IsSessionRunning,
        starting = _isStartingSession && !IsSessionRunning,
        paused = IsReplayPaused,
        processedObservations = _automationProcessedObservations,
        totalObservations = _automationTotalObservations,
        completedStrategyBars = HasAutomationLiveContext ? (long?)null : _scriptSignalEngine?.Bars.CompletedBarCount ?? 0,
        fast = _replaySessionRunner.Fast,
        settings = CreateSettings(),
        strategy = HasAutomationLiveContext ? null : _pinnedStrategy?.Descriptor,
        replayHistory = HasAutomationLiveContext || _automationOperationId.HasValue && !_automationOperationSessionId.HasValue
            ? null : AutomationReplayHistory,
        message = HasAutomationLiveContext ? "LIVE cannot be controlled by automation." : StatusMessage,
    };

    private object AutomationResults() => new
    {
        sessionId = _automationSession?.Id,
        mode = _automationSession?.Mode,
        outcome = _automationOutcome,
        replayHistory = AutomationReplayHistory,
        settings = _automationSession is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(_automationSession.SettingsJson),
        account = _automationAccount,
        summary = _automationSession is null ? null : _journal.GetSummary(_automationSession.Id),
        decisions = _automationDecisions.ToArray(),
        fills = _automationFills.ToArray(),
        recordLimit = AutomationResultLimit,
    };

    private void ResetAutomationForUiStart()
    {
        if (_automationStartRequested) return;
        _automationOperationId = null;
        _automationOperationSessionId = null;
        _automationOperationState = "idle";
        _automationOperationError = null;
        _automationStopRequested = false;
        _automationProcessedObservations = 0;
        _automationTotalObservations = 0;
        _automationPauseAtObservation = null;
        _automationPauseAtStrategyBar = null;
        _replaySessionRunner.Fast = false;
    }

    private void CaptureAutomationSession()
    {
        RecordAutomationChartSession();
        if (_activeSession?.Mode is not (TradingMode.Replay or TradingMode.PaperTrader)) return;
        _automationSession = _activeSession;
        _automationOperationSessionId = _automationOperationId.HasValue ? _activeSession.Id : null;
        _automationOutcome = null;
        decimal balance = _activeSession.StartingBalance;
        _automationAccount = new(balance, balance, balance, 0m, 0m, 0m, 0m, 0m, 0, false);
        _automationProcessedObservations = 0;
        _automationTotalObservations = 0;
        _automationDecisions.Clear();
        _automationFills.Clear();
        ResetAutomationResearch();
        if (_automationOperationId.HasValue) _automationOperationState = "running";
    }

    private void CaptureAutomationDecision(PaperTradeResult result)
    {
        CaptureAutomationResearchDecision(result);
        _automationAccount = result.Account;
        _automationDecisions.Enqueue(result.Decision);
        if (_automationDecisions.Count > AutomationResultLimit) _automationDecisions.Dequeue();
        if (result.Fill is null) return;
        _automationFills.Enqueue(result.Fill);
        if (_automationFills.Count > AutomationResultLimit) _automationFills.Dequeue();
    }

    private void AutomationReplayBoundary(int processed, int total)
    {
        _automationProcessedObservations = processed;
        _automationTotalObservations = total;
        if (processed < total &&
            (_automationPauseAtObservation is { } observations && processed >= observations ||
             _automationPauseAtStrategyBar is { } bars && (_scriptSignalEngine?.Bars.CompletedBarCount ?? 0) >= bars))
        {
            _automationPauseAtObservation = null;
            _automationPauseAtStrategyBar = null;
            PauseReplay();
            _automationStepCompletion?.TrySetResult();
        }
    }

    private void CompleteAutomationSession(string outcome)
    {
        if (_activeSession?.Mode is not (TradingMode.Replay or TradingMode.PaperTrader)) return;
        _automationOutcome = outcome;
        // Publish completion only after the start command has fully unwound in
        // ObserveAutomationSessionAsync, so a completed replay can be restarted.
        if (_automationOperationId.HasValue && outcome != "COMPLETED")
            _automationOperationState = outcome == "ERROR" ? "failed" : "stopped";
        if (outcome != "ERROR") _automationOperationError = null;
        _automationStepCompletion?.TrySetResult();
    }

    private static T ReadArguments<T>(AutomationRequest request)
    {
        if (request.Arguments.ValueKind == JsonValueKind.Undefined)
            return JsonSerializer.Deserialize<T>("{}", AutomationProtocol.JsonOptions)!;
        if (request.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Arguments must be an object.");
        return request.Arguments.Deserialize<T>(AutomationProtocol.JsonOptions)!;
    }

    private sealed record EmptyArguments;
    private sealed record StrategyArguments(bool Refresh = false);
    private sealed record ConfigureArguments(TradingMode? Mode = null, JsonElement? Settings = null);
    private sealed record ReplayArguments(int? PauseAfterObservations = null, int? PauseAfterStrategyBars = null, bool? Fast = null);
}
