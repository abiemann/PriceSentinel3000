using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Controls;

public sealed class PriceChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IEnumerable),
        typeof(PriceChart),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnPointsChanged));

    public static readonly DependencyProperty WindowMinutesProperty = DependencyProperty.Register(
        nameof(WindowMinutes),
        typeof(double),
        typeof(PriceChart),
        new FrameworkPropertyMetadata(
            7d,
            FrameworkPropertyMetadataOptions.AffectsRender));

    private INotifyCollectionChanged? _observedCollection;

    public IEnumerable? Points
    {
        get => (IEnumerable?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double WindowMinutes
    {
        get => (double)GetValue(WindowMinutesProperty);
        set => SetValue(WindowMinutesProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        PricePointViewModel[] points =
        [
            .. (Points?.Cast<PricePointViewModel>() ?? []),
        ];

        if (points.Length == 0 || RenderSize.Width <= 100 || RenderSize.Height <= 70)
        {
            return;
        }

        decimal observedMinimum = points.Min(point => point.Price);
        decimal observedMaximum = points.Max(point => point.Price);
        decimal openingPrice = points[0].Price;
        decimal minimumRange = Math.Max(0.01m, Math.Abs(openingPrice) * 0.02m);
        decimal halfMinimumRange = minimumRange / 2m;
        decimal observedRange = observedMaximum - observedMinimum;
        decimal expansionPadding = Math.Max(minimumRange * 0.08m, observedRange * 0.12m);
        decimal minimum = Math.Min(
            openingPrice - halfMinimumRange,
            observedMinimum - expansionPadding);
        decimal maximum = Math.Max(
            openingPrice + halfMinimumRange,
            observedMaximum + expansionPadding);
        decimal range = maximum - minimum;
        const double plotLeft = 12d;
        const double plotTop = 12d;
        double plotRight = Math.Max(plotLeft + 1d, RenderSize.Width - 70d);
        double plotBottom = Math.Max(plotTop + 1d, RenderSize.Height - 30d);
        double plotWidth = plotRight - plotLeft;
        double plotHeight = plotBottom - plotTop;
        DateTimeOffset lastTimestamp = points[^1].TimestampUtc;
        double windowMinutes = double.IsFinite(WindowMinutes)
            ? Math.Clamp(WindowMinutes, 1d, 60d)
            : 7d;
        DateTimeOffset firstTimestamp = lastTimestamp.AddMinutes(-windowMinutes);

        DrawAxes(
            drawingContext,
            minimum,
            maximum,
            firstTimestamp,
            lastTimestamp,
            plotLeft,
            plotTop,
            plotRight,
            plotBottom);

        var geometry = new StreamGeometry();

        using (StreamGeometryContext context = geometry.Open())
        {
            for (int index = 0; index < points.Length; index++)
            {
                double x = MapTimestamp(
                    points[index].TimestampUtc,
                    firstTimestamp,
                    lastTimestamp,
                    plotLeft,
                    plotWidth);
                double y = MapPrice(
                    points[index].Price,
                    minimum,
                    range,
                    plotTop,
                    plotHeight);
                var point = new Point(x, y);

                if (index == 0)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        var accentBrush = new SolidColorBrush(Color.FromRgb(94, 230, 177));

        if (points.Length > 1)
        {
            geometry.Freeze();
            var linePen = new Pen(accentBrush, 2);
            drawingContext.DrawGeometry(null, linePen, geometry);
        }

        DrawTradeMarkers(
            drawingContext,
            points,
            minimum,
            range,
            firstTimestamp,
            lastTimestamp,
            plotLeft,
            plotTop,
            plotWidth,
            plotHeight);

        PricePointViewModel last = points[^1];
        var lastPoint = new Point(
            MapTimestamp(
                last.TimestampUtc,
                firstTimestamp,
                lastTimestamp,
                plotLeft,
                plotWidth),
            MapPrice(last.Price, minimum, range, plotTop, plotHeight));
        drawingContext.DrawEllipse(accentBrush, null, lastPoint, 4, 4);
    }

    private void DrawAxes(
        DrawingContext drawingContext,
        decimal minimum,
        decimal maximum,
        DateTimeOffset firstTimestamp,
        DateTimeOffset lastTimestamp,
        double plotLeft,
        double plotTop,
        double plotRight,
        double plotBottom)
    {
        const int tickCount = 5;
        double plotWidth = plotRight - plotLeft;
        double plotHeight = plotBottom - plotTop;
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(25, 38, 54)), 1);
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(48, 65, 88)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(113, 133, 156));
        var typeface = new Typeface("Segoe UI");
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        TimeSpan visibleTime = lastTimestamp - firstTimestamp;

        for (int index = 0; index < tickCount; index++)
        {
            double fraction = index / (double)(tickCount - 1);
            double y = plotTop + plotHeight * fraction;
            double x = plotLeft + plotWidth * fraction;
            drawingContext.DrawLine(gridPen, new(plotLeft, y), new(plotRight, y));
            drawingContext.DrawLine(gridPen, new(x, plotTop), new(x, plotBottom));

            decimal price = maximum - (maximum - minimum) * (decimal)fraction;
            FormattedText priceLabel = CreateLabel(
                FormatPrice(price),
                typeface,
                labelBrush,
                pixelsPerDip);
            drawingContext.DrawText(
                priceLabel,
                new(plotRight + 7d, y - priceLabel.Height / 2d));

            DateTimeOffset timestamp = firstTimestamp.AddTicks(
                (long)(visibleTime.Ticks * fraction));
            string timeFormat = visibleTime >= TimeSpan.FromHours(1)
                ? "HH:mm"
                : "HH:mm:ss";
            FormattedText timeLabel = CreateLabel(
                timestamp.ToLocalTime().ToString(timeFormat, CultureInfo.InvariantCulture),
                typeface,
                labelBrush,
                pixelsPerDip);
            double labelX = index switch
            {
                0 => x,
                tickCount - 1 => x - timeLabel.Width,
                _ => x - timeLabel.Width / 2d,
            };
            drawingContext.DrawText(timeLabel, new(labelX, plotBottom + 7d));
        }

        drawingContext.DrawLine(axisPen, new(plotRight, plotTop), new(plotRight, plotBottom));
        drawingContext.DrawLine(axisPen, new(plotLeft, plotBottom), new(plotRight, plotBottom));
    }

    private void DrawTradeMarkers(
        DrawingContext drawingContext,
        IReadOnlyList<PricePointViewModel> points,
        decimal minimum,
        decimal range,
        DateTimeOffset firstTimestamp,
        DateTimeOffset lastTimestamp,
        double plotLeft,
        double plotTop,
        double plotWidth,
        double plotHeight)
    {
        var typeface = new Typeface("Segoe UI Semibold");
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int index = 0; index < points.Count; index++)
        {
            PricePointViewModel item = points[index];

            if (item.Marker is ChartTradeMarker.None)
            {
                continue;
            }

            double x = MapTimestamp(
                item.TimestampUtc,
                firstTimestamp,
                lastTimestamp,
                plotLeft,
                plotWidth);
            double y = MapPrice(
                item.Price,
                minimum,
                range,
                plotTop,
                plotHeight);
            bool isBuy = item.Marker is ChartTradeMarker.Buy;
            Color color = isBuy
                ? Color.FromRgb(94, 230, 177)
                : Color.FromRgb(255, 138, 120);
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, 2);
            double direction = isBuy ? 1d : -1d;
            var marker = new StreamGeometry();

            using (StreamGeometryContext context = marker.Open())
            {
                context.BeginFigure(new(x, y), isFilled: true, isClosed: true);
                context.LineTo(new(x - 6, y + direction * 10), true, false);
                context.LineTo(new(x + 6, y + direction * 10), true, false);
            }

            marker.Freeze();
            drawingContext.DrawGeometry(brush, pen, marker);
            var text = new FormattedText(
                isBuy ? "BUY" : "SELL",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                9d,
                brush,
                pixelsPerDip);
            double labelY = isBuy ? y + 12d : y - text.Height - 12d;
            drawingContext.DrawText(text, new(x - text.Width / 2d, labelY));
        }
    }

    private static double MapTimestamp(
        DateTimeOffset timestamp,
        DateTimeOffset firstTimestamp,
        DateTimeOffset lastTimestamp,
        double plotLeft,
        double plotWidth)
    {
        double totalTicks = Math.Max(1d, (lastTimestamp - firstTimestamp).Ticks);
        double elapsedTicks = (timestamp - firstTimestamp).Ticks;
        return plotLeft + plotWidth * Math.Clamp(elapsedTicks / totalTicks, 0d, 1d);
    }

    private static double MapPrice(
        decimal price,
        decimal minimum,
        decimal range,
        double plotTop,
        double plotHeight)
    {
        double normalized = (double)((price - minimum) / range);
        return plotTop + plotHeight * (1d - normalized);
    }

    private static string FormatPrice(decimal price) => price switch
    {
        < 1m => price.ToString("$0.0000", CultureInfo.InvariantCulture),
        _ => price.ToString("$0.00", CultureInfo.InvariantCulture),
    };

    private static FormattedText CreateLabel(
        string text,
        Typeface typeface,
        Brush brush,
        double pixelsPerDip) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            9d,
            brush,
            pixelsPerDip);

    private static void OnPointsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var chart = (PriceChart)dependencyObject;

        if (chart._observedCollection is not null)
        {
            chart._observedCollection.CollectionChanged -= chart.OnCollectionChanged;
        }

        chart._observedCollection = eventArgs.NewValue as INotifyCollectionChanged;

        if (chart._observedCollection is not null)
        {
            chart._observedCollection.CollectionChanged += chart.OnCollectionChanged;
        }

        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();
}
