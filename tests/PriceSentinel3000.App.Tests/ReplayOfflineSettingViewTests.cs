using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PriceSentinel3000.App.Views;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task ReplayLocalFilesOption_IsBeforeStartAndOnlyEditableForIdleReplay() => host.RunAsync(async () =>
    {
        using var files = new ReplayLibraryFiles();
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        files.Library.Save(LibraryDownload(start, 15));
        vm.DataRetention = files.CreateRetention();
        vm.RequestModeSelection(TradingMode.Replay);
        vm.ReplayDate = start.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        vm.ReplayTime = start.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        vm.ReplayEndTime = start.AddMinutes(2).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var panel = new TradingConfigurationPanel { DataContext = vm };
        var window = new Window
        {
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Width = 460, Height = 900, ShowActivated = false,
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var timing = Assert.Single(FindRetentionVisuals<DataTimingView>(panel));
            var checkbox = (CheckBox)timing.FindName("ReplayOfflineOnlyCheckBox");
            Button startButton = Assert.Single(FindRetentionVisuals<Button>(panel), button =>
                ReferenceEquals(button.Command, vm.StartSessionCommand));
            Assert.True(checkbox.IsVisible);
            Assert.True(checkbox.IsEnabled);
            Assert.False(checkbox.IsChecked);
            Assert.False(vm.DataRetention.ReplayOfflineOnly);
            Assert.True(checkbox.TransformToAncestor(panel).Transform(new Point(0, checkbox.ActualHeight)).Y <=
                startButton.TransformToAncestor(panel).Transform(new Point()).Y);

            await vm.CheckReplayAvailabilityAsync();
            Assert.Equal("Disk15", vm.ReplayAvailabilityStatus);
            checkbox.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(vm.DataRetention.ReplayOfflineOnly);
            Assert.Equal("Unknown", vm.ReplayAvailabilityStatus);
            vm.DataRetention.ReplayOfflineOnly = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(checkbox.IsChecked);

            foreach (TradingMode mode in new[] { TradingMode.PaperTrader, TradingMode.Live, TradingMode.Off })
            {
                vm.RequestModeSelection(mode);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(checkbox.IsVisible);
            }

            vm.RequestModeSelection(TradingMode.Replay);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(checkbox.IsVisible);
            Assert.True(checkbox.IsEnabled);
            workspace.Prepare(TradingMode.Replay);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(vm.IsSessionRunning);
            Assert.True(checkbox.IsVisible);
            Assert.False(checkbox.IsEnabled);
            Assert.Equal(0, files.Provider.Calls);
            Assert.Equal(0, workspace.Broker.Connections);
        }
        finally { window.Close(); }
    });
}
