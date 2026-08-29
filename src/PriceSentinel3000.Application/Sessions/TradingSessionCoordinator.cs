namespace PriceSentinel3000.Application.Sessions;

public sealed class TradingSessionCoordinator : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    public bool IsActive => _cancellation is { IsCancellationRequested: false };

    public CancellationToken Token => _cancellation?.Token ?? CancellationToken.None;

    public CancellationToken Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel();
        _cancellation = new();
        return _cancellation.Token;
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
    }
}
