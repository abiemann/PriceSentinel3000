using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Controls;

public sealed class PriceChart : FrameworkElement
{
    private static readonly TimeSpan CandleInterval = TimeSpan.FromSeconds(15);
    private static readonly Color UpCandleColor = Color.FromRgb(90, 203, 60);
    private static readonly Color DownCandleColor = Color.FromRgb(255, 90, 31);
    private static readonly Color FlatCandleColor = Color.FromRgb(142, 160, 183);
    private Point? _pointerPosition;
    private MouseButton? _scaleDragButton;
    private Point _scaleDragStart;
    private decimal _scaleDragMinimum;
    private decimal _scaleDragMaximum;
    private decimal? _manualMinimum;
    private decimal? _manualMaximum;
    private decimal _lastRenderedMinimum;
    private decimal _lastRenderedMaximum;
    private bool _hasRenderedScale;

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

    public static readonly DependencyProperty IsManualScaleProperty = DependencyProperty.Register(
        nameof(IsManualScale),
        typeof(bool),
        typeof(PriceChart),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnIsManualScaleChanged));

    public static readonly DependencyProperty ScaleResetVersionProperty = DependencyProperty.Register(
        nameof(ScaleResetVersion),
        typeof(int),
        typeof(PriceChart),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.None,
            OnScaleResetVersionChanged));

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

    public bool IsManualScale
    {
        get => (bool)GetValue(IsManualScaleProperty);
        set => SetValue(IsManualScaleProperty, value);
    }

    public int ScaleResetVersion
    {
        get => (int)GetValue(ScaleResetVersionProperty);
        set => SetValue(ScaleResetVersionProperty, value);
    }

    public PriceChart()
    {
        Cursor = Cursors.Cross;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(
            Brushes.Transparent,
            null,
            new Rect(RenderSize));

        PricePointViewModel[] points =
        [
            .. (Points?.Cast<PricePointViewModel>() ?? []),
        ];

        if (points.Length == 0 || RenderSize.Width <= 100 || RenderSize.Height <= 70)
        {
            return;
        }

        decimal observedMinimum = points.Min(point => point.Low);
        decimal observedMaximum = points.Max(point => point.High);
        decimal openingPrice = points[0].Open;
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
        (minimum, maximum, decimal priceStep) = CreatePriceScale(minimum, maximum);

        if (IsManualScale)
        {
            _manualMinimum ??= minimum;
            _manualMaximum ??= maximum;
            minimum = _manualMinimum.Value;
            maximum = _manualMaximum.Value;
            priceStep = (maximum - minimum) / 4m;
        }

        _lastRenderedMinimum = minimum;
        _lastRenderedMaximum = maximum;
        _hasRenderedScale = true;
        decimal range = maximum - minimum;
        Rect plotBounds = GetPlotBounds();
        double plotLeft = plotBounds.Left;
        double plotTop = plotBounds.Top;
        double plotRight = plotBounds.Right;
        double plotBottom = plotBounds.Bottom;
        double plotWidth = plotRight - plotLeft;
        double plotHeight = plotBottom - plotTop;
        DateTimeOffset lastTimestamp = points[^1].TimestampUtc + CandleInterval;
        double windowMinutes = double.IsFinite(WindowMinutes)
            ? Math.Clamp(WindowMinutes, 1d, 60d)
            : 7d;
        DateTimeOffset firstTimestamp = lastTimestamp.AddMinutes(-windowMinutes);

        DrawAxes(
            drawingContext,
            minimum,
            maximum,
            priceStep,
            firstTimestamp,
            lastTimestamp,
            plotLeft,
            plotTop,
            plotRight,
            plotBottom);

        if (points[^1].Close >= minimum && points[^1].Close <= maximum)
        {
            DrawLastPriceGuide(
                drawingContext,
                points[^1],
                minimum,
                range,
                plotTop,
                plotRight,
                plotWidth,
                plotHeight);
        }

        var plotClip = new RectangleGeometry(new(
            plotLeft,
            plotTop,
            plotWidth,
            plotHeight));
        plotClip.Freeze();
        drawingContext.PushClip(plotClip);

        DrawCandles(
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

        drawingContext.Pop();

        DrawPointerReadout(
            drawingContext,
            minimum,
            range,
            firstTimestamp,
            lastTimestamp,
            plotLeft,
            plotTop,
            plotRight,
            plotBottom);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        _pointerPosition = eventArgs.GetPosition(this);

        if (_scaleDragButton.HasValue)
        {
            ApplyScaleDrag(_pointerPosition.Value);
            eventArgs.Handled = true;
        }

        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);

        if (!IsManualScale ||
            eventArgs.ChangedButton is not (MouseButton.Left or MouseButton.Right) ||
            !_hasRenderedScale)
        {
            return;
        }

        Point pointer = eventArgs.GetPosition(this);
        Rect plotBounds = GetPlotBounds();

        if (!plotBounds.Contains(pointer))
        {
            return;
        }

        _manualMinimum ??= _lastRenderedMinimum;
        _manualMaximum ??= _lastRenderedMaximum;
        _scaleDragButton = eventArgs.ChangedButton;
        _scaleDragStart = pointer;
        _scaleDragMinimum = _manualMinimum.Value;
        _scaleDragMaximum = _manualMaximum.Value;
        Cursor = Cursors.SizeNS;
        CaptureMouse();
        eventArgs.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);

        if (_scaleDragButton != eventArgs.ChangedButton)
        {
            return;
        }

        EndScaleDrag();
        eventArgs.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);

        if (!_scaleDragButton.HasValue)
        {
            _pointerPosition = null;
        }

        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs eventArgs)
    {
        base.OnLostMouseCapture(eventArgs);
        EndScaleDrag(releaseCapture: false);
    }

    private Rect GetPlotBounds()
    {
        const double plotLeft = 4d;
        const double plotTop = 4d;
        double plotRight = Math.Max(plotLeft + 1d, RenderSize.Width - 52d);
        double plotBottom = Math.Max(plotTop + 1d, RenderSize.Height - 22d);
        return new(
            plotLeft,
            plotTop,
            plotRight - plotLeft,
            plotBottom - plotTop);
    }

    private void ApplyScaleDrag(Point pointer)
    {
        if (_scaleDragButton is not MouseButton button)
        {
            return;
        }

        Rect plotBounds = GetPlotBounds();
        decimal startingRange = _scaleDragMaximum - _scaleDragMinimum;

        if (plotBounds.Height <= 0d || startingRange <= 0m)
        {
            return;
        }

        decimal dragFraction = (decimal)(
            (pointer.Y - _scaleDragStart.Y) / plotBounds.Height);
        decimal priceDelta = startingRange * dragFraction;
        decimal minimumSpan = Math.Max(0.0001m, startingRange * 0.02m);
        decimal minimum = _scaleDragMinimum;
        decimal maximum = _scaleDragMaximum;

        if (button is MouseButton.Left)
        {
            if (priceDelta < 0m)
            {
                minimum = Math.Max(0m, _scaleDragMinimum + priceDelta);
            }
            else
            {
                maximum = _scaleDragMaximum + priceDelta;
            }
        }
        else if (priceDelta > 0m)
        {
            minimum = Math.Min(
                _scaleDragMinimum + priceDelta,
                _scaleDragMaximum - minimumSpan);
        }
        else
        {
            maximum = Math.Max(
                _scaleDragMaximum + priceDelta,
                _scaleDragMinimum + minimumSpan);
        }

        _manualMinimum = minimum;
        _manualMaximum = maximum;
        InvalidateVisual();
    }

    private void EndScaleDrag(bool releaseCapture = true)
    {
        if (!_scaleDragButton.HasValue)
        {
            return;
        }

        _scaleDragButton = null;
        Cursor = Cursors.Cross;

        if (releaseCapture && IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        InvalidateVisual();
    }

    private static void DrawCandles(
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
        double candleSlotWidth = plotWidth *
            CandleInterval.Ticks /
            Math.Max(1d, (lastTimestamp - firstTimestamp).Ticks);
        double bodyWidth = Math.Clamp(candleSlotWidth * 0.80d, 4d, 28d);

        foreach (PricePointViewModel candle in points)
        {
            DateTimeOffset centerTimestamp = candle.TimestampUtc + CandleInterval / 2d;

            if (centerTimestamp < firstTimestamp || centerTimestamp > lastTimestamp)
            {
                continue;
            }

            double x = MapTimestamp(
                centerTimestamp,
                firstTimestamp,
                lastTimestamp,
                plotLeft,
                plotWidth);
            double highY = MapPrice(
                candle.High, minimum, range, plotTop, plotHeight);
            double lowY = MapPrice(
                candle.Low, minimum, range, plotTop, plotHeight);
            double openY = MapPrice(
                candle.Open, minimum, range, plotTop, plotHeight);
            double closeY = MapPrice(
                candle.Close, minimum, range, plotTop, plotHeight);
            Color color = candle.Close.CompareTo(candle.Open) switch
            {
                > 0 => UpCandleColor,
                < 0 => DownCandleColor,
                _ => FlatCandleColor,
            };
            var wickBrush = new SolidColorBrush(color);
            var bodyBrush = new SolidColorBrush(color);
            var candlePen = new Pen(wickBrush, 1.15d);
            wickBrush.Freeze();
            bodyBrush.Freeze();
            candlePen.Freeze();
            drawingContext.DrawLine(candlePen, new(x, highY), new(x, lowY));

            double bodyTop = Math.Min(openY, closeY);
            double bodyHeight = Math.Max(2.4d, Math.Abs(closeY - openY));
            drawingContext.DrawRectangle(
                bodyBrush,
                null,
                new(
                    x - bodyWidth / 2d,
                    bodyTop - (bodyHeight == 2.4d ? 1.2d : 0d),
                    bodyWidth,
                    bodyHeight));
        }
    }

    private void DrawAxes(
        DrawingContext drawingContext,
        decimal minimum,
        decimal maximum,
        decimal priceStep,
        DateTimeOffset firstTimestamp,
        DateTimeOffset lastTimestamp,
        double plotLeft,
        double plotTop,
        double plotRight,
        double plotBottom)
    {
        const int timeTickCount = 5;
        double plotWidth = plotRight - plotLeft;
        double plotHeight = plotBottom - plotTop;
        var horizontalGridPen = new Pen(
            new SolidColorBrush(Color.FromRgb(29, 42, 56)),
            1d);
        var verticalGridPen = new Pen(
            new SolidColorBrush(Color.FromArgb(115, 29, 42, 56)),
            1d);
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 57, 75)), 1d);
        var labelBrush = new SolidColorBrush(Color.FromRgb(126, 140, 155));
        horizontalGridPen.Freeze();
        verticalGridPen.Freeze();
        axisPen.Freeze();
        labelBrush.Freeze();
        var typeface = new Typeface("Segoe UI");
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        TimeSpan visibleTime = lastTimestamp - firstTimestamp;

        int priceTickCount = Math.Max(
            2,
            (int)Math.Round((maximum - minimum) / priceStep) + 1);

        for (int index = 0; index < priceTickCount; index++)
        {
            decimal price = maximum - priceStep * index;
            double fraction = (double)((maximum - price) / (maximum - minimum));
            double y = plotTop + plotHeight * fraction;
            drawingContext.DrawLine(
                horizontalGridPen,
                new(plotLeft, y),
                new(plotRight, y));

            FormattedText priceLabel = CreateLabel(
                FormatPrice(price),
                typeface,
                labelBrush,
                pixelsPerDip);
            drawingContext.DrawText(
                priceLabel,
                new(
                    plotRight + 7d,
                    Math.Clamp(
                        y - priceLabel.Height / 2d,
                        0d,
                        RenderSize.Height - priceLabel.Height)));
        }

        for (int index = 0; index < timeTickCount; index++)
        {
            double fraction = index / (double)(timeTickCount - 1);
            double x = plotLeft + plotWidth * fraction;
            drawingContext.DrawLine(
                verticalGridPen,
                new(x, plotTop),
                new(x, plotBottom));

            DateTimeOffset timestamp = firstTimestamp.AddTicks(
                (long)(visibleTime.Ticks * fraction));
            string timeFormat = visibleTime >= TimeSpan.FromMinutes(5)
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
                timeTickCount - 1 => x - timeLabel.Width,
                _ => x - timeLabel.Width / 2d,
            };
            drawingContext.DrawText(timeLabel, new(labelX, plotBottom + 7d));
        }

        drawingContext.DrawLine(axisPen, new(plotRight, plotTop), new(plotRight, plotBottom));
        drawingContext.DrawLine(axisPen, new(plotLeft, plotBottom), new(plotRight, plotBottom));
    }

    private void DrawLastPriceGuide(
        DrawingContext drawingContext,
        PricePointViewModel candle,
        decimal minimum,
        decimal range,
        double plotTop,
        double plotRight,
        double plotWidth,
        double plotHeight)
    {
        Color color = candle.Close.CompareTo(candle.Open) switch
        {
            > 0 => UpCandleColor,
            < 0 => DownCandleColor,
            _ => FlatCandleColor,
        };
        var guideBrush = new SolidColorBrush(Color.FromArgb(
            170,
            color.R,
            color.G,
            color.B));
        var guidePen = new Pen(guideBrush, 1d)
        {
            DashStyle = new DashStyle([2d, 3d], 0d),
        };
        var labelBrush = new SolidColorBrush(color);
        guideBrush.Freeze();
        guidePen.Freeze();
        labelBrush.Freeze();

        double y = MapPrice(
            candle.Close,
            minimum,
            range,
            plotTop,
            plotHeight);
        drawingContext.DrawLine(
            guidePen,
            new(plotRight - plotWidth, y),
            new(plotRight, y));

        FormattedText priceLabel = new(
            FormatPrice(candle.Close),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"),
            9d,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        const double horizontalPadding = 5d;
        const double verticalPadding = 2d;
        var labelBounds = new Rect(
            plotRight + 4d,
            Math.Clamp(
                y - priceLabel.Height / 2d - verticalPadding,
                0d,
                RenderSize.Height - priceLabel.Height - verticalPadding * 2d),
            priceLabel.Width + horizontalPadding * 2d,
            priceLabel.Height + verticalPadding * 2d);
        drawingContext.DrawRoundedRectangle(
            labelBrush,
            null,
            labelBounds,
            3d,
            3d);
        drawingContext.DrawText(
            priceLabel,
            new(
                labelBounds.Left + horizontalPadding,
                labelBounds.Top + verticalPadding));
    }

    private void DrawPointerReadout(
        DrawingContext drawingContext,
        decimal minimum,
        decimal range,
        DateTimeOffset firstTimestamp,
        DateTimeOffset lastTimestamp,
        double plotLeft,
        double plotTop,
        double plotRight,
        double plotBottom)
    {
        if (_pointerPosition is not Point pointer ||
            pointer.X < plotLeft ||
            pointer.X > plotRight ||
            pointer.Y < plotTop ||
            pointer.Y > plotBottom)
        {
            return;
        }

        double plotWidth = plotRight - plotLeft;
        double plotHeight = plotBottom - plotTop;
        double xFraction = Math.Clamp(
            (pointer.X - plotLeft) / plotWidth,
            0d,
            1d);
        double yFraction = Math.Clamp(
            (pointer.Y - plotTop) / plotHeight,
            0d,
            1d);
        DateTimeOffset timestamp = firstTimestamp.AddTicks(
            (long)((lastTimestamp - firstTimestamp).Ticks * xFraction));
        decimal price = minimum + range * (decimal)(1d - yFraction);
        var guidePen = new Pen(
            new SolidColorBrush(Color.FromArgb(195, 126, 147, 171)),
            1d)
        {
            DashStyle = new DashStyle([3d, 3d], 0d),
        };
        var labelBrush = new SolidColorBrush(Color.FromRgb(51, 70, 92));
        guidePen.Freeze();
        labelBrush.Freeze();

        drawingContext.DrawLine(
            guidePen,
            new(pointer.X, plotTop),
            new(pointer.X, plotBottom));
        drawingContext.DrawLine(
            guidePen,
            new(plotLeft, pointer.Y),
            new(plotRight, pointer.Y));

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI Semibold");
        FormattedText timeLabel = new(
            timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            9d,
            Brushes.White,
            pixelsPerDip);
        FormattedText priceLabel = new(
            FormatPrice(price),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            9d,
            Brushes.White,
            pixelsPerDip);

        const double horizontalPadding = 5d;
        const double verticalPadding = 2d;
        double timeWidth = timeLabel.Width + horizontalPadding * 2d;
        double timeLeft = Math.Clamp(
            pointer.X - timeWidth / 2d,
            plotLeft,
            plotRight - timeWidth);
        var timeBounds = new Rect(
            timeLeft,
            plotBottom + 4d,
            timeWidth,
            timeLabel.Height + verticalPadding * 2d);
        double priceWidth = priceLabel.Width + horizontalPadding * 2d;
        double priceLeft = Math.Min(
            plotRight + 4d,
            RenderSize.Width - priceWidth - 2d);
        var priceBounds = new Rect(
            priceLeft,
            Math.Clamp(
                pointer.Y - priceLabel.Height / 2d - verticalPadding,
                0d,
                RenderSize.Height - priceLabel.Height - verticalPadding * 2d),
            priceWidth,
            priceLabel.Height + verticalPadding * 2d);

        DrawAxisReadout(
            drawingContext,
            labelBrush,
            timeBounds,
            timeLabel,
            horizontalPadding,
            verticalPadding);
        DrawAxisReadout(
            drawingContext,
            labelBrush,
            priceBounds,
            priceLabel,
            horizontalPadding,
            verticalPadding);
    }

    private static void DrawAxisReadout(
        DrawingContext drawingContext,
        Brush background,
        Rect bounds,
        FormattedText text,
        double horizontalPadding,
        double verticalPadding)
    {
        drawingContext.DrawRoundedRectangle(
            background,
            null,
            bounds,
            3d,
            3d);
        drawingContext.DrawText(
            text,
            new(
                bounds.Left + horizontalPadding,
                bounds.Top + verticalPadding));
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

            DateTimeOffset centerTimestamp =
                item.TimestampUtc + CandleInterval / 2d;

            if (centerTimestamp < firstTimestamp || centerTimestamp > lastTimestamp)
            {
                continue;
            }

            double x = MapTimestamp(
                centerTimestamp,
                firstTimestamp,
                lastTimestamp,
                plotLeft,
                plotWidth);
            double y = MapPrice(
                item.MarkerPrice ?? item.Close,
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

    private static (decimal Minimum, decimal Maximum, decimal Step) CreatePriceScale(
        decimal rawMinimum,
        decimal rawMaximum)
    {
        decimal rawRange = Math.Max(0.0001m, rawMaximum - rawMinimum);
        decimal step = NiceStep(rawRange / 4m);
        decimal minimum = Math.Floor(rawMinimum / step) * step;
        decimal maximum = Math.Ceiling(rawMaximum / step) * step;

        if (maximum <= minimum)
        {
            maximum = minimum + step;
        }

        return (minimum, maximum, step);
    }

    private static decimal NiceStep(decimal rawStep)
    {
        double exponent = Math.Floor(Math.Log10((double)rawStep));
        decimal magnitude = (decimal)Math.Pow(10d, exponent);
        decimal fraction = rawStep / magnitude;
        decimal niceFraction = fraction switch
        {
            <= 1m => 1m,
            <= 2m => 2m,
            <= 2.5m => 2.5m,
            <= 5m => 5m,
            _ => 10m,
        };
        return niceFraction * magnitude;
    }

    private static string FormatPrice(decimal price) => price switch
    {
        < 1m => price.ToString("0.0000", CultureInfo.InvariantCulture),
        _ => price.ToString("0.00", CultureInfo.InvariantCulture),
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

    private static void OnIsManualScaleChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var chart = (PriceChart)dependencyObject;
        chart.EndScaleDrag();

        if ((bool)eventArgs.NewValue && chart._hasRenderedScale)
        {
            chart._manualMinimum = chart._lastRenderedMinimum;
            chart._manualMaximum = chart._lastRenderedMaximum;
        }
        else
        {
            chart._manualMinimum = null;
            chart._manualMaximum = null;
        }

        chart.InvalidateVisual();
    }

    private static void OnScaleResetVersionChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var chart = (PriceChart)dependencyObject;
        chart.ResetManualScale();
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs) => InvalidateVisual();

    private void ResetManualScale()
    {
        EndScaleDrag();
        _manualMinimum = null;
        _manualMaximum = null;
        _hasRenderedScale = false;
    }
}
