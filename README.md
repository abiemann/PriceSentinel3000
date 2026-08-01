# PriceSentinel 3000

Real-time price-action monitoring and simulation-first trade execution for a user-selected stock or ETF.

> [!WARNING]
> This project is experimental software, not financial advice. Trading involves substantial risk of loss. Validate strategies in simulation before considering live execution.

## Status

The .NET 10 / WPF solution scaffold is ready. It currently provides the project
boundaries and a safe mode-state foundation; market connectivity, strategy
evaluation, persistence, simulation, and order execution are not implemented yet.

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

The application starts in Simulation mode. A future LIVE workflow will keep the
selected mode separate from the effective mode and will require an explicit risk
acknowledgment, Robinhood authorization, account checks, and successful data
initialization before live execution can be armed.
