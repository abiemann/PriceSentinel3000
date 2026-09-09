namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
    private bool _loadedSavedCoverage;

    private void LoadSavedCoverage()
    {
        if (_loadedSavedCoverage) return;
        // Backfill retained jobs from older versions once, using the same cached
        // library metadata as collection. Never issue broker requests for display.
        var percentages = new Dictionary<Guid, decimal?>();
        foreach (var group in _state.Jobs.GroupBy(job =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.LibraryRootPath)),
            StringComparer.OrdinalIgnoreCase))
        {
            MarketDataLibraryScan scan = GetCollectionLibrary(group.Key).Scan();
            var days = scan.Datasets.ToLookup(dataset => (dataset.Symbol, dataset.TradingDate));
            foreach (CollectionJob job in group)
                percentages[job.Id] = scan.Diagnostics.Count > 0 ? null :
                    CollectionDayCoverage.Calculate(job, days[(job.Symbol, job.SessionDate)]);
        }
        CollectionJob[] jobs = _state.Jobs.Select(job =>
            job with { SavedCoveragePercent = percentages.GetValueOrDefault(job.Id) }).ToArray();
        if (jobs.Where((job, index) => job.SavedCoveragePercent != _state.Jobs[index].SavedCoveragePercent).Any())
            Commit(_state with { Jobs = jobs });
        _loadedSavedCoverage = true;
    }
}