using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Core.Charting;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task ChartEmas_IncludeHistoryOlderThanProjectedCandles() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset open = new(2026, 10, 23, 13, 30, 0, TimeSpan.Zero);
        MarketQuote[] history = Enumerable.Range(0, 360).Select(index =>
            OverlayBar(open.AddSeconds(index * 15), index < 200 ? 10m : 20m)).ToArray();
        SeedOverlayHistory(workspace, history);

        Assert.True(vm.ShowEma50 && vm.ShowEma100 && vm.ShowEma200 && vm.ShowVwap);
        Assert.All(vm.ChartPoints, point => Assert.Equal(20m, point.Close));
        PricePointViewModel latest = vm.ChartPoints[^1];
        Assert.Equal(ExpectedStepEma(50), latest.Ema50!.Value, 10);
        Assert.Equal(ExpectedStepEma(100), latest.Ema100!.Value, 10);
        Assert.Equal(ExpectedStepEma(200), latest.Ema200!.Value, 10);

        // The first 200 candles are 10, followed by 160 candles at 20. A
        // viewport-only calculation would incorrectly seed every EMA at 20.
        static decimal ExpectedStepEma(int period) =>
            20m - 10m * (decimal)Math.Pow(1d - 2d / (period + 1), 160);
    });

    [Fact]
    public Task ChartVwap_UsesRegularSessionHistoryBeforeTheVisibleWindow() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        DateTimeOffset open = new(2026, 10, 23, 13, 30, 0, TimeSpan.Zero);
        MarketQuote[] history = Enumerable.Range(-120, 720).Select(index =>
            OverlayBar(open.AddSeconds(index * 15), index < 0 ? 1_000m : index < 240 ? 10m : 20m,
                index < 0 ? 1_000m : index < 240 ? 300m : 100m)).ToArray();
        SeedOverlayHistory(workspace, history);

        MainViewModel vm = workspace.ViewModel;
        Assert.True(vm.ChartPoints[0].TimestampUtc > open.AddMinutes(60));
        Assert.All(vm.ChartPoints, point => Assert.Equal(20m, point.Close));
        Assert.Equal((240m * 10m * 300m + 360m * 20m * 100m) / (240m * 300m + 360m * 100m),
            vm.ChartPoints[^1].Vwap);
        Assert.Equal(VwapStatus.Available, vm.ChartPoints[^1].VwapStatus);
        Assert.Equal("VWAP: regular session", vm.VwapAvailabilityText);
    });

    [Theory]
    [InlineData(true, VwapStatus.MissingVolume)]
    [InlineData(false, VwapStatus.IncompleteSession)]
    public Task ChartVwap_AnEarlierUnknownVolumeOrGapRemainsUnavailable(
        bool unknownVolume, VwapStatus expectedStatus) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        DateTimeOffset open = new(2026, 10, 23, 13, 30, 0, TimeSpan.Zero);
        MarketQuote[] history = Enumerable.Range(0, 360)
            .Where(index => unknownVolume || index != 10)
            .Select(index => OverlayBar(open.AddSeconds(index * 15), 10m) with
            {
                HasKnownVolume = !unknownVolume || index != 10,
            }).ToArray();
        SeedOverlayHistory(workspace, history);

        MainViewModel vm = workspace.ViewModel;
        Assert.True(vm.ChartPoints[0].TimestampUtc > open.AddMinutes(30));
        Assert.All(vm.ChartPoints, point => Assert.Null(point.Vwap));
        Assert.Equal(expectedStatus, vm.ChartPoints[^1].VwapStatus);
        Assert.NotNull(vm.ChartPoints[^1].Ema200);
        Assert.Equal(unknownVolume ? "VWAP: volume unavailable" : "VWAP: needs complete history from 9:30 ET",
            vm.VwapAvailabilityText);
    });

    [Fact]
    public Task ChartOverlayIntervalChange_RecalculatesWithoutChangingExecutionHistory() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        DateTimeOffset open = new(2026, 10, 23, 13, 30, 0, TimeSpan.Zero);
        MarketQuote[] history = Enumerable.Range(0, 360).Select(index =>
            OverlayBar(open.AddSeconds(index * 15), 10m + index * 0.01m)).ToArray();
        SeedOverlayHistory(workspace, history);
        PriceRingBuffer execution = workspace.Get<PriceRingBuffer>("_ringBuffer");
        MarketQuote[] before = execution.Snapshot().ToArray();
        MainViewModel vm = workspace.ViewModel;
        PricePointViewModel fifteenSeconds = vm.ChartPoints[^1];
        Assert.Equal(11.795m, fifteenSeconds.Vwap);

        vm.ChartCandleIntervalSeconds = 60;
        PricePointViewModel minute = vm.ChartPoints[^1];
        Assert.Equal(11.8m, minute.Vwap);
        Assert.NotEqual(fifteenSeconds.Ema50, minute.Ema50);
        Assert.NotEqual(fifteenSeconds.Ema100, minute.Ema100);
        Assert.NotEqual(fifteenSeconds.Ema200, minute.Ema200);
        Assert.Equal(VwapStatus.Available, minute.VwapStatus);

        vm.ChartCandleIntervalSeconds = 15;
        Assert.Equal(fifteenSeconds, vm.ChartPoints[^1]);
        Assert.Equal(before, execution.Snapshot());
    });

    [Fact]
    public Task ChartReconciliation_RecoversVwapAndRefreshesCorrectionsWithoutChangingExecution() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        DateTimeOffset open = new(2026, 10, 23, 13, 30, 0, TimeSpan.Zero);
        MarketQuote[] history = Enumerable.Range(0, 359).Select(index =>
            OverlayBar(open.AddSeconds(index * 15), 10m)).ToArray();
        MarketQuote completed = OverlayBar(open.AddSeconds(359 * 15), 20m);
        MarketQuote sample = new(new Instrument("SOFI"), completed.SourceEndsAtUtc,
            completed.SourceTimestampUtc.AddSeconds(5), 99.99m, 100.01m, 100m, 0m, HasKnownVolume: false);
        SeedOverlayHistory(workspace, [.. history, sample]);
        MainViewModel vm = workspace.ViewModel;
        PriceRingBuffer execution = workspace.Get<PriceRingBuffer>("_ringBuffer");
        MarketQuote[] before = execution.Snapshot().ToArray();
        PriceRingBuffer chart = workspace.Get<PriceRingBuffer>("_chartRingBuffer");
        Assert.Null(vm.ChartPoints[^1].Vwap);

        chart.Merge([completed]);
        workspace.Invoke("RefreshMarketView");
        Assert.Equal(20m, vm.ChartPoints[^1].Close);
        Assert.Equal((359m * 10m + 20m) / 360m, vm.ChartPoints[^1].Vwap);
        Assert.Equal(VwapStatus.Available, vm.ChartPoints[^1].VwapStatus);

        chart.Merge([completed with { ObservedAtUtc = completed.ObservedAtUtc.AddSeconds(1), Volume = 200m }]);
        workspace.Invoke("RefreshMarketView");
        Assert.Equal((359m * 10m * 100m + 20m * 200m) / (359m * 100m + 200m), vm.ChartPoints[^1].Vwap);
        Assert.Equal(before, execution.Snapshot());
    });

    [Fact]
    public Task ChartSymbolChange_ClearsOverlaysAndSeedsTheNewSymbolIndependently() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        DateTimeOffset open = new(2026, 10, 23, 13, 30, 0, TimeSpan.Zero);
        SeedOverlayHistory(workspace, [OverlayBar(open, 10m), OverlayBar(open.AddSeconds(15), 20m)]);
        MainViewModel vm = workspace.ViewModel;
        Assert.NotNull(vm.ChartPoints[^1].Ema200);
        Assert.NotNull(vm.ChartPoints[^1].Vwap);
        await vm.StopSessionCommand.ExecuteAsync();

        vm.Symbol = "MSFT";
        Assert.Empty(vm.ChartPoints);
        Assert.Equal("VWAP: waiting for data", vm.VwapAvailabilityText);
        vm.ChartCandleIntervalSeconds = 60;
        Assert.Empty(vm.ChartPoints);
        vm.ChartCandleIntervalSeconds = 15;
        TradingSessionSettings settings = TradingSessionSettings.Default with { Symbol = "MSFT" };
        workspace.Invoke("PrepareDataSession", new Instrument("MSFT"), settings, TradingMode.PaperTrader, null!);
        SeedOverlayHistory(workspace, [OverlayBar(open, 200m) with { Instrument = new Instrument("MSFT") }]);

        PricePointViewModel point = Assert.Single(vm.ChartPoints);
        Assert.Equal(200m, point.Ema50);
        Assert.Equal(200m, point.Ema100);
        Assert.Equal(200m, point.Ema200);
        Assert.Equal(200m, point.Vwap);
    });

    private static void SeedOverlayHistory(TestWorkspace workspace, MarketQuote[] history)
    {
        workspace.Get<PriceRingBuffer>("_chartRingBuffer").Merge(history);
        workspace.Get<PriceRingBuffer>("_ringBuffer").Merge(history);
        workspace.Invoke("RefreshMarketView");
    }

    private static MarketQuote OverlayBar(DateTimeOffset at, decimal price, decimal volume = 100m) =>
        new(new Instrument("SOFI"), at.AddSeconds(15), at, 0m, 0m, price, volume,
            price, price + 0.3m, price - 0.3m, price, SourceIntervalSeconds: 15, HasKnownVolume: true);
}
