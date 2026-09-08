using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Core.Charting;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Indicators;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(15)]
    public Task ChartIntervalChange_UsesRetainedHistoryWithoutChangingExecutionBuffer(
        int bufferMinutes) => host.RunAsync(async () =>
    {
        TradingSessionSettings settings = TradingSessionSettings.Default with
        {
            BufferMinutes = bufferMinutes,
            ScriptBarIntervalSeconds = 60,
        };
        await using var workspace = new TestWorkspace(preferences: settings);
        MainViewModel vm = workspace.ViewModel;
        var instrument = new Instrument("SOFI");
        vm.RequestModeSelection(TradingMode.PaperTrader);
        workspace.Invoke("PrepareDataSession", instrument, settings, TradingMode.PaperTrader, null!);
        PriceRingBuffer chart = workspace.Get<PriceRingBuffer>("_chartRingBuffer");
        PriceRingBuffer execution = workspace.Get<PriceRingBuffer>("_ringBuffer");
        Assert.Equal(TimeSpan.FromMinutes(bufferMinutes * 8 + 28), chart.Retention);
        Assert.Equal(TimeSpan.FromMinutes(bufferMinutes), execution.Retention);

        MarketQuote[] quotes = Enumerable.Range(0, 961).Select(index =>
        {
            DateTimeOffset at = workspace.Clock.Now.AddHours(-4).AddSeconds(index * 15);
            decimal last = 10m + index % 20 * 0.01m;
            return new MarketQuote(instrument, at, at, last - 0.01m, last + 0.01m, last, 100m);
        }).ToArray();
        chart.Merge(quotes);
        execution.Merge(quotes);
        MarketQuote[] executionBefore = execution.Snapshot().ToArray();
        workspace.Get<Dictionary<DateTimeOffset, ChartTradeMarker>>("_tradeMarkers")
            .Add(quotes[^1].SourceTimestampUtc, ChartTradeMarker.Buy);

        foreach (int intervalSeconds in new[] { 15, 120, 60, 30, 15 })
        {
            vm.ChartCandleIntervalSeconds = intervalSeconds;
            workspace.Invoke("RefreshMarketView");
            PriceChartTimeWindow viewport = PriceChartViewportCalculator.CreateTimeWindow(
                vm.ChartPoints[^1].TimestampUtc, intervalSeconds, bufferMinutes);
            PricePointViewModel[] visible = vm.ChartPoints
                .Where(point => viewport.ContainsCandle(point.TimestampUtc)).ToArray();
            Assert.Equal(bufferMinutes * 4, visible.Length);
            Assert.Equal(TimeSpan.FromSeconds((visible.Length - 1) * intervalSeconds),
                visible[^1].TimestampUtc - visible[0].TimestampUtc);
            Assert.Equal(ChartTradeMarker.Buy, visible[^1].Marker);
            IReadOnlyList<decimal?> rsi = SimpleRsiCalculator.CalculateSeries(
                vm.ChartPoints.Select(point => point.Close).ToArray());
            Assert.NotNull(rsi[vm.ChartPoints.IndexOf(visible[0])]);
            Assert.Equal(executionBefore, execution.Snapshot());
            Assert.Equal(60, vm.ScriptBarIntervalSeconds);
        }
    });
}
