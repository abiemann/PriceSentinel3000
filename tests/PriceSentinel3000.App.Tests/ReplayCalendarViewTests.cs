using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PriceSentinel3000.App.Views;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task ReplayDateEnter_CommitsTypedDateBeforeCheckingAndDoesNotStartSession() => host.RunAsync(async () =>
    {
        using var files = new ReplayLibraryFiles();
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        files.Library.Save(LibraryDownload(start, 15));
        vm.DataRetention = files.CreateRetention();
        vm.RequestModeSelection(TradingMode.Replay);
        vm.ReplayDate = start.AddDays(-1).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        vm.ReplayTime = start.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        vm.ReplayEndTime = start.AddMinutes(2).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var view = new DataTimingView { DataContext = vm };
        var window = new Window { Content = view, Width = 340, Height = 820, ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var input = (TextBox)view.FindName("ReplayDateInput");
            string typedDate = start.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            input.Focus();
            input.SetCurrentValue(TextBox.TextProperty, typedDate);
            Assert.NotEqual(typedDate, vm.ReplayDate);
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await WaitForCalendarUi(() => vm.ReplayAvailabilityStatus == "Disk15");

            Assert.Equal(typedDate, vm.ReplayDate);
            Assert.Contains("15-second data on disk", vm.ReplayAvailabilityText);
            Assert.False(vm.IsSessionRunning);
            Assert.Equal(0, files.Provider.Calls);
            Assert.Equal(0, workspace.Broker.Connections);
            Assert.Equal(Color.FromRgb(0x17, 0x67, 0x47), Assert.IsType<SolidColorBrush>(input.Background).Color);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ReplayCalendar_OpensWithLocalColorsAndAccessibleDescriptionsWithoutBrokerFanout() => host.RunAsync(async () =>
    {
        using var files = new ReplayLibraryFiles();
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        files.Library.Save(LibraryDownload(start, 15));
        files.Library.Save(LibraryDownload(start.AddDays(-1), 60));
        vm.DataRetention = files.CreateRetention();
        vm.RequestModeSelection(TradingMode.Replay);
        vm.ReplayDate = start.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        vm.ReplayTime = start.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        vm.ReplayEndTime = start.AddMinutes(2).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var view = new DataTimingView { DataContext = vm };
        var window = new Window { Content = view, Width = 340, Height = 820, ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ((Button)view.FindName("ReplayCalendarButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForCalendarUi(() => vm.ReplayCalendarDays.Count == 42);
            var popup = (Popup)view.FindName("ReplayCalendarPopup");
            Assert.True(popup.IsOpen);
            var grid = (UniformGrid)view.FindName("CalendarDaysGrid");
            Assert.Equal(42, grid.Children.Count);
            DateOnly date = DateOnly.FromDateTime(start.ToLocalTime().DateTime);
            Button localDay = grid.Children.OfType<Button>().Single(button => Equals(button.Tag, date));
            Button coarseDay = grid.Children.OfType<Button>().Single(button => Equals(button.Tag, date.AddDays(-1)));
            Assert.Equal(Color.FromRgb(0x17, 0x67, 0x47), Assert.IsType<SolidColorBrush>(localDay.Background).Color);
            Assert.Equal(Color.FromRgb(0xE8, 0xAB, 0x4D), Assert.IsType<SolidColorBrush>(coarseDay.Background).Color);
            Assert.Contains("15-second", AutomationProperties.GetName(localDay));
            Assert.Contains("60-second", AutomationProperties.GetName(coarseDay));
            Assert.Contains(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), AutomationProperties.GetName(localDay));
            Assert.Equal(0, files.Provider.Calls);
            Assert.Equal(0, workspace.Broker.Connections);
            Assert.False(vm.IsSessionRunning);
        }
        finally { window.Close(); }
    });

    private static async Task WaitForCalendarUi(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Calendar UI did not reach the expected state.");
            await Task.Delay(10);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
