using System.Text.Json;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private Guid? _automationChartSessionId;

    internal Func<int, int, AutomationChartCapture>? AutomationChartCaptureRequested { get; set; }

    private void RecordAutomationChartSession() => _automationChartSessionId =
        _activeSession?.Mode is TradingMode.Replay or TradingMode.PaperTrader
            ? _activeSession.Id
            : null;

    internal AutomationResponse CaptureAutomationChart(JsonElement arguments)
    {
        _automationDispatcher.VerifyAccess();
        ChartCaptureArguments limits = ReadArguments<ChartCaptureArguments>(new("capture_chart", arguments));
        if (limits.MaxWidth is < 1 or > 1280 || limits.MaxHeight is < 1 or > 900)
            return AutomationResponse.Fail("invalid_arguments", "maxWidth must be 1–1280 and maxHeight must be 1–900 pixels.");
        if (HasAutomationLiveContext)
            return AutomationResponse.Fail("live_forbidden", "Chart capture is available only for Replay and Paper Trader.");
        if (AutomationClosing || _shutdownTask is not null)
            return AutomationResponse.Fail("closing", "The application is closing.");
        if (_automationSession is null || _automationChartSessionId != _automationSession.Id ||
            ChartPoints.Count == 0 || AutomationChartCaptureRequested is null)
            return AutomationResponse.Fail("chart_unavailable", "There is no displayed Replay or Paper Trader chart to capture.");

        AutomationChartCapture capture;
        try { capture = AutomationChartCaptureRequested(limits.MaxWidth ?? 1280, limits.MaxHeight ?? 900); }
        catch (InvalidOperationException exception)
        {
            return AutomationResponse.Fail("chart_unavailable", exception.Message);
        }

        using JsonDocument settings = JsonDocument.Parse(_automationSession.SettingsJson);
        object? strategy = settings.RootElement.TryGetProperty("Strategy", out JsonElement provenance)
            ? new
            {
                id = provenance.GetProperty("Id").GetString(),
                name = provenance.GetProperty("Name").GetString(),
                sourceSha256 = provenance.GetProperty("SourceSha256").GetString(),
                runtimeVersion = provenance.GetProperty("RuntimeVersion").GetString(),
            }
            : null;
        return AutomationResponse.Ok(new
        {
            sessionId = _automationChartSessionId,
            symbol = _automationSession.Instrument.Symbol,
            strategy,
            capture.MimeType,
            capture.Width,
            capture.Height,
            capture.Data,
            capture.CapturedAtUtc,
            capture.CandleIntervalSeconds,
            capture.VisibleFromUtc,
            capture.VisibleToUtc,
            capture.PointCount,
            capture.VisiblePointCount,
            capture.RsiShown,
            capture.RsiPeriod,
            capture.RsiLatestValue,
            rsiMethod = "Simple-average RSI of displayed chart candle closes; independent of script indicators.",
            timestampNote = "Image axis labels use the app's local time zone; visibleFromUtc and visibleToUtc are UTC.",
        });
    }

    private sealed record ChartCaptureArguments(int? MaxWidth = null, int? MaxHeight = null);
}
