# SOXL and SOXX: frozen full-day script experiment

Requested September 6, 2026 Pacific. Research original scripts for SOXL and SOXX
over August 31 through September 4, 2026, in the existing PriceSentinel app through
its MCP interface. Historical Replay uses simulated money; no app features,
live broker execution or release publication are included.

## Frozen hypotheses

Use the same five previously validated original formulas, copied byte-identically
from the low-price experiment. Pin source hashes, inputs and warmup requirements
in the manifest before inspecting SOXL/SOXX Replay prices or P&L. No candidate
will be added or tuned after inspecting development outcomes.

| ID | Pattern | Fixed parameters | One-minute warmup bars |
| --- | --- | --- | ---: |
| C1 | Trend-pullback confirmation | EMA8/21, RSI7, entry50 / exit50 | 85 |
| C2 | Confirmation with more patient exit | EMA8/21, RSI7, entry50 / exit45 | 85 |
| C3 | Stronger momentum confirmation | EMA8/21, RSI7, entry55 / exit50 | 85 |
| B1 | Previous-channel breakout | Previous20-bar high, EMA21, previous10-bar low exit | 84 |
| R1 | Oversold recovery toward the mean | RSI7 crosses above35 below SMA20; exit at SMA20 or RSI below25 | 51 |

The host retains execution sizing, long-only positions, re-entry and risk rules.
SOXL and SOXX are tested on their own observed share-price histories. SOXL's daily
leverage is not modeled by multiplying SOXX prices or P&L. Equal $500 positions
are a cash-allocation comparison, not equal underlying exposure or matched risk.

## Dates, settings, and evidence

Attempt all four Monday-Thursday dates with C1 for each fund, then all other
candidates on every available date. Dates with no usable history are unavailable,
never zero P&L. The app's history parser excludes null and interpolated bars;
accepted exports alone do not identify why a particular source interval is absent.
Retain gaps without filling them or bypassing warmup resets.

Each session covers exactly 06:30-13:00 Pacific (UTC-07:00 on these dates) using
Robinhood split-adjusted historical 15-second OHLC bars and completed one-minute
script candles. Reset to $1,400 cash each day, fixed $500 positions, immediate
settlement, unlimited entries/quantity, 1% purchase-price stop, $50 daily loss
limit, 15-minute buffer, five-second quote poll, reconciliation 45/900/30 seconds,
and the existing re-entry guards. Fast Replay changes pacing only. No preloaded
warmup or future observations. Each day and fund is a separately reset account.

Only the root agent controls the visible app. Export each full session before
the next run: exact numeric JSON, pinned settings/source/runtime, source and
strategy candles, indicators, proposals, host risk overrides, fills and accounts.
Check pagination/retention, operation and session identities, temporal cutoffs,
exact Decimal accounting and OHLC aggregation, source consistency, warmup and
independent indicator/action values. Preserve earlier working-tree changes.

## Per-fund selection

Each fund independently needs at least two development dates, each with at least
1,529 of the expected 1,560 source observations and exact requested start/end
boundaries. All five candidates must run on every available date and share the
same source hash. Missing comparisons or inconsistent/invalid data block that
fund's selection; a shortfall in one fund does not prevent the other qualifying.
The selected profile must make at least one entry on eligible development dates.

Gross P&L is final marked equity minus $1,400, including open positions. Replay
does not liquidate at 13:00. The primary score deducts 5 basis points per recorded
traded notional plus 5 basis points on ending marked exposure as a closing-cost
allowance. This is a static-ledger sensitivity, not a predicted cost or simulated
closing fill, and does not recalculate risk decisions. Retain gross, realized,
unrealized and sensitivity at 0/1/2.5/5/10/25 bps and $0.005/$0.01 per share.

For each eligible fund choose the highest unrounded sum of primary scores across
eligible development days. Tie-break by higher worst-day primary score, lower
maximum daily equity drawdown, fewer entries, then C1/C2/C3/B1/R1. A selected
profile can lose money; best of five does not mean globally optimal or proven.

Freeze source, settings, exact ranking and development file/data hashes before
Friday. Create one fund/ticker-named experimental script per eligible selection
and verify it matches its candidate on development data. Run Friday once with
that frozen profile. Friday never changes a script or ranking. It must meet the
same coverage/boundary rule and finish warmup to count as performance validation.

If a fund is ineligible, record no winner and provide unchanged C1 as an explicitly
unvalidated research starter. Choose this fallback by protocol, not by P&L. Its
Friday run checks availability/warmup only, not a selected strategy's performance.
Do not shorten warmup or choose a later winning candidate to conceal a shortfall.

## Deliverables

Install named scripts in the user's existing Strategies folder, remove only
temporary candidates owned by this experiment, and restore initial app settings.
Provide daily P&L and data-quality tables, formulas and original sources, cost
sensitivity, drawdown, ending exposure, exact audits and reproduction commands.
Record relevant fund structure/corporate-action context with primary sources.
Commit and push validated milestones under the user's standing authorization for
long-running autonomous work. Real-time Paper testing remains future work.
