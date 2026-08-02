using System.Collections;
using System.Collections.Specialized;
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

    private INotifyCollectionChanged? _observedCollection;

    public IEnumerable? Points
    {
        get => (IEnumerable?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(25, 38, 54)), 1);

        for (int line = 1; line < 5; line++)
        {
            double y = RenderSize.Height * line / 5d;
            drawingContext.DrawLine(gridPen, new(0, y), new(RenderSize.Width, y));
        }

        PricePointViewModel[] points =
        [
            .. (Points?.Cast<PricePointViewModel>() ?? []),
        ];

        if (points.Length < 2 || RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            return;
        }

        decimal minimum = points.Min(point => point.Price);
        decimal maximum = points.Max(point => point.Price);
        decimal range = maximum - minimum;

        if (range <= 0m)
        {
            range = Math.Max(0.01m, maximum * 0.001m);
            minimum -= range / 2m;
            maximum += range / 2m;
        }

        decimal padding = range * 0.12m;
        minimum -= padding;
        maximum += padding;
        range = maximum - minimum;
        const double horizontalPadding = 12d;
        const double verticalPadding = 12d;
        double chartWidth = Math.Max(1d, RenderSize.Width - horizontalPadding * 2d);
        double chartHeight = Math.Max(1d, RenderSize.Height - verticalPadding * 2d);

        var geometry = new StreamGeometry();

        using (StreamGeometryContext context = geometry.Open())
        {
            for (int index = 0; index < points.Length; index++)
            {
                double x = horizontalPadding +
                    chartWidth * index / Math.Max(1d, points.Length - 1d);
                double normalized = (double)((points[index].Price - minimum) / range);
                double y = verticalPadding + chartHeight * (1d - normalized);
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

        geometry.Freeze();
        var accentBrush = new SolidColorBrush(Color.FromRgb(94, 230, 177));
        var linePen = new Pen(accentBrush, 2);
        drawingContext.DrawGeometry(null, linePen, geometry);

        PricePointViewModel last = points[^1];
        double lastNormalized = (double)((last.Price - minimum) / range);
        var lastPoint = new Point(
            horizontalPadding + chartWidth,
            verticalPadding + chartHeight * (1d - lastNormalized));
        drawingContext.DrawEllipse(accentBrush, null, lastPoint, 4, 4);
    }

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
