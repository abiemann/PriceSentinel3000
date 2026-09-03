# TODO — roadmap

PriceSentinel already provides deterministic, broker-isolated Paper Trader and Replay modes, real Robinhood market data, documented bid/ask and replay-close fills, risk controls, SQLite WAL journaling, and optional immediate or weekday T+1 settlement. The roadmap below contains only capabilities that are missing or partially covered.

## Selectable deterministic strategies

- Implement the one-file source-plugin system described in [DESIGN.md](DESIGN.md). A `*.strategy.cs` file declares its identity, version, required data, parameters, and deterministic evaluation logic; compatible files appear automatically in a strategy dropdown.
- Extract the current reactionary `PriceActionSignalEngine` behind a user-selectable strategy registry and preserve it as the first built-in strategy. Start with exactly one selected strategy per session, then add an optional priority-ordered list after single-strategy selection is proven.
- Make scripted `BUY` and `SELL` results actionable. Replay and Paper Trader create simulated orders; LIVE may create real Robinhood orders after the same signal passes PriceSentinel's non-bypassable sizing, buying-power, stop-loss, daily-loss, cooldown, market-hours, reconciliation, review, and execution gates.
- Publish and enforce a versioned Strategy SDK compatibility surface. Strategy code may read approved immutable market and position data, calculate approved indicators, and return `BUY`, `SELL`, or `HOLD`; it may not access credentials, call Robinhood directly, submit an order itself, alter account state, or weaken host risk controls.
- Compile and pin the selected source artifact before a session starts. Record its stable ID, declared version, source SHA-256, SDK/compiler version, and parameters; do not hot-reload a running session. Compile errors, incompatible API calls, timeouts, crashes, and malformed results must fail closed.
- Run source strategies outside the WPF process. Replay and Paper Trader can introduce the worker first; LIVE eligibility requires explicit approval of the exact tested source hash and an OS-enforced isolation boundary rather than relying on an in-process assembly loader.
- Add additional tested strategies, starting with cup-and-handle. Each strategy declares its required market-data interval and lookback, keeps every quantifiable rule and default-deny disqualifier in deterministic code, and includes unit tests.
- Build a raw OHLCV regression-fixture corpus with annotations explaining why each setup qualifies or fails. Keep at least two negative examples per positive example so false-positive and disqualifier behavior remains machine-checkable.
- Add fail-closed external-event disqualifiers where a strategy requires them, including earnings and FOMC or other calendar events, with explicit freshness and unavailable-data rules.
- Keep sizing, stops, daily-loss limits, cooldowns, circuit breakers, and execution guards outside strategy modules. A strategy may tighten those protections but must never loosen them.
- Persist strategy identity and artifact provenance on sessions, decisions, orders, and fills. Expose per-strategy, per-version, and per-source-hash trade count, win rate, and expectancy.

## Paper research completeness

- Persist and restore the paper account across launches, including cash, open positions, unsettled proceeds, entry counters, and risk-lock state.
- Extend the current single-symbol session model to an optional multi-symbol paper portfolio. Refresh open symbols in batches and isolate a stale quote, malformed response, or update failure to the affected symbol.
- Add a research and history view for open positions, closed trades, realized and unrealized P&L, maximum drawdown, and strategy/version attribution. Label every result clearly as hypothetical.
- Version the simulation model. The current live bid/ask fills, replay-close fills, buying-power rules, and weekday T+1 behavior are already implemented; define and test the remaining assumptions for configurable spread/slippage, partial fills, exchange-holiday settlement, fees, and corporate actions.
