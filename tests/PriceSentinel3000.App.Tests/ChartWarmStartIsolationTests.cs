using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(2, 15)]
    [InlineData(21, 60)]
    public Task ExpandedChartWarmStart_PreservesThePreviousScriptBarsAndIndicators(
        int averageLength, int scriptIntervalSeconds) => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog
        {
            Source = $"def trend = ExpAverage(close, {averageLength}); " +
                "def strength = RSI(length = 7); " +
                "AddOrder(OrderType.BUY_TO_OPEN, close > trend and strength > 1000);",
        };
        await using var workspace = new TestWorkspace(catalog);
        workspace.Clock.Now = new(2026, 9, 3, 16, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = workspace.Clock.Now;
        workspace.Broker.History = Enumerable.Range(0, 592)
            .Select(index => Bar(now.AddMinutes(-148).AddSeconds(index * 15),
                9m + index * 0.001m + (index % 7) * 0.01m)).ToArray();
        var vm = workspace.ViewModel;
        vm.BufferMinutes = 15;
        vm.SelectedStrategyId = "test.thinkscript";
        vm.ScriptBarIntervalSeconds = scriptIntervalSeconds;
        vm.RequestModeSelection(TradingMode.PaperTrader);

        Task session = vm.StartSessionCommand.ExecuteAsync();
        try
        {
            Assert.True(vm.IsSessionRunning);
            var actual = workspace.Get<ThinkScriptSignalEngine>("_scriptSignalEngine");
            CompiledThinkScript program = ThinkScriptCompiler.Compile(catalog.Source);
            // Previous startup retained buffer15 + RSI28 minutes, extended only
            // when the script's explicit warmup needed more history.
            TimeSpan previousDuration = TimeSpan.FromSeconds(Math.Max(43 * 60,
                (program.RequiredWarmupBars + 2) * scriptIntervalSeconds));
            var previous = new ThinkScriptSignalEngine(program, scriptIntervalSeconds);
            previous.Bars.SeedHistory(workspace.Broker.History.Where(quote =>
                quote.SourceTimestampUtc >= now - previousDuration), now);
            MarketQuote current = new(new Instrument("SOFI"), now, now, 9.99m, 10.01m, 10m, 0m);
            previous.Bars.ObserveQuote(current);
            previous.Evaluate([current], StrategyPositionContext.Flat);

            Assert.Equal(previous.Bars.Snapshot(), actual.Bars.Snapshot());
            Assert.Equal(previous.LastEvaluation!.Proposal.Indicators.ToArray(),
                actual.LastEvaluation!.Proposal.Indicators.ToArray());
            Assert.Equal(previous.LastEvaluation.Proposal.Action, actual.LastEvaluation.Proposal.Action);
            Assert.Equal(593, workspace.Get<PriceRingBuffer>("_chartRingBuffer").Count);
            Assert.Equal(TimeSpan.FromMinutes(148),
                workspace.Get<MarketDataRequest>("_marketDataRequest").WarmStartDuration);
            Assert.True(actual.Bars.Snapshot()[0].StartsAtUtc > workspace.Broker.History[0].SourceTimestampUtc);
        }
        finally
        {
            await vm.StopSessionCommand.ExecuteAsync();
            await session;
        }
    });
}
