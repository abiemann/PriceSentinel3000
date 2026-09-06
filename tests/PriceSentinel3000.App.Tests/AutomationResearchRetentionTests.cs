using System.Text.Json;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task Research_SourceRetentionReportsEvictionAndPagesEveryRemainingPaperObservation() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        DateTimeOffset at = workspace.Clock.Now;
        for (int sequence = 1; sequence <= 4100; sequence++)
        {
            MarketQuote quote = ResearchSample(at.AddSeconds(sequence), 10m + sequence / 100_000m);
            workspace.Clock.Now = quote.ObservedAtUtc;
            workspace.Invoke("CaptureAutomationObservation", quote, quote, false);
        }

        AutomationResponse response = await Automate(workspace.ViewModel, "candles", new { kind = "source", limit = 100 });
        Assert.True(response.Success, response.Error);
        JsonElement page = response.Result!.Value;
        Guid sessionId = page.GetProperty("sessionId").GetGuid();
        Assert.True(page.GetProperty("truncated").GetBoolean());
        Assert.Equal(5, page.GetProperty("firstAvailableSequence").GetInt64());
        Assert.Equal(4096, page.GetProperty("recordLimit").GetInt32());
        JsonElement first = page.GetProperty("records")[0];
        Assert.Equal("sampled_quote", first.GetProperty("kind").GetString());
        Assert.False(first.GetProperty("finalized").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("endsAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("intervalSeconds").ValueKind);
        Assert.Equal(at.AddSeconds(5), first.GetProperty("sourceTimestampUtc").GetDateTimeOffset());
        Assert.Equal(at.AddSeconds(6), first.GetProperty("observedAtUtc").GetDateTimeOffset());
        Assert.Equal(10.00005m, first.GetProperty("last").GetDecimal());
        Assert.Equal(9.99905m, first.GetProperty("bid").GetDecimal());
        Assert.Equal(10.00105m, first.GetProperty("ask").GetDecimal());

        var records = new List<JsonElement>();
        while (true)
        {
            Assert.Equal(sessionId, page.GetProperty("sessionId").GetGuid());
            records.AddRange(page.GetProperty("records").EnumerateArray());
            if (!page.GetProperty("hasMore").GetBoolean()) break;
            long cursor = page.GetProperty("nextSequence").GetInt64();
            response = await Automate(workspace.ViewModel, "candles", new { kind = "source", afterSequence = cursor, limit = 100, sessionId });
            Assert.True(response.Success, response.Error);
            page = response.Result!.Value;
            Assert.False(page.GetProperty("truncated").GetBoolean());
            Assert.InRange(page.GetProperty("records").GetArrayLength(), 1, 100);
        }

        Assert.Equal(Enumerable.Range(5, 4096).Select(value => (long)value), records.Select(record => record.GetProperty("sequence").GetInt64()));
        Assert.Equal(4100, page.GetProperty("nextSequence").GetInt64());
        Assert.Equal(10.041m, records[^1].GetProperty("close").GetDecimal());
    });

    [Fact]
    public Task Research_LargeDecisionEventsRespectByteRetentionAndReturnCompleteBoundedPages() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        DateTimeOffset at = workspace.Clock.Now;
        string explanation = new('x', 120 * 1024);
        for (int sequence = 1; sequence <= 60; sequence++)
        {
            MarketQuote quote = ResearchSample(at.AddSeconds(sequence), 10m);
            workspace.Clock.Now = quote.ObservedAtUtc;
            workspace.Invoke("CaptureAutomationObservation", quote, quote, false);
            StrategyDecision proposal = StrategyDecision.Hold(quote.SourceTimestampUtc, "HOLD", explanation);
            workspace.Invoke("CaptureAutomationDecision", new PaperTradeResult(proposal, null, null,
                workspace.Get<PaperAccountSnapshot>("_automationAccount")) { StrategyProposal = proposal });
        }

        AutomationResponse response = await Automate(workspace.ViewModel, "events", new { limit = 100 });
        Assert.True(response.Success, response.Error);
        JsonElement page = response.Result!.Value;
        Guid sessionId = page.GetProperty("sessionId").GetGuid();
        long firstSequence = page.GetProperty("firstAvailableSequence").GetInt64();
        Assert.True(page.GetProperty("truncated").GetBoolean());
        Assert.InRange(firstSequence, 2, 59);
        Assert.InRange(page.GetProperty("records").GetArrayLength(), 1, 3);
        Assert.True(page.GetProperty("hasMore").GetBoolean());

        var sequences = new List<long>();
        long retainedBytes = 0;
        while (true)
        {
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(response, AutomationProtocol.JsonOptions).Length, 1, 1024 * 1024);
            int pageBytes = 0;
            foreach (JsonElement record in page.GetProperty("records").EnumerateArray())
            {
                Assert.False(record.TryGetProperty("omitted", out _));
                Assert.Equal(explanation, record.GetProperty("strategyProposal").GetProperty("reasons")[0].GetString());
                Assert.Equal(explanation, record.GetProperty("decision").GetProperty("reasons")[0].GetString());
                long sequence = record.GetProperty("sequence").GetInt64();
                Assert.Equal(sequence, record.GetProperty("observationSequence").GetInt64());
                sequences.Add(sequence);
                pageBytes += JsonSerializer.SerializeToUtf8Bytes(record).Length;
            }
            Assert.InRange(pageBytes, 1, 600 * 1024);
            retainedBytes += pageBytes;
            if (!page.GetProperty("hasMore").GetBoolean()) break;
            long cursor = page.GetProperty("nextSequence").GetInt64();
            response = await Automate(workspace.ViewModel, "events", new { afterSequence = cursor, limit = 100, sessionId });
            Assert.True(response.Success, response.Error);
            page = response.Result!.Value;
            Assert.False(page.GetProperty("truncated").GetBoolean());
            Assert.NotEmpty(page.GetProperty("records").EnumerateArray());
        }

        Assert.Equal(Enumerable.Range((int)firstSequence, 61 - (int)firstSequence).Select(value => (long)value), sequences);
        Assert.InRange(retainedBytes, 11 * 1024 * 1024, 12 * 1024 * 1024);
        Assert.Equal(60, page.GetProperty("nextSequence").GetInt64());
    });

    [Fact]
    public Task Research_OversizedDecisionIsExplicitlyOmittedAndDoesNotBlockTheNextEvent() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        DateTimeOffset at = workspace.Clock.Now;
        PaperAccountSnapshot account = workspace.Get<PaperAccountSnapshot>("_automationAccount");
        workspace.Invoke("CaptureAutomationDecision", new PaperTradeResult(
            StrategyDecision.Hold(at, "HOLD", new string('x', 700 * 1024)), null, null, account));
        workspace.Invoke("CaptureAutomationDecision", new PaperTradeResult(
            StrategyDecision.Hold(at.AddSeconds(1), "HOLD", "Next ordinary event."), null, null, account));

        AutomationResponse response = await Automate(workspace.ViewModel, "events", new { limit = 1 });
        Assert.True(response.Success, response.Error);
        JsonElement page = response.Result!.Value;
        JsonElement omitted = Assert.Single(page.GetProperty("records").EnumerateArray());
        Assert.True(omitted.GetProperty("omitted").GetBoolean());
        Assert.Contains("byte limit", omitted.GetProperty("reason").GetString());
        Assert.Equal(1, omitted.GetProperty("sequence").GetInt64());
        Assert.True(page.GetProperty("hasMore").GetBoolean());

        response = await Automate(workspace.ViewModel, "events", new
        {
            afterSequence = page.GetProperty("nextSequence").GetInt64(),
            sessionId = page.GetProperty("sessionId").GetGuid(),
        });
        Assert.True(response.Success, response.Error);
        page = response.Result!.Value;
        JsonElement next = Assert.Single(page.GetProperty("records").EnumerateArray());
        Assert.Equal(2, next.GetProperty("sequence").GetInt64());
        Assert.Equal("Next ordinary event.", next.GetProperty("decision").GetProperty("reasons")[0].GetString());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, page.GetProperty("nextSequence").GetInt64());
    });

    private static MarketQuote ResearchSample(DateTimeOffset sourceAt, decimal price) =>
        new(new Instrument("SOFI"), sourceAt.AddSeconds(1), sourceAt, price - 0.001m, price + 0.001m, price, 0m);
}
