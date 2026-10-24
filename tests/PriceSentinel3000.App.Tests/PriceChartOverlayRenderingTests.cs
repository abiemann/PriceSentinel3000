using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PriceSentinel3000.App.Controls;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    private static readonly Color[] OverlayColors =
    [
        Color.FromRgb(244, 201, 93), Color.FromRgb(109, 184, 255),
        Color.FromRgb(194, 154, 255), Color.FromRgb(255, 146, 180),
    ];

    [Theory]
    [InlineData(240, true)]
    [InlineData(480, false)]
    [InlineData(1000, true)]
    public Task ChartOverlays_DrawAtCandleCentersWithReadableLegend(int width, bool showRsi) =>
        host.RunAsync(async () =>
    {
        PricePointViewModel[] points = CreateOverlayPoints(60);
        var chart = new PriceChart
        {
            Width = width, Height = 500, WindowMinutes = 15, ShowRsi = showRsi, Points = points,
        };
        await WithIndicatorChartAsync(chart, async () =>
        {
            DrawingGroup drawing = await CaptureIndicatorDrawingAsync(chart);
            GeometryDrawing[] paths = IndicatorPaths(drawing);
            Assert.Equal(4, paths.Length);
            Rect[] candles = CandleRectangles(drawing);
            Assert.Equal(60, candles.Length);
            foreach (GeometryDrawing path in paths)
            {
                PathFigure figure = Assert.Single(PathGeometry.CreateFromGeometry(path.Geometry).Figures);
                Point[] samples = FigurePoints(figure);
                // The first round-capped zero-length segment preserves isolated values.
                Assert.Equal(candles[0].Left + candles[0].Width / 2d, samples[0].X, 4);
                Assert.Equal(candles[^1].Left + candles[^1].Width / 2d, samples[^1].X, 4);
                Assert.InRange(path.Geometry.Bounds.Top, 4, 478);
                Assert.InRange(path.Geometry.Bounds.Bottom, 4, showRsi ? 341 : 478);
                Assert.True(path.Pen.IsFrozen);
            }

            GlyphRunDrawing[] legend = FlattenDrawing(drawing).OfType<GlyphRunDrawing>()
                .Where(item => item.ForegroundBrush is SolidColorBrush brush &&
                    OverlayColors.Contains(brush.Color)).ToArray();
            Assert.Equal(4, legend.Length);
            Assert.All(legend, item => Assert.InRange(item.Bounds.Right, 0, width - 52));
            for (int index = 0; index < legend.Length; index++)
            {
                Assert.All(legend.Skip(index + 1), other =>
                    Assert.False(legend[index].Bounds.IntersectsWith(other.Bounds)));
            }
            Assert.Contains(legend, item => GlyphText(item).Contains("EMA 50"));
            Assert.Contains(legend, item => GlyphText(item).Contains("VWAP"));
        });
    });

    [Fact]
    public Task ChartOverlays_TogglesIndependentlyAndRemovesDisabledValuesFromAutoScale() =>
        host.RunAsync(async () =>
    {
        var chart = new PriceChart
        {
            Width = 480, Height = 500, Points = CreateOverlayPoints(20),
        };
        await WithIndicatorChartAsync(chart, async () =>
        {
            DrawingGroup enabled = await CaptureIndicatorDrawingAsync(chart);
            double expandedCandleHeight = CandleRectangles(enabled)[0].Height;
            DependencyProperty[] toggles =
            [
                PriceChart.ShowEma50Property, PriceChart.ShowEma100Property,
                PriceChart.ShowEma200Property, PriceChart.ShowVwapProperty,
            ];
            for (int index = 0; index < toggles.Length; index++)
            {
                chart.SetValue(toggles[index], false);
                DrawingGroup disabled = await CaptureIndicatorDrawingAsync(chart);
                Assert.Equal(3 - index, IndicatorPaths(disabled).Length);
                Assert.DoesNotContain(IndicatorPaths(disabled), path =>
                    ((SolidColorBrush)path.Pen.Brush).Color == OverlayColors[index]);
            }

            DrawingGroup candlesOnly = await CaptureIndicatorDrawingAsync(chart);
            Assert.True(CandleRectangles(candlesOnly)[0].Height > expandedCandleHeight * 5d);
            chart.ShowEma50 = true;
            Assert.Single(IndicatorPaths(await CaptureIndicatorDrawingAsync(chart)));
        });
    });

    [Fact]
    public Task ChartOverlays_BreakNullGapsAndKeepIsolatedValuesVisible() => host.RunAsync(async () =>
    {
        PricePointViewModel[] points = CreateOverlayPoints(5);
        points[1] = points[1] with { Ema50 = null, Ema100 = null, Ema200 = null, Vwap = null };
        var chart = new PriceChart { Width = 480, Height = 500, Points = points };
        await WithIndicatorChartAsync(chart, async () =>
        {
            foreach (GeometryDrawing path in IndicatorPaths(await CaptureIndicatorDrawingAsync(chart)))
            {
                PathFigureCollection figures = PathGeometry.CreateFromGeometry(path.Geometry).Figures;
                Assert.Equal(2, figures.Count);
                Assert.Equal(figures[0].StartPoint, FigurePoints(figures[0])[^1]);
                Assert.True(figures[1].StartPoint.X > figures[0].StartPoint.X);
            }
        });
    });

    [Fact]
    public Task ChartOverlays_BreakVwapBetweenSessionsWithoutBreakingEma() => host.RunAsync(async () =>
    {
        PricePointViewModel[] points = CreateOverlayPoints(4);
        var start = new DateTimeOffset(2026, 10, 20, 13, 30, 0, TimeSpan.Zero);
        points[0] = points[0] with { TimestampUtc = start };
        points[1] = points[1] with { TimestampUtc = start.AddHours(1) };
        points[2] = points[2] with { TimestampUtc = start.AddDays(1) };
        points[3] = points[3] with { TimestampUtc = start.AddDays(1).AddHours(1) };
        var chart = new PriceChart
        {
            Width = 1000, Height = 500, Points = points,
            CandleIntervalSeconds = 3600, WindowMinutes = 15,
        };
        await WithIndicatorChartAsync(chart, async () =>
        {
            GeometryDrawing[] paths = IndicatorPaths(await CaptureIndicatorDrawingAsync(chart));
            Assert.All(paths.Take(3), path =>
                Assert.Single(PathGeometry.CreateFromGeometry(path.Geometry).Figures));
            Assert.Equal(2, PathGeometry.CreateFromGeometry(paths[3].Geometry).Figures.Count);
        });
    });

    [Fact]
    public Task ChartOverlays_ManualFitIncludesIndicatorsAndClipsLaterOutOfRangeValues() =>
        host.RunAsync(async () =>
    {
        PricePointViewModel[] points = CreateOverlayPoints(10);
        var chart = new PriceChart { Width = 480, Height = 500, Points = points, IsManualScale = true };
        await WithIndicatorChartAsync(chart, async () =>
        {
            DrawingGroup initial = await CaptureIndicatorDrawingAsync(chart);
            Assert.All(IndicatorPaths(initial), path =>
            {
                Assert.InRange(path.Geometry.Bounds.Top, 4, 478);
                Assert.InRange(path.Geometry.Bounds.Bottom, 4, 478);
            });
            chart.Points = points.Select(point => point with { Ema50 = 1000m }).ToArray();
            DrawingGroup changed = await CaptureIndicatorDrawingAsync(chart);
            GeometryDrawing path = IndicatorPaths(changed)[0];
            Assert.True(path.Geometry.Bounds.Top < 4d);
            DrawingGroup clipped = Assert.Single(DrawingGroups(changed), group =>
                group.ClipGeometry is not null && FlattenDrawing(group).Contains(path));
            Rect clipBounds = clipped.ClipGeometry.Bounds;
            Assert.Equal(4d, clipBounds.Left, 5);
            Assert.Equal(4d, clipBounds.Top, 5);
            Assert.Equal(424d, clipBounds.Width, 5);
            Assert.Equal(474d, clipBounds.Height, 5);
        });
    });

    private static PricePointViewModel[] CreateOverlayPoints(int count)
    {
        var start = new DateTimeOffset(2026, 10, 21, 13, 30, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count).Select(index => new PricePointViewModel(
            start.AddSeconds(index * 15), 100m, 101m, 99m, 100.5m,
            Ema50: 50m + index / 100m, Ema100: 80m + index / 100m,
            Ema200: 200m + index / 100m, Vwap: 120m + index / 100m)).ToArray();
    }

    private static async Task WithIndicatorChartAsync(PriceChart chart, Func<Task> assertions)
    {
        var window = new Window
        {
            Content = chart, SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
        };
        try
        {
            window.Show();
            await assertions();
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<DrawingGroup> CaptureIndicatorDrawingAsync(PriceChart chart)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)chart.Width, (int)chart.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(chart);
        return VisualTreeHelper.GetDrawing(chart)!;
    }

    private static GeometryDrawing[] IndicatorPaths(DrawingGroup drawing) =>
        FlattenDrawing(drawing).OfType<GeometryDrawing>().Where(item =>
            item.Geometry is StreamGeometry && item.Pen?.Brush is SolidColorBrush brush &&
            OverlayColors.Contains(brush.Color)).ToArray();

    private static Rect[] CandleRectangles(DrawingGroup drawing) =>
        FlattenDrawing(drawing).OfType<GeometryDrawing>().Where(item =>
            item.Geometry is RectangleGeometry { RadiusX: 0, RadiusY: 0 } &&
            item.Brush is SolidColorBrush brush &&
            brush.Color == Color.FromRgb(90, 203, 60)).Select(item => item.Geometry.Bounds)
            .OrderBy(bounds => bounds.Left).ToArray();

    private static Point[] FigurePoints(PathFigure figure) =>
        [figure.StartPoint, .. figure.Segments.SelectMany(segment => segment switch
        {
            PolyLineSegment polyline => polyline.Points.AsEnumerable(),
            LineSegment line => [line.Point],
            _ => Enumerable.Empty<Point>(),
        })];

    private static string GlyphText(GlyphRunDrawing drawing) => new(drawing.GlyphRun.Characters.ToArray());

    private static IEnumerable<DrawingGroup> DrawingGroups(DrawingGroup drawing)
    {
        yield return drawing;
        foreach (DrawingGroup group in drawing.Children.OfType<DrawingGroup>())
        {
            foreach (DrawingGroup descendant in DrawingGroups(group))
            {
                yield return descendant;
            }
        }
    }
}
