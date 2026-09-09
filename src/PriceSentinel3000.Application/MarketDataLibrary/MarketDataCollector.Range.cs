namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
    /// <summary>Retries only the selected interval, preserving saved candles and stopping at its boundaries.</summary>
    public async Task<IReadOnlyList<Guid>> QueueRangeAsync(string symbol, DateTimeOffset fromUtc,
        DateTimeOffset throughUtc, CancellationToken cancellationToken = default)
    {
        string normalized = CollectionSettings.NormalizeSymbol(symbol);
        const long intervalTicks = 15 * TimeSpan.TicksPerSecond;
        if (throughUtc <= fromUtc || throughUtc - fromUtc > TimeSpan.FromDays(2) ||
            fromUtc.UtcTicks % intervalTicks != 0)
            throw new ArgumentException("Select a range starting on a 15-second boundary and no longer than two days.");
        var ids = new List<Guid>();
        await MutateAsync(() =>
        {
            DateTimeOffset now = _clock.GetUtcNow();
            DateTimeOffset cutoff = throughUtc < now ? throughUtc : now;
            cutoff = new(cutoff.UtcTicks - cutoff.UtcTicks % intervalTicks, TimeSpan.Zero);
            if (cutoff <= fromUtc) return;
            const string bounds = CollectionSettings.AllAvailableSessionBounds;
            DownloadListMember? member = _state.Settings.Lists.SelectMany(list => list.Members)
                .FirstOrDefault(item => item.Symbol == normalized && item.ProviderInstrumentId is not null);
            DateOnly first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fromUtc, CollectionEastern).DateTime);
            DateOnly last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(cutoff.AddTicks(-1), CollectionEastern).DateTime);
            var jobs = _state.Jobs.ToList();
            for (DateOnly date = first; date <= last; date = date.AddDays(1))
            {
                IReadOnlyList<CollectionSessionWindow> sessions = CollectionSchedule.GetSessionWindows(date, bounds);
                CollectionSessionWindow[] overlap = sessions.Select(session => new CollectionSessionWindow(
                    session.FromUtc > fromUtc ? session.FromUtc : fromUtc,
                    session.ThroughUtc < cutoff ? session.ThroughUtc : cutoff))
                    .Where(session => session.FromUtc < session.ThroughUtc).ToArray();
                if (overlap.Length == 0) continue;
                var next = new CollectionJob
                {
                    Symbol = normalized, ProviderInstrumentId = member?.ProviderInstrumentId,
                    SessionDate = date, SessionBounds = bounds, LibraryRootPath = _state.Settings.LibraryRootPath,
                    RequestedFromUtc = overlap[0].FromUtc, RequestedThroughUtc = overlap[^1].ThroughUtc,
                    IgnoreKnownGaps = true, QueuedAtUtc = now,
                };
                int existing = jobs.FindIndex(job => SameWork(job, next));
                if (existing >= 0)
                {
                    CollectionJob previous = jobs[existing];
                    if (previous.Status is not (CollectionJobStatus.Pending or CollectionJobStatus.Downloading))
                        jobs[existing] = next with { Id = previous.Id };
                    ids.Add(previous.Id);
                }
                else
                {
                    if (jobs.Count(job => job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading) >= 10_000)
                        throw new InvalidOperationException("The pending queue is limited to 10,000 downloads. Complete queued work before adding another range.");
                    jobs.Add(next);
                    ids.Add(next.Id);
                }
            }
            if (ids.Count > 0) Commit(_state with { Jobs = jobs.ToArray() });
        }, cancellationToken).ConfigureAwait(false);
        return ids.ToArray();
    }
}