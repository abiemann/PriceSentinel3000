using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PriceSentinel3000.App.Controls;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(600, 24)]
    [InlineData(740, 24)]
    [InlineData(1000, 11)]
    public Task LibraryCoverageTimeline_RendersDistinctBlocksAndReadableTimeAxis(
        int width,
        int hours) => host.RunAsync(() =>
    {
        LibraryCoverageTimeline timeline = RenderingTimeline(hours);
        var control = new LibraryCoverageTimelineControl { Timeline = timeline };
        control.Measure(new Size(width, 104));
        control.Arrange(new Rect(0, 0, width, 104));
        control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, 104, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);

        Drawing[] drawings = FlattenDrawing(VisualTreeHelper.GetDrawing(control)!).ToArray();
        GeometryDrawing[] blocks = drawings.OfType<GeometryDrawing>()
            .Where(drawing => drawing.Brush is SolidColorBrush &&
                drawing.Geometry is RectangleGeometry rectangle && rectangle.Rect.Height == 44)
            .ToArray();
        Assert.Equal(hours * 4, blocks.Length);
        Color[] expectedColors =
        [
            Color.FromRgb(36, 189, 131),
            Colors.Black,
            Color.FromRgb(168, 237, 189),
            Color.FromRgb(51, 65, 85),
            Color.FromRgb(24, 35, 47),
        ];
        for (int index = 0; index < blocks.Length; index++)
        {
            Assert.Equal(expectedColors[index % expectedColors.Length], ((SolidColorBrush)blocks[index].Brush).Color);
            Assert.InRange(blocks[index].Bounds.Width, 4, 30);
            if (index > 0)
            {
                Assert.Equal(blocks[index - 1].Bounds.Right, blocks[index].Bounds.Left, 5);
            }
        }

        GlyphRunDrawing[] labels = drawings.OfType<GlyphRunDrawing>().OrderBy(label => label.Bounds.Left).ToArray();
        Assert.InRange(labels.Length, 8, hours + 1);
        Assert.Equal(hours == 24 ? "12 AM" : "6 AM", new string(labels[0].GlyphRun.Characters.ToArray()));
        Assert.Equal(hours == 24 ? "12 AM" : "5 PM", new string(labels[^1].GlyphRun.Characters.ToArray()));
        Assert.True(labels[0].Bounds.Left >= 0);
        Assert.True(labels[^1].Bounds.Right <= width);
        for (int index = 1; index < labels.Length; index++)
        {
            Assert.True(labels[index].Bounds.Left >= labels[index - 1].Bounds.Right + 4,
                "Time labels should remain readable without overlaps at the dialog's smallest width.");
        }

        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(control);
        Assert.Contains("AAPL", peer.GetName());
        Assert.Contains("2026-09-09", peer.GetName());
        Assert.Contains("Pacific", peer.GetHelpText());
        Assert.Contains("Black: missing", peer.GetHelpText());
        Assert.Contains("Hatched gray: market closed", peer.GetHelpText());
        return Task.CompletedTask;
    });

    private static LibraryCoverageTimeline RenderingTimeline(int hours)
    {
        var date = new DateOnly(2026, 9, 9);
        DateTimeOffset from = new(2026, 9, 9, hours == 24 ? 7 : 13, 0, 0, TimeSpan.Zero);
        LibraryCoverageBlockState[] states =
        [
            LibraryCoverageBlockState.Complete,
            LibraryCoverageBlockState.Missing,
            LibraryCoverageBlockState.Partial,
            LibraryCoverageBlockState.Closed,
            LibraryCoverageBlockState.Future,
        ];
        LibraryCoverageBlock[] blocks = Enumerable.Range(0, hours * 4).Select(index =>
        {
            LibraryCoverageBlockState state = states[index % states.Length];
            int expected = state is LibraryCoverageBlockState.Closed or LibraryCoverageBlockState.Future ? 0 : 60;
            int saved = state == LibraryCoverageBlockState.Complete ? 60 :
                state == LibraryCoverageBlockState.Partial ? 30 : 0;
            return new LibraryCoverageBlock(from.AddMinutes(index * 15), from.AddMinutes((index + 1) * 15),
                saved, expected, state, $"Block {index}: {state}; {saved} of {expected} candles.");
        }).ToArray();
        LibraryCoverageTick[] ticks = Enumerable.Range(0, hours + 1).Select(index =>
        {
            int hour = ((hours == 24 ? 0 : 6) + index) % 24;
            string label = $"{(hour % 12 == 0 ? 12 : hour % 12)} {(hour < 12 ? "AM" : "PM")}";
            return new LibraryCoverageTick(index / (double)hours, label);
        }).ToArray();
        return new LibraryCoverageTimeline("AAPL", date, "Pacific time", hours == 24 ? "12 AM to 12 AM" : "6 AM to 5 PM",
            "Saved and missing coverage", string.Empty, from, from.AddHours(hours), blocks, ticks);
    }
}