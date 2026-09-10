namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
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
