# September 4, 2026 audit fixes

Implemented the 12 findings from the audit of commit `b04905b`, with regression
coverage across Core, Application, Infrastructure, and the WPF application.

| Finding | Implemented behavior | Regression coverage |
| --- | --- | --- |
| Unrelated order adopted during recovery | Recovery requires the exact non-empty client reference, including direct lookup and fallback results. Conflicting or unknown identity stays unresolved. | Coordinator recovery and cancellation tests |
| Malformed broker state interpreted as flat | Required position/order collections, rows, quantities, and execution identities are validated. Explicit empty collections remain valid. | Broker parser tests |
| Daily limits never roll over | Eastern date transitions reset daily entry counts and risk locks, carry forward the last observed equity baseline, and preserve position and settlement state. LIVE persists baselines per account/date, including rollover during pending-order handling and STOP. | Core overnight tests; application rollover, STOP, and position-confirmation tests |
| Visible settings diverge from active settings | Session inputs are fixed during startup and execution; labels use the captured instrument. Chart interval remains adjustable. | WPF configuration and session tests |
| Invalid numeric text ignored on START | START commits enabled bindings and refuses conversion errors; disabled optional fields do not block startup. | Tests using actual WPF controls and bindings |
| Share cap rounded upward | Sizing floors the final capped quantity to six decimal places before constructing an intent or fill. | LIVE and Paper quantity tests |
| Reconciliation replaces executable prices | Execution receives the original validated quote with its bid/ask, after strictly earlier strategy history. | Application fill-price and invalid-quote tests |
| Distinct fills collapsed | A unique `(order_id, execution_id)` index preserves distinct executions and makes repeated snapshots idempotent. Existing journals migrate from recorded execution snapshots. | SQLite identity, migration, and replay tests |
| Sparse polling loses RSI history | Strategy history retains at least 16 observations for RSI and its previous value; pattern detection still uses the configured time window. | Sparse polling, buffer, and strategy tests |
| Replay ignores low playback speeds | Delays follow source-time differences divided by selected speed; pause and cancellation remain responsive. | Replay pacing and cancellation tests |
| STOP disabled during startup | Starting-state changes notify both commands, including LIVE acknowledgement and connection startup. | Bound WPF button tests |
| Reconciliation stalls quote delivery | One history request runs alongside quote polling. Paper execution checks quote age against the current injected clock. | Slow reconciliation, cancellation, failure, and stale-quote tests |

## Performance

The fill identity index removes repeated full-ledger scans during deduplication.
Chart projection skips unchanged inputs, groups markers by candle in one pass, and
caches RSI values between point changes.

In the same isolated 200-refresh scenario used by the audit (2,581 one-second
observations, a 15-minute visible window, 75 candles, unchanged data, no markers),
average projection time fell from approximately **1.09 ms to 0.08 ms**, and
allocation fell from **174,848 to 28,740 bytes per refresh**. This measurement
excludes WPF rendering and database work; it is not an overall application
throughput measurement.

## Upgrade behavior and validation scope

Final verification on Windows:

- `dotnet build PriceSentinel3000.sln --configuration Release --no-restore --warnaserror`
  succeeded with zero warnings and zero errors.
- `dotnet test PriceSentinel3000.sln --configuration Release --no-build --verbosity quiet`
  passed **297 tests**: 159 Core, 33 Application, 89 Infrastructure, and 16 App.
- `git diff --check` passed.

LIVE sessions now record their account number. If today's older LIVE sessions
lack account attribution and no account-specific daily baseline exists, LIVE
stays disarmed until the next Eastern date. This preserves the daily-loss limit
instead of guessing an account or silently resetting its baseline. Paper Trader
and Replay remain available.

Entry reconstruction retains the existing broker policy of counting filled BUY
orders created since Eastern midnight. The new baseline is based on observed
portfolio equity, not a broker daily-P&L endpoint. A daily risk lock remains an
in-memory latch; its baseline survives restart, but the historical fact that the
limit was previously touched is not separately persisted.

Validation uses fake broker/data ports, temporary SQLite journals, and a plain
WPF test application on an STA dispatcher. No real orders or connected-market
acceptance tests are part of this verification. The test host does not start the
production application.
