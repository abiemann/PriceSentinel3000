using System.Globalization;
using System.Windows.Data;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Converters;

public sealed class DatasetSessionCoverageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool details = parameter is "Details";
        if (value is not HistoricalDatasetInfo dataset ||
            dataset.SessionBounds is not ("regular" or "extended" or "24_5") ||
            dataset.SourceIntervalSeconds is not (15 or 30 or 60 or 120) ||
            !CollectionSchedule.IsCollectionDate(dataset.TradingDate, dataset.SessionBounds))
            return details
                ? "Full-session coverage is unavailable for this dataset's session, date, or source interval."
                : "--";

        IReadOnlyList<CollectionSessionWindow> sessions = CollectionSchedule.GetSessionWindows(dataset.TradingDate, dataset.SessionBounds);
        HistoricalCoverage coverage = dataset.Coverage;
        long sessionTicks = sessions.Sum(session => (session.ThroughUtc - session.FromUtc).Ticks);
        long savedTicks = sessions.Sum(session => OverlapTicks(coverage.RequestedFromUtc, coverage.RequestedThroughUtc, session));
        foreach (HistoricalGap gap in coverage.Gaps)
            savedTicks -= sessions.Sum(session => OverlapTicks(gap.FromUtc, gap.ThroughUtc, session));
        savedTicks = Math.Clamp(savedTicks, 0, sessionTicks);

        // Coverage describes the requested portion, which can end before today's close.
        // Its validated gaps let us count saved session candles without loading the file.
        long intervalTicks = dataset.SourceIntervalSeconds * TimeSpan.TicksPerSecond;
        long savedCandles = savedTicks / intervalTicks;
        long expectedCandles = sessionTicks / intervalTicks;
        if (details)
            return $"{savedCandles.ToString("N0", culture)} of {expectedCandles.ToString("N0", culture)} " +
                $"{dataset.SourceIntervalSeconds}-second candles saved for the full {dataset.SessionBounds} session. " +
                "An open session stays below 100% until the full day's candles are saved.";

        decimal percent = 100m * savedTicks / sessionTicks;
        return percent.ToString("0.##", culture) + "%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static long OverlapTicks(DateTimeOffset from, DateTimeOffset through, CollectionSessionWindow session)
    {
        DateTimeOffset start = from > session.FromUtc ? from : session.FromUtc;
        DateTimeOffset end = through < session.ThroughUtc ? through : session.ThroughUtc;
        return end > start ? (end - start).Ticks : 0;
    }
}
