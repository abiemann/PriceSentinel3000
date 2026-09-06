using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Core.Charting;
using PriceSentinel3000.Core.Indicators;

namespace PriceSentinel3000.App.Views;

public partial class ChartWorkspaceView : UserControl
{
    public ChartWorkspaceView()
    {
        InitializeComponent();
    }

    internal AutomationChartCapture CaptureChart(int maxWidth, int maxHeight)
    {
        Dispatcher.VerifyAccess();
        if (maxWidth is < 1 or > 1280 || maxHeight is < 1 or > 900)
            throw new ArgumentOutOfRangeException(nameof(maxWidth), "Chart image dimensions exceed the supported limits.");
        if (!IsLoaded || !IsVisible || ChartCard.ActualWidth <= 0 || ChartCard.ActualHeight <= 0)
            throw new InvalidOperationException("The chart must be loaded and visible before it can be captured.");

        ChartCard.UpdateLayout();
        PricePointViewModel[] points = [.. (Chart.Points?.Cast<PricePointViewModel>() ?? [])];
        if (points.Length == 0)
            throw new InvalidOperationException("The displayed chart has no candle data to capture.");

        PriceChartTimeWindow window = PriceChartViewportCalculator.CreateTimeWindow(
            points[^1].TimestampUtc, Chart.CandleIntervalSeconds, Chart.WindowMinutes);
        double scale = Math.Min(1d, Math.Min(maxWidth / ChartCard.ActualWidth, maxHeight / ChartCard.ActualHeight));
        int width = Math.Max(1, (int)Math.Floor(ChartCard.ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Floor(ChartCard.ActualHeight * scale));
        byte[] png;
        do
        {
            var drawing = new DrawingVisual();
            using (DrawingContext context = drawing.RenderOpen())
            {
                var brush = new VisualBrush(ChartCard)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, ChartCard.ActualWidth, ChartCard.ActualHeight),
                    Stretch = Stretch.Fill,
                };
                context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96d, 96d, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            png = stream.ToArray();
            // Base64 expansion plus metadata must stay within the 1 MiB pipe frame.
            if (png.Length <= 600 * 1024) break;
            if (width == 1 && height == 1)
                throw new InvalidOperationException("The chart image could not fit within the automation response limit.");
            width = Math.Max(1, width * 3 / 4);
            height = Math.Max(1, height * 3 / 4);
        } while (true);

        return new(
            "image/png", width, height, Convert.ToBase64String(png), DateTimeOffset.UtcNow,
            Chart.CandleIntervalSeconds, window.FirstTimestamp.ToUniversalTime(), window.LastTimestamp.ToUniversalTime(),
            points.Length, points.Count(point => window.ContainsCandle(point.TimestampUtc)),
            Chart.ShowRsi && Chart.ActualHeight - 26d >= 220d, SimpleRsiCalculator.DefaultPeriod,
            SimpleRsiCalculator.Calculate(points.Select(point => point.Close).ToArray()));
    }
}
