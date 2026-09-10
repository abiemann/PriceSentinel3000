using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PriceSentinel3000.App.Views;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SymbolAutocomplete_LocalReplayPreservesConnectedSearchAfterCompletionOrStop(bool stop) => host.RunAsync(async () =>
    {
        using var files = new ReplayLibraryFiles();
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        workspace.Broker.SearchResults = [new("NVDA", "NVIDIA Corporation")];
        await vm.ConnectRobinhoodAtStartupAsync(CancellationToken.None);
        Assert.True(workspace.Get<bool>("_isMarketDataConnected"));
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        files.Library.Save(LibraryDownload(start, 15));
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayOfflineOnly = true;
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        Assert.True((await Automate(vm, "start", new { fast = true, pauseAfterObservations = 1 })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("paused").GetBoolean());
        Assert.True(workspace.Get<bool>("_isMarketDataConnected"));
        Assert.True((await Automate(vm, stop ? "stop" : "run_to_end")).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == (stop ? "stopped" : "completed"));
        Assert.True(workspace.Get<bool>("_isMarketDataConnected"));
        Assert.False(vm.IsSessionRunning);

        vm.RequestModeSelection(TradingMode.Off);
        vm.RequestModeSelection(TradingMode.Replay);
        Assert.True(workspace.Get<bool>("_isMarketDataConnected"));
        var view = new AccountConfigurationView { DataContext = vm };
        var window = new Window { Content = view, Width = 460, Height = 700, ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var input = (TextBox)view.FindName("SymbolTextBox");
            input.SetCurrentValue(TextBox.TextProperty, "NVD");
            Assert.Equal("NVD", vm.Symbol);
            vm.ScheduleSymbolSuggestionsRefresh(input.Text);
            await WaitForSymbolSuggestions(workspace);

            Assert.True(((Popup)view.FindName("SymbolSuggestionsPopup")).IsOpen);
            Assert.Equal("NVDA", Assert.IsType<InstrumentSearchResult>(
                Assert.Single(((ListBox)view.FindName("SymbolSuggestionsList")).Items.Cast<object>())).Symbol);
            Assert.Equal(["NVD"], workspace.Broker.SearchQueries);
            Assert.Equal(1, workspace.Broker.Connections);
            Assert.Equal(0, files.Connections);
            Assert.Equal(0, files.Provider.Calls);
        }
        finally
        {
            vm.DismissSymbolSuggestions();
            window.Close();
        }
    });

    [Fact]
    public Task SymbolAutocomplete_DisconnectedOffAndOfflineReplayDoNotConnectOrSearch() => host.RunAsync(async () =>
    {
        using var files = new ReplayLibraryFiles();
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        workspace.Broker.SearchResults = [new("NVDA", "NVIDIA Corporation")];
        vm.RequestModeSelection(TradingMode.Off);
        vm.Symbol = "NVD";
        vm.ScheduleSymbolSuggestionsRefresh("NVD");
        Assert.False(workspace.Get<bool>("_isMarketDataConnected"));
        vm.Symbol = "SOFI";
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        files.Library.Save(LibraryDownload(start, 15));
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayOfflineOnly = true;
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        Assert.True((await Automate(vm, "start", new { fast = true })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        vm.RequestModeSelection(TradingMode.Off);
        vm.RequestModeSelection(TradingMode.Replay);
        vm.Symbol = "NVD";
        vm.ScheduleSymbolSuggestionsRefresh("NVD");
        await Task.Delay(350);

        Assert.False(workspace.Get<bool>("_isMarketDataConnected"));
        Assert.Empty(vm.SymbolSuggestions);
        Assert.False(vm.IsSymbolSuggestionsOpen);
        Assert.Empty(workspace.Broker.SearchQueries);
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.Equal(0, files.Connections);
        Assert.Equal(0, files.Provider.Calls);
    });

    [Fact]
    public Task SymbolAutocomplete_LocalReplayReadFailurePreservesExistingConnection() => host.RunAsync(async () =>
    {
        using var files = new ReplayLibraryFiles();
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        workspace.Broker.SearchResults = [new("NVDA", "NVIDIA Corporation")];
        await vm.ConnectRobinhoodAtStartupAsync(CancellationToken.None);
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        files.Library.Save(LibraryDownload(start, 15));
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayPinnedHashes = new string('a', 64);
        vm.DataRetention.ReplayOfflineOnly = true;
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        Assert.True((await Automate(vm, "start", new { fast = true })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "failed");
        Assert.Contains("pinned dataset", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsSessionRunning);
        Assert.True(workspace.Get<bool>("_isMarketDataConnected"));
        vm.Symbol = "NVD";
        vm.ScheduleSymbolSuggestionsRefresh("NVD");
        await WaitForSymbolSuggestions(workspace);

        Assert.Equal(["NVD"], workspace.Broker.SearchQueries);
        Assert.Equal(1, workspace.Broker.Connections);
        Assert.Equal(0, files.Connections);
        Assert.Equal(0, files.Provider.Calls);
    });

    private static async Task WaitForSymbolSuggestions(TestWorkspace workspace)
    {
        for (int attempt = 0; attempt < 500 && !workspace.ViewModel.IsSymbolSuggestionsOpen; attempt++)
            await Task.Delay(10);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(workspace.ViewModel.IsSymbolSuggestionsOpen);
        Assert.Equal("NVDA", Assert.Single(workspace.ViewModel.SymbolSuggestions).Symbol);
    }
}
