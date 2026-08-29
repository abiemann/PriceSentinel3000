using System.Runtime.CompilerServices;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.Sessions;

public sealed record ReplaySessionUpdate(
    int Index,
    int Total,
    MarketQuote Quote);

public sealed class ReplaySessionRunner(TimeProvider? timeProvider = null)
{
    private readonly object _pauseGate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private TaskCompletionSource? _resumeSource;

    public bool IsPaused
    {
        get
        {
            lock (_pauseGate)
            {
                return _resumeSource is not null;
            }
        }
    }

    public bool Pause()
    {
        lock (_pauseGate)
        {
            if (_resumeSource is not null)
            {
                return false;
            }

            _resumeSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public bool Resume()
    {
        TaskCompletionSource? resumeSource;

        lock (_pauseGate)
        {
            resumeSource = _resumeSource;
            _resumeSource = null;
        }

        return resumeSource?.TrySetResult() is true;
    }

    public async IAsyncEnumerable<ReplaySessionUpdate> RunAsync(
        IReadOnlyList<MarketQuote> quotes,
        decimal speed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quotes);

        for (int index = 0; index < quotes.Count; index++)
        {
            if (index > 0)
            {
                await DelayAsync(
                    CalculateDelay(
                        quotes[index - 1].SourceTimestampUtc,
                        quotes[index].SourceTimestampUtc,
                        speed),
                    cancellationToken);
            }
            else
            {
                await WaitWhilePausedAsync(cancellationToken);
            }

            yield return new(index, quotes.Count, quotes[index]);
        }
    }

    public static TimeSpan CalculateDelay(
        DateTimeOffset previous,
        DateTimeOffset current,
        decimal speed)
    {
        double milliseconds =
            (current - previous).TotalMilliseconds / decimal.ToDouble(speed);
        return TimeSpan.FromMilliseconds(
            Math.Clamp(
                double.IsFinite(milliseconds) ? milliseconds : 20d,
                20d,
                2_000d));
    }

    private async Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = delay;
        TimeSpan maximumSlice = TimeSpan.FromMilliseconds(100);

        while (remaining > TimeSpan.Zero)
        {
            await WaitWhilePausedAsync(cancellationToken);
            TimeSpan slice = remaining < maximumSlice ? remaining : maximumSlice;
            await Task.Delay(slice, _timeProvider, cancellationToken);
            remaining -= slice;
        }

        await WaitWhilePausedAsync(cancellationToken);
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? resumeTask;

            lock (_pauseGate)
            {
                resumeTask = _resumeSource?.Task;
            }

            if (resumeTask is null)
            {
                return;
            }

            await resumeTask.WaitAsync(cancellationToken);
        }
    }
}
