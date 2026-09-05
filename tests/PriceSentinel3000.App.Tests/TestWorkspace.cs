using System.IO;
using System.Reflection;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.Configuration;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.App.Tests;

internal sealed class TestWorkspace : IAsyncDisposable
{
    public TestWorkspace()
    {
        Journal = new SqliteTradingJournal(Path.Combine(Path.GetTempPath(), $"pricesentinel-ui-{Guid.NewGuid():N}.db"));
        ViewModel = new(Broker, Broker, Broker, Broker, Journal, new Preferences(), Clock);
    }

    public FakeBroker Broker { get; } = new();
    public TestClock Clock { get; } = new();
    public SqliteTradingJournal Journal { get; }
    public MainViewModel ViewModel { get; }

    public void Prepare(TradingMode mode = TradingMode.PaperTrader)
    {
        ViewModel.RequestModeSelection(mode);
        if (mode is TradingMode.Live)
        {
            Set("_liveAccount", Broker.Account);
        }
        Invoke("PrepareDataSession", new Instrument("SOFI"), TradingSessionSettings.Default, mode);
    }

    public object? Invoke(string method, params object[] args) => typeof(MainViewModel)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ViewModel, args);

    public T Get<T>(string field) => (T)typeof(MainViewModel)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ViewModel)!;

    public void Set(string field, object value) => typeof(MainViewModel)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ViewModel, value);

    public async ValueTask DisposeAsync()
    {
        await ViewModel.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(Journal.DatabasePath + suffix);
        }
    }

    private sealed class Preferences : IUserPreferencesStore
    {
        public TradingSessionSettings? Load() => TradingSessionSettings.Default;
        public bool Save(TradingSessionSettings settings) => true;
    }
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 3, 16, 2, 30, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeBroker : IMarketDataSource, ICachedAuthenticationMarketDataSource, IInstrumentSearchSource, ILiveBrokerGateway
{
    public bool HoldConnection { get; set; }
    public int Connections { get; private set; }
    public BrokerAccount Account { get; } = new("test-account", true, true, "individual");
    public BrokerPortfolio Portfolio { get; set; } = new(10_000m, 0m, 10_000m, 10_000m, "USD");
    public IReadOnlyList<BrokerOrderSnapshot> OrdersToday { get; set; } = [];
    public BrokerPosition Position { get; set; } = BrokerPosition.Flat("SOFI");
    public string Name => "Fake broker";
    public bool HasCachedAuthentication => false;
    public Task<bool> TryConnectUsingCachedAuthenticationAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        Connections++;
        return HoldConnection ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask;
    }

    public Task<IReadOnlyList<MarketQuote>> GetHistoryAsync(MarketDataRequest request, DateTimeOffset fromUtc, DateTimeOffset throughUtc, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MarketQuote>>([]);
    public Task<MarketQuote> GetQuoteAsync(MarketDataRequest request, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) => Task.FromResult(new MarketQuote(request.Instrument, observedAtUtc, observedAtUtc, 9.99m, 10.01m, 10m, 0m));
    public Task<IReadOnlyList<MarketQuote>> GetReplayHistoryAsync(Instrument instrument, DateTimeOffset fromUtc, DateTimeOffset throughUtc, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MarketQuote>>([]);
    public Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InstrumentSearchResult>>([]);
    public Task<BrokerAccount> GetAgenticAccountAsync(CancellationToken cancellationToken) => Task.FromResult(Account);
    public Task<BrokerPortfolio> GetPortfolioAsync(string accountNumber, CancellationToken cancellationToken) => Task.FromResult(Portfolio);
    public Task<BrokerPosition> GetPositionAsync(string accountNumber, Instrument instrument, CancellationToken cancellationToken) => Task.FromResult(Position);
    public Task<EquityTradability> GetTradabilityAsync(string accountNumber, string accountType, Instrument instrument, CancellationToken cancellationToken) => Task.FromResult(new EquityTradability(instrument.Symbol, true, true, "active", null));
    public Task<IReadOnlyList<BrokerOrderSnapshot>> GetOpenOrdersAsync(string accountNumber, Instrument instrument, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BrokerOrderSnapshot>>([]);
    public Task<IReadOnlyList<BrokerOrderSnapshot>> GetOrdersCreatedSinceAsync(string accountNumber, DateTimeOffset createdAtGteUtc, CancellationToken cancellationToken) => Task.FromResult(OrdersToday);
    public Task<BrokerOrderReview> ReviewOrderAsync(string accountNumber, BrokerOrderIntent intent, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected broker review in UI regression.");
    public Task<BrokerOrderSnapshot> PlaceOrderAsync(string accountNumber, BrokerOrderIntent intent, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected broker placement in UI regression.");
    public Task<BrokerOrderSnapshot> GetOrderAsync(string accountNumber, string brokerOrderId, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected order lookup in UI regression.");
    public Task<BrokerOrderSnapshot?> FindOrderByClientReferenceAsync(string accountNumber, Instrument instrument, Guid clientReferenceId, CancellationToken cancellationToken) => Task.FromResult<BrokerOrderSnapshot?>(null);
    public Task CancelOrderAsync(string accountNumber, string brokerOrderId, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected broker cancellation in UI regression.");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
