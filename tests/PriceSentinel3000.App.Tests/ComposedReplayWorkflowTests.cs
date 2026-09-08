using System.IO;
using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LibraryReplay_FillsOnlyTheLocalGapThenReplaysWholeMinutesAtTheirClose(bool checkBeforeStart) => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles { AllowConnections = !checkBeforeStart };
        await using var workspace = new TestWorkspace(new TestScriptCatalog
        {
            Source = "AddOrder(OrderType.BUY_TO_OPEN, close > 0);",
        });
        var vm = workspace.ViewModel;
        DateTimeOffset start = AvailabilityStart(workspace);
        HistoricalDownload fine = LibraryDownload(start, 15);
        string fineHash = Assert.Single(files.Library.Save(fine with { Candles = fine.Candles.Take(4).ToArray() })).DatasetHash;
        files.Provider.CompleteInterval = 60;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60);

        if (checkBeforeStart)
        {
            await vm.CheckReplayAvailabilityAsync();
            Assert.Equal("Coarse60", vm.ReplayAvailabilityStatus);
            Assert.Contains("from disk and Robinhood gap fills", vm.ReplayAvailabilityText);
            Assert.Contains("2/2 candles", vm.ReplayAvailabilityText);
            Assert.Contains("15, 60-second sources", vm.ReplayAvailabilityText);
            Assert.Equal(fineHash, Assert.Single(files.Library.Scan().Datasets).DatasetHash);
            files.Provider.Error = new IOException("Prepared Replay must reuse its checked candles without fetching again.");
        }

        Assert.True((await Automate(vm, "start", new { fast = true, pauseAfterObservations = 1 })).Success);
        await WaitForAutomation(vm, state => state.GetProperty("paused").GetBoolean() || state.GetProperty("operationState").GetString() == "failed");
        Assert.True(vm.IsReplayPaused, vm.StatusMessage);
        Assert.Equal(new[] { 15, 30, 60 }, files.Provider.Requests.Select(request => request.SourceIntervalSeconds));
        Assert.All(files.Provider.Requests, request =>
        {
            Assert.Equal(start.AddMinutes(1), request.FromUtc);
            Assert.Equal(start.AddMinutes(2), request.ThroughUtc);
        });
        Assert.Equal(checkBeforeStart ? 0 : 1, files.Connections);
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.Equal("60 SEC REPLAY (COMBINED)", vm.DataResolutionLabel);
        Assert.All(vm.ChartCandleIntervalOptions, option => Assert.Equal(0, option.Value % 60));

        JsonElement status = (await Automate(vm, "status")).Result!.Value;
        JsonElement provenance = status.GetProperty("replayHistory");
        Assert.Equal("local-and-provider-saved-library", provenance.GetProperty("Source").GetString());
        Assert.Equal(60, provenance.GetProperty("ReplayIntervalSeconds").GetInt32());
        Assert.Equal(new[] { 15, 60 }, provenance.GetProperty("NativeSourceIntervals").EnumerateArray().Select(value => value.GetInt32()));
        HistoricalDatasetInfo[] retained = files.Library.Scan().Datasets.OrderBy(dataset => dataset.SourceIntervalSeconds).ToArray();
        Assert.Equal(new[] { 15, 60 }, retained.Select(dataset => dataset.SourceIntervalSeconds));
        Assert.Equal(fineHash, retained[0].DatasetHash);
        Assert.Equal(retained.Select(dataset => dataset.DatasetHash).Order(),
            provenance.GetProperty("DatasetHashes").EnumerateArray().Select(value => value.GetString()).Order());
        Assert.True(provenance.GetProperty("Coverage").GetProperty("Complete").GetBoolean());

        JsonElement source = Assert.Single((await Automate(vm, "candles", new { kind = "source" })).Result!.Value
            .GetProperty("records").EnumerateArray());
        Assert.Equal(60, source.GetProperty("intervalSeconds").GetInt32());
        Assert.Equal(start.AddMinutes(1), source.GetProperty("availableAtUtc").GetDateTimeOffset());
        Assert.Equal(fine.Candles[0].Open, source.GetProperty("open").GetDecimal());
        Assert.Equal(fine.Candles.Take(4).Max(candle => candle.High), source.GetProperty("high").GetDecimal());
        Assert.Equal(fine.Candles.Take(4).Min(candle => candle.Low), source.GetProperty("low").GetDecimal());
        Assert.Equal(fine.Candles[3].Close, source.GetProperty("close").GetDecimal());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("volume").ValueKind);
        JsonElement paused = (await Automate(vm, "results")).Result!.Value;
        JsonElement buy = Assert.Single(paused.GetProperty("fills").EnumerateArray());
        Assert.Equal("Buy", buy.GetProperty("side").GetString());
        Assert.Equal(start.AddMinutes(1), buy.GetProperty("filledAtUtc").GetDateTimeOffset());
        Assert.Equal(fine.Candles[3].Close, buy.GetProperty("price").GetDecimal());
        JsonElement firstDecision = Assert.Single((await Automate(vm, "events")).Result!.Value.GetProperty("records").EnumerateArray());
        Assert.Equal(start.AddMinutes(1), firstDecision.GetProperty("evaluatedAtUtc").GetDateTimeOffset());

        Assert.True((await Automate(vm, "run_to_end")).Success);
        await WaitForAutomation(vm, state => state.GetProperty("operationState").GetString() == "completed");
        JsonElement events = (await Automate(vm, "events")).Result!.Value.GetProperty("records");
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal("STOP LOSS", events[1].GetProperty("riskOverride").GetString());
        Assert.Equal(start.AddMinutes(2), events[1].GetProperty("fill").GetProperty("filledAtUtc").GetDateTimeOffset());
        Assert.Equal(10m, events[1].GetProperty("fill").GetProperty("price").GetDecimal());
        JsonElement completed = (await Automate(vm, "results")).Result!.Value;
        MarketQuote[] quotes = workspace.Journal.ReadSessionQuotes(completed.GetProperty("sessionId").GetGuid(), new Instrument("SOFI")).ToArray();
        Assert.Equal(2, quotes.Length);
        Assert.All(quotes, quote => Assert.Equal(60, quote.SourceIntervalSeconds));
        Assert.Equal(3, files.Provider.Calls);
        Assert.Equal(provenance.GetRawText(), completed.GetProperty("replayHistory").GetRawText());
    });

    [Fact]
    public Task LibraryReplay_SavedFifteenSecondGapFillCombinesWithTheOriginalFileOnTheNextReplay() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        DateTimeOffset start = AvailabilityStart(workspace);
        HistoricalDownload fine = LibraryDownload(start, 15);
        string originalHash = Assert.Single(files.Library.Save(fine with { Candles = fine.Candles.Take(4).ToArray() })).DatasetHash;
        files.Provider.CompleteInterval = 15;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Broker15", vm.ReplayAvailabilityStatus);
        Assert.Equal(start.AddMinutes(1), Assert.Single(files.Provider.Requests).FromUtc);
        Assert.Equal(start.AddMinutes(2), files.Provider.Requests[0].ThroughUtc);

        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, state => state.GetProperty("operationState").GetString() is "completed" or "failed");
        Assert.Contains("Replay completed", vm.StatusMessage);
        HistoricalDatasetInfo[] saved = files.Library.Scan().Datasets.ToArray();
        Assert.Equal(2, saved.Length);
        Assert.Contains(saved, dataset => dataset.DatasetHash == originalHash);
        Assert.All(saved, dataset => Assert.Equal(15, dataset.SourceIntervalSeconds));

        files.Provider.Error = new IOException("Saved coverage must be reusable without fetching it again.");
        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Disk15", vm.ReplayAvailabilityStatus);
        Assert.Contains("8/8 candles", vm.ReplayAvailabilityText);
        Assert.Equal(1, files.Provider.Calls);
        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, state => state.GetProperty("operationState").GetString() is "completed" or "failed");
        Assert.Contains("Replay completed", vm.StatusMessage);
        JsonElement results = (await Automate(vm, "results")).Result!.Value;
        JsonElement provenance = results.GetProperty("replayHistory");
        Assert.Equal("local-library", provenance.GetProperty("Source").GetString());
        Assert.Equal(15, provenance.GetProperty("ReplayIntervalSeconds").GetInt32());
        Assert.Equal(2, provenance.GetProperty("DatasetHashes").GetArrayLength());
        Assert.Equal(8, results.GetProperty("summary").GetProperty("quoteCount").GetInt32());
        Assert.Equal(1, files.Provider.Calls);
        Assert.Equal(0, files.Connections);
        Assert.Equal(0, workspace.Broker.Connections);
    });

    [Fact]
    public Task LibraryReplay_ComposedMinuteHistoryRejectsFifteenSecondScriptWithoutChangingItsInterval() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace(new TestScriptCatalog());
        var vm = workspace.ViewModel;
        DateTimeOffset start = AvailabilityStart(workspace);
        HistoricalDownload fine = LibraryDownload(start, 15);
        files.Library.Save(fine with { Candles = fine.Candles.Take(4).ToArray() });
        files.Provider.CompleteInterval = 60;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 15);
        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Coarse60", vm.ReplayAvailabilityStatus);

        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, state => state.GetProperty("operationState").GetString() == "failed");

        Assert.False(vm.IsSessionRunning);
        Assert.Equal(15, vm.ScriptBarIntervalSeconds);
        Assert.Equal("INTERVAL MISMATCH", vm.MarketDataStateLabel);
        Assert.Contains("multiple of 60 seconds", vm.StatusMessage);
        JsonElement results = (await Automate(vm, "results")).Result!.Value;
        Assert.Equal(JsonValueKind.Null, results.GetProperty("sessionId").ValueKind);
        Assert.Empty(results.GetProperty("fills").EnumerateArray());
        Assert.Equal(3, files.Provider.Calls);
        Assert.Equal(0, files.Connections);
        Assert.Equal(0, workspace.Broker.Connections);
    });
}
