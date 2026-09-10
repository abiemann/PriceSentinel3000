namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
    private const int MaximumActiveJobs = 8;

    private static bool IsEligibleForActiveQueue(CollectionJob job, CollectionState state) =>
        job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading &&
        (!job.IsAutomatic || state.Settings.AutomaticDownloadsEnabled);

    private CollectionJob[] AdmitActiveJobs()
    {
        CollectionJob[] pending = _state.Jobs.Where(j => IsEligibleForActiveQueue(j, _state)).ToArray();
        HashSet<Guid> eligible = pending.Select(j => j.Id).ToHashSet();
        var active = _state.ActiveJobIds.Where(eligible.Contains).Distinct().Take(MaximumActiveJobs).ToList();
        // Retry waits keep their slots. Admit replacements only when an active job leaves the queue.
        active.AddRange(pending.OrderByDescending(j => j.SessionDate).ThenBy(j => j.QueuedAtUtc)
            .Where(j => !active.Contains(j.Id)).Take(MaximumActiveJobs - active.Count).Select(j => j.Id));
        if (!_state.ActiveJobIds.SequenceEqual(active))
            Commit(_state with { ActiveJobIds = active.ToArray() });
        return pending.Where(j => active.Contains(j.Id)).ToArray();
    }

    public Task ClearFinishedJobsAsync(CancellationToken cancellationToken = default)
    {
        EnsureCollectionFinished();
        return MutateAsync(() =>
        {
            EnsureCollectionFinished();
            Commit(_state with { Jobs = [], AvailabilityRun = null });
        }, cancellationToken);
    }

    private void EnsureCollectionFinished()
    {
        if (IsBusy || Volatile.Read(ref _state).Jobs.Any(j =>
            j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading))
            throw new InvalidOperationException("Wait until all queued downloads have finished before clearing the queue.");
    }
}
