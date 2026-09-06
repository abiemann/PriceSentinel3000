using System.Collections.ObjectModel;
using System.Text.Json;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Scripting;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task Research_StalePaperObservationClearsEvaluationFlagsAndPreservesHighWaterMark() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        MarketQuote[] quotes = BottomPattern(workspace.Clock.Now);
        workspace.Get<PriceRingBuffer>("_ringBuffer").Merge(quotes);
        workspace.Invoke("ProcessPaperObservation", quotes[^1], false, null!);
        JsonElement evaluated = (await Automate(workspace.ViewModel, "indicators")).Result!.Value;
        Assert.True(evaluated.GetProperty("lastStrategyEvaluated").GetBoolean());
        Assert.Equal(workspace.Clock.Now, evaluated.GetProperty("observedThroughUtc").GetDateTimeOffset());
        Assert.NotEqual(JsonValueKind.Null, evaluated.GetProperty("builtIn").ValueKind);

        MarketQuote stale = quotes[^1] with
        {
            ObservedAtUtc = workspace.Clock.Now,
            SourceTimestampUtc = workspace.Clock.Now.AddMinutes(-5),
        };
        workspace.Invoke("ProcessPaperObservation", stale, false, null!);

        JsonElement skipped = (await Automate(workspace.ViewModel, "indicators")).Result!.Value;
        Assert.False(skipped.GetProperty("lastStrategyEvaluated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, skipped.GetProperty("lastRiskOverride").ValueKind);
        Assert.Equal(evaluated.GetProperty("builtIn").GetRawText(), skipped.GetProperty("builtIn").GetRawText());
        Assert.Equal(workspace.Clock.Now, skipped.GetProperty("observedThroughUtc").GetDateTimeOffset());
        Assert.Equal(2, skipped.GetProperty("observationSequence").GetInt64());
        JsonElement sources = (await Automate(workspace.ViewModel, "candles", new { kind = "source" })).Result!.Value;
        Assert.False(sources.GetProperty("records")[1].GetProperty("fresh").GetBoolean());
        Assert.Equal(stale.SourceTimestampUtc, sources.GetProperty("records")[1].GetProperty("sourceTimestampUtc").GetDateTimeOffset());
        Assert.Single((await Automate(workspace.ViewModel, "events")).Result!.Value.GetProperty("records").EnumerateArray());
    });

    [Fact]
    public Task Research_OversizedIndicatorSnapshotKeepsWarmupWithinTheTransportLimit() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        workspace.Set("_researchRequiredBars", 85);
        workspace.Set("_researchRetainedBars", 12);
        workspace.Set("_researchCompletedBars", 12L);
        workspace.Set("_researchBarVersion", 12L);
        workspace.Set("_researchEvaluationSequence", 12L);
        workspace.Set("_researchObservedThroughUtc", workspace.Clock.Now);
        workspace.Set("_researchLastEvaluation", new ScriptEvaluationSnapshot(
            workspace.Clock.Now, 12, 12, 12, 85,
            new StrategyBar(workspace.Clock.Now.AddMinutes(-1), workspace.Clock.Now, 10, 10, 10, 10, 0),
            new(ScriptAction.Hold, "WARMING UP", "Waiting for completed bars.", ReadOnlyDictionary<string, decimal?>.Empty)));
        JsonElement expected = (await Automate(workspace.ViewModel, "indicators")).Result!.Value;
        workspace.Set("_researchStrategy", JsonSerializer.SerializeToElement(new
        {
            Name = new string('x', 1024 * 1024),
        }));

        AutomationResponse response = await Automate(workspace.ViewModel, "indicators");

        Assert.True(response.Success, response.Error);
        JsonElement bounded = response.Result!.Value;
        Assert.True(bounded.GetProperty("omitted").GetBoolean());
        Assert.Contains("response byte limit", bounded.GetProperty("reason").GetString());
        Assert.Equal(expected.GetProperty("sessionId").GetGuid(), bounded.GetProperty("sessionId").GetGuid());
        Assert.Equal(expected.GetProperty("currentWarmup").GetRawText(), bounded.GetProperty("currentWarmup").GetRawText());
        Assert.Equal(73, bounded.GetProperty("currentWarmup").GetProperty("remainingBars").GetInt32());
        Assert.False(bounded.GetProperty("currentWarmup").GetProperty("ready").GetBoolean());
        Assert.Equal(12, bounded.GetProperty("evaluationSequence").GetInt64());
        Assert.Equal(workspace.Clock.Now, bounded.GetProperty("observedThroughUtc").GetDateTimeOffset());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(response, AutomationProtocol.JsonOptions).Length < 1024 * 1024);
    });
}
