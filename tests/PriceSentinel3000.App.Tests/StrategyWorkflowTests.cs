using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.Scripting;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task StrategySelector_DefaultsToBuiltInAndRetainsSharedChoiceAcrossModesAndRefresh() => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog();
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.PaperTrader);
        var panel = CreatePanel(vm);
        var selector = (ComboBox)panel.FindName("StrategySelector");
        Assert.Equal("Built-In", ((StrategyDescriptor)selector.SelectedItem).Name);
        Assert.Contains(VisualText(selector), text => text == "Built-In");
        Assert.Equal(2, selector.Items.Count);
        selector.SelectedValue = "test.thinkscript";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        vm.RefreshScripts();
        Assert.Equal("test.thinkscript", selector.SelectedValue);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        panel.UpdateLayout();
        Assert.Contains(VisualText(selector), text => text == "Test script");
        Assert.Contains(VisualText(panel), text => text == "1 min");
        catalog.Missing = true;
        vm.RefreshScripts();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Null(selector.SelectedItem);
        Assert.Equal("test.thinkscript", vm.SelectedStrategyId);
        catalog.Missing = false;
        vm.RefreshScripts();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        panel.UpdateLayout();
        Assert.Contains(VisualText(selector), text => text == "Test script");
        vm.RequestModeSelection(TradingMode.Live);
        Assert.Equal("test.thinkscript", vm.SelectedStrategyId);
        vm.RequestModeSelection(TradingMode.Replay);
        Assert.Equal("test.thinkscript", vm.SelectedStrategyId);
    });

    [Fact]
    public Task MissingSelectedScript_BlocksStartupWithoutFallbackOrBrokerConnection() => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog { Missing = true };
        await using var workspace = new TestWorkspace(catalog, TradingSessionSettings.Default with { StrategyId = "test.thinkscript" });
        MainViewModel vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.PaperTrader);
        CreatePanel(vm);
        await vm.StartSessionCommand.ExecuteAsync();
        Assert.Equal("test.thinkscript", vm.SelectedStrategyId);
        Assert.Contains("unavailable", vm.ScriptDiagnostics);
        Assert.Contains("unavailable", vm.StatusMessage);
        Assert.False(vm.IsSessionRunning);
        Assert.Equal(0, workspace.Broker.Connections);
    });

    [Fact]
    public Task PaperScript_PinsSourceAndSettingsWhileRunning() => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog();
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        vm.SelectedStrategyId = "test.thinkscript";
        vm.ScriptBarIntervalSeconds = 30;
        vm.RequestModeSelection(TradingMode.PaperTrader);
        var panel = CreatePanel(vm);
        var selector = (ComboBox)panel.FindName("StrategySelector");
        string original = catalog.Source;
        Task session = vm.StartSessionCommand.ExecuteAsync();
        Assert.True(vm.IsSessionRunning);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(selector.IsEnabled);
        catalog.Source += "\n# Changed after session start";
        vm.RefreshScripts();
        vm.SelectedStrategyId = StrategyDescriptor.BuiltInId;
        vm.ScriptBarIntervalSeconds = 60;
        Assert.Equal("test.thinkscript", vm.SelectedStrategyId);
        Assert.Equal(30, vm.ScriptBarIntervalSeconds);
        JsonNode metadata = JsonNode.Parse(ReadScalar(workspace, "SELECT settings_json FROM sessions"))!["Strategy"]!;
        Assert.Equal(original, metadata["Source"]!.GetValue<string>());
        Assert.Equal(Hash(original), metadata["SourceSha256"]!.GetValue<string>());
        Assert.Equal(30, metadata["CandleIntervalSeconds"]!.GetValue<int>());
        Assert.NotNull(metadata["Inputs"]);
        Assert.Equal("completed-price-bars-v1", metadata["DataModel"]!.GetValue<string>());
        await vm.StopSessionCommand.ExecuteAsync();
        await session;
    });

    [Fact]
    public Task LiveScript_RequiresApprovalOfPinnedVersionAndRechecksOnNextStart() => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog();
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        vm.SelectedStrategyId = "test.thinkscript";
        vm.RequestModeSelection(TradingMode.Live);
        await vm.AcknowledgeLiveRiskAsync();
        int connectedBeforeStart = workspace.Broker.Connections;
        await vm.StartSessionCommand.ExecuteAsync();
        Assert.False(vm.IsSessionRunning);
        Assert.False(vm.LiveArmed);
        Assert.Contains("not approved", vm.StatusMessage);
        Assert.Equal(connectedBeforeStart, workspace.Broker.Connections);

        var approvals = new List<string>();
        vm.ExternalScriptApprovalPrompt = message => { approvals.Add(message); return true; };
        Task session = vm.StartSessionCommand.ExecuteAsync();
        Assert.True(vm.LiveArmed);
        Assert.Contains(Hash(catalog.Source), Assert.Single(approvals));
        await vm.StopSessionCommand.ExecuteAsync();
        await session;
        catalog.Source += "\n# New version";
        Task second = vm.StartSessionCommand.ExecuteAsync();
        Assert.Equal(2, approvals.Count);
        Assert.Contains(Hash(catalog.Source), approvals[1]);
        Assert.NotEqual(approvals[0], approvals[1]);
        await vm.StopSessionCommand.ExecuteAsync();
        await second;
    });

    [Theory]
    [InlineData(TradingMode.PaperTrader)]
    [InlineData(TradingMode.Live)]
    public Task ScriptFeed_UsesIdenticalCompletedWarmupInPaperAndLive(TradingMode mode) => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog();
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        vm.SelectedStrategyId = "test.thinkscript";
        vm.ScriptBarIntervalSeconds = 15;
        DateTimeOffset now = workspace.Clock.Now;
        workspace.Broker.History = [Bar(now.AddSeconds(-15), 9m), Bar(now, 999m), Bar(now.AddSeconds(15), 999m)];
        vm.RequestModeSelection(mode);
        if (mode is TradingMode.Live)
        {
            await vm.AcknowledgeLiveRiskAsync();
            vm.ExternalScriptApprovalPrompt = _ => true;
        }
        Task session = vm.StartSessionCommand.ExecuteAsync();
        var engine = workspace.Get<ThinkScriptSignalEngine>("_scriptSignalEngine");
        StrategyBar bar = Assert.Single(engine.Bars.Snapshot());
        Assert.Equal(now, bar.EndsAtUtc);
        Assert.Equal(9m, bar.Close);
        // Late corrections may update the chart but cannot rewrite finalized script bars.
        workspace.Get<PriceRingBuffer>("_ringBuffer").Merge([Bar(now.AddSeconds(-15), 1000m)]);
        workspace.Invoke("ObserveScriptQuote", Bar(now.AddSeconds(5), 10m), null!);
        Assert.Equal(9m, engine.Bars.Snapshot()[0].Close);
        await vm.StopSessionCommand.ExecuteAsync();
        await session;
    });

    [Fact]
    public Task ReplayScript_DecisionAndFillUseHistoricalCandleEnd() => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog { Source = "AddOrder(OrderType.BUY_TO_OPEN, close > 0);" };
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset start = workspace.Clock.Now.AddHours(-1);
        workspace.Broker.ReplayHistory = [Bar(start, 10m)];
        vm.SelectedStrategyId = "test.thinkscript";
        vm.ScriptBarIntervalSeconds = 15;
        vm.RequestModeSelection(TradingMode.Replay);
        await vm.StartSessionCommand.ExecuteAsync();
        Assert.False(vm.IsSessionRunning);
        Assert.Equal(start.AddSeconds(15), DateTimeOffset.Parse(ReadScalar(workspace, "SELECT evaluated_at_utc FROM decisions")));
        Assert.Equal("Buy", ReadScalar(workspace, "SELECT signal FROM decisions"));
        Assert.Equal("1", ReadScalar(workspace, "SELECT count(*) FROM fills"));
    });

    private static MarketQuote Bar(DateTimeOffset at, decimal price) => new(new Instrument("SOFI"), at, at, 0m, 0m, price, 0m, price, price, price, price);

    private static string ReadScalar(TestWorkspace workspace, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={workspace.Journal.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!.ToString()!;
    }

    private static string Hash(string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private static IEnumerable<string> VisualText(System.Windows.DependencyObject parent)
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock text) yield return text.Text;
            foreach (string descendant in VisualText(child)) yield return descendant;
        }
    }

    private sealed class TestScriptCatalog : IStrategyCatalog
    {
        public string Source { get; set; } = "input threshold = 100000; AddOrder(OrderType.BUY_TO_OPEN, close > threshold); AddOrder(OrderType.SELL_TO_CLOSE, close < 0);";
        public bool Missing { get; set; }
        public string DirectoryPath => string.Empty;
        public StrategyCatalogSnapshot Load() => new(Missing ? [StrategyDescriptor.BuiltIn] : [StrategyDescriptor.BuiltIn, Descriptor()], []);
        public PinnedStrategy GetPinned(string id) => id == StrategyDescriptor.BuiltInId
            ? new(StrategyDescriptor.BuiltIn, null, null)
            : Missing ? throw new InvalidOperationException("Selected script unavailable.")
                : new(Descriptor(), Source, ThinkScriptCompiler.Compile(Source));
        private StrategyDescriptor Descriptor() => new("test.thinkscript", "Test script", "test.thinkscript", Hash(Source), ThinkScriptCompiler.RuntimeVersion);
    }
}
