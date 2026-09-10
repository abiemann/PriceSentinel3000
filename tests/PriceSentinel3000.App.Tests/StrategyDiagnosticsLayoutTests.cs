using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task ScriptDiagnostics_LongTextRemainsScrollableWhenSessionInputsLock() => host.RunAsync(async () =>
    {
        StrategyCatalogDiagnostic[] messages = Enumerable.Range(1, 40)
            .Select(line => new StrategyCatalogDiagnostic("strategy.thinkscript",
                $"Diagnostic {line} — text with enough detail to wrap across the configuration panel.")).ToArray();
        await using var workspace = new TestWorkspace(new DiagnosticsCatalog(messages));
        MainViewModel vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.PaperTrader);
        string diagnostics = vm.ScriptDiagnostics;
        Assert.Contains(messages[^1].Message, diagnostics);
        var panel = CreatePanel(vm);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        panel.UpdateLayout();
        var scroll = Assert.IsType<ScrollViewer>(panel.FindName("ScriptDiagnosticsScrollViewer"));
        var text = Assert.IsType<TextBlock>(panel.FindName("ScriptDiagnosticsText"));
        var selector = Assert.IsType<ComboBox>(panel.FindName("StrategySelector"));
        TextBox symbol = Inputs(panel).Single(input => BindingPath(input) == nameof(MainViewModel.Symbol));
        string ScrollState() => $"Offset={scroll.VerticalOffset}, scrollable={scroll.ScrollableHeight}, " +
            $"viewport={scroll.ViewportHeight}, textHeight={text.ActualHeight}, " +
            $"textLength={text.Text.Length}, diagnosticsLength={vm.ScriptDiagnostics.Length}, " +
            $"visibility={scroll.Visibility}, enabled={scroll.IsEnabled}.";

        Assert.Equal(diagnostics, text.Text);
        Assert.Null(text.ToolTip);
        Assert.Null(scroll.ToolTip);
        Assert.True(scroll.ViewportHeight > 0, ScrollState());
        Assert.InRange(scroll.ActualHeight, 1, 150);
        Assert.True(scroll.ScrollableHeight > 0, ScrollState());
        Assert.True(text.ActualHeight > scroll.ViewportHeight, ScrollState());
        Assert.Equal(new[] { "Copy", "Cancel" }, scroll.ContextMenu.Items.Cast<MenuItem>()
            .Select(item => (string)item.Header));

        scroll.ScrollToEnd();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 1);
        Assert.True(text.ActualHeight <= scroll.VerticalOffset + scroll.ViewportHeight + 1, ScrollState());
        Assert.Equal(diagnostics, text.Text);

        workspace.Broker.HoldConnection = true;
        Task starting = vm.StartSessionCommand.ExecuteAsync();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(selector.IsEnabled);
            Assert.False(symbol.IsEnabled);
            Assert.True(scroll.IsEnabled);
            Assert.True(text.IsEnabled);
            Assert.Equal(diagnostics, text.Text);
            Assert.True(scroll.ScrollableHeight > 0, ScrollState());
            scroll.ScrollToTop();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
            });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(scroll.VerticalOffset > 0, ScrollState());
        }
        finally
        {
            await vm.StopSessionCommand.ExecuteAsync();
            await starting;
        }
    });

    private sealed class DiagnosticsCatalog(IReadOnlyList<StrategyCatalogDiagnostic> diagnostics) : IStrategyCatalog
    {
        public string DirectoryPath => string.Empty;
        public StrategyCatalogSnapshot Load() => new([StrategyDescriptor.BuiltIn], diagnostics);
        public PinnedStrategy GetPinned(string id) => new(StrategyDescriptor.BuiltIn, null, null);
    }
}
