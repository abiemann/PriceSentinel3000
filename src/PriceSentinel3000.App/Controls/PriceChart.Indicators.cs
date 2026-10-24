using System.Windows;
using System.Windows.Media;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Controls;

public sealed partial class PriceChart
{
    public static readonly DependencyProperty ShowEma50Property = RegisterIndicator(nameof(ShowEma50));
    public static readonly DependencyProperty ShowEma100Property = RegisterIndicator(nameof(ShowEma100));
    public static readonly DependencyProperty ShowEma200Property = RegisterIndicator(nameof(ShowEma200));
    public static readonly DependencyProperty ShowVwapProperty = RegisterIndicator(nameof(ShowVwap));

    private static readonly IndicatorStyle[] IndicatorStyles =
    [
        new("EMA 50", ShowEma50Property, Color.FromRgb(244, 201, 93), point => point.Ema50),
        new("EMA 100", ShowEma100Property, Color.FromRgb(109, 184, 255), point => point.Ema100),
        new("EMA 200", ShowEma200Property, Color.FromRgb(194, 154, 255), point => point.Ema200),
        new("VWAP", ShowVwapProperty, Color.FromRgb(255, 146, 180), point => point.Vwap),
    ];
    private static readonly Typeface IndicatorTypeface = new("Segoe UI Semibold");
    private static readonly Brush IndicatorLegendBackground = CreateIndicatorLegendBackground();

    public bool ShowEma50
    {
        get => (bool)GetValue(ShowEma50Property);
        set => SetValue(ShowEma50Property, value);
    }

    public bool ShowEma100
    {
        get => (bool)GetValue(ShowEma100Property);
        set => SetValue(ShowEma100Property, value);
    }

    public bool ShowEma200
    {
        get => (bool)GetValue(ShowEma200Property);
        set => SetValue(ShowEma200Property, value);
    }

    public bool ShowVwap
    {
        get => (bool)GetValue(ShowVwapProperty);
        set => SetValue(ShowVwapProperty, value);
    }

    private static DependencyProperty RegisterIndicator(string name) => DependencyProperty.Register(
        name, typeof(bool), typeof(PriceChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private void GetObservedPriceRange(
        IReadOnlyList<PricePointViewModel> points,
        out decimal minimum,
        out decimal maximum)
    {
        minimum = points[0].Low;
        maximum = points[0].High;
        foreach (PricePointViewModel point in points)
        {
            minimum = Math.Min(minimum, point.Low);
            maximum = Math.Max(maximum, point.High);
            foreach (IndicatorStyle style in IndicatorStyles)
            {
                if ((bool)GetValue(style.VisibilityProperty) && style.GetValue(point) is decimal value)
                {
                    minimum = Math.Min(minimum, value);
                    maximum = Math.Max(maximum, value);
                }
            }
        }
    }

    private void DrawPriceIndicators(
        DrawingContext drawingContext,
        IReadOnlyList<PricePointViewModel> points,
        TimeSpan candleInterval,
        decimal minimum,
        decimal range,
        DateTimeOffset firstTimestamp,
        DateTimeOffset lastTimestamp,
        Rect bounds)
    {
        foreach (IndicatorStyle style in IndicatorStyles)
        {
            if (!(bool)GetValue(style.VisibilityProperty))
            {
                continue;
            }

            var geometry = new StreamGeometry();
            bool hasSamples = false;
            using (StreamGeometryContext context = geometry.Open())
            {
                bool hasPrevious = false;
                DateTime previousSessionDate = default;
                foreach (PricePointViewModel candle in points)
                {
                    if (style.GetValue(candle) is not decimal value)
                    {
                        hasPrevious = false;
                        continue;
                    }

                    var point = new Point(
                        MapTimestamp(candle.TimestampUtc + candleInterval / 2d,
                            firstTimestamp, lastTimestamp, bounds.Left, bounds.Width),
                        MapPrice(value, minimum, range, bounds.Top, bounds.Height));
                    // Regular-session VWAP candles share a UTC date even across DST changes.
                    DateTime sessionDate = candle.TimestampUtc.UtcDateTime.Date;
                    if (!hasPrevious ||
                        (style.VisibilityProperty == ShowVwapProperty && sessionDate != previousSessionDate))
                    {
                        context.BeginFigure(point, isFilled: false, isClosed: false);
                        // A zero-length round-capped segment keeps an isolated valid value visible.
                        context.LineTo(point, isStroked: true, isSmoothJoin: true);
                    }
                    else
                    {
                        context.LineTo(point, isStroked: true, isSmoothJoin: true);
                    }

                    hasPrevious = true;
                    hasSamples = true;
                    previousSessionDate = sessionDate;
                }
            }

            if (hasSamples)
            {
                geometry.Freeze();
                drawingContext.DrawGeometry(null, style.Pen, geometry);
            }
        }
    }

    private void DrawIndicatorLegend(
        DrawingContext drawingContext,
        PricePointViewModel latest,
        Rect bounds)
    {
        const double padding = 5d;
        const double swatchWidth = 17d;
        double left = bounds.Left + padding;
        double x = left;
        double y = bounds.Top + padding;
        double availableWidth = Math.Max(1d, bounds.Width - padding * 2d);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (IndicatorStyle style in IndicatorStyles)
        {
            if (!(bool)GetValue(style.VisibilityProperty))
            {
                continue;
            }

            string value = style.GetValue(latest) is decimal price ? FormatPrice(price) : "--";
            FormattedText label = CreateLabel(
                $"{style.Name}  {value}", IndicatorTypeface, style.Pen.Brush, pixelsPerDip);
            label.MaxTextWidth = Math.Max(1d, availableWidth - swatchWidth - padding * 2d);
            label.Trimming = TextTrimming.CharacterEllipsis;
            double width = label.Width + swatchWidth + padding * 2d;
            double height = label.Height + padding;
            if (x > left && x + width > bounds.Right - padding)
            {
                x = left;
                y += height + 2d;
            }

            drawingContext.DrawRoundedRectangle(
                IndicatorLegendBackground, null, new Rect(x, y, width, height), 3d, 3d);
            double centerY = y + height / 2d;
            drawingContext.DrawLine(style.Pen,
                new(x + padding, centerY), new(x + swatchWidth, centerY));
            drawingContext.DrawText(label, new(x + swatchWidth + padding, y + padding / 2d));
            x += width + padding;
        }
    }

    private static Brush CreateIndicatorLegendBackground()
    {
        var brush = new SolidColorBrush(Color.FromArgb(230, 11, 18, 26));
        brush.Freeze();
        return brush;
    }

    private sealed class IndicatorStyle(
        string name,
        DependencyProperty visibilityProperty,
        Color color,
        Func<PricePointViewModel, decimal?> getValue)
    {
        public string Name { get; } = name;
        public DependencyProperty VisibilityProperty { get; } = visibilityProperty;
        public Func<PricePointViewModel, decimal?> GetValue { get; } = getValue;
        public Pen Pen { get; } = CreatePen(color);

        private static Pen CreatePen(Color color)
        {
            var pen = new Pen(new SolidColorBrush(color), 1.6d)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            return pen;
        }
    }
}
