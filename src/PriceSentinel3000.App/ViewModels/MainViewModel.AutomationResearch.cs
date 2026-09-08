using System.Text.Json;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private const int ResearchRecordLimit = 4096;
    private const int ResearchByteLimit = 12 * 1024 * 1024;
    private const int ResearchPageByteLimit = 600 * 1024;
    private readonly ResearchBuffer _researchSources = new();
    private readonly ResearchBuffer _researchCandles = new();
    private readonly ResearchBuffer _researchEvents = new();
    private long _researchObservationSequence;
    private long _researchEventSequence;
    private long _researchEvaluationSequence;
    private long _researchCompletedBars;
    private long _researchBarVersion = -1;
    private int _researchRetainedBars;
    private int? _researchRequiredBars;
    private DateTimeOffset? _researchHistoryStartsAtUtc;
    private ScriptEvaluationSnapshot? _researchLastEvaluation;
    private StrategyDecision? _researchBuiltInDecision;
    private bool _researchLastStrategyEvaluated;
    private string? _researchLastRiskOverride;
    private DateTimeOffset? _researchObservedThroughUtc;
    private JsonElement? _researchStrategy;

    private void ResetAutomationResearch()
    {
        _researchSources.Clear();
        _researchCandles.Clear();
        _researchEvents.Clear();
        _researchObservationSequence = _researchEventSequence = _researchEvaluationSequence = 0;
        _researchCompletedBars = 0;
        _researchBarVersion = -1;
        _researchRetainedBars = 0;
        _researchRequiredBars = _scriptSignalEngine?.RequiredWarmupBars;
        _researchHistoryStartsAtUtc = _researchObservedThroughUtc = null;
        _researchLastEvaluation = null;
        _researchBuiltInDecision = null;
        _researchLastStrategyEvaluated = false;
        _researchLastRiskOverride = null;
        JsonElement settings = JsonSerializer.Deserialize<JsonElement>(_automationSession!.SettingsJson);
        _researchStrategy = settings.TryGetProperty("Strategy", out JsonElement strategy)
            ? JsonSerializer.SerializeToElement(strategy.EnumerateObject()
                .Where(property => property.Name != "Source")
                .ToDictionary(property => property.Name, property => property.Value), AutomationProtocol.JsonOptions)
            : null;
    }

    private void CaptureAutomationObservation(MarketQuote execution, MarketQuote source, bool historical)
    {
        if (_activeSession?.Id != _automationSession?.Id || _automationSession is null) return;
        long sequence = ++_researchObservationSequence;
        DateTimeOffset availableAt = historical ? source.SourceEndsAtUtc : source.SourceTimestampUtc;
        if (_researchObservedThroughUtc is null || availableAt > _researchObservedThroughUtc)
            _researchObservedThroughUtc = availableAt;
        _researchLastStrategyEvaluated = false;
        _researchLastRiskOverride = null;
        _researchSources.Add(sequence, new
        {
            sequence,
            kind = historical ? "historical_candle" : "sampled_quote",
            source.SourceTimestampUtc, source.ObservedAtUtc,
            availableAtUtc = availableAt,
            evaluationTimestampUtc = execution.SourceTimestampUtc,
            startsAtUtc = source.SourceTimestampUtc,
            endsAtUtc = historical ? (DateTimeOffset?)availableAt : null,
            intervalSeconds = historical ? (int?)source.SourceIntervalSeconds : null,
            finalized = historical,
            open = source.CandleOpen, high = source.CandleHigh, low = source.CandleLow, close = source.CandleClose,
            source.Last, source.Bid, source.Ask,
            volume = source.HasKnownVolume ? (decimal?)source.Volume : null,
            fresh = historical || IsFreshObservation(execution),
        });
        if (_scriptSignalEngine is not { } engine || _researchBarVersion == engine.Bars.Version) return;
        var bars = engine.Bars.Snapshot();
        long completed = engine.Bars.CompletedBarCount;
        for (int index = 0; index < bars.Count; index++)
        {
            long barSequence = completed - bars.Count + index + 1;
            if (barSequence <= _researchCompletedBars) continue;
            var bar = bars[index];
            _researchCandles.Add(barSequence, new
            {
                sequence = barSequence, observationSequence = sequence,
                availableAtUtc = availableAt, barVersion = engine.Bars.Version,
                bar.StartsAtUtc, bar.EndsAtUtc, bar.Open, bar.High, bar.Low, bar.Close,
                volume = bar.HasKnownVolume ? (decimal?)bar.Volume : null,
                finalized = true,
            });
        }
        _researchCompletedBars = completed;
        _researchRetainedBars = bars.Count;
        _researchBarVersion = engine.Bars.Version;
        _researchHistoryStartsAtUtc = bars.Count > 0 ? bars[0].StartsAtUtc : null;
    }

    private void CaptureAutomationResearchDecision(PaperTradeResult result)
    {
        if (_activeSession?.Id != _automationSession?.Id || _automationSession is null) return;
        ScriptEvaluationSnapshot? evaluation = _scriptSignalEngine?.LastEvaluation;
        bool scriptEvaluated = result.StrategyEvaluated && evaluation is not null &&
                               !ReferenceEquals(evaluation, _researchLastEvaluation);
        if (scriptEvaluated)
        {
            _researchLastEvaluation = evaluation;
            _researchEvaluationSequence++;
        }
        if (_scriptSignalEngine is null && result.StrategyProposal is not null)
            _researchBuiltInDecision = result.StrategyProposal;
        _researchLastStrategyEvaluated = result.StrategyEvaluated;
        _researchLastRiskOverride = result.RiskOverride;
        long sequence = ++_researchEventSequence;
        _researchEvents.Add(sequence, new
        {
            sequence, observationSequence = _researchObservationSequence,
            result.Decision.EvaluatedAtUtc,
            result.StrategyEvaluated, scriptEvaluated,
            evaluationSequence = result.StrategyEvaluated && _researchLastEvaluation is not null
                ? (long?)_researchEvaluationSequence : null,
            // Only the event that actually ran the script embeds its values. Waiting
            // events refer back to that evaluation; risk preemption has no proposal.
            scriptEvaluation = scriptEvaluated ? ResearchEvaluation(evaluation!) : null,
            result.StrategyProposal, result.RiskOverride, result.Decision,
            result.Order, result.Fill, result.Account,
        });
    }

    private AutomationResponse ReadAutomationCandles(CandleArguments arguments)
    {
        RequireResearchSession(arguments.SessionId);
        ValidateResearchPage(arguments.AfterSequence, arguments.Limit);
        ResearchBuffer buffer = arguments.Kind switch
        {
            "strategy" => _researchCandles,
            "source" => _researchSources,
            _ => throw new ArgumentException("Kind must be strategy or source."),
        };
        return AutomationResponse.Ok(ResearchPage(buffer, arguments.AfterSequence, arguments.Limit));
    }

    private AutomationResponse ReadAutomationEvents(ResearchPageArguments arguments)
    {
        RequireResearchSession(arguments.SessionId);
        ValidateResearchPage(arguments.AfterSequence, arguments.Limit);
        return AutomationResponse.Ok(ResearchPage(_researchEvents, arguments.AfterSequence, arguments.Limit));
    }

    private AutomationResponse ReadAutomationIndicators(ResearchArguments arguments)
    {
        RequireResearchSession(arguments.SessionId);
        AutomationResponse response = AutomationResponse.Ok(new
        {
            sessionId = _automationSession!.Id, _automationSession.Mode,
            replayHistory = AutomationReplayHistory,
            observedThroughUtc = _researchObservedThroughUtc,
            observationSequence = _researchObservationSequence,
            strategy = _researchStrategy,
            kind = _researchRequiredBars.HasValue ? "script" : "built_in",
            currentWarmup = new
            {
                requiredBars = _researchRequiredBars, retainedBars = _researchRetainedBars,
                remainingBars = _researchRequiredBars is { } required ? (int?)Math.Max(0, required - _researchRetainedBars) : null,
                ready = _researchRequiredBars.HasValue ? _researchRetainedBars >= _researchRequiredBars : (bool?)null,
                completedBarCount = _researchCompletedBars, barVersion = _researchBarVersion,
                historyStartsAtUtc = _researchHistoryStartsAtUtc,
            },
            evaluationSequence = _researchLastEvaluation is null ? (long?)null : _researchEvaluationSequence,
            latestEvaluation = _researchLastEvaluation is null ? null : ResearchEvaluation(_researchLastEvaluation),
            builtIn = _researchBuiltInDecision is not { } decision ? null : new
            {
                decision.EvaluatedAtUtc, decision.State,
                warmingUp = decision.State == "WARMING UP",
                rsiPeriod = PriceActionSignalEngine.RsiPeriod, decision.SimpleRsi,
                momentumPercent = decision.SimpleRsi.HasValue ? (decimal?)decision.MomentumPercent : null,
                referenceMovePercent = decision.SimpleRsi.HasValue ? (decimal?)decision.ReferenceMovePercent : null,
            },
            lastStrategyEvaluated = _researchLastStrategyEvaluated,
            lastRiskOverride = _researchLastRiskOverride,
        });
        if (JsonSerializer.SerializeToUtf8Bytes(response, AutomationProtocol.JsonOptions).Length <= 900 * 1024)
            return response;
        // Adversarially long source identifiers/order labels must not make the
        // transport unusable. Keep the small timing/warmup metadata explicit.
        return AutomationResponse.Ok(new
        {
            sessionId = _automationSession.Id,
            observedThroughUtc = _researchObservedThroughUtc,
            observationSequence = _researchObservationSequence,
            currentWarmup = response.Result!.Value.GetProperty("currentWarmup"),
            evaluationSequence = _researchEvaluationSequence,
            omitted = true,
            reason = "Indicator snapshot exceeds the response byte limit; shorten script identifiers or order labels.",
        });
    }

    private static object ResearchEvaluation(ScriptEvaluationSnapshot evaluation) => new
    {
        evaluation.EvaluatedAtUtc, evaluation.BarVersion, evaluation.CompletedBarCount,
        evaluation.RetainedBars, evaluation.RequiredWarmupBars,
        latestBar = evaluation.LatestBar is not { } bar ? null : new
        {
            bar.StartsAtUtc, bar.EndsAtUtc, bar.Open, bar.High, bar.Low, bar.Close,
            volume = bar.HasKnownVolume ? (decimal?)bar.Volume : null,
        },
        evaluation.State, evaluation.IsWarmingUp,
        proposal = new
        {
            evaluation.Proposal.Action, evaluation.Proposal.State, evaluation.Proposal.Reason,
            // Plots remains part of the engine API, but MCP uses the single bounded
            // def/plot list rather than duplicating an unbounded plot dictionary.
            evaluation.Proposal.Indicators, evaluation.Proposal.IndicatorCount,
            evaluation.Proposal.IndicatorLimit, evaluation.Proposal.IndicatorsTruncated,
        },
    };

    private void RequireResearchSession(string? sessionId)
    {
        if (_automationSession is null) throw new InvalidOperationException("No Replay or Paper Trader research session is available.");
        if (sessionId is not null && (!Guid.TryParse(sessionId, out Guid expected) || expected != _automationSession.Id))
            throw new InvalidOperationException("The requested session is no longer retained. Read status/results for the current session ID.");
    }

    private static void ValidateResearchPage(long afterSequence, int limit)
    {
        if (afterSequence < 0 || limit is < 1 or > 100)
            throw new ArgumentException("afterSequence must be nonnegative and limit must be between 1 and 100.");
    }

    private object ResearchPage(ResearchBuffer buffer, long afterSequence, int limit)
    {
        var page = new List<JsonElement>();
        int bytes = 0;
        long next = afterSequence;
        foreach (ResearchRecord record in buffer.Records.Where(item => item.Sequence > afterSequence))
        {
            if (page.Count == limit || bytes + record.Bytes > ResearchPageByteLimit) break;
            page.Add(record.Data);
            bytes += record.Bytes;
            next = record.Sequence;
        }
        long first = buffer.Records.TryPeek(out ResearchRecord? earliest) ? earliest.Sequence : 0;
        return new
        {
            sessionId = _automationSession!.Id, _automationSession.Mode,
            observedThroughUtc = _researchObservedThroughUtc,
            records = page, nextSequence = next,
            hasMore = buffer.Records.Any(item => item.Sequence > next),
            firstAvailableSequence = first,
            truncated = first > 0 && afterSequence < first - 1,
            recordLimit = ResearchRecordLimit, byteLimit = ResearchByteLimit,
        };
    }

    private sealed record ResearchArguments(string? SessionId = null);
    private sealed record ResearchPageArguments(long AfterSequence = 0, int Limit = 50, string? SessionId = null);
    private sealed record CandleArguments(string Kind = "strategy", long AfterSequence = 0, int Limit = 50, string? SessionId = null);
    private sealed record ResearchRecord(long Sequence, JsonElement Data, int Bytes);

    private sealed class ResearchBuffer
    {
        internal Queue<ResearchRecord> Records { get; } = new();
        private int _bytes;
        internal void Clear() { Records.Clear(); _bytes = 0; }
        internal void Add(long sequence, object value)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, AutomationProtocol.JsonOptions);
            if (json.Length > ResearchPageByteLimit)
                json = JsonSerializer.SerializeToUtf8Bytes(new { sequence, omitted = true, reason = "Record exceeds the research page byte limit." }, AutomationProtocol.JsonOptions);
            JsonElement data = JsonSerializer.Deserialize<JsonElement>(json);
            Records.Enqueue(new(sequence, data, json.Length));
            _bytes += json.Length;
            while (Records.Count > ResearchRecordLimit || _bytes > ResearchByteLimit)
                _bytes -= Records.Dequeue().Bytes;
        }
    }
}
