namespace PriceSentinel3000.Application.MarketDataLibrary;

public enum CollectionJobStatus { Pending, Downloading, Complete, Partial, Unavailable, Failed }
public enum CollectionBatchResult { Idle, Ready, WaitingForRetry, Disconnected, Busy }

public sealed record CollectionContinuityGap(string Symbol, DateOnly FromSessionDate, DateOnly ThroughSessionDate,
    string SessionBounds, string LibraryRootPath);

public sealed record CollectionJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Symbol { get; init; } = "";
    public string? ProviderInstrumentId { get; init; }
    public DateOnly SessionDate { get; init; }
    public int SourceIntervalSeconds { get; init; } = 15;
    public int? ActualSourceIntervalSeconds { get; init; }
    public int NextSourceIntervalSeconds { get; init; } = 15;
    public string SessionBounds { get; init; } = "regular";
    public string AdjustmentPolicy { get; init; } = "split";
    public string AdjustmentBasis { get; init; } = "robinhood-split-unversioned";
    public string LibraryRootPath { get; init; } = "";
    public CollectionJobStatus Status { get; init; } = CollectionJobStatus.Pending;
    public int Attempts { get; init; }
    public DateTimeOffset QueuedAtUtc { get; init; }
    public DateTimeOffset? LastAttemptAtUtc { get; init; }
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> DatasetHashes { get; init; } = [];
    public bool IsAutomatic { get; init; }
    public bool IsAvailabilityProbe { get; init; }
    public bool IgnoreKnownGaps { get; init; }
    public DateTimeOffset? RequestedThroughUtc { get; init; }
    public DateOnly? DiscoveryAsOfDate { get; init; }
    public bool AvailabilityCheckPending { get; init; }
    public int? DiscoveryEmptySessions { get; init; }
    public DateTimeOffset? NextGapFromUtc { get; init; }
    public bool ReceivedCandlesThisRun { get; init; }
}

public sealed record CollectionState
{
    public int SchemaVersion { get; init; } = 1;
    public CollectionSettings Settings { get; init; } = new();
    public IReadOnlyList<CollectionJob> Jobs { get; init; } = [];
    public IReadOnlyList<CollectionContinuityGap> ContinuityGaps { get; init; } = [];
    public DateTimeOffset? LastScheduledOccurrenceUtc { get; init; }
}

public interface ICollectionStateStore
{
    CollectionState Load();
    void Save(CollectionState state);
}

public sealed record CollectionRunOptions
{
    public int MaximumRequestsPerTick { get; init; } = 8;
    public int MaximumTransientAttempts { get; init; } = 3;
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
}
