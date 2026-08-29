using System.Runtime.CompilerServices;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.Sessions;

public sealed record RealtimeSessionUpdate(
    IReadOnlyList<MarketQuote> WarmStart,
    MarketQuote Quote,
    IReadOnlyList<MarketQuote> Reconciliation);

public sealed class RealtimeSessionRunner(
    IMarketDataSource marketDataSource,
    TimeProvider? timeProvider = null)
{
    private readonly IMarketDataSource _marketDataSource =
        marketDataSource ?? throw new ArgumentNullException(nameof(marketDataSource));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async IAsyncEnumerable<RealtimeSessionUpdate> RunAsync(
        MarketDataRequest request,
        int reconciliationSeconds,
        int reconciliationLookbackSeconds,
        int reconciliationCompletionDelaySeconds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        IReadOnlyList<MarketQuote> history = await _marketDataSource.GetHistoryAsync(
            request,
            now - request.WarmStartDuration,
            now,
            now,
            cancellationToken);
        MarketQuote current = await _marketDataSource.GetQuoteAsync(
            request,
            _timeProvider.GetUtcNow(),
            cancellationToken);

        yield return new(history, current, []);

        DateTimeOffset nextReconciliation = _timeProvider.GetUtcNow()
            .AddSeconds(reconciliationSeconds);

        while (true)
        {
            await Task.Delay(
                request.PollingInterval,
                _timeProvider,
                cancellationToken);
            DateTimeOffset observedAt = _timeProvider.GetUtcNow();
            MarketQuote quote = await _marketDataSource.GetQuoteAsync(
                request,
                observedAt,
                cancellationToken);
            IReadOnlyList<MarketQuote> reconciliation = [];

            if (observedAt >= nextReconciliation)
            {
                DateTimeOffset through = observedAt.AddSeconds(
                    -reconciliationCompletionDelaySeconds);
                DateTimeOffset from = through.AddSeconds(
                    -reconciliationLookbackSeconds);
                reconciliation = await _marketDataSource.GetHistoryAsync(
                    request,
                    from,
                    through,
                    observedAt,
                    cancellationToken);
                nextReconciliation = observedAt.AddSeconds(reconciliationSeconds);
            }

            yield return new([], quote, reconciliation);
        }
    }
}
