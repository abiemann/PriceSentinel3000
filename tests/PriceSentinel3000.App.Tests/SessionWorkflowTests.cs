using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.App.Views;
using PriceSentinel3000.Application.LiveTrading;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed class SessionWorkflowTests(WpfTestHost host) : IClassFixture<WpfTestHost>
{
    [Fact]
    public Task Startup_EnablesStopAndLocksInputsBeforeConnectionCompletes() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        workspace.Broker.HoldConnection = true;
        vm.RequestModeSelection(TradingMode.PaperTrader);
        var panel = CreatePanel(vm);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var stop = new Button { Command = vm.StopSessionCommand };
        TextBox symbol = Inputs(panel).Single(input => BindingPath(input) == nameof(MainViewModel.Symbol));
        Assert.True(symbol.IsEnabled);
        Assert.False(stop.IsEnabled);

        Task starting = vm.StartSessionCommand.ExecuteAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(stop.IsEnabled);
        Assert.False(symbol.IsEnabled);
        vm.Symbol = "AAPL";
        vm.StopLossValue = 0.1m;
        Assert.Equal("SOFI", vm.Symbol);
        Assert.Equal(1m, vm.StopLossValue);

        await vm.StopSessionCommand.ExecuteAsync();
        await starting;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(stop.IsEnabled);
        Assert.True(symbol.IsEnabled);
    });

    [Fact]
    public Task LiveAcknowledgement_EnablesStopDuringConnection() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        workspace.Broker.HoldConnection = true;
        vm.RequestModeSelection(TradingMode.Live);
        var stop = new Button { Command = vm.StopSessionCommand };
        Task acknowledgement = vm.AcknowledgeLiveRiskAsync();
        Assert.True(stop.IsEnabled);
        await vm.StopSessionCommand.ExecuteAsync();
        await acknowledgement;
        Assert.False(stop.IsEnabled);
        Assert.False(vm.LiveArmed);
    });

    [Fact]
    public Task InvalidNumericText_BlocksStartUntilCorrected() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.Live);
        await vm.AcknowledgeLiveRiskAsync();
        var panel = CreatePanel(vm);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        TextBox stop = Inputs(panel).Single(input => BindingPath(input) == nameof(MainViewModel.StopLossValue));
        int connections = workspace.Broker.Connections;
        stop.Text = "0.1%";

        // START commits the focused text even before LostFocus validation has happened.
        Task start = vm.StartSessionCommand.ExecuteAsync();
        Assert.Equal(connections, workspace.Broker.Connections);
        await start;
        Assert.True(Validation.GetHasError(stop));
        Assert.True(vm.HasConfigurationErrors);
        Assert.False(vm.StartSessionCommand.CanExecute(null));
        Assert.Equal(connections, workspace.Broker.Connections);
        Assert.False(vm.LiveArmed);

        stop.Text = "0.1";
        stop.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Assert.False(vm.HasConfigurationErrors);
        Assert.True(vm.StartSessionCommand.CanExecute(null));
        Assert.Equal(0.1m, vm.StopLossValue);
    });

    [Fact]
    public Task InactiveEntryLimit_DoesNotBlockStartWithUnusedInvalidText() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.PaperTrader);
        var panel = CreatePanel(vm);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        TextBox limit = Inputs(panel).Single(input => BindingPath(input) == nameof(MainViewModel.MaximumEntriesPerDay));
        limit.Text = "invalid";
        limit.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Assert.True(vm.HasConfigurationErrors);
        vm.UnlimitedEntries = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(limit.IsEnabled);
        Assert.False(vm.HasConfigurationErrors);
        Assert.True(vm.StartSessionCommand.CanExecute(null));
        vm.UnlimitedEntries = false;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(vm.HasConfigurationErrors);
    });

    [Fact]
    public Task RunningSession_PreservesSettingsAndCapturedChartSymbol() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        MainViewModel vm = workspace.ViewModel;
        vm.Symbol = "AAPL";
        vm.StopLossValue = 0.1m;
        Assert.Equal("SOFI", vm.SymbolDisplay);
        Assert.Equal(1m, vm.StopLossValue);
        vm.ChartCandleIntervalSeconds = 30;
        Assert.Equal(30, vm.ChartCandleIntervalSeconds);
        PriceRingBuffer buffer = workspace.Get<PriceRingBuffer>("_ringBuffer");
        buffer.Merge(Enumerable.Range(0, 16).Select(index => new MarketQuote(new Instrument("SOFI"),
            workspace.Clock.Now, workspace.Clock.Now.AddMinutes(-index), 10m, 10m, 10m, 0m)));
        Assert.Equal(16, buffer.Count);
    });

    [Fact]
    public Task Reconciliation_CannotReplaceCurrentAskForPaperExecution() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        MarketQuote[] quotes = BottomPattern(workspace.Clock.Now.AddSeconds(-60));
        MarketQuote trigger = quotes[^1] with { ObservedAtUtc = workspace.Clock.Now };
        PriceRingBuffer buffer = workspace.Get<PriceRingBuffer>("_ringBuffer");
        buffer.Merge(quotes);
        buffer.Merge([trigger with { Bid = 0m, Ask = 0m }]);
        buffer.Merge([trigger with { SourceTimestampUtc = trigger.SourceTimestampUtc.AddSeconds(15), Bid = 0m, Ask = 0m, Last = 90m }]);

        workspace.Invoke("ProcessPaperObservation", trigger, false);

        Assert.True(workspace.Get<decimal>("_paperPositionQuantity") > 0m);
        Assert.Equal(trigger.Ask, workspace.Get<decimal>("_paperAveragePrice"));
    });

    [Fact]
    public Task DelayedQuote_DoesNotPassPaperFreshnessGate() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        MarketQuote[] quotes = BottomPattern(workspace.Clock.Now.AddMinutes(-3));
        workspace.Get<PriceRingBuffer>("_ringBuffer").Merge(quotes);
        workspace.Invoke("ProcessPaperObservation", quotes[^1], false);
        Assert.Equal("MARKET CLOSED", workspace.ViewModel.StrategyStateLabel);
        Assert.Equal(0m, workspace.Get<decimal>("_paperPositionQuantity"));
    });

    [Fact]
    public Task RawExecutionTrigger_StillMustPassBufferValidation() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        MarketQuote invalid = BottomPattern(workspace.Clock.Now)[^1] with { Last = 0m };
        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() => workspace.Invoke("ProcessPaperObservation", invalid, false));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    });

    [Fact]
    public Task LiveRollover_PersistsPriorEquityAndRetainsAlreadyObservedEntry() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare(TradingMode.Live);
        DateTimeOffset priorDay = workspace.Clock.Now;
        var engine = new LiveExecutionEngine(TradingSessionSettings.Default, 10_000m,
            initialTradingDate: EasternTradingDay.GetDate(priorDay));
        engine.ObserveAccount(priorDay, 10_500m);
        workspace.Set("_liveExecutionEngine", engine);
        workspace.Set("_liveAccount", workspace.Broker.Account);
        workspace.Set("_liveTradability", new EquityTradability("SOFI", true, true, "active", null));
        workspace.Clock.Now = priorDay.AddDays(1);
        workspace.Broker.Portfolio = workspace.Broker.Portfolio with { TotalValue = 10_200m };
        engine.ObserveTerminalOrder(new(Guid.NewGuid(), "overnight-buy", "SOFI", BrokerOrderSide.Buy,
            BrokerOrderState.Filled, 1m, 1m, 10m, null, workspace.Clock.Now, []));

        await (Task<LiveBrokerSnapshot>)workspace.Invoke("CaptureLiveBrokerAsync", new Instrument("SOFI"), CancellationToken.None)!;

        DateTimeOffset midnight = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);
        Assert.Equal(10_500m, workspace.Journal.GetLiveDailyStartingBalance(workspace.Broker.Account.AccountNumber, midnight));
        Assert.Equal(10_500m, engine.DailyStartingEquity);
        Assert.Equal(1, engine.EntriesToday);
    });

    [Fact]
    public Task ExistingPositionConfirmation_CannotCrossTradingDays() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        workspace.Clock.Now = new DateTimeOffset(2026, 9, 4, 3, 59, 30, TimeSpan.Zero);
        workspace.Broker.Position = new("SOFI", 1m, 10m, 1m, 0m);
        vm.RequestModeSelection(TradingMode.Live);
        await vm.AcknowledgeLiveRiskAsync();
        vm.ExistingLivePositionPrompt = _ =>
        {
            workspace.Clock.Now = workspace.Clock.Now.AddMinutes(1);
            return ExistingLivePositionChoice.MonitorForProfit;
        };

        await vm.StartSessionCommand.ExecuteAsync();

        Assert.False(vm.LiveArmed);
        Assert.False(vm.IsSessionRunning);
        Assert.Contains("trading day changed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    });

    [Theory]
    [InlineData(BrokerOrderState.Filled, false)]
    [InlineData(BrokerOrderState.Cancelled, false)]
    [InlineData(BrokerOrderState.Filled, true)]
    [InlineData(BrokerOrderState.Cancelled, true)]
    public Task StopReconciliation_PersistsCurrentDayBaselineEvenWithoutNewFills(
        BrokerOrderState terminalState,
        bool completedOnPriorDay) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare(TradingMode.Live);
        DateTimeOffset priorDay = workspace.Clock.Now;
        DateOnly priorTradingDate = EasternTradingDay.GetDate(priorDay);
        var engine = new LiveExecutionEngine(TradingSessionSettings.Default, 10_000m,
            initialTradingDate: priorTradingDate);
        engine.ObserveAccount(priorDay, 10_500m);
        workspace.Set("_liveExecutionEngine", engine);
        workspace.Set("_paperEquity", 10_500m);
        workspace.Set("_persistedLiveTradingDate", priorTradingDate);
        workspace.Clock.Now = priorDay.AddDays(1);
        var intent = new BrokerOrderIntent(Guid.NewGuid(), priorDay, "SOFI", BrokerOrderSide.Buy, 1m, "Test order");
        var terminalOrder = new BrokerOrderSnapshot(intent.ClientReferenceId, "stop-order", "SOFI",
            BrokerOrderSide.Buy, terminalState, 1m, terminalState is BrokerOrderState.Filled ? 1m : 0m,
            10m, null, completedOnPriorDay ? priorDay : workspace.Clock.Now, []);
        LiveOrderCoordinator coordinator = workspace.Get<LiveOrderCoordinator>("_liveOrderCoordinator");
        typeof(LiveOrderCoordinator).GetField("_activeIntent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, intent);
        typeof(LiveOrderCoordinator).GetField("_activeOrder", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, terminalOrder);

        await workspace.ViewModel.StopSessionCommand.ExecuteAsync();

        DateTimeOffset midnight = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);
        Assert.False(workspace.ViewModel.IsSessionRunning);
        Assert.False(coordinator.HasActiveContext);
        Assert.Equal(new DateOnly(2026, 9, 4), engine.TradingDate);
        Assert.Equal(10_500m, workspace.Journal.GetLiveDailyStartingBalance(workspace.Broker.Account.AccountNumber, midnight));
        Assert.Equal(10_500m, engine.DailyStartingEquity);
    });

    [Fact]
    public Task LegacySameDaySession_CannotSilentlyResetRiskBaseline() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Journal.StartSession(new Instrument("SOFI"), TradingMode.Live, 10_500m, "{}", workspace.Clock.Now.AddMinutes(-30));
        MainViewModel vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.Live);
        await vm.AcknowledgeLiveRiskAsync();
        await vm.StartSessionCommand.ExecuteAsync();
        Assert.False(vm.LiveArmed);
        Assert.Contains("restore today's risk baseline", vm.StatusMessage);

        workspace.Clock.Now = workspace.Clock.Now.AddDays(1);
        Task session = vm.StartSessionCommand.ExecuteAsync();
        Assert.True(vm.LiveArmed);
        Assert.True(vm.IsSessionRunning);
        await vm.StopSessionCommand.ExecuteAsync();
        await session;
    });

    [Fact]
    public Task ChartProjection_RefreshesCorrectionsMarkersAndIntervalChanges() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        MainViewModel vm = workspace.ViewModel;
        MarketQuote[] quotes = BottomPattern(workspace.Clock.Now);
        PriceRingBuffer chart = workspace.Get<PriceRingBuffer>("_chartRingBuffer");
        workspace.Get<PriceRingBuffer>("_ringBuffer").Merge(quotes);
        chart.Merge(quotes);
        workspace.Invoke("RefreshMarketView");
        int originalCount = vm.ChartPoints.Count;
        workspace.Invoke("ProcessPaperObservation", quotes[^1], false);
        workspace.Invoke("RefreshMarketView");
        Assert.Equal(ChartTradeMarker.Buy, vm.ChartPoints[^1].Marker);

        chart.Merge([quotes[0] with { ObservedAtUtc = workspace.Clock.Now.AddSeconds(1), Last = 101m }]);
        workspace.Invoke("RefreshMarketView");
        Assert.Equal(101m, vm.ChartPoints[0].High);
        vm.ChartCandleIntervalSeconds = 30;
        Assert.True(vm.ChartPoints.Count < originalCount);
        Assert.Equal(ChartTradeMarker.Buy, vm.ChartPoints[^1].Marker);
    });

    private static string? BindingPath(TextBox input) => input.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path;

    private static TradingConfigurationPanel CreatePanel(MainViewModel viewModel)
    {
        var panel = new TradingConfigurationPanel { DataContext = viewModel };
        panel.Measure(new Size(400, 1600));
        panel.Arrange(new Rect(0, 0, 400, 1600));
        panel.UpdateLayout();
        return panel;
    }

    private static IEnumerable<TextBox> Inputs(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is TextBox input) yield return input;
            else if (child is DependencyObject element)
                foreach (TextBox descendant in Inputs(element)) yield return descendant;
        }
    }

    private static MarketQuote[] BottomPattern(DateTimeOffset end)
    {
        decimal[] prices = [100m, 99.95m, 99.90m, 99.85m, 99.80m, 99.75m, 99.70m, 99.65m, 99.60m, 99.55m,
            99.50m, 99.48m, 99.46m, 99.45m, 99.46m, 99.45m, 99.46m, 99.45m, 99.47m, 99.50m];
        return prices.Select((price, index) =>
        {
            DateTimeOffset at = end.AddSeconds((index - prices.Length + 1) * 5);
            return new MarketQuote(new Instrument("SOFI"), at, at, price - 0.01m, price + 0.01m, price, 0m);
        }).ToArray();
    }
}
