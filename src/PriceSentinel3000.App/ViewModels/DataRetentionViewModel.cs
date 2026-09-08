using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
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
public sealed class DataRetentionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IPersonalWatchlistSource _watchlists;
    private readonly IEquityCatalogSource _equities;
    private readonly Func<string, IMarketDataLibrary> _libraryFactory;
    private readonly Func<CancellationToken, Task> _connect;
    private readonly Func<bool> _isConnected;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _downloadCancellation;
    private Task? _pollTask;
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
    private bool _automatic;
    private string _dailyTime;
    private string _timeZone;
    private string _bounds;
    private string _from;
    private string _through;
    private string _replayPins = "";
    private bool _replayOffline;
    private bool _replayLatest;

    public DataRetentionViewModel(MarketDataCollector collector, IMarketHistoryProvider provider,
        IPersonalWatchlistSource watchlists, IEquityCatalogSource equities,
        Func<string, IMarketDataLibrary> libraryFactory, Func<CancellationToken, Task> connect,
        Func<bool> isConnected, Dispatcher? dispatcher = null)
    {
        Collector = collector;
        Provider = provider;
        _watchlists = watchlists;
        _equities = equities;
        _libraryFactory = libraryFactory;
        _connect = connect;
        _isConnected = isConnected;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        CollectionSettings settings = collector.State.Settings;
        _libraryRoot = settings.LibraryRootPath;
        _automatic = settings.AutomaticDownloadsEnabled;
        _dailyTime = settings.DailyDownloadTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        _timeZone = settings.TimeZoneId;
        _bounds = settings.SessionBounds;
        _from = _through = CollectionSchedule.LatestFinalizedSession(DateTimeOffset.UtcNow, _bounds, 15).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (DownloadList list in settings.Lists) Lists.Add(list);
        if (Lists.Count > 0) SelectedList = Lists[0];
        NewListCommand = new RelayCommand(NewList, () => !IsBusy);
        SaveListCommand = Command(SaveListAsync);
        DeleteListCommand = Command(DeleteListAsync);
        AddTickersCommand = Command(AddTickersAsync);
        LoadWatchlistsCommand = Command(LoadWatchlistsAsync);
        ImportWatchlistCommand = Command(() => PreviewWatchlistAsync(false));
        RefreshWatchlistCommand = Command(() => PreviewWatchlistAsync(true));
        SaveScheduleCommand = Command(SaveScheduleAsync);
        DownloadNowCommand = Command(DownloadNowAsync);
        RetryMissingCommand = Command(async () => { await Collector.RetryMissingAsync(cancellationToken: _lifetime.Token); await RunDownloadsAsync(); });
        ScanLibraryCommand = Command(ScanLibraryAsync);
        PinDatasetCommand = new RelayCommand(() => { if (SelectedDataset is { } d) ReplayPinnedHashes = d.DatasetHash; });
        ClearPinsCommand = new RelayCommand(() => ReplayPinnedHashes = "");
        OpenFolderCommand = Command(() => { string root = Collector.State.Settings.LibraryRootPath; Directory.CreateDirectory(root); Process.Start(new ProcessStartInfo(root) { UseShellExecute = true }); return Task.CompletedTask; });
        CancelDownloadsCommand = new RelayCommand(() => _downloadCancellation?.Cancel());
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, OnTimerTick, _dispatcher);
        _timer.Stop();
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
    public ObservableCollection<CollectionJob> Jobs { get; } = [];
    public ObservableCollection<HistoricalDatasetInfo> Datasets { get; } = [];
    public IReadOnlyList<TimeZoneInfo> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones();
    public IReadOnlyList<string> SessionChoices { get; } = ["regular", "extended"];
    public PersonalWatchlist? SelectedRobinhoodList { get; set; }
    public HistoricalDatasetInfo? SelectedDataset { get; set; }
    public DownloadList? SelectedList { get => _selectedList; set { _selectedList = value; if (value is not null) LoadEditor(value); Changed(); Changed(nameof(ListSummary)); } }
    public string ListSummary => SelectedList is { } list
        ? $"{list.Members.Count(m => m.IsIncluded)} of {list.Members.Count} saved equities included · {(list.SourceListId is null ? "Local list" : "Robinhood snapshot")}" : "New local list";
    public string ListName { get => _listName; set { _listName = value; Changed(); } }
    public bool ListEnabled { get => _listEnabled; set { _listEnabled = value; Changed(); } }
    public string TickerInput { get => _tickerInput; set { _tickerInput = value; Changed(); } }
    public string LibraryRootPath { get => _libraryRoot; set { _libraryRoot = value; Changed(); } }
    public bool AutomaticDownloadsEnabled { get => _automatic; set { _automatic = value; Changed(); } }
    public bool SavedAutomaticDownloadsEnabled => Collector.State.Settings.AutomaticDownloadsEnabled;
    public string DailyTime { get => _dailyTime; set { _dailyTime = value; Changed(); } }
    public string TimeZoneId { get => _timeZone; set { _timeZone = value; Changed(); } }
    public string SessionBounds { get => _bounds; set { _bounds = value; Changed(); } }
    public string FromDate { get => _from; set { _from = value; Changed(); } }
    public string ThroughDate { get => _through; set { _through = value; Changed(); } }
    public bool ReplayOfflineOnly { get => _replayOffline; set { _replayOffline = value; Changed(); } }
    public bool ReplayUseLatestRevision { get => _replayLatest; set { _replayLatest = value; Changed(); } }
    public string ReplayPinnedHashes { get => _replayPins; set { _replayPins = value; Changed(); } }
    public bool IsBusy => _busy || Collector.IsBusy || _pollTask is { IsCompleted: false };
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string SavedSchedule => SavedAutomaticDownloadsEnabled
        ? $"Automatic downloads enabled: {Collector.State.Settings.DailyDownloadTime:HH:mm} · {Collector.State.Settings.TimeZoneId}. Keep this app open and connected."
        : "Automatic downloads are off. Manual downloads remain available.";
    public string JobSummary => $"{Jobs.Count(j => j.Status == CollectionJobStatus.Complete)} complete · {Jobs.Count(j => j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading)} queued / downloading · {Jobs.Count(j => j.Status is CollectionJobStatus.Partial or CollectionJobStatus.Unavailable or CollectionJobStatus.Failed)} need attention";
    public RelayCommand NewListCommand { get; }
    public AsyncRelayCommand SaveListCommand { get; }
    public AsyncRelayCommand DeleteListCommand { get; }
    public AsyncRelayCommand AddTickersCommand { get; }
    public AsyncRelayCommand LoadWatchlistsCommand { get; }
    public AsyncRelayCommand ImportWatchlistCommand { get; }
    public AsyncRelayCommand RefreshWatchlistCommand { get; }
    public AsyncRelayCommand SaveScheduleCommand { get; }
    public AsyncRelayCommand DownloadNowCommand { get; }
    public AsyncRelayCommand RetryMissingCommand { get; }
    public AsyncRelayCommand ScanLibraryCommand { get; }
    public AsyncRelayCommand OpenFolderCommand { get; }
    public RelayCommand PinDatasetCommand { get; }
    public RelayCommand ClearPinsCommand { get; }
    public RelayCommand CancelDownloadsCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start() { _timer.Start(); OnTimerTick(null, EventArgs.Empty); }

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
            DailyDownloadTime = time, TimeZoneId = TimeZoneId, SessionBounds = SessionBounds,
        }, _lifetime.Token);
        Status = "Schedule and library folder saved. Collection uses the latest finalized session at your chosen time.";
        RefreshState();
    }

    public async Task DownloadNowAsync()
    {
        if (!DateOnly.TryParseExact(FromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly from) ||
            !DateOnly.TryParseExact(ThroughDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly through))
            throw new ArgumentException("Enter dates as yyyy-MM-dd.");
        string[] symbols = Collector.State.Settings.Lists.Where(l => l.IsEnabled).SelectMany(l => l.Members)
            .Where(m => m.IsIncluded).Select(m => m.Symbol).Distinct().ToArray();
        await Collector.QueueManualAsync(symbols, from, through, cancellationToken: _lifetime.Token);
        await RunDownloadsAsync();
    }

    private async Task RunDownloadsAsync()
    {
        await PollAsync(connect: true);
        Status = "Collection updated. Remaining queued work continues while the app is open and connected.";
    }

    public async Task ScanLibraryAsync()
    {
        IMarketDataLibrary library = CreateLibrary();
        MarketDataLibraryScan scan = await Task.Run(library.Scan, _lifetime.Token);
        Datasets.Clear();
        foreach (HistoricalDatasetInfo dataset in scan.Datasets.OrderByDescending(d => d.TradingDate).ThenBy(d => d.Symbol)) Datasets.Add(dataset);
        Status = $"Found {scan.Datasets.Count} validated datasets. " + string.Join(" ", scan.Diagnostics.Take(8).Select(d => $"{d.RelativePath}: {d.Message}"));
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
        if (_disposed || IsBusy) return;
        _pollTask = PollAsync();
        try { await _pollTask; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status = $"Collection needs attention: {exception.Message}"; }
        finally { RefreshState(); }
    }

    private async Task PollAsync(bool connect = false)
    {
        if (_downloadCancellation is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _downloadCancellation = cancellation;
        try
        {
            if (connect) await PrepareConnectionAsync(cancellation.Token);
            await Collector.TickAsync(_isConnected(), cancellation.Token);
        }
        finally { _downloadCancellation = null; RefreshState(); }
    }

    private void OnCollectorChanged(object? sender, EventArgs e)
    {
        if (!_disposed) _dispatcher.BeginInvoke(RefreshState);
    }

    private void RefreshState()
    {
        Jobs.Clear();
        foreach (CollectionJob job in Collector.State.Jobs.OrderByDescending(j => j.QueuedAtUtc)) Jobs.Add(job);
        Changed(nameof(SavedAutomaticDownloadsEnabled)); Changed(nameof(SavedSchedule)); Changed(nameof(JobSummary)); Changed(nameof(IsBusy));
        NewListCommand?.RaiseCanExecuteChanged();
        foreach (AsyncRelayCommand command in Commands()) command?.RaiseCanExecuteChanged();
    }

    private AsyncRelayCommand[] Commands() => [SaveListCommand, DeleteListCommand, AddTickersCommand,
        LoadWatchlistsCommand, ImportWatchlistCommand, RefreshWatchlistCommand, SaveScheduleCommand,
        DownloadNowCommand, RetryMissingCommand, ScanLibraryCommand, OpenFolderCommand];
    private AsyncRelayCommand Command(Func<Task> action) => new(() => ExecuteAsync(action), () => !IsBusy && !_disposed);
    public async Task ExecuteAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true;
        RefreshState();
        try { await action(); }
        catch (OperationCanceledException) { Status = "Current request cancelled. Pending work is retained and retries while the app remains open and connected."; }
        catch (Exception exception) { Status = exception.Message; }
        finally { _busy = false; RefreshState(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        Collector.StateChanged -= OnCollectorChanged;
        await _lifetime.CancelAsync();
        Task[] active = Commands().Select(c => c.ExecutionTask).Append(_pollTask).OfType<Task>().ToArray();
        try { await Task.WhenAll(active); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
