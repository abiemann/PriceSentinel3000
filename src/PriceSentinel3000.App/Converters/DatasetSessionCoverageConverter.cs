using System.Globalization;
using System.Windows.Data;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Converters;

public sealed class DatasetSessionCoverageConverter : IValueConverter
{
    private readonly TimeProvider _clock;

    public DatasetSessionCoverageConverter() : this(TimeProvider.System) { }
    public DatasetSessionCoverageConverter(TimeProvider clock) => _clock = clock;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool details = parameter is "Details";
        LibraryDaySummary? day = value as LibraryDaySummary;
        if (value is HistoricalDatasetInfo dataset)
            day = LibraryDaySummary.Create([dataset], _clock.GetUtcNow())[0];
        if (day is null) return details ? "Coverage is unavailable for this item." : "--";
        return details ? day.CoverageDetails : day.CoveragePercent is { } percent
            ? percent.ToString("0.##", culture) + "%" : "--";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
