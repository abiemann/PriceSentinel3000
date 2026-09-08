using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.ViewModels;

public sealed record ReplayCalendarDay(string Status, string Description);

public sealed partial class MainViewModel
{
    private DataRetentionViewModel? _dataRetention;
    private CancellationTokenSource? _replayAvailabilityCancellation;
    private CancellationTokenSource? _replayCalendarCancellation;
    private Task? _replayAvailabilityTask;
    private Task? _replayCalendarTask;
    private readonly HashSet<Task> _replayAvailabilityWork = [];
    private long _replayAvailabilityGeneration;
    private string _replayRetentionScope = "";
    private string _replayAvailabilityStatus = "Unknown";
    private string _replayAvailabilityText = "Choose a date or press Enter to check the selected Replay window.";
    private bool _isCheckingReplayAvailability;
    private PreparedReplay? _preparedReplay;
    private IReadOnlyDictionary<DateOnly, ReplayCalendarDay> _replayCalendarDays = new Dictionary<DateOnly, ReplayCalendarDay>();
    private sealed record CheckedReplayDay(ReplayCalendarDay Display, DateTimeOffset CheckedAtUtc);
    private readonly Dictionary<DateOnly, CheckedReplayDay> _checkedReplayDays = [];

    private sealed record ReplayCheckContext(string Root, HistoricalDataQuery Query, bool OfflineOnly, DataRetentionViewModel Retention)
    {
        public string Key => JsonSerializer.Serialize(new { Root, Query, OfflineOnly });
    }
    private sealed record PreparedReplay(string Key, ReplayHistoryAvailability Availability, ReplayHistoryAvailabilityService Service,
        DateTimeOffset CheckedAtUtc);

    public string ReplayAvailabilityStatus => _replayAvailabilityStatus;
    public string ReplayAvailabilityText => _replayAvailabilityText;
    public bool IsCheckingReplayAvailability => _isCheckingReplayAvailability;
    public IReadOnlyDictionary<DateOnly, ReplayCalendarDay> ReplayCalendarDays => _replayCalendarDays;

    public Task CheckReplayAvailabilityAsync()
    {
        _replayAvailabilityTask = TrackReplayAvailabilityWorkAsync(CheckReplayAvailabilityCoreAsync());
        return _replayAvailabilityTask;
    }

    private async Task CheckReplayAvailabilityCoreAsync()
    {
        if (_disposed || !IsReplaySelected || !IsSessionConfigurationEditable) return;
        InvalidateReplayAvailability(clearCalendar: false);
        ReplayCheckContext context;
        try { context = CreateReplayCheckContext(ReplayDate); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            SetReplayAvailability("Unknown", exception.Message);
            return;
        }
        if (context.Query.ThroughUtc > _timeProvider.GetUtcNow())
        {
            SetReplayAvailability("Unknown", "This Replay window has not finished yet. Choose a completed date and time range.");
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _replayAvailabilityCancellation = cancellation;
        long generation = _replayAvailabilityGeneration;
        SetReplayAvailability("Checking", $"Checking {context.Query.Symbol} for the full selected time range…");
        SetCheckingReplayAvailability(true);
        try
        {
            var service = new ReplayHistoryAvailabilityService(context.Retention.CreateLibrary(), context.Retention.Provider);
            ReplayHistoryAvailability result = await Task.Run(() => service.CheckAsync(context.Query, context.OfflineOnly,
                cancellation.Token), cancellation.Token);
            if (_disposed || !IsReplaySelected || generation != _replayAvailabilityGeneration || cancellation.IsCancellationRequested) return;
            _preparedReplay = new(context.Key, result, service, _timeProvider.GetUtcNow());
            ReplayCalendarDay display = DescribeReplayAvailability(result);
            SetReplayAvailability(display.Status, display.Description);
            DateOnly day = DateOnly.FromDateTime(context.Query.FromUtc.ToLocalTime().DateTime);
            if (_checkedReplayDays.Count >= 128) _checkedReplayDays.Remove(_checkedReplayDays.Keys.First());
            if (result.IsLocal) _checkedReplayDays.Remove(day);
            else _checkedReplayDays[day] = new(display, _timeProvider.GetUtcNow());
            var days = new Dictionary<DateOnly, ReplayCalendarDay>(_replayCalendarDays) { [day] = display };
            _replayCalendarDays = days;
            OnPropertyChanged(nameof(ReplayCalendarDays));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_disposed && generation == _replayAvailabilityGeneration)
                SetReplayAvailability("Unknown", $"Availability could not be verified: {exception.Message} Connect to Robinhood or choose local-only Replay, then check again.");
        }
        finally
        {
            if (ReferenceEquals(_replayAvailabilityCancellation, cancellation)) _replayAvailabilityCancellation = null;
            if (generation == _replayAvailabilityGeneration) SetCheckingReplayAvailability(false);
        }
    }

    public Task LoadReplayCalendarMonthAsync(DateTime displayedMonth)
    {
        _replayCalendarTask = TrackReplayAvailabilityWorkAsync(LoadReplayCalendarMonthCoreAsync(displayedMonth));
        return _replayCalendarTask;
    }

    private async Task LoadReplayCalendarMonthCoreAsync(DateTime displayedMonth)
    {
        _replayCalendarCancellation?.Cancel();
        if (_disposed || DataRetention is null) return;
        using var cancellation = new CancellationTokenSource();
        _replayCalendarCancellation = cancellation;
        string scope = ReplayCalendarScope();
        try
        {
            DateOnly first = new(displayedMonth.Year, displayedMonth.Month, 1);
            DateOnly gridFirst = first.AddDays(-(int)first.DayOfWeek);
            ReplayCheckContext[] contexts = Enumerable.Range(0, 42)
                .Select(i => CreateReplayCheckContext(gridFirst.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).ToArray();
            IMarketDataLibrary library = DataRetention.CreateLibrary();
            MarketDataLibraryScan scan = await Task.Run(library.Scan, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || scope != ReplayCalendarScope()) return;
            var days = new Dictionary<DateOnly, ReplayCalendarDay>();
            for (int i = 0; i < contexts.Length; i++)
            {
                DateOnly day = gridFirst.AddDays(i);
                ReplayCalendarDay local = contexts[i].Query.ThroughUtc > _timeProvider.GetUtcNow()
                    ? new("Unknown", "The selected time range has not finished yet.") : DescribeLocalCalendarDay(contexts[i].Query, scan);
                // Disk availability is always rescanned. Broker evidence is short-lived
                // because its retention window moves even when the dashboard does not.
                days[day] = local.Status == "Disk15" ? local :
                    _checkedReplayDays.TryGetValue(day, out CheckedReplayDay? checkedDay) && checkedDay.Display.Status != "Disk15" &&
                    _timeProvider.GetUtcNow() - checkedDay.CheckedAtUtc < TimeSpan.FromMinutes(5)
                        ? checkedDay.Display : local;
            }
            _replayCalendarDays = days;
            OnPropertyChanged(nameof(ReplayCalendarDays));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_disposed && scope == ReplayCalendarScope())
                SetReplayAvailability("Unknown", $"Calendar availability could not be checked: {exception.Message}");
        }
        finally { if (ReferenceEquals(_replayCalendarCancellation, cancellation)) _replayCalendarCancellation = null; }
    }

    private ReplayCheckContext CreateReplayCheckContext(string date)
    {
        if (DataRetention is not { } retention) throw new InvalidOperationException("The local Replay library is unavailable.");
        string symbol = CollectionSettings.NormalizeSymbol(Symbol);
        if (!ReplaySchedule.TryParseLocalRange(date, ReplayTime, ReplayEndTime, out DateTimeOffset from, out DateTimeOffset through))
            throw new InvalidOperationException("Enter a date as yyyy-MM-dd and valid local start/end times as HH:mm.");
        string[] pins = retention.ReplayPinnedHashes.Split([',', '\r', '\n', ' ', '\t'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var settings = retention.Collector.State.Settings;
        var query = new HistoricalDataQuery(symbol, from.ToUniversalTime(), through.ToUniversalTime(), AdjustmentPolicy: "split",
            SessionBounds: "24_5", PinnedHashes: pins,
            RevisionPolicy: retention.ReplayUseLatestRevision ? HistoricalRevisionPolicy.LatestFetched : HistoricalRevisionPolicy.CompatibleCoverage,
            IncludeCompatibleSessions: true);
        return new(settings.LibraryRootPath, query, retention.ReplayOfflineOnly, retention);
    }

    private static ReplayCalendarDay DescribeReplayAvailability(ReplayHistoryAvailability result)
    {
        if (!result.HasData) return new("Unavailable", "No history was returned at the supported resolutions for this window.");
        string source = result.IsLocal ? "on disk" : result.Source == "local-and-provider"
            ? "from disk and Robinhood gap fills" : "verified with Robinhood";
        string text = $"{result.SourceIntervalSeconds}-second replay data {source}: {result.Coverage.ActualCandleCount}/{result.Coverage.ExpectedCandleCount} candles.";
        if (result.NativeSourceIntervals.Count > 1)
            text += $" Combined from genuine {string.Join(", ", result.NativeSourceIntervals)}-second sources; no finer candles are invented.";
        if (!result.Complete) return new("Partial", text + " Incomplete coverage; missing candles remain gaps. START can replay the available candles.");
        string status = result.SourceIntervalSeconds == 15 ? result.IsLocal ? "Disk15" : "Broker15"
            : result.SourceIntervalSeconds <= 60 ? "Coarse60" : "Coarse120";
        return new(status, text + " Ready for START.");
    }

    private static ReplayCalendarDay DescribeLocalCalendarDay(HistoricalDataQuery query, MarketDataLibraryScan scan)
    {
        if (scan.Diagnostics.Any(d => d.Code is "scan_failed" or "scan_limit"))
            return new("Unknown", "The local library could not be fully scanned. Select this date to check.");
        var pins = query.PinnedHashes;
        HistoricalDatasetInfo[] available = scan.Datasets.Where(d => d.Symbol == query.Symbol &&
            d.AdjustmentPolicy == query.AdjustmentPolicy && query.MatchesSessionBounds(d.SessionBounds) &&
            d.Coverage.RequestedFromUtc < query.ThroughUtc && d.Coverage.RequestedThroughUtc > query.FromUtc &&
            (pins is not { Count: > 0 } || pins.Contains(d.DatasetHash))).ToArray();
        bool partial = false;
        foreach (int interval in new[] { 15, 30, 60, 120 })
        {
            HistoricalDatasetInfo[] candidates = available.Where(d => d.SourceIntervalSeconds == interval).ToArray();
            if (candidates.Length == 0) continue;
            if (query.FromUtc.Ticks % TimeSpan.FromSeconds(interval).Ticks != 0 ||
                query.ThroughUtc.Ticks % TimeSpan.FromSeconds(interval).Ticks != 0) continue;
            if (pins is { Count: > 0 } && (candidates.Length != pins.Count || pins.Distinct().Count() != pins.Count)) continue;
            if (candidates.Select(d => (d.Provider, d.InstrumentId, d.AdjustmentBasis)).Distinct().Count() > 1)
                return new("Unknown", "Conflicting data sources require a revision selection before availability can be verified.");
            if ((query.RevisionPolicy == HistoricalRevisionPolicy.RejectConflicts || pins is { Count: > 0 }) &&
                candidates.GroupBy(d => d.TradingDate).Any(g => g.Count() > 1))
                return new("Unknown", "Multiple daily revisions match. Choose one dataset hash per day or an applicable revision policy.");
            if (query.RevisionPolicy == HistoricalRevisionPolicy.CompatibleCoverage &&
                candidates.GroupBy(d => d.TradingDate).Any(g => g.Count() > 1))
                return new("Unknown", "Multiple saved pieces may cover this day. Select the date to verify their candles and combined coverage.");
            if (query.IncludeCompatibleSessions && candidates.GroupBy(d => d.TradingDate)
                .Any(g => g.Select(d => d.SessionBounds).Distinct().Count() > 1))
                return new("Unknown", "Multiple market sessions may cover this day. Select the date to verify their candles and combined coverage.");
            candidates = candidates.GroupBy(d => d.TradingDate).Select(g => g.OrderByDescending(d => d.FetchedAtUtc)
                .ThenBy(d => d.DatasetHash, StringComparer.Ordinal).First()).OrderBy(d => d.TradingDate).ToArray();
            partial |= candidates.Any(d => d.Coverage.ActualCandleCount > 0);
            DateTimeOffset cursor = query.FromUtc;
            foreach (HistoricalDatasetInfo dataset in candidates)
            {
                HistoricalCoverage coverage = dataset.Coverage;
                DateTimeOffset from = coverage.RequestedFromUtc > query.FromUtc ? coverage.RequestedFromUtc : query.FromUtc;
                DateTimeOffset through = coverage.RequestedThroughUtc < query.ThroughUtc ? coverage.RequestedThroughUtc : query.ThroughUtc;
                if (from > cursor || coverage.CoveredFromUtc is null || coverage.CoveredThroughUtc is null ||
                    coverage.CoveredFromUtc > from || coverage.CoveredThroughUtc < through ||
                    coverage.Gaps.Any(g => g.FromUtc < through && g.ThroughUtc > from)) break;
                cursor = through;
            }
            if (cursor >= query.ThroughUtc)
                return new(interval == 15 ? "Disk15" : interval <= 60 ? "Coarse60" : "Coarse120",
                    $"Complete {interval}-second coverage on disk for the selected times." +
                    (interval == 15 ? "" : " Select the date to check whether finer broker data is available."));
        }
        return partial ? new("Partial", "Partial local coverage. Select the date to check broker availability.")
            : new("Unknown", "No verified local coverage. Broker availability has not been checked; select this date.");
    }

    private string ReplayCalendarScope() => JsonSerializer.Serialize(new
    {
        Symbol, ReplayTime, ReplayEndTime, Retention = RetentionAvailabilityScope(), Zone = TimeZoneInfo.Local.Id,
    });
    private async Task TrackReplayAvailabilityWorkAsync(Task task)
    {
        _replayAvailabilityWork.Add(task);
        try { await task; }
        finally { _replayAvailabilityWork.Remove(task); }
    }
    private string RetentionAvailabilityScope() => DataRetention is { } r ? JsonSerializer.Serialize(new
    {
        r.Collector.State.Settings.LibraryRootPath, r.Collector.State.Settings.SessionBounds,
        r.ReplayOfflineOnly, r.ReplayPinnedHashes, r.ReplayUseLatestRevision,
    }) : "";
    private void OnRetentionAvailabilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        string scope = RetentionAvailabilityScope();
        if (scope == _replayRetentionScope) return;
        _replayRetentionScope = scope;
        InvalidateReplayAvailability(clearCalendar: true);
    }
    private void InvalidateReplayAvailability(bool clearCalendar)
    {
        _replayAvailabilityGeneration++;
        _replayAvailabilityCancellation?.Cancel();
        _preparedReplay = null;
        SetCheckingReplayAvailability(false);
        SetReplayAvailability("Unknown", "Choose a date or press Enter to check the selected Replay window.");
        if (!clearCalendar) return;
        _replayCalendarCancellation?.Cancel();
        _checkedReplayDays.Clear();
        _replayCalendarDays = new Dictionary<DateOnly, ReplayCalendarDay>();
        OnPropertyChanged(nameof(ReplayCalendarDays));
    }
    private void SetReplayAvailability(string status, string text)
    {
        _replayAvailabilityStatus = status;
        _replayAvailabilityText = text;
        OnPropertyChanged(nameof(ReplayAvailabilityStatus));
        OnPropertyChanged(nameof(ReplayAvailabilityText));
    }
    private void SetCheckingReplayAvailability(bool value)
    {
        _isCheckingReplayAvailability = value;
        OnPropertyChanged(nameof(IsCheckingReplayAvailability));
        StartSessionCommand?.RaiseCanExecuteChanged();
    }
}
