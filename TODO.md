# TODO — roadmap

Reviewed September 9, 2026. [Release 1.3](https://github.com/abiemann/PriceSentinel3000/releases/tag/1.3) is published. PriceSentinel provides broker-isolated Paper Trader and Replay modes, real Robinhood market data, documented bid/ask and replay-close fills, risk controls, SQLite WAL journaling, optional immediate or weekday T+1 settlement, external strategies, and MCP control with candle/indicator/event telemetry. Historical Replay preserves the actual source duration when falling back from 15-second to 30-second or one-minute data. The roadmap below separates completed release validation from remaining supervised runtime checks and future work.

## Release 1.3 validation and remaining runtime checks

- [x] Initial publication of release 1.3 from commit `1bea13a24d9ed3a07012acbdfe0c4c3e8ac459e0` on September 9, 2026 Pacific time (September 10 UTC), with installer, checksums, and provenance attached and verified.
- [x] Validate the local Release build with zero warnings/errors and all 1,404 tests passing; complete [Windows CI](https://github.com/abiemann/PriceSentinel3000/actions/runs/34422818384) and the [Release workflow](https://github.com/abiemann/PriceSentinel3000/actions/runs/34422844466) successfully.
- [ ] Complete and record real-time Paper Trader testing during an open market. Verify quote freshness, completed-candle timing and warmup, script signals, host risk overrides, simulated bid/ask fills, and journal/MCP agreement. Historical Replay checks do not complete this item.
- [ ] Complete and record an interactive smoke check of the packaged 1.3 installer/app, including launch, basic controls, and a footer identifying release 1.3. Successful builds, automated tests, and published artifacts do not complete this item.

## Desktop controls — implemented

- [x] Save the LIVE loss-warning acknowledgement once in local preferences across restarts. LIVE execution remains off by default and each LIVE session requires an explicit start.
- [x] Use a gray rounded outer window border, with square corners when maximized.

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

## Local market-data library — implemented

See the [user guide](docs/market-data-library.md) and [design](DESIGN.md#local-market-data-library).

- [x] App-styled Tools entry, saved-state clock, modeless list/schedule/library UI, editable lists, individual equity inclusion, and read-only Robinhood snapshot import/refresh.
- [x] User-selected daily time and saved time zone, holiday/early-close/DST handling, app-open collection, durable deduplicated jobs, bounded retries, and restart/disconnection recovery.
- [x] Keep unavailable 15-second ranges and attempt counts in the daily JSON, including unresolved portions of partial responses. Always queue today; precheck older dates before queueing, allow two normal attempts per gap, and stop each ticker at a wholly unavailable older date. Force bypasses the cap; saved portions clear their attempt records.
- [x] Scheduled runs and the date-free **Download gaps now** action collect all available regular, premarket, after-hours and overnight 15-second history, reuse saved coverage, collect today's completed candles, check older gaps in hourly windows before queuing remaining work, skip complete days, and stop each equity at the first older regular trading date whose missing ranges return no data.
- [x] **Forced download** rechecks remembered empty ranges for that run while preserving saved candles; **Clear** removes finished queue entries when all downloads and queued work are idle, preserving files, remembered gaps, continuity, and the schedule.
- [x] Group nearby missing candles into bounded requests, validate saved overlaps, avoid redundant files, and show progress within each stock/date.
- [x] Continue ready download batches without a fixed pause; keep collection running after closing its window, with live progress inside the header button and current status on reopening.
- [x] Portable symbol-only list transfer and year / numbered English month / ticker / daily JSON history with exact candles, source-close timing, provenance, gaps, nullable volume and archived exact-hash history.
- [x] Merge compatible 15-second sections into one current file per stock/Eastern date; preserve every unique candle across midnight/month/year boundaries, archive superseded snapshots, and retain exact-hash Replay.
- [x] Accept newer compatible 15-second price and volume corrections, retaining saved values for zero/null/empty updates and preserving archived snapshots. Keep timing, provenance, duplicate and negative-value validation; ordinary downloads continue to target gaps.
- [x] Local-first and offline Replay, explicit revision selection and dataset pins, actual-resolution enforcement, and read-only paginated MCP library discovery/candle access without an active session.
- [x] Per-equity continuity from saved coverage, genuine completed 15-second-only collection, reuse of existing regular/extended files when expanding coverage, partial-day repair, expected candle counts excluding market closures, and persistent reporting of older unresolved gaps.
- [x] Sort both download and library tables by multiple columns, show local scan progress and active saved-file size in MB, and keep full library notices in a bounded, scrollable details panel.
- [x] Open a saved-coverage timeline in local time while retaining Eastern-dated daily files; distinguish saved, partial, missing, closed, and future intervals, and calculate today's coverage only through completed candles at the current time.
- [x] Download a selected missing timeline block and its touching missing blocks through the connected broker, retrying remembered empty ranges for that span, merging candles into the appropriate Eastern daily files, and refreshing the selected timeline without starting older-day discovery.
- [x] Replay preflight on Enter/CHECK/date selection, a colored availability calendar, exact-range coverage checks, and reuse of checked data at START without another broker download.
- [x] Dashboard local-only Replay option; default disk-first reuse of agreeing saved pieces and broker gap fills at genuine supported intervals, with uniform Replay aggregation and native-source provenance.
- [x] Automated storage, scheduler, import, Replay, MCP and UI integration checks; authenticated read-only list/candle provider smoke checks.
- [ ] Compare fixed scripts empirically across retained source resolutions using the same period and strategy interval. Preserving finer data does not guarantee higher P&L.
- [ ] Consider a separately installed background worker if users need collection while the app is closed. Current scheduling explicitly requires an open, connected app.

## Strategy scripting follow-ups

Release 1.3 offers **Built-In** and compatible folder-based thinkScript strategies in one selector for Paper, LIVE, and Replay. It pins source and parameters per session, enforces bounded interpretation, preserves host risk controls, and packages one original example. See [DESIGN.md](DESIGN.md) and the [compatibility guide](docs/strategy-scripting.md).

- [x] Read optional tested-interval comments, select the declared interval when choosing a script, show unspecified/mismatch guidance, and retain declared and actual intervals in session/MCP provenance. Refresh and app startup preserve saved overrides; active sessions keep their pinned configuration.
- [x] Keep script diagnostics in a bounded, scrollable panel with a right-click **Copy** action for the complete diagnostic text.

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

Keep detailed experiments and generated reports under ignored `artifacts/`; follow the [research index](research/README.md) when deliberately archiving a study. Current app instructions belong in [docs](docs/README.md), with dated software validation records in [archive/app](archive/app/README.md). Do not mix research results into application guides.
