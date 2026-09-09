using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Controls;

public sealed class LibraryCoverageTimelineControl : FrameworkElement
{
    private const double PreferredHeight = 104;
    private const double HorizontalPadding = 28;
    private const double AxisY = 30;
    private const double BlocksTop = 48;
    private const double BlocksHeight = 44;
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly SolidColorBrush CompleteBrush = FrozenBrush("#24BD83");
    private static readonly SolidColorBrush PartialBrush = FrozenBrush("#A8EDBD");
    private static readonly SolidColorBrush MissingBrush = FrozenBrush("#000000");
    private static readonly SolidColorBrush ClosedBrush = FrozenBrush("#334155");
    private static readonly SolidColorBrush FutureBrush = FrozenBrush("#173B5C");
    private static readonly SolidColorBrush LabelBrush = FrozenBrush("#CBD5E1");
    private static readonly Pen AxisPen = FrozenPen("#40546C");
    private static readonly Pen SeparatorPen = FrozenPen("#34445A");
    private static readonly Pen ClosedHatchPen = FrozenPen("#8292A5");
    private static readonly Pen SelectionPen = FrozenPen("#F8FAFC", 2);

    private static readonly DependencyPropertyKey SelectedBlockPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedBlock),
        typeof(LibraryCoverageBlock),
        typeof(LibraryCoverageTimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedBlockProperty = SelectedBlockPropertyKey.DependencyProperty;

    public static readonly DependencyProperty TimelineProperty = DependencyProperty.Register(
        nameof(Timeline),
        typeof(LibraryCoverageTimeline),
        typeof(LibraryCoverageTimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTimelineChanged));

    public LibraryCoverageTimelineControl()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = true;
    }

    public LibraryCoverageTimeline? Timeline
    {
        get => (LibraryCoverageTimeline?)GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    public LibraryCoverageBlock? SelectedBlock => (LibraryCoverageBlock?)GetValue(SelectedBlockProperty);

    public event EventHandler? BlockSelectionChanged;

    internal void SelectBlockStartingAt(DateTimeOffset fromUtc) =>
        SetValue(SelectedBlockPropertyKey, Timeline?.Blocks.FirstOrDefault(block => block.FromUtc == fromUtc));

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width, PreferredHeight);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Timeline is not { } timeline || ActualWidth <= HorizontalPadding * 2 ||
            timeline.ThroughUtc <= timeline.FromUtc)
        {
            return;
        }

        double width = ActualWidth - HorizontalPadding * 2;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double axisY = Snap(AxisY, pixelsPerDip);
        drawingContext.DrawLine(AxisPen, new(HorizontalPadding, axisY), new(ActualWidth - HorizontalPadding, axisY));

        var labels = timeline.Ticks.Select(tick =>
        {
            var text = new FormattedText(tick.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface, 12, LabelBrush, pixelsPerDip);
            double x = HorizontalPadding + Math.Clamp(tick.Position, 0, 1) * width;
            return (Text: text, X: x, Left: x - text.Width / 2);
        }).OrderBy(label => label.X).ToArray();
        double lastLabelLeft = labels.Length > 0 ? labels[^1].Left : double.PositiveInfinity;
        double previousLabelRight = double.NegativeInfinity;
        for (int index = 0; index < labels.Length; index++)
        {
            var label = labels[index];
            double tickX = Snap(label.X, pixelsPerDip);
            drawingContext.DrawLine(AxisPen, new(tickX, axisY), new(tickX, BlocksTop - 5));
            bool endpoint = index == 0 || index == labels.Length - 1;
            if (endpoint || (label.Left >= previousLabelRight + 8 &&
                label.Left + label.Text.Width + 8 <= lastLabelLeft))
            {
                drawingContext.DrawText(label.Text, new(label.Left, 4));
                previousLabelRight = label.Left + label.Text.Width;
            }
        }

        double totalSeconds = (timeline.ThroughUtc - timeline.FromUtc).TotalSeconds;
        Rect? selectedRectangle = null;
        foreach (var block in timeline.Blocks)
        {
            double left = HorizontalPadding + Math.Clamp(
                (block.FromUtc - timeline.FromUtc).TotalSeconds / totalSeconds, 0, 1) * width;
            double right = HorizontalPadding + Math.Clamp(
                (block.ThroughUtc - timeline.FromUtc).TotalSeconds / totalSeconds, 0, 1) * width;
            left = Math.Round(left * pixelsPerDip) / pixelsPerDip;
            right = Math.Round(right * pixelsPerDip) / pixelsPerDip;
            if (right <= left)
            {
                continue;
            }

            var rectangle = new Rect(left, BlocksTop, right - left, BlocksHeight);
            if (ReferenceEquals(block, SelectedBlock))
            {
                selectedRectangle = rectangle;
            }
            Brush fill = block.State switch
            {
                LibraryCoverageBlockState.Complete => CompleteBrush,
                LibraryCoverageBlockState.Partial => PartialBrush,
                LibraryCoverageBlockState.Missing => MissingBrush,
                LibraryCoverageBlockState.Closed => ClosedBrush,
                _ => FutureBrush,
            };
            drawingContext.DrawRectangle(fill, null, rectangle);
            if (block.State == LibraryCoverageBlockState.Closed)
            {
                drawingContext.PushClip(new RectangleGeometry(rectangle));
                for (double x = rectangle.Left - BlocksHeight; x < rectangle.Right; x += 8)
                {
                    drawingContext.DrawLine(ClosedHatchPen,
                        new(x, rectangle.Bottom), new(x + BlocksHeight, rectangle.Top));
                }

                drawingContext.Pop();
            }

            drawingContext.DrawLine(SeparatorPen,
                new(Snap(left, pixelsPerDip), BlocksTop),
                new(Snap(left, pixelsPerDip), BlocksTop + BlocksHeight));
        }

        drawingContext.DrawRectangle(null, SeparatorPen,
            new Rect(Snap(HorizontalPadding, pixelsPerDip), BlocksTop + 0.5 / pixelsPerDip,
                width, BlocksHeight));
        if (selectedRectangle is { } selection)
        {
            selection.Inflate(-Math.Min(1, selection.Width / 4), -1);
            drawingContext.DrawRectangle(null, SelectionPen, selection);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = HitTestBlock(e.GetPosition(this)) >= 0 ? Cursors.Hand : null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (SelectBlockAt(e.GetPosition(this)))
        {
            Focus();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Timeline is not { Blocks.Count: > 0 } timeline ||
            e.Key is not (Key.Left or Key.Right or Key.Home or Key.End))
        {
            return;
        }

        int current = -1;
        for (int index = 0; index < timeline.Blocks.Count; index++)
        {
            if (ReferenceEquals(timeline.Blocks[index], SelectedBlock))
            {
                current = index;
                break;
            }
        }

        int next = e.Key switch
        {
            Key.Home => 0,
            Key.End => timeline.Blocks.Count - 1,
            Key.Left => Math.Max(0, current - 1),
            _ => Math.Min(timeline.Blocks.Count - 1, current + 1),
        };
        SelectUserBlock(timeline.Blocks[next]);
        e.Handled = true;
    }

    internal bool SelectBlockAt(Point point)
    {
        int index = HitTestBlock(point);
        if (index < 0)
        {
            return false;
        }

        SelectUserBlock(Timeline!.Blocks[index]);
        return true;
    }

    private void SelectUserBlock(LibraryCoverageBlock block)
    {
        if (SelectedBlock?.FromUtc == block.FromUtc) return;
        SetValue(SelectedBlockPropertyKey, block);
        BlockSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineAutomationPeer(this);

    private int HitTestBlock(Point point)
    {
        if (Timeline is not { } timeline || point.Y < BlocksTop || point.Y > BlocksTop + BlocksHeight ||
            point.X < HorizontalPadding || point.X >= ActualWidth - HorizontalPadding ||
            ActualWidth <= HorizontalPadding * 2 || timeline.ThroughUtc <= timeline.FromUtc)
        {
            return -1;
        }

        double fraction = (point.X - HorizontalPadding) / (ActualWidth - HorizontalPadding * 2);
        DateTimeOffset timestamp = timeline.FromUtc +
            TimeSpan.FromTicks((long)((timeline.ThroughUtc - timeline.FromUtc).Ticks * fraction));
        for (int index = 0; index < timeline.Blocks.Count; index++)
        {
            if (timestamp >= timeline.Blocks[index].FromUtc && timestamp < timeline.Blocks[index].ThroughUtc)
            {
                return index;
            }
        }

        return -1;
    }

    private static void OnTimelineChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var control = (LibraryCoverageTimelineControl)source;
        control.SetValue(SelectedBlockPropertyKey, null);
        control.Cursor = null;
    }

    private static double Snap(double value, double pixelsPerDip) =>
        (Math.Floor(value * pixelsPerDip) + 0.5) / pixelsPerDip;

    private static SolidColorBrush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string color, double thickness = 1)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private sealed class TimelineAutomationPeer(LibraryCoverageTimelineControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(LibraryCoverageTimelineControl);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetNameCore() => owner.Timeline is { } timeline
            ? $"{timeline.Symbol} coverage for {timeline.Date:yyyy-MM-dd}"
            : "Daily data coverage";
        protected override string GetHelpTextCore() => owner.Timeline is { } timeline
            ? $"{timeline.RangeLabel}. {timeline.TimeZoneLabel}. {timeline.Summary}. {timeline.Notice} " +
              "Green: complete. Black: missing. Light green: partial. Hatched gray: market closed. Solid blue: future. " +
              "Click a block for details, or use the Left and Right arrow keys to select a block."
            : "Hourly time axis with 15-minute data coverage blocks.";
    }
}