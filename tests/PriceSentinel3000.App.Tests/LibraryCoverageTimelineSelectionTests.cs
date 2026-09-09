using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PriceSentinel3000.App.Controls;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(600, 24)]
    [InlineData(1000, 11)]
    public Task LibraryCoverageTimeline_ClickSelectsEveryBlockStateAndIgnoresAxisAndMargins(
        int width, int hours) => host.RunAsync(() =>
    {
        LibraryCoverageTimeline timeline = RenderingTimeline(hours);
        var control = new LibraryCoverageTimelineControl { Timeline = timeline };
        ArrangeCoverageSelection(control, width);
        Assert.Null(control.SelectedBlock);
        Assert.False(control.SelectBlockAt(new Point(width / 2d, 30)));
        Assert.Null(control.SelectedBlock);

        for (int index = 0; index < 5; index++)
        {
            Assert.True(control.SelectBlockAt(CoverageBlockCenter(control, index)));
            Assert.Same(timeline.Blocks[index], control.SelectedBlock);
            Assert.Null(control.ToolTip);
        }

        LibraryCoverageBlock selected = control.SelectedBlock!;
        Point[] outside = [new(10, 60), new(width - 10, 60), new(width - 28, 60), new(40, 47), new(40, 93)];
        foreach (Point point in outside)
        {
            Assert.False(control.SelectBlockAt(point));
            Assert.Same(selected, control.SelectedBlock);
        }

        Assert.True(control.SelectBlockAt(CoverageBlockCenter(control, timeline.Blocks.Count - 1)));
        Assert.Same(timeline.Blocks[^1], control.SelectedBlock);
        return Task.CompletedTask;
    });

    [Fact]
    public Task LibraryCoverageTimeline_SelectionSurvivesResizeAndClearsWhenTimelineChanges() => host.RunAsync(() =>
    {
        LibraryCoverageTimeline timeline = RenderingTimeline(24);
        var control = new LibraryCoverageTimelineControl { Timeline = timeline };
        ArrangeCoverageSelection(control, 600);
        Assert.True(control.SelectBlockAt(CoverageBlockCenter(control, 2)));
        ArrangeCoverageSelection(control, 1200);
        Assert.Same(timeline.Blocks[2], control.SelectedBlock);
        Assert.True(control.SelectBlockAt(CoverageBlockCenter(control, 35)));
        Assert.Same(timeline.Blocks[35], control.SelectedBlock);

        LibraryCoverageTimeline replacement = RenderingTimeline(11);
        control.Timeline = replacement;
        Assert.Null(control.SelectedBlock);
        Assert.True(control.SelectBlockAt(CoverageBlockCenter(control, 2)));
        Assert.Same(replacement.Blocks[2], control.SelectedBlock);
        control.Timeline = null;
        Assert.Null(control.SelectedBlock);
        Assert.False(control.SelectBlockAt(new Point(200, 60)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task LibraryCoverageTimeline_SelectedBlockHasVisibleOutlineWithoutChangingCoverageFill() => host.RunAsync(() =>
    {
        LibraryCoverageTimeline timeline = RenderingTimeline(24);
        var control = new LibraryCoverageTimelineControl { Timeline = timeline };
        ArrangeCoverageSelection(control, 800);
        Assert.True(control.SelectBlockAt(CoverageBlockCenter(control, 2)));
        control.UpdateLayout();

        var bitmap = new RenderTargetBitmap(800, 104, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        GeometryDrawing[] drawings = FlattenDrawing(VisualTreeHelper.GetDrawing(control)!)
            .OfType<GeometryDrawing>().ToArray();
        GeometryDrawing outline = Assert.Single(drawings, drawing =>
            drawing.Brush is null && drawing.Pen?.Thickness == 2 &&
            drawing.Geometry is RectangleGeometry);
        Assert.Equal(Color.FromRgb(248, 250, 252), ((SolidColorBrush)outline.Pen.Brush).Color);
        GeometryDrawing[] blocks = drawings.Where(drawing => drawing.Brush is SolidColorBrush &&
            drawing.Geometry is RectangleGeometry rectangle && rectangle.Rect.Height == 44).ToArray();
        Assert.Equal(timeline.Blocks.Count, blocks.Length);
        Assert.Equal(Color.FromRgb(168, 237, 189), ((SolidColorBrush)blocks[2].Brush).Color);
        Assert.True(blocks[2].Bounds.Contains(outline.Bounds));
        return Task.CompletedTask;
    });

    [Fact]
    public Task LibraryCoverageTimeline_KeyboardNavigatesBlocksWithoutChangingTheTimeline() => host.RunAsync(() =>
    {
        LibraryCoverageTimeline timeline = RenderingTimeline(24);
        var control = new LibraryCoverageTimelineControl { Timeline = timeline };
        var window = new Window
        {
            Content = control, Width = 800, Height = 180, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(control.Focusable);
            foreach ((Key key, int index) in new[]
            {
                (Key.Right, 0), (Key.Right, 1), (Key.Left, 0), (Key.Left, 0),
                (Key.End, timeline.Blocks.Count - 1), (Key.Right, timeline.Blocks.Count - 1), (Key.Home, 0),
            })
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                };
                control.RaiseEvent(args);
                Assert.True(args.Handled);
                Assert.Same(timeline.Blocks[index], control.SelectedBlock);
            }

            Assert.Same(timeline, control.Timeline);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static void ArrangeCoverageSelection(LibraryCoverageTimelineControl control, double width)
    {
        control.Measure(new Size(width, 104));
        control.Arrange(new Rect(0, 0, width, 104));
        control.UpdateLayout();
    }

    private static Point CoverageBlockCenter(LibraryCoverageTimelineControl control, int index)
    {
        LibraryCoverageTimeline timeline = control.Timeline!;
        LibraryCoverageBlock block = timeline.Blocks[index];
        double fraction = ((block.FromUtc - timeline.FromUtc).TotalSeconds +
            (block.ThroughUtc - block.FromUtc).TotalSeconds / 2) /
            (timeline.ThroughUtc - timeline.FromUtc).TotalSeconds;
        return new Point(28 + fraction * (control.ActualWidth - 56), 70);
    }
}
