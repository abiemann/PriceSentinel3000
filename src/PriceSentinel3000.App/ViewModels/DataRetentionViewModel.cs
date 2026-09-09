using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

public sealed class DownloadMemberViewModel(DownloadListMember member) : INotifyPropertyChanged
{
    private bool _included = member.IsIncluded;
    public string Symbol => member.Symbol;
    public string CompanyName => member.CompanyName ?? "Identity checked when downloading";
    public bool IsIncluded { get => _included; set { _included = value; PropertyChanged?.Invoke(this, new(nameof(IsIncluded))); } }
    public DownloadListMember ToMember() => member with { IsIncluded = IsIncluded };
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The editable UI is separate from the saved collection plan used by background jobs.</summary>
public sealed partial class DataRetentionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IPersonalWatchlistSource _watchlists;
    private readonly IEquityCatalogSource _equities;
    private readonly Func<string, IMarketDataLibrary> _libraryFactory;
    private readonly Func<CancellationToken, Task> _connect;
    private readonly Func<CancellationToken, Task> _reconnect;
    private readonly Func<bool> _isConnected;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _downloadCancellation;
    private Task? _pollTask;
    private int _refreshQueued;
    private bool _busy;
    private bool _disposed;
    private string _status = "Choose a list, then save the equities to collect. Existing history is kept when lists change.";
    private DownloadList? _selectedList;
    private string? _sourceListId;
    private string? _sourceListName;
    private string _listName = "My equities";
    private bool _listEnabled = true;
    private string _tickerInput = "";
    private string _libraryRoot;
    private string _libraryDiagnostics = "";
    private bool _automatic;
    private string _dailyTime;
    private string _timeZone;
    private string _replayPins = "";
    private bool _replayOffline;
    private bool _replayLatest;

    public DataRetentionViewModel(MarketDataCollector collector, IMarketHistoryProvider provider,
        IPersonalWatchlistSource watchlists, IEquityCatalogSource equities,
        Func<string, IMarketDataLibrary> libraryFactory, Func<CancellationToken, Task> connect,
        Func<bool> isConnected, Dispatcher? dispatcher = null,
        Func<CancellationToken, Task>? reconnect = null, TimeProvider? clock = null)
    {
        Collector = collector;
        Provider = provider;
        _watchlists = watchlists;
        _equities = equities;
        _libraryFactory = libraryFactory;
        _connect = connect;
        _reconnect = reconnect ?? connect;
        _isConnected = isConnected;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _clock = clock ?? TimeProvider.System;
        VisibleJobs = new ListCollectionView(Jobs)
        {
            Filter = item => item is DownloadJobViewModel row &&
                (row.Status != CollectionJobStatus.Unavailable || !row.IsAvailabilityProbe),
            IsLiveFiltering = true,
            LiveFilteringProperties = { nameof(DownloadJobViewModel.Status), nameof(DownloadJobViewModel.IsAvailabilityProbe) },
            SortDescriptions =
            {
                new(nameof(DownloadJobViewModel.SessionDate), ListSortDirection.Descending),
                new(nameof(DownloadJobViewModel.Symbol), ListSortDirection.Ascending),
            },
        };
        CollectionSettings settings = collector.State.Settings;
        _libraryRoot = settings.LibraryRootPath;
        _automatic = settings.AutomaticDownloadsEnabled;
        _dailyTime = settings.DailyDownloadTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        _timeZone = settings.TimeZoneId;
        foreach (DownloadList list in settings.Lists) Lists.Add(list);
        if (Lists.Count > 0) SelectedList = Lists[0];
        NewListCommand = new RelayCommand(NewList, () => !IsBusy);
        SaveListCommand = Command(SaveListAsync);
        DeleteListCommand = Command(DeleteListAsync);
        AddTickersCommand = Command(AddTickersAsync);
        LoadWatchlistsCommand = Command(LoadWatchlistsAsync);
        ReconnectCommand = Command(async () =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _downloadCancellation = cancellation;
            _connecting = true;
            RefreshState();
            try { await _reconnect(cancellation.Token); Status = "Robinhood connected. Queued downloads can continue."; }
            finally { _connecting = false; _downloadCancellation = null; RefreshState(); }
        });
        ImportWatchlistCommand = Command(() => PreviewWatchlistAsync(false));
        RefreshWatchlistCommand = Command(() => PreviewWatchlistAsync(true));
        SaveScheduleCommand = Command(SaveScheduleAsync);
        DownloadNowCommand = Command(DownloadNowAsync, () => !_downloadsPaused);
        ScanLibraryCommand = Command(ScanLibraryAsync);
        PinDatasetCommand = new RelayCommand(() => { if (SelectedDataset is { } d) ReplayPinnedHashes = d.DatasetHash; });
        ClearPinsCommand = new RelayCommand(() => ReplayPinnedHashes = "");
        OpenFolderCommand = Command(() => { string root = Collector.State.Settings.LibraryRootPath; Directory.CreateDirectory(root); Process.Start(new ProcessStartInfo(root) { UseShellExecute = true }); return Task.CompletedTask; });
        CancelDownloadsCommand = new RelayCommand(() => _downloadCancellation?.Cancel(),
            () => !_disposed && _downloadCancellation is { IsCancellationRequested: false });
        PauseDownloadsCommand = new AsyncRelayCommand(ToggleDownloadPauseAsync, CanToggleDownloadPause,
            () => !_downloadsPaused && _downloadCancellation is { IsCancellationRequested: false });
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, OnTimerTick, _dispatcher);
        _timer.Stop();
        _progressTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => RefreshDownloadPresentation(), _dispatcher);
        _progressTimer.Stop();
        Collector.StateChanged += OnCollectorChanged;
        RefreshState();
    }

    public MarketDataCollector Collector { get; }
    public IMarketHistoryProvider Provider { get; }
    public IMarketDataLibrary CreateLibrary() => _libraryFactory(Collector.State.Settings.LibraryRootPath);
    public Task PrepareConnectionAsync(CancellationToken token) => _connect(token);
    public ObservableCollection<DownloadList> Lists { get; } = [];
    public ObservableCollection<DownloadMemberViewModel> Members { get; } = [];
    public ObservableCollection<PersonalWatchlist> RobinhoodLists { get; } = [];
    public ObservableCollection<DownloadJobViewModel> Jobs { get; } = [];
    public ListCollectionView VisibleJobs { get; }
    public ObservableCollection<HistoricalDatasetInfo> Datasets { get; } = [];
    public ObservableCollection<LibraryDaySummary> LibraryDays { get; } = [];
    public LibraryDaySummary? SelectedLibraryDay { get; set; }
    public IReadOnlyList<TimeZoneInfo> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones();
    public PersonalWatchlist? SelectedRobinhoodList { get; set; }
    public HistoricalDatasetInfo? SelectedDataset { get; set; }
    public DownloadList? SelectedList { get => _selectedList; set { _selectedList = value; if (value is not null) LoadEditor(value); Changed(); Changed(nameof(ListSummary)); } }
    public string ListSummary => SelectedList is { } list
        ? $"{list.Members.Count(m => m.IsIncluded)} of {list.Members.Count} saved equities included · {(list.SourceListId is null ? "Local list" : "Robinhood snapshot")}" : "New local list";
    public string ListName { get => _listName; set { _listName = value; Changed(); } }
    public bool ListEnabled { get => _listEnabled; set { _listEnabled = value; Changed(); } }
    public string TickerInput { get => _tickerInput; set { _tickerInput = value; Changed(); } }
    public string LibraryRootPath { get => _libraryRoot; set { _libraryRoot = value; Changed(); ScheduleDraftChanged(); } }
    public bool AutomaticDownloadsEnabled { get => _automatic; set { _automatic = value; Changed(); ScheduleDraftChanged(); } }
    public bool SavedAutomaticDownloadsEnabled => Collector.State.Settings.AutomaticDownloadsEnabled;
    public string DailyTime { get => _dailyTime; set { _dailyTime = value; Changed(); ScheduleDraftChanged(); } }
    public string TimeZoneId { get => _timeZone; set { _timeZone = value; Changed(); ScheduleDraftChanged(); } }
    public bool ReplayOfflineOnly { get => _replayOffline; set { _replayOffline = value; Changed(); } }
    public bool ReplayUseLatestRevision { get => _replayLatest; set { _replayLatest = value; Changed(); } }
    public string ReplayPinnedHashes { get => _replayPins; set { _replayPins = value; Changed(); } }
    public bool IsBusy => _busy || Collector.IsBusy || _downloadCancellation is not null || _pollTask is { IsCompleted: false };
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string LibraryDiagnostics
    {
        get => _libraryDiagnostics;
        private set { _libraryDiagnostics = value; Changed(); Changed(nameof(HasLibraryDiagnostics)); }
    }
    public bool HasLibraryDiagnostics => LibraryDiagnostics.Length > 0;
    public string SavedSchedule => SavedAutomaticDownloadsEnabled
        ? $"Daily at {Collector.State.Settings.DailyDownloadTime:HH:mm} · {Collector.State.Settings.TimeZoneId}: today's completed candles and earlier missing history. Keep this app open and connected."
        : "Automatic downloads are off. Manual downloads remain available.";
    public string ScheduleHelp => SavedAutomaticDownloadsEnabled
        ? "Runs at your saved daily time while PriceSentinel is open and connected. Each run saves today's completed candles and checks earlier missing history for every included equity. Saved files are reused; older unresolved gaps remain visible."
        : "Automatic downloads are off. Download now saves today's completed candles and checks earlier missing history. To run daily while PriceSentinel is open and connected, enable automatic downloads and save the schedule.";
    public string JobSummary => $"Retained queue: {DownloadProcessed}/{DownloadTotal} checked · {Jobs.Count(j => j.Status == CollectionJobStatus.Complete && j.RequestedThroughUtc is null)} complete · {Jobs.Count(j => j.Status == CollectionJobStatus.Complete && j.RequestedThroughUtc is not null)} saved so far · {Jobs.Count(j => j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading)} remaining · {Jobs.Count(j => j.NeedsAttention)} need attention";
    public string ContinuityWarnings
    {
        get
        {
            CollectionState state = Collector.State;
            CollectionContinuityGap[] gaps = state.ContinuityGaps.Where(g =>
                string.Equals(g.LibraryRootPath, state.Settings.LibraryRootPath, StringComparison.OrdinalIgnoreCase) &&
                g.SessionBounds is "regular" or "extended" or "24_5").ToArray();
            if (gaps.Length == 0) return "Only genuine, completed 15-second candles are downloaded, across all available trading hours. Saved candles are reused when filling missing history.";
            return "Older unresolved gaps remain recorded below. Download now checks how far back 15-second history is still available; expired data cannot be recreated:\n" +
                string.Join("\n", gaps.Select(g => FormattableString.Invariant($"{g.Symbol}: {g.FromSessionDate:yyyy-MM-dd} through {g.ThroughSessionDate:yyyy-MM-dd}")));
        }
    }
    public RelayCommand NewListCommand { get; }
    public AsyncRelayCommand SaveListCommand { get; }
    public AsyncRelayCommand DeleteListCommand { get; }
    public AsyncRelayCommand AddTickersCommand { get; }
    public AsyncRelayCommand LoadWatchlistsCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
    public AsyncRelayCommand ImportWatchlistCommand { get; }
    public AsyncRelayCommand RefreshWatchlistCommand { get; }
    public AsyncRelayCommand SaveScheduleCommand { get; }
    public AsyncRelayCommand DownloadNowCommand { get; }
    public AsyncRelayCommand ScanLibraryCommand { get; }
    public AsyncRelayCommand OpenFolderCommand { get; }
    public RelayCommand PinDatasetCommand { get; }
    public RelayCommand ClearPinsCommand { get; }
    public RelayCommand CancelDownloadsCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start() { _started = true; _progressTimer.Start(); OnTimerTick(null, EventArgs.Empty); }

    public void NewList()
    {
        SelectedList = null;
        _sourceListId = _sourceListName = null;
        ListName = "My equities";
        ListEnabled = true;
        Members.Clear();
        Status = "Enter a name and paste symbols separated by spaces or commas.";
    }

    private void LoadEditor(DownloadList list)
    {
        ListName = list.Name;
        ListEnabled = list.IsEnabled;
        _sourceListId = list.SourceListId;
        _sourceListName = list.SourceListName;
        Members.Clear();
        foreach (DownloadListMember member in list.Members) Members.Add(new(member));
    }

    public async Task SaveListAsync()
    {
        var list = new DownloadList(SelectedList?.Id ?? Guid.NewGuid(), ListName, ListEnabled,
            Members.Select(m => m.ToMember()).ToArray(), _sourceListId, _sourceListName);
        DownloadList[] next = Lists.Where(l => l.Id != list.Id).Append(list).ToArray();
        await Collector.SaveSettingsAsync(Collector.State.Settings with { Lists = next }, _lifetime.Token);
        ReloadLists(list.Id);
        Status = $"Saved {list.Name}: {list.Members.Count(m => m.IsIncluded)} included equities. History is preserved.";
    }

    private async Task DeleteListAsync()
    {
        if (SelectedList is not { } list) throw new InvalidOperationException("Select a saved list to remove.");
        await Collector.SaveSettingsAsync(Collector.State.Settings with { Lists = Lists.Where(l => l.Id != list.Id).ToArray() }, _lifetime.Token);
        ReloadLists(null);
        NewList();
        Status = "List removed. Previously downloaded history is still in the library.";
    }

    public async Task AddTickersAsync()
    {
        string[] symbols = TickerInput.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(CollectionSettings.NormalizeSymbol).Distinct().Except(Members.Select(m => m.Symbol)).ToArray();
        if (symbols.Length == 0) { Status = "No new symbols to add."; return; }
        if (symbols.Length + Members.Count > 1000) throw new ArgumentException("A list can contain at most 1,000 equities.");
        if (_isConnected())
        {
            IReadOnlyList<EquityResolution> resolved = await _equities.ResolveEquitiesAsync(symbols, _lifetime.Token);
            foreach (EquityResolution equity in resolved.Where(r => r.IsSupported))
                Members.Add(new(new(equity.Symbol, equity.CompanyName, true, equity.ProviderInstrumentId)));
            Status = string.Join(" ", resolved.Where(r => !r.IsSupported).Select(r => $"{r.RequestedSymbol}: {r.Message}"));
            if (Status.Length == 0) Status = $"Added {symbols.Length} equities. Save this list when ready.";
        }
        else
        {
            foreach (string symbol in symbols) Members.Add(new(new(symbol)));
            Status = "Added symbols offline. Robinhood will validate their identities when downloading; unsupported symbols will fail visibly.";
        }
        TickerInput = "";
    }

    public async Task LoadWatchlistsAsync()
    {
        await PrepareConnectionAsync(_lifetime.Token);
        IReadOnlyList<PersonalWatchlist> lists = await _watchlists.GetWatchlistsAsync(_lifetime.Token);
        RobinhoodLists.Clear();
        foreach (PersonalWatchlist list in lists) RobinhoodLists.Add(list);
        SelectedRobinhoodList = RobinhoodLists.FirstOrDefault();
        Changed(nameof(SelectedRobinhoodList));
        Status = $"Found {lists.Count} Robinhood lists. Choose one, then preview its equities.";
    }

    public async Task PreviewWatchlistAsync(bool refresh)
    {
        await PrepareConnectionAsync(_lifetime.Token);
        PersonalWatchlist? source = refresh && _sourceListId is not null
            ? (await _watchlists.GetWatchlistsAsync(_lifetime.Token)).SingleOrDefault(l => l.Id == _sourceListId)
            : SelectedRobinhoodList;
        if (source is null) throw new InvalidOperationException("Choose a Robinhood list, or load the available lists first.");
        PersonalWatchlistMembers result = await _watchlists.GetWatchlistMembersAsync(source, _lifetime.Token);
        string[] symbols = result.Members.Where(m => m.ObjectType == "instrument" && m.Symbol is not null)
            .Select(m => m.Symbol!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IReadOnlyList<EquityResolution> resolved = symbols.Length == 0 ? [] : await _equities.ResolveEquitiesAsync(symbols, _lifetime.Token);
        var previous = Members.ToDictionary(m => m.Symbol, m => m.IsIncluded);
        if (!refresh) { NewList(); ListName = source.Name; }
        Members.Clear();
        foreach (EquityResolution equity in resolved.Where(r => r.IsSupported))
            Members.Add(new(new(equity.Symbol, equity.CompanyName,
                !refresh || !previous.TryGetValue(equity.Symbol, out bool included) || included, equity.ProviderInstrumentId)));
        _sourceListId = source.Id;
        _sourceListName = source.Name;
        int added = Members.Count(m => !previous.ContainsKey(m.Symbol));
        int removed = previous.Keys.Except(Members.Select(m => m.Symbol)).Count();
        int excluded = result.Members.Count - Members.Count;
        Status = refresh
            ? $"Refresh preview: {added} added, {removed} removed, {excluded} unsupported / duplicate items omitted. Your exclusions are preserved. Save to apply."
            : $"Import preview: {Members.Count} equities, {excluded} unsupported / duplicate items omitted. Choose individual checkmarks, then save your local copy.";
    }

    public async Task SaveScheduleAsync()
    {
        if (!TimeOnly.TryParseExact(DailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
            throw new ArgumentException("Enter the daily time as HH:mm, for example 13:15.");
        await Collector.SaveSettingsAsync(Collector.State.Settings with
        {
            LibraryRootPath = LibraryRootPath, AutomaticDownloadsEnabled = AutomaticDownloadsEnabled,
            DailyDownloadTime = time, TimeZoneId = TimeZoneId, SessionBounds = CollectionSettings.AllAvailableSessionBounds,
        }, _lifetime.Token);
        Status = "Schedule and library folder saved. " + (SavedAutomaticDownloadsEnabled
            ? "Automatic downloads collect today's completed candles and earlier missing history at the saved daily time."
            : "Automatic downloads are off. Download now remains available.");
        RefreshState();
    }

    public async Task DownloadNowAsync()
    {
        await Collector.QueueAvailableAsync(cancellationToken: _lifetime.Token);
        await RunDownloadsAsync();
    }

    private async Task RunDownloadsAsync()
    {
        _downloadsPaused = false;
        await PollAsync(connect: true);
        Status = DownloadProcessed == DownloadTotal
            ? "Queue checked. See the download status for complete files and any dates that need attention."
            : "Queued work is kept. The download status shows any connection or retry wait.";
    }

    public async Task ScanLibraryAsync()
    {
        LibraryDiagnostics = "";
        IMarketDataLibrary library = CreateLibrary();
        var (scan, days) = await Task.Run(() =>
        {
            MarketDataLibraryScan result = library.ConsolidateDailyFiles();
            return (result, LibraryDaySummary.Create(result.Datasets));
        }, _lifetime.Token);
        LibraryDaySummary? selected = SelectedLibraryDay;
        LibraryDays.Clear();
        foreach (LibraryDaySummary day in days) LibraryDays.Add(day);
        SelectedLibraryDay = LibraryDays.FirstOrDefault(day => selected is not null && day.Symbol == selected.Symbol && day.TradingDate == selected.TradingDate);
        Changed(nameof(SelectedLibraryDay));
        Datasets.Clear();
        foreach (HistoricalDatasetInfo dataset in scan.Datasets.OrderByDescending(d => d.TradingDate).ThenBy(d => d.Symbol)) Datasets.Add(dataset);
        LibraryDiagnostics = string.Join("\n\n", scan.Diagnostics.Select(d => $"{d.RelativePath} [{d.Code}]\n{d.Message}"));
        Status = $"Found {days.Count} daily entries from {scan.Datasets.Count} saved files. {scan.Diagnostics.Count} scan notices." +
            (HasLibraryDiagnostics ? " Open Library details." : "");
    }

    public string ExportLists() => DownloadListTransfer.Export(Collector.State.Settings.Lists);
    public async Task ImportListsAsync(string json)
    {
        IReadOnlyList<DownloadList> imported = DownloadListTransfer.Import(json);
        await Collector.SaveSettingsAsync(Collector.State.Settings with { Lists = Lists.Concat(imported).ToArray() }, _lifetime.Token);
        ReloadLists(imported.FirstOrDefault()?.Id);
        Status = $"Imported {imported.Count} portable lists. Review included equities before downloading.";
    }

    private void ReloadLists(Guid? selectedId)
    {
        Lists.Clear();
        foreach (DownloadList list in Collector.State.Settings.Lists) Lists.Add(list);
        SelectedList = Lists.FirstOrDefault(l => l.Id == selectedId);
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        await CheckDownloadsAsync();
    }

    private async Task PollAsync(bool connect = false)
    {
        if (_downloadCancellation is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _downloadCancellation = cancellation;
        _collectionError = null;
        _timer.Stop();
        CollectionBatchResult result = CollectionBatchResult.Idle;
        try
        {
            if (connect)
            {
                _connecting = true;
                RefreshState();
                try { await PrepareConnectionAsync(cancellation.Token); }
                finally { _connecting = false; RefreshState(); }
            }
            while ((result = await Collector.TickAsync(_isConnected(), cancellation.Token)) == CollectionBatchResult.Ready)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_downloadsPaused || !_isConnected()) break;
                RefreshState();
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _collectionError = exception.Message;
            throw;
        }
        finally
        {
            _downloadCancellation = null;
            ScheduleNextDownloadCheck(result == CollectionBatchResult.WaitingForRetry);
            RefreshState();
        }
    }

    private void OnCollectorChanged(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed) RefreshState();
        });
    }

    private void RefreshState()
    {
        RefreshJobRows();
        Changed(nameof(SavedAutomaticDownloadsEnabled)); Changed(nameof(SavedSchedule)); Changed(nameof(ScheduleHelp)); Changed(nameof(JobSummary)); Changed(nameof(ContinuityWarnings)); Changed(nameof(IsBusy));
        ScheduleDraftChanged();
        RefreshDownloadPresentation();
        Changed(nameof(CanEditPlan));
        CancelDownloadsCommand?.RaiseCanExecuteChanged();
        PauseDownloadsCommand?.RaiseCanExecuteChanged();
        NewListCommand?.RaiseCanExecuteChanged();
        foreach (AsyncRelayCommand command in Commands()) command?.RaiseCanExecuteChanged();
    }

    private AsyncRelayCommand[] Commands() => [SaveListCommand, DeleteListCommand, AddTickersCommand,
        LoadWatchlistsCommand, ReconnectCommand, ImportWatchlistCommand, RefreshWatchlistCommand, SaveScheduleCommand,
        DownloadNowCommand, ScanLibraryCommand, OpenFolderCommand, PauseDownloadsCommand];
    private AsyncRelayCommand Command(Func<Task> action, Func<bool>? canExecute = null) =>
        new(() => ExecuteAsync(action), () => !IsBusy && !_disposed && (canExecute?.Invoke() ?? true));
    public async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy || _disposed) return;
        _busy = true;
        RefreshState();
        try { await action(); }
        catch (OperationCanceledException) { Status = _downloadsPaused
            ? "Current request cancelled; downloads are paused. Saved files and queued work are kept. Choose Resume downloads to continue."
            : "Current request cancelled. Pending work is retained and retries at the next queue check."; }
        catch (Exception exception) { Status = exception.Message; }
        finally { _busy = false; RefreshState(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _progressTimer.Stop();
        Collector.StateChanged -= OnCollectorChanged;
        await _lifetime.CancelAsync();
        Task[] active = Commands().Select(c => c.ExecutionTask).Append(PauseDownloadsCommand.ExecutionTask)
            .Append(_pollTask).OfType<Task>().ToArray();
        try { await Task.WhenAll(active); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
