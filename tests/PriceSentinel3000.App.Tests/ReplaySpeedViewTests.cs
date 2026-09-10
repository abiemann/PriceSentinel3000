using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PriceSentinel3000.App.Views;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(10, "999", 500, false)]
    [InlineData(500, "999", 500, false)]
    [InlineData(10, "999", 500, true)]
    [InlineData(500, "999", 500, true)]
    [InlineData(10, "0", 1, false)]
    [InlineData(1, "0", 1, true)]
    [InlineData(1, "-1", 1, false)]
    [InlineData(10, "-1", 1, true)]
    [InlineData(10, "0.5", 1, false)]
    [InlineData(1, "0.5", 1, true)]
    public Task ReplaySpeed_ClampsTypedValueOnCommit(int previousSpeed, string typedSpeed, int expectedSpeed, bool commitForStart) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.Replay);
        vm.ReplaySpeed = previousSpeed;
        var panel = new TradingConfigurationPanel { DataContext = vm };
        var window = new Window { Content = panel, Width = 460, Height = 900, ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var timing = Assert.Single(FindRetentionVisuals<DataTimingView>(panel));
            var input = (TextBox)timing.FindName("ReplaySpeedInput");
            input.SetCurrentValue(TextBox.TextProperty, typedSpeed);
            Assert.Equal(previousSpeed, vm.ReplaySpeed);

            if (commitForStart)
                Assert.True(vm.ValidateConfigurationInputs!());
            else
                input.RaiseEvent(new RoutedEventArgs(FocusManager.LostFocusEvent, input));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Equal(expectedSpeed, vm.ReplaySpeed);
            Assert.Equal(expectedSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture), input.Text);
            Assert.False(Validation.GetHasError(input));
            Assert.False(vm.HasConfigurationErrors);
            Assert.False(vm.IsSessionRunning);
            Assert.Equal(0, workspace.Broker.Connections);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ReplaySpeed_StillRejectsNonNumericText() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.Replay);
        var panel = CreatePanel(vm);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var timing = Assert.Single(FindRetentionVisuals<DataTimingView>(panel));
        var input = (TextBox)timing.FindName("ReplaySpeedInput");
        input.SetCurrentValue(TextBox.TextProperty, "invalid");

        await vm.StartSessionCommand.ExecuteAsync();

        Assert.True(Validation.GetHasError(input));
        Assert.StartsWith("Cannot start:", vm.StatusMessage);
        Assert.False(vm.IsSessionRunning);
        Assert.Equal(0, workspace.Broker.Connections);
    });

    [Theory]
    [InlineData("invalid", true)]
    [InlineData("25", false)]
    public Task ReplayMaxSpeed_ReplacesInvalidOrUncommittedTextAndAllowsManualEdits(string editedText, bool commit) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.Replay);
        vm.ReplaySpeed = 500m;
        var view = new DataTimingView { DataContext = vm };
        var window = new Window { Content = view, Width = 340, Height = 820, ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var input = (TextBox)view.FindName("ReplaySpeedInput");
            var maximum = (Button)view.FindName("ReplayMaxSpeedButton");
            input.Focus();
            input.SetCurrentValue(TextBox.TextProperty, editedText);
            if (commit)
            {
                input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.True(Validation.GetHasError(input));
            }
            Assert.Equal(500m, vm.ReplaySpeed);

            maximum.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Equal(500m, vm.ReplaySpeed);
            Assert.Equal("500", input.Text);
            Assert.False(Validation.GetHasError(input));
            Assert.True(input.IsEnabled);
            input.SetCurrentValue(TextBox.TextProperty, "125");
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.Equal(125m, vm.ReplaySpeed);
            Assert.False(Validation.GetHasError(input));
            Assert.False(vm.IsSessionRunning);
            Assert.Equal(0, workspace.Broker.Connections);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ReplayMaxSpeed_LocksDuringStartupAndRunningSession() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.Replay);
        vm.ReplayDate = "2026-09-02";
        vm.ReplaySpeed = 100m;
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
            var input = (TextBox)timing.FindName("ReplaySpeedInput");
            var maximum = (Button)timing.FindName("ReplayMaxSpeedButton");
            Assert.True(maximum.IsEnabled);
            Assert.True(input.IsEnabled);

            workspace.Broker.HoldConnection = true;
            Task starting = vm.StartSessionCommand.ExecuteAsync();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(vm.IsSessionConfigurationEditable);
                Assert.False(maximum.IsEnabled);
                Assert.False(input.IsEnabled);
                maximum.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(100m, vm.ReplaySpeed);
            }
            finally
            {
                await vm.StopSessionCommand.ExecuteAsync();
                await starting;
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(maximum.IsEnabled);
            Assert.True(input.IsEnabled);

            workspace.Prepare(TradingMode.Replay);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(vm.IsSessionRunning);
            Assert.False(maximum.IsEnabled);
            Assert.False(input.IsEnabled);
            maximum.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(100m, vm.ReplaySpeed);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(340)]
    [InlineData(460)]
    public Task ReplaySpeedControls_KeepMaxBesideInputAndCheckBeforeGuidance(int width) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.ViewModel.RequestModeSelection(TradingMode.Replay);
        workspace.ViewModel.ReplaySpeed = 500m;
        var view = new DataTimingView { DataContext = workspace.ViewModel };
        var window = new Window { Content = view, Width = width, Height = 820, ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var input = (TextBox)view.FindName("ReplaySpeedInput");
            var maximum = (Button)view.FindName("ReplayMaxSpeedButton");
            var check = (Button)view.FindName("ReplayCheckButton");
            var guidance = (TextBlock)view.FindName("ReplayAvailabilityText");
            Point inputRight = input.TranslatePoint(new Point(input.ActualWidth, 0), view);
            Point maximumLeft = maximum.TranslatePoint(new Point(), view);
            Point checkRight = check.TranslatePoint(new Point(check.ActualWidth, 0), view);
            Point guidanceLeft = guidance.TranslatePoint(new Point(), view);
            Assert.True(input.ActualWidth >= 36);
            Assert.True(maximum.ActualWidth >= 40);
            Assert.True(inputRight.X <= maximumLeft.X);
            Assert.True(checkRight.X <= guidanceLeft.X);
            Assert.True(guidance.ActualWidth > 0);
            Assert.True(maximum.TranslatePoint(new Point(maximum.ActualWidth, 0), view).X <= view.ActualWidth);
            Assert.True(guidance.TranslatePoint(new Point(guidance.ActualWidth, 0), view).X <= view.ActualWidth);
            Assert.Equal("MAX", maximum.Content);
            Assert.Equal("CHECK", check.Content);
            CaptureLocalLayout(window, $"replay-speed-{width}.png");
        }
        finally { window.Close(); }
    });
}
