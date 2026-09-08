using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PriceSentinel3000.App.Controls;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(480, false)]
    [InlineData(1000, true)]
    public Task ChartIntervalChange_PreservesRenderedCandleWidthAndShowsMoreHistory(
        int width,
        bool showRsi) => host.RunAsync(async () =>
    {
        ChartDrawingSnapshot fine = await RenderChartAsync(width, showRsi, 15);
        ChartDrawingSnapshot coarse = await RenderChartAsync(width, showRsi, 120);

        Assert.Equal(60, fine.CandleBodies.Length);
        Assert.Equal(fine.CandleBodies.Length, coarse.CandleBodies.Length);
        for (int index = 0; index < fine.CandleBodies.Length; index++)
        {
            Assert.Equal(fine.CandleBodies[index].Width, coarse.CandleBodies[index].Width, 5);
            Assert.Equal(fine.CandleBodies[index].Left, coarse.CandleBodies[index].Left, 5);
        }

        TimeSpan fineLabelSpan = LabelSpan(fine.TimeLabels);
        TimeSpan coarseLabelSpan = LabelSpan(coarse.TimeLabels);
        Assert.InRange(fineLabelSpan.TotalMinutes, 5, 15);
        Assert.InRange(coarseLabelSpan.TotalMinutes, 90, 120);
        AssertReadableTimeLabels(fine.TimeLabels);
        AssertReadableTimeLabels(coarse.TimeLabels);
    });

    private static async Task<ChartDrawingSnapshot> RenderChartAsync(
        int width,
        bool showRsi,
        int intervalSeconds)
    {
        var end = new DateTimeOffset(2026, 9, 8, 17, 0, 0, TimeSpan.Zero);
        int count = 3 * 60 * 60 / intervalSeconds;
        var chart = new PriceChart
        {
            Width = width,
            Height = 500,
            WindowMinutes = 15,
            CandleIntervalSeconds = intervalSeconds,
            ShowRsi = showRsi,
            Points = Enumerable.Range(0, count).Select(index => new PricePointViewModel(
                end.AddSeconds((index - count) * intervalSeconds),
                10m, 10.1m, 9.95m, 10.05m)).ToArray(),
        };
        var window = new Window
        {
            Content = chart, SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap(width, 500, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chart);
            Drawing[] drawings = FlattenDrawing(VisualTreeHelper.GetDrawing(chart)!).ToArray();
            Rect[] bodies = drawings.OfType<GeometryDrawing>()
                .Where(drawing => drawing.Brush is SolidColorBrush brush &&
                    brush.Color == Color.FromRgb(90, 203, 60) &&
                    drawing.Geometry is RectangleGeometry rectangle &&
                    rectangle.Rect.Right <= width - 52d)
                .Select(drawing => drawing.Geometry.Bounds)
                .OrderBy(bounds => bounds.Left)
                .ToArray();
            TimeLabelDrawing[] labels = drawings.OfType<GlyphRunDrawing>()
                .Where(drawing => drawing.GlyphRun.BaselineOrigin.Y > 478d)
                .Select(drawing => new TimeLabelDrawing(
                    new string(drawing.GlyphRun.Characters.ToArray()), drawing.Bounds))
                .Where(label => label.Text.Length == 5 && label.Text[2] == ':')
                .OrderBy(label => label.Bounds.Left)
                .ToArray();
            return new(bodies, labels);
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<Drawing> FlattenDrawing(Drawing drawing)
    {
        if (drawing is DrawingGroup group)
        {
            foreach (Drawing child in group.Children)
            {
                foreach (Drawing descendant in FlattenDrawing(child))
                {
                    yield return descendant;
                }
            }
        }
        else
        {
            yield return drawing;
        }
    }

    private static TimeSpan LabelSpan(TimeLabelDrawing[] labels)
    {
        Assert.True(labels.Length >= 2, "The rendered chart should have multiple readable time labels.");
        TimeSpan first = TimeSpan.ParseExact(labels[0].Text, @"hh\:mm", CultureInfo.InvariantCulture);
        TimeSpan last = TimeSpan.ParseExact(labels[^1].Text, @"hh\:mm", CultureInfo.InvariantCulture);
        return last >= first ? last - first : last - first + TimeSpan.FromDays(1);
    }

    private static void AssertReadableTimeLabels(TimeLabelDrawing[] labels)
    {
        for (int index = 1; index < labels.Length; index++)
        {
            Assert.True(labels[index].Bounds.Left >= labels[index - 1].Bounds.Right + 5d,
                $"Time labels {labels[index - 1].Text} and {labels[index].Text} overlap or lack spacing.");
        }
    }

    private sealed record ChartDrawingSnapshot(Rect[] CandleBodies, TimeLabelDrawing[] TimeLabels);
    private sealed record TimeLabelDrawing(string Text, Rect Bounds);
}
