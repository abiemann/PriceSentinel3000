using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.App.Views;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Core.Indicators;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task Automation_ChartCaptureValidatesBoundsAndUnavailableCharts() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        Assert.Equal("chart_unavailable", (await Automate(vm, "capture_chart")).ErrorCode);
        foreach (object arguments in new object[]
        {
            new { maxWidth = 0 }, new { maxWidth = 1281 }, new { maxHeight = -1 },
            new { maxHeight = 901 }, new { maxWidth = "large" }, new { filePath = "image.png" },
        })
        {
            Assert.Equal("invalid_arguments", (await Automate(vm, "capture_chart", arguments)).ErrorCode);
        }

        workspace.Prepare();
        vm.ChartPoints.Add(new(workspace.Clock.Now, 10m, 11m, 9m, 10m));
        var unloaded = new ChartWorkspaceView { DataContext = vm };
        vm.AutomationChartCaptureRequested = unloaded.CaptureChart;
        Assert.Equal("chart_unavailable", (await Automate(vm, "capture_chart")).ErrorCode);
    });

    [Fact]
    public Task Automation_ChartCaptureRendersBoundedCandleAndRsiPixelsWithSessionMetadata() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare(TradingMode.Replay);
        MainViewModel vm = workspace.ViewModel;
        vm.ChartCandleIntervalSeconds = 30;
        vm.ShowRsi = true;
        workspace.Set("_hasMarketData", true);
        DateTimeOffset first = workspace.Clock.Now.AddMinutes(-20);
        for (int index = 0; index < 40; index++)
        {
            decimal open = 10m + index * 0.01m;
            decimal close = open + (index % 2 == 0 ? 0.05m : -0.05m);
            vm.ChartPoints.Add(new(first.AddSeconds(index * 30), open, open + 0.08m, open - 0.08m, close,
                index == 25 ? ChartTradeMarker.Buy : index == 30 ? ChartTradeMarker.Sell : ChartTradeMarker.None));
        }
        var view = new ChartWorkspaceView { DataContext = vm };
        vm.AutomationChartCaptureRequested = view.CaptureChart;
        var window = new Window
        {
            Content = view, Width = 1050, Height = 850, ShowActivated = false,
            ShowInTaskbar = false, Left = -10000, Top = -10000,
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            AutomationResponse response = await Automate(vm, "capture_chart", new { maxWidth = 640, maxHeight = 450 });
            Assert.True(response.Success, response.Error);
            JsonElement capture = response.Result!.Value;
            Assert.Equal("image/png", capture.GetProperty("mimeType").GetString());
            Assert.Equal("SOFI", capture.GetProperty("symbol").GetString());
            Assert.Equal((await Automate(vm, "results")).Result!.Value.GetProperty("sessionId").GetGuid(), capture.GetProperty("sessionId").GetGuid());
            Assert.Equal("builtin", capture.GetProperty("strategy").GetProperty("id").GetString());
            Assert.False(capture.GetProperty("strategy").TryGetProperty("Source", out _));
            Assert.Equal(30, capture.GetProperty("candleIntervalSeconds").GetInt32());
            Assert.Equal(first.AddMinutes(20), capture.GetProperty("visibleToUtc").GetDateTimeOffset());
            Assert.Equal(first.AddMinutes(20 - vm.BufferMinutes), capture.GetProperty("visibleFromUtc").GetDateTimeOffset());
            Assert.Equal(40, capture.GetProperty("pointCount").GetInt32());
            Assert.True(capture.GetProperty("rsiShown").GetBoolean());
            Assert.Equal(SimpleRsiCalculator.Calculate(vm.ChartPoints.Select(point => point.Close).ToArray()), capture.GetProperty("rsiLatestValue").GetDecimal());

            byte[] png = Convert.FromBase64String(capture.GetProperty("data").GetString()!);
            Assert.True(png.Length <= 600 * 1024);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
            using var stream = new MemoryStream(png);
            BitmapFrame bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(capture.GetProperty("width").GetInt32(), bitmap.PixelWidth);
            Assert.Equal(capture.GetProperty("height").GetInt32(), bitmap.PixelHeight);
            Assert.InRange(bitmap.PixelWidth, 1, 640);
            Assert.InRange(bitmap.PixelHeight, 1, 450);
            var colors = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            colors.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Assert.True(Enumerable.Range(0, pixels.Length / 4).Any(index =>
                pixels[index * 4 + 1] > 150 && pixels[index * 4 + 2] < 120 && pixels[index * 4] < 110), "Rendered chart should contain green candles.");
            Assert.True(Enumerable.Range(0, pixels.Length / 4).Any(index =>
                pixels[index * 4 + 2] > 200 && pixels[index * 4 + 1] is > 50 and < 130 && pixels[index * 4] < 80), "Rendered chart should contain orange candles.");

            vm.ChartPoints[^1] = vm.ChartPoints[^1] with { Close = 11m, High = 11m };
            JsonElement updated = (await Automate(vm, "capture_chart", new { maxWidth = 640, maxHeight = 450 })).Result!.Value;
            Assert.NotEqual(capture.GetProperty("data").GetString(), updated.GetProperty("data").GetString());
            Assert.Equal(SimpleRsiCalculator.Calculate(vm.ChartPoints.Select(point => point.Close).ToArray()), updated.GetProperty("rsiLatestValue").GetDecimal());

            await Automate(vm, "stop");
            vm.Symbol = "AAPL";
            Assert.Equal("SOFI", (await Automate(vm, "capture_chart")).Result!.Value.GetProperty("symbol").GetString());
            vm.RequestModeSelection(TradingMode.Live);
            Assert.Equal("live_forbidden", (await Automate(vm, "capture_chart")).ErrorCode);
            vm.CancelModeSelection();

            // A chart from a later LIVE session must never be labeled with retained paper results.
            workspace.Prepare(TradingMode.Live);
            vm.ChartPoints.Add(new(first, 10m, 11m, 9m, 10m));
            workspace.Set("_isSessionRunning", false);
            workspace.Set("_activeSession", null!);
            vm.RequestModeSelection(TradingMode.Replay);
            Assert.Equal("chart_unavailable", (await Automate(vm, "capture_chart")).ErrorCode);
        }
        finally { window.Close(); }
    });
}
