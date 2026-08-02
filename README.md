# PriceSentinel 3000

Real-time price-action monitoring and simulation-first trade execution for a
user-selected stock or ETF.

> [!WARNING]
> This project is experimental software, not financial advice. Trading involves
> substantial risk of loss. Validate strategies in simulation before considering
> live execution.

## Status

Stage 3 provides a functional WPF simulation data engine and local research
journal on top of the .NET 10 solution. The application includes:

- OFF / Replay / Simulation / LIVE rotary mode selection, with OFF at startup
- Deterministic synthetic quotes with a four-minute warm start and tunable polling
- A tunable 5-15 minute rolling buffer analyzed as serial one-minute blocks
- Overlap reconciliation that distinguishes verified duplicates from corrections
- A live WPF price chart with bid/ask, direction, close, and quote-count updates
- SQLite WAL journaling for sessions, observations, activities, and future trading data
- Replay of the latest SQLite-recorded simulation for the selected symbol
- Separate selected and effective modes so selecting LIVE cannot arm execution
- The first-session LIVE risk warning with explicit I AGREE and CANCEL actions
- Simulation starting balance, symbol, position size, entry limit, daily-loss,
  stop-loss, buffer, polling, and reconciliation settings
- Validated conservative defaults and contextual instructions in the status bar

The Stage 3 feed is deliberately synthetic and cannot demonstrate that a strategy
will be profitable on real market data. Strategy evaluation, simulated fills,
Robinhood market connectivity, Robinhood authorization, and real order execution
are not implemented yet. LIVE remains explicitly disarmed.

## Stage 3 workflow

1. Select **Simulation**, configure the symbol and timing, and click
   **Start Simulation**. The app immediately loads four synthetic minutes and then
   appends one deterministic quote per polling interval.
2. Every reconciliation interval, the app re-reads the full interval plus the
   configured overlap. Missing points are filled, and matching timestamps are
   recorded as verified or corrected.
3. Stop the session, choose **Replay**, and click **Start Replay** to play the most
   recent recorded simulation for that symbol at 10x speed.

The journal is stored at
%LOCALAPPDATA%\PriceSentinel3000\journal.db. It uses normalized tables, indexed
symbol/timestamp lookups, prepared inserts, short transactions, and write-ahead
logging. Passwords and broker credentials are never stored in this database.

## Solution layout

~~~text
PriceSentinel3000.sln
src/
  PriceSentinel3000.App/             WPF desktop interface
  PriceSentinel3000.Core/            Strategy, market models, simulation, and risk
  PriceSentinel3000.Infrastructure/  Robinhood MCP and SQLite adapters
tests/
  PriceSentinel3000.Core.Tests/      Deterministic strategy and safety tests
~~~

Dependencies point inward: the application references Core and Infrastructure,
Infrastructure references Core, and Core remains independent of WPF, Robinhood,
and SQLite. Broker interfaces will be defined in Core when that integration stage
begins. The market-data and journal interfaces are already asset-neutral so a
future crypto adapter can reuse the same buffer and persistence engine.

## Development

Requirements:

- Visual Studio 2026 with the .NET desktop development workload
- .NET SDK 10.0.302 or a compatible .NET 10 patch

Open PriceSentinel3000.sln in Visual Studio, or use the command line:

~~~powershell
dotnet build PriceSentinel3000.sln
dotnet test PriceSentinel3000.sln
~~~

The application starts in OFF mode and asks the user to explicitly select Replay,
Simulation, or LIVE. After the risk acknowledgment, LIVE becomes the effective
operating mode while broker execution remains explicitly disarmed. A later
integration stage must complete Robinhood authorization, account checks, risk
checks, and real market-data initialization before Core will permit LIVE to be
armed.
