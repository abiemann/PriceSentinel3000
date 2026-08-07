# TODO — roadmap

PriceSentinel already provides deterministic, broker-isolated Paper Trader and Replay modes, real Robinhood market data, documented bid/ask and replay-close fills, risk controls, SQLite WAL journaling, and optional immediate or weekday T+1 settlement. The roadmap below contains only capabilities that are missing or partially covered.

## Selectable deterministic strategies

- Extract the current reactionary `PriceActionSignalEngine` into a user-selectable strategy registry. Support one strategy or a priority-ordered list, use the identical selection and evaluation path in Replay, Paper Trader, and LIVE, and preserve the current behavior as the first built-in strategy.
- Add additional tested strategies, starting with cup-and-handle. Each strategy declares its required market-data interval and lookback, keeps every quantifiable rule and default-deny disqualifier in deterministic code, and includes unit tests.
- Build a raw OHLCV regression-fixture corpus with annotations explaining why each setup qualifies or fails. Keep at least two negative examples per positive example so false-positive and disqualifier behavior remains machine-checkable.
- Add fail-closed external-event disqualifiers where a strategy requires them, including earnings and FOMC or other calendar events, with explicit freshness and unavailable-data rules.
- Keep sizing, stops, daily-loss limits, cooldowns, circuit breakers, and execution guards outside strategy modules. A strategy may tighten those protections but must never loosen them.
- Persist `strategy_id` and `strategy_version` on sessions, decisions, orders, and fills. Expose per-strategy and per-version trade count, win rate, and expectancy.

## Paper research completeness

- Persist and restore the paper account across launches, including cash, open positions, unsettled proceeds, entry counters, and risk-lock state.
- Extend the current single-symbol session model to an optional multi-symbol paper portfolio. Refresh open symbols in batches and isolate a stale quote, malformed response, or update failure to the affected symbol.
- Add a research and history view for open positions, closed trades, realized and unrealized P&L, maximum drawdown, and strategy/version attribution. Label every result clearly as hypothetical.
- Version the simulation model. The current live bid/ask fills, replay-close fills, buying-power rules, and weekday T+1 behavior are already implemented; define and test the remaining assumptions for configurable spread/slippage, partial fills, exchange-holiday settlement, fees, and corporate actions.
