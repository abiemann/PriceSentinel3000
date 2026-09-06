# TODO — roadmap

PriceSentinel already provides deterministic, broker-isolated Paper Trader and Replay modes, real Robinhood market data, documented bid/ask and replay-close fills, risk controls, SQLite WAL journaling, and optional immediate or weekday T+1 settlement. The roadmap below contains only capabilities that are missing or partially covered.

## Strategy scripting follow-ups

The first release now offers **Built-In** and compatible folder-based thinkScript strategies in one selector for Paper, LIVE, and Replay. It pins source and parameters per session, enforces bounded interpretation, preserves host risk controls, and packages one original example. See [DESIGN.md](DESIGN.md) and the [compatibility guide](docs/strategy-scripting.md).

- Eventually port the existing compiled strategy to the script interface while preserving its behavior through recorded regression fixtures. Keep **Built-In** available until that parity is demonstrated.
- Extend thinkScript compatibility only with documented semantics and unchanged-source fixtures. Prioritize features needed by a small number of useful strategies; do not silently approximate unsupported trading rules.
- Add reliable volume and explicit data availability before admitting volume-dependent strategies. Add secondary timeframes only with completed-bar timing and repainting tests.
- Evaluate custom functions and recursive series separately, preserving enforceable parser, memory, and operation limits. Arbitrary C# plugins remain deferred and would require an OS-isolated worker.
- Add editable input controls and saved parameter presets if the source-default workflow proves too cumbersome.
- Build a broader historical evaluation corpus for the original example, including negative setups, spread/slippage sensitivity, and different market conditions. Synthetic compatibility fixtures do not establish profitability.
- Add a research view with per-strategy/source-hash trade count, win rate, expectancy, drawdown, and links to the session's pinned source. Existing decisions/orders/fills already reference that session.
- Consider a second packaged strategy only after it adds a distinct, well-tested approach. Keep the default collection small.

## Paper research completeness

- Persist and restore the paper account across launches, including cash, open positions, unsettled proceeds, entry counters, and risk-lock state.
- Extend the current single-symbol session model to an optional multi-symbol paper portfolio. Refresh open symbols in batches and isolate a stale quote, malformed response, or update failure to the affected symbol.
- Add a research and history view for open positions, closed trades, realized and unrealized P&L, maximum drawdown, and strategy/version attribution. Label every result clearly as hypothetical.
- Version the simulation model. The current live bid/ask fills, replay-close fills, buying-power rules, and weekday T+1 behavior are already implemented; define and test the remaining assumptions for configurable spread/slippage, partial fills, exchange-holiday settlement, fees, and corporate actions.
