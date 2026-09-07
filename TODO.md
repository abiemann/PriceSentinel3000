# TODO — roadmap

Reviewed September 7, 2026. PriceSentinel already provides broker-isolated Paper Trader and Replay modes, real Robinhood market data, documented bid/ask and replay-close fills, risk controls, SQLite WAL journaling, optional immediate or weekday T+1 settlement, external strategies, and MCP control with candle/indicator/event telemetry. Historical Replay preserves the actual source duration when falling back from 15-second to 30-second or one-minute data. The roadmap below tracks remaining work and release verification.

## Next release verification

- [ ] Complete real-time Paper Trader testing during an open market; the planned next session is Tuesday, September 8. Verify quote freshness, completed-candle timing and warmup, script signals, host risk overrides, simulated bid/ask fills, and journal/MCP agreement. Historical Replay checks do not complete this item.
- [ ] After that session, review failures, run the release build/tests and packaged-app smoke checks, and cut the next release only when those checks pass. Verify the footer identifies the built release tag.

## Concurrent multi-symbol trading — proposed, not implemented

Run several symbols in one portfolio, with one shared account and operating mode (Paper, Replay, or LIVE). Each symbol has its own Built-In or external strategy, pinned source/parameters, candle interval, warmup, position, and entry controls. Start with one strategy per symbol. The current paper engine owns one position and its cash balance; separate instances with the full starting balance would incorrectly multiply the available capital.

### Phase 1: shared portfolio engine and Replay

- [ ] Separate account cash, unsettled proceeds, buying power, and the daily risk baseline from per-symbol strategy/position state. Serialize account changes and reserve capital for pending orders so simultaneous buys cannot spend the same funds. Record a deterministic priority when several signals compete for limited capital, with eligible risk exits ahead of new entries.
- [ ] Enforce portfolio-wide maximum exposure, maximum open positions, daily entry limits, and a daily loss limit alongside per-symbol sizing, stops, entry caps, and re-entry rules. Include realized and unrealized P&L across positions; define the loss lock and exit behavior explicitly. Roll the shared daily baseline and counters once per account on Eastern trading-date changes, consistently across symbols and restarts. Stale marks must be visible and must not let an unreliable account valuation authorize more exposure.
- [ ] Replay all selected symbols on one chronological clock, respecting each source candle's actual availability and resolution. Define stable ordering for equal timestamps, preserve gaps, and never evaluate a symbol using future prices from another. Verify one-symbol parity, simultaneous entries with insufficient cash, settlement, cross-symbol losses, and identical results across replay speeds and pause/resume.
- [ ] Persist and restore the shared paper account, positions, unsettled proceeds, daily counters, and risk locks. Give portfolio sessions, symbols, strategies, orders, and fills explicit identities in journal records and exports; migrate existing single-symbol settings/history without losing their attribution.

### Phase 2: real-time Paper Trader and portfolio controls

- [ ] Add a portfolio table with symbol, strategy, entry enabled/disabled state, data freshness, warmup, position, and realized/unrealized P&L, plus account totals. Selecting a row shows its chart and details while all enabled symbols continue running. Disabling new entries or changing chart selection must not stop monitoring an open position; make closing positions a distinct action.
- [ ] Share market-data scheduling and batch requests where supported, with bounded concurrency and provider limits. Isolate symbol-specific data/strategy failures and block affected entries while continuing healthy symbols and available position risk checks. Treat account-wide valuation or broker uncertainty separately. Keep data subscriptions separate from trade eligibility so future benchmark ETFs can be observed without placing orders in them.
- [ ] Extend MCP configuration, status, replay controls, and candle/indicator/decision/fill exports with portfolio and symbol selection. Preserve existing single-symbol callers and Replay/Paper-only automation. Add aggregate account checks and per-symbol event attribution, then validate several concurrent symbols during an open market, including a stale feed and simultaneous signals.

### Phase 3: concurrent LIVE positions

- [ ] Extend the existing LIVE coordinator's single active-order context to tracked orders by symbol/order ID under one account coordinator. Reconcile broker buying power, positions, manual/external orders, partial fills, cancellation, and uncertain submissions before allowing conflicting new exposure; retain capital reservations until broker state resolves them. Prevent duplicate submissions across retries and restart recovery.
- [ ] Require explicit LIVE arming of the reviewed symbol/strategy/allocation list and re-arming for material changes. Apply shared broker-account limits and define Stop/entry-disable/close-position behavior across all managed symbols, without silently abandoning open positions or closing unrelated holdings. Preserve the MCP prohibition on controlling LIVE execution.
- [ ] Validate with fake broker concurrency/recovery tests and a user-supervised LIVE readiness review after Paper/Replay acceptance. Multiple open positions must not depend on parallel order submission; begin with serialized submissions if they can service exits promptly. Treat this as a separate feature milestone from the current release verification above.

## Local market-data library — proposed, not implemented

The journal saves session observations, but Replay currently fetches history from Robinhood; it does not read a reusable daily history library. See the [proposed design](DESIGN.md#proposed-local-market-data-library).

- [ ] Add an explicit download workflow for selected symbols and dates, preserving genuine 15-second OHLCV while the provider still makes it available. Display actual resolution and coverage; do not promise that older fine-resolution history can be recovered.
- [ ] Store market history separately from session journaling and repository research reports. Preserve exact prices, UTC boundaries, provider/adjustment provenance, revisions, and source hashes; distinguish historical candles from sampled live quotes.
- [ ] Validate completeness and deduplication, report gaps, and prevent mixed resolutions or incompatible price adjustments from silently entering a replay.
- [ ] Allow Replay and script analysis to reuse local history offline and aggregate complete fine candles into larger intervals. Never generate artificial 15-second prices from coarser history.
- [ ] Verify reproducible results from a pinned dataset, interrupted-download recovery, and fixed-script comparisons across source resolutions. Keep strategy interval fixed when measuring the effect of source resolution.

## Strategy scripting follow-ups

The current development build offers **Built-In** and compatible folder-based thinkScript strategies in one selector for Paper, LIVE, and Replay. It pins source and parameters per session, enforces bounded interpretation, preserves host risk controls, and packages one original example. See [DESIGN.md](DESIGN.md) and the [compatibility guide](docs/strategy-scripting.md).

- Eventually port the existing compiled strategy to the script interface while preserving its behavior through recorded regression fixtures. Keep **Built-In** available until that parity is demonstrated.
- Extend thinkScript compatibility only with documented semantics and unchanged-source fixtures. Prioritize features needed by a small number of useful strategies; do not silently approximate unsupported trading rules.
- Add reliable volume and explicit data availability before admitting volume-dependent strategies. Add secondary timeframes only with completed-bar timing and repainting tests.
- Evaluate custom functions and recursive series separately, preserving enforceable parser, memory, and operation limits. Arbitrary C# plugins remain deferred and would require an OS-isolated worker.
- Add editable input controls and saved parameter presets if the source-default workflow proves too cumbersome.
- Extend the completed two-week original/revised comparisons with broader history for both the original example and stock-specific profiles. Freeze rules before evaluation, reserve unused periods, compare consistent data/settings, and report realized/unrealized P&L, trade count, drawdown, and cost sensitivity. Existing retrospective results and synthetic compatibility fixtures do not establish future profitability.
- Diagnose entry, exit, and repeated-trade failures before adding indicators or conditions. Test one explained change at a time against the unchanged baseline; a better result in one week does not establish that a revision is generally better. Keep unpromoted candidates separate from installed versions.
- Consider a second packaged strategy only after it adds a distinct, well-tested approach. Keep the default collection small.

## Paper research completeness

- Shared paper-account persistence and concurrent symbols are tracked in the [multi-symbol trading plan](#concurrent-multi-symbol-trading--proposed-not-implemented).
- Add a research and history view for open positions, closed trades, realized and unrealized P&L, trade count, win rate, expectancy, drawdown, and strategy/version attribution. Link results to the session's pinned source hash. Existing journal records already reference that session; label every result clearly as hypothetical.
- Extend the documented simulation assumptions and per-session model identity when adding configurable spread/slippage, partial fills, exchange-holiday settlement, fees, and corporate actions. Live bid/ask fills, replay-close fills, buying-power rules, weekday T+1 behavior, and source-duration provenance are already implemented.
