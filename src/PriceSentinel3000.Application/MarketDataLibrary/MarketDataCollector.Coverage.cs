using System.Collections.Concurrent;

namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
    private bool _loadedSavedCoverage;
    private readonly ConcurrentDictionary<Guid, HistoricalDatasetInfo[]?> _savedCoverageDatasets = new();

    // Display-only projection: no disk access, requests, or state writes as the clock advances.
    public decimal? GetSavedCoverage(CollectionJob job, DateTimeOffset now) =>
        _savedCoverageDatasets.TryGetValue(job.Id, out HistoricalDatasetInfo[]? datasets)
            ? datasets is null ? null : CollectionDayCoverage.Calculate(job, datasets, now)
            : job.SavedCoveragePercent;

    private decimal? CalculateSavedCoverage(CollectionJob job, IEnumerable<HistoricalDatasetInfo> datasets,
        bool refreshRelated = false)
    {
        HistoricalDatasetInfo[] saved = datasets.ToArray();
        _savedCoverageDatasets[job.Id] = saved;
        if (refreshRelated)
        {
            // A range download also adds coverage to other queued views of this saved day.
            foreach (CollectionJob related in _state.Jobs.Where(other => other.Id != job.Id &&
                other.SessionDate == job.SessionDate && string.Equals(other.Symbol, job.Symbol, StringComparison.OrdinalIgnoreCase) &&
                SamePath(other.LibraryRootPath, job.LibraryRootPath)))
                if (_savedCoverageDatasets.TryGetValue(related.Id, out HistoricalDatasetInfo[]? previous) && previous is not null)
                    _savedCoverageDatasets[related.Id] = previous.Concat(saved).DistinctBy(dataset => dataset.DatasetHash).ToArray();
        }
        return CollectionDayCoverage.Calculate(job, saved, _clock.GetUtcNow());
    }

    public Task<MarketDataLibraryScan> ScanLibraryAsync(IMarketDataLibrary library, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        // Serialize the scan and publication with downloads so an older scan cannot
        // replace coverage metadata from a newer saved request.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MarketDataLibraryScan scan = progress is null ? library.ConsolidateDailyFiles() : library.ConsolidateDailyFiles(progress);
            cancellationToken.ThrowIfCancellationRequested();
            ApplySavedCoverage(library.RootPath, scan);
            return scan;
        }
        finally { _gate.Release(); }
    }, cancellationToken);

    private void LoadSavedCoverage()
    {
        if (_loadedSavedCoverage) return;
        // Backfill retained jobs once using the same cached library metadata as collection.
        foreach (string root in _state.Jobs.Select(job => Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(job.LibraryRootPath))).Distinct(StringComparer.OrdinalIgnoreCase))
            ApplySavedCoverage(root, GetCollectionLibrary(root).Scan());
        _loadedSavedCoverage = true;
    }

    private void ApplySavedCoverage(string root, MarketDataLibraryScan scan)
    {
        var days = scan.Datasets.ToLookup(dataset => (dataset.Symbol, dataset.TradingDate));
        CollectionJob[] jobs = _state.Jobs.Select(job =>
        {
            if (!SamePath(job.LibraryRootPath, root)) return job;
            if (scan.Diagnostics.Count > 0)
            {
                _savedCoverageDatasets[job.Id] = null;
                return job with { SavedCoveragePercent = null };
            }
            return job with { SavedCoveragePercent = CalculateSavedCoverage(job, days[(job.Symbol, job.SessionDate)]) };
        }).ToArray();
        if (jobs.Where((job, index) => job.SavedCoveragePercent != _state.Jobs[index].SavedCoveragePercent).Any())
            Commit(_state with { Jobs = jobs });
    }
}
