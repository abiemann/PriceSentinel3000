namespace PriceSentinel3000.Application.Automation;

/// <summary>A bounded rendering of the chart currently shown by the application.</summary>
public sealed record AutomationChartCapture(
    string MimeType,
    int Width,
    int Height,
    string Data,
    DateTimeOffset CapturedAtUtc,
    int CandleIntervalSeconds,
    DateTimeOffset VisibleFromUtc,
    DateTimeOffset VisibleToUtc,
    int PointCount,
    int VisiblePointCount,
    bool RsiShown,
    int RsiPeriod,
    decimal? RsiLatestValue);
