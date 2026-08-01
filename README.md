# PriceSentinel 3000

Real-time price-action monitoring and simulation-first trade execution for a user-selected stock or ETF.

> [!WARNING]
> This project is experimental software, not financial advice. Trading involves substantial risk of loss. Validate strategies in simulation before considering live execution.

## Status

Stage 2 provides a functional WPF simulation control surface on top of the .NET 10
solution scaffold. The interface includes:

- OFF / Replay / Simulation / LIVE rotary mode selection, with OFF at startup
- Separate selected and effective modes so selecting LIVE cannot arm execution
- The first-session LIVE risk warning with explicit I AGREE and CANCEL actions
- Simulation starting balance, symbol, position size, entry limit, daily-loss,
  stop-loss, buffer, polling, and reconciliation settings
- Validated conservative defaults, a tunable 5-15 minute buffer display, session
  state, and an in-memory activity journal

Market connectivity, SQLite persistence, strategy evaluation, simulated fills,
Robinhood authorization, and real order execution are not implemented yet. The UI
labels those adapters offline and keeps LIVE disarmed.

## Solution layout

```text
PriceSentinel3000.sln
src/
  PriceSentinel3000.App/             WPF desktop interface
  PriceSentinel3000.Core/            Strategy, market models, simulation, and risk
  PriceSentinel3000.Infrastructure/  Robinhood MCP and SQLite adapters
tests/
  PriceSentinel3000.Core.Tests/      Deterministic strategy and safety tests
```

Dependencies point inward: the application references Core and Infrastructure,
Infrastructure references Core, and Core remains independent of WPF, Robinhood,
and SQLite. Broker and persistence interfaces will be defined in Core as those
adapters are implemented.

## Development

Requirements:

- Visual Studio 2026 with the .NET desktop development workload
- .NET SDK 10.0.302 or a compatible .NET 10 patch

Open `PriceSentinel3000.sln` in Visual Studio, or use the command line:

```powershell
dotnet build PriceSentinel3000.sln
dotnet test PriceSentinel3000.sln
```

The application starts in OFF mode and asks the user to explicitly select Replay,
Simulation, or LIVE. Its current LIVE workflow records the risk acknowledgment but
does not arm execution. A later integration stage must complete Robinhood
authorization, account checks, risk checks, and data initialization before Core
will permit LIVE to be armed.
