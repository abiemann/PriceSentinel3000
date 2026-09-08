using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.Scripting;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task TestedInterval_SelectionAppliesRecommendationAndOverrideWarningTracksTheActualInterval() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(new IntervalScriptCatalog());
        MainViewModel vm = workspace.ViewModel;
        vm.ScriptBarIntervalSeconds = 15;
        vm.ChartCandleIntervalSeconds = 15;
        var panel = CreatePanel(vm);
        var strategy = (ComboBox)panel.FindName("StrategySelector");
        var interval = (ComboBox)panel.FindName("ScriptBarIntervalSelector");
        var label = (TextBlock)panel.FindName("TestedScriptIntervalText");
        var warning = (TextBlock)panel.FindName("ScriptIntervalWarningText");

        strategy.SelectedValue = IntervalScriptCatalog.AnnotatedId;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(60, vm.ScriptBarIntervalSeconds);
        Assert.Equal(60, interval.SelectedValue);
        Assert.Equal(15, vm.ChartCandleIntervalSeconds);
        Assert.Equal("Tested interval: 1 minute", label.Text);
        Assert.False(vm.HasScriptIntervalMismatch);
        Assert.Equal(Visibility.Collapsed, warning.Visibility);

        interval.SelectedValue = 30;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(30, vm.ScriptBarIntervalSeconds);
        Assert.True(vm.HasScriptIntervalMismatch);
        Assert.Equal(Visibility.Visible, warning.Visibility);
        Assert.Contains("previous test results may not apply", warning.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(15, vm.ChartCandleIntervalSeconds);

        interval.SelectedValue = 60;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(vm.HasScriptIntervalMismatch);
        Assert.Equal(Visibility.Collapsed, warning.Visibility);
    });

    [Fact]
    public Task TestedInterval_PersistedOverrideAndRefreshArePreservedUntilAnotherSelection() => host.RunAsync(async () =>
    {
        var catalog = new IntervalScriptCatalog();
        await using var workspace = new TestWorkspace(catalog, TradingSessionSettings.Default with
        {
            StrategyId = IntervalScriptCatalog.AnnotatedId,
            ScriptBarIntervalSeconds = 30,
            ChartCandleIntervalSeconds = 15,
        });
        MainViewModel vm = workspace.ViewModel;
        var panel = CreatePanel(vm);
        Assert.Equal(30, vm.ScriptBarIntervalSeconds);
        Assert.True(vm.HasScriptIntervalMismatch);
        Assert.Equal("Tested interval: 1 minute", vm.TestedScriptIntervalLabel);

        catalog.TestedSeconds = 120;
        vm.RefreshScripts();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(30, vm.ScriptBarIntervalSeconds);
        Assert.Equal("Tested interval: 2 minutes", vm.TestedScriptIntervalLabel);
        Assert.True(vm.HasScriptIntervalMismatch);
        Assert.Equal(IntervalScriptCatalog.AnnotatedId, ((ComboBox)panel.FindName("StrategySelector")).SelectedValue);
        Assert.Equal(120, Assert.IsType<StrategyDescriptor>(((ComboBox)panel.FindName("StrategySelector")).SelectedItem).TestedCandleIntervalSeconds);

        vm.SelectedStrategyId = IntervalScriptCatalog.UnspecifiedId;
        Assert.Equal(30, vm.ScriptBarIntervalSeconds);
        Assert.Equal("Tested interval not specified.", vm.TestedScriptIntervalLabel);
        Assert.False(vm.HasScriptIntervalMismatch);

        vm.SelectedStrategyId = IntervalScriptCatalog.AnnotatedId;
        Assert.Equal(120, vm.ScriptBarIntervalSeconds);
        Assert.False(vm.HasScriptIntervalMismatch);
        Assert.Equal(15, vm.ChartCandleIntervalSeconds);
    });

    [Fact]
    public Task TestedInterval_AutomationAppliesOnlyNewSelectionsAndPreservesExplicitOverrides() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(new IntervalScriptCatalog());
        MainViewModel vm = workspace.ViewModel;
        vm.ScriptBarIntervalSeconds = 15;
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { strategyId = IntervalScriptCatalog.AnnotatedId },
        })).Success);
        Assert.Equal(60, vm.ScriptBarIntervalSeconds);
        Assert.Equal(15, vm.ChartCandleIntervalSeconds);
        var status = (await Automate(vm, "status")).Result!.Value;
        Assert.Equal(60, status.GetProperty("testedCandleIntervalSeconds").GetInt32());
        Assert.True(string.IsNullOrEmpty(status.GetProperty("scriptIntervalWarning").GetString()));

        Assert.True((await Automate(vm, "configure", new
        {
            settings = new { strategyId = IntervalScriptCatalog.UnspecifiedId },
        })).Success);
        Assert.True((await Automate(vm, "configure", new
        {
            settings = new { strategyId = IntervalScriptCatalog.AnnotatedId, scriptBarIntervalSeconds = 30 },
        })).Success);
        Assert.Equal(30, vm.ScriptBarIntervalSeconds);
        Assert.True(vm.HasScriptIntervalMismatch);
        status = (await Automate(vm, "status")).Result!.Value;
        Assert.Equal(60, status.GetProperty("testedCandleIntervalSeconds").GetInt32());
        Assert.Contains("previous test results may not apply", status.GetProperty("scriptIntervalWarning").GetString(), StringComparison.OrdinalIgnoreCase);

        Assert.True((await Automate(vm, "configure", new
        {
            settings = new { strategyId = IntervalScriptCatalog.AnnotatedId, symbol = "AAPL" },
        })).Success);
        Assert.Equal(30, vm.ScriptBarIntervalSeconds);
        Assert.True(vm.HasScriptIntervalMismatch);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TestedInterval_FreshFileMetadataRemainsConsistentAcrossConfigureStartAndStop(bool newlyDiscovered) => host.RunAsync(async () =>
    {
        var catalog = new IntervalScriptCatalog { IncludeAnnotated = !newlyDiscovered };
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        var panel = CreatePanel(vm);
        var label = (TextBlock)panel.FindName("TestedScriptIntervalText");
        var selector = (ComboBox)panel.FindName("StrategySelector");
        catalog.IncludeAnnotated = true;
        catalog.TestedSeconds = 120;

        Assert.True((await Automate(vm, "configure", new
        {
            mode = "PaperTrader", settings = new { strategyId = IntervalScriptCatalog.AnnotatedId },
        })).Success);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(120, vm.ScriptBarIntervalSeconds);
        Assert.Equal(120, vm.TestedScriptIntervalSeconds);
        Assert.Equal("Tested interval: 2 minutes", label.Text);
        Assert.Equal(IntervalScriptCatalog.AnnotatedId, Assert.IsType<StrategyDescriptor>(selector.SelectedItem).Id);
        Assert.False(vm.HasScriptIntervalMismatch);
        var status = (await Automate(vm, "status")).Result!.Value;
        Assert.Equal(120, status.GetProperty("testedCandleIntervalSeconds").GetInt32());
        Assert.True(string.IsNullOrEmpty(status.GetProperty("scriptIntervalWarning").GetString()));

        vm.ScriptBarIntervalSeconds = 30;
        catalog.TestedSeconds = 300;
        Task session = vm.StartSessionCommand.ExecuteAsync();
        try
        {
            Assert.True(vm.IsSessionRunning);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(30, vm.ScriptBarIntervalSeconds);
            Assert.Equal(300, vm.TestedScriptIntervalSeconds);
            Assert.Equal("Tested interval: 5 minutes", label.Text);
            Assert.True(vm.HasScriptIntervalMismatch);
            status = (await Automate(vm, "status")).Result!.Value;
            Assert.Equal(300, status.GetProperty("testedCandleIntervalSeconds").GetInt32());
            JsonNode provenance = JsonNode.Parse(ReadScalar(workspace, "SELECT settings_json FROM sessions"))!["Strategy"]!;
            Assert.Equal(300, provenance["TestedCandleIntervalSeconds"]!.GetValue<int>());
            Assert.Equal(30, provenance["CandleIntervalSeconds"]!.GetValue<int>());
        }
        finally
        {
            await vm.StopSessionCommand.ExecuteAsync();
            await session;
        }

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(30, vm.ScriptBarIntervalSeconds);
        Assert.Equal(300, vm.TestedScriptIntervalSeconds);
        Assert.Equal("Tested interval: 5 minutes", label.Text);
        Assert.True(vm.HasScriptIntervalMismatch);
        Assert.Equal(300, Assert.IsType<StrategyDescriptor>(selector.SelectedItem).TestedCandleIntervalSeconds);
    });

    [Fact]
    public Task TestedInterval_PaperSessionPinsDeclaredAndActualIntervalsAndLocksConfiguration() => host.RunAsync(async () =>
    {
        var catalog = new IntervalScriptCatalog();
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        vm.SelectedStrategyId = IntervalScriptCatalog.AnnotatedId;
        vm.ScriptBarIntervalSeconds = 30;
        vm.RequestModeSelection(TradingMode.PaperTrader);
        var panel = CreatePanel(vm);
        Task session = vm.StartSessionCommand.ExecuteAsync();
        try
        {
            Assert.True(vm.IsSessionRunning);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(((ComboBox)panel.FindName("ScriptBarIntervalSelector")).IsEnabled);
            catalog.TestedSeconds = 120;
            vm.RefreshScripts();
            vm.SelectedStrategyId = IntervalScriptCatalog.UnspecifiedId;
            vm.ScriptBarIntervalSeconds = 120;
            Assert.Equal(IntervalScriptCatalog.AnnotatedId, vm.SelectedStrategyId);
            Assert.Equal(30, vm.ScriptBarIntervalSeconds);
            Assert.Equal("Tested interval: 1 minute", vm.TestedScriptIntervalLabel);
            Assert.True(vm.HasScriptIntervalMismatch);
            JsonNode provenance = JsonNode.Parse(ReadScalar(workspace, "SELECT settings_json FROM sessions"))!["Strategy"]!;
            Assert.Equal(60, provenance["TestedCandleIntervalSeconds"]!.GetValue<int>());
            Assert.Equal(30, provenance["CandleIntervalSeconds"]!.GetValue<int>());
        }
        finally
        {
            await vm.StopSessionCommand.ExecuteAsync();
            await session;
        }
    });

    [Fact]
    public Task TestedInterval_PausedReplayRetainsTheSelectedIntervalAndDeclaration() => host.RunAsync(async () =>
    {
        var catalog = new IntervalScriptCatalog();
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        vm.SelectedStrategyId = IntervalScriptCatalog.AnnotatedId;
        workspace.Broker.ReplayHistory = Enumerable.Range(0, 8)
            .Select(index => Bar(workspace.Clock.Now.AddHours(-1).AddSeconds(index * 15), 10m)).ToArray();
        vm.RequestModeSelection(TradingMode.Replay);
        var panel = CreatePanel(vm);
        Assert.True((await Automate(vm, "start", new { fast = true, pauseAfterObservations = 2 })).Success);
        try
        {
            await WaitForAutomation(vm, value => value.GetProperty("paused").GetBoolean());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(((ComboBox)panel.FindName("ScriptBarIntervalSelector")).IsEnabled);
            catalog.TestedSeconds = 120;
            vm.RefreshScripts();
            vm.SelectedStrategyId = IntervalScriptCatalog.UnspecifiedId;
            vm.ScriptBarIntervalSeconds = 15;
            Assert.Equal(IntervalScriptCatalog.AnnotatedId, vm.SelectedStrategyId);
            Assert.Equal(60, vm.ScriptBarIntervalSeconds);
            Assert.Equal("Tested interval: 1 minute", vm.TestedScriptIntervalLabel);
            Assert.False(vm.HasScriptIntervalMismatch);
        }
        finally
        {
            await vm.StopSessionCommand.ExecuteAsync();
        }
    });

    private sealed class IntervalScriptCatalog : IStrategyCatalog
    {
        public const string AnnotatedId = "annotated.thinkscript";
        public const string UnspecifiedId = "unspecified.thinkscript";
        public int TestedSeconds { get; set; } = 60;
        public bool IncludeAnnotated { get; set; } = true;
        public string DirectoryPath => string.Empty;
        public StrategyCatalogSnapshot Load() => new(IncludeAnnotated
            ? [StrategyDescriptor.BuiltIn, Descriptor(AnnotatedId), Descriptor(UnspecifiedId)]
            : [StrategyDescriptor.BuiltIn, Descriptor(UnspecifiedId)], []);
        public PinnedStrategy GetPinned(string id) => id == StrategyDescriptor.BuiltInId
            ? new(StrategyDescriptor.BuiltIn, null, null)
            : new(Descriptor(id), Source(id), ThinkScriptCompiler.Compile(Source(id)));

        private string Source(string id) => (id == AnnotatedId ? $"# PriceSentinel: tested-candle-seconds={TestedSeconds}\n" : "") +
            "AddOrder(OrderType.BUY_TO_OPEN, close > 100000);";

        private StrategyDescriptor Descriptor(string id) => new(id, id, id, Hash(Source(id)), ThinkScriptCompiler.RuntimeVersion)
        {
            TestedCandleIntervalSeconds = id == AnnotatedId ? TestedSeconds : null,
        };
    }
}
