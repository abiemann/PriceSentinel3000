# USO: frozen full-day script experiment

Requested September 6, 2026 Pacific. Research an original trading script for
United States Oil Fund (USO) over August 31 through September 4, 2026. Use the
existing PriceSentinel MCP interface in historical Replay with simulated money.
No application features or broker execution are part of this experiment.

## Candidates fixed before USO Replay inspection

Reuse five original, already synthetically validated price-only formulas from
the earlier low-price experiment, byte for byte. These are hypotheses to compare
on USO, not strategies claimed to be specific to oil or optimized globally.

| ID | Pattern | Fixed parameters |
| --- | --- | --- |
| C1 | Trend-pullback confirmation | EMA8/21, RSI7, entry50 / exit50 |
| C2 | Trend-pullback confirmation, patient exit | EMA8/21, RSI7, entry50 / exit45 |
| C3 | Stronger momentum confirmation | EMA8/21, RSI7, entry55 / exit50 |
| B1 | Prior-channel breakout | Previous20-bar high, EMA21, previous10-bar low exit |
| R1 | Oversold mean recovery | RSI7 crosses above35 below SMA20; exit at SMA20 or RSI below25 |

The candidate manifest pins exact source hashes and warmup (85 bars for C1-C3,
84 for B1, 51 for R1). Compiler compatibility must pass in the running app.
No copied third-party script or new parameter candidate after inspecting P&L.

## Fixed sessions and availability

Attempt all four Monday-Thursday dates with C1 to establish availability, then
run all other candidates on every available date. Each session requests exactly
06:30-13:00 Pacific (UTC-07:00), with one-minute completed strategy candles and
Robinhood's historical 15-second source bars. Friday is reserved for the selected
candidate only after the selection is frozen; it never changes the selection.

Every day starts a new $1,400 account, fixed $500 positions, immediate paper
settlement, unlimited entries/quantity, a 1% purchase-price stop, a $50 daily
loss limit, 15-minute buffer, five-second quote polling, reconciliation settings
45/900/30 seconds, and the existing host re-entry restrictions. Fast Replay only
changes pacing. No synthetic bars, future observations, or preloaded warmup.

No-history attempts are unavailable, never zero P&L. Keep partial data with its
gaps. Selection requires at least two development days, each with at least
1,529 of 1,560 expected source observations and the exact opening/final boundaries.
Every available date must have all five candidates and matching source hashes.
Report insufficient evidence if eligibility fails; do not manufacture a winner.

## Score, tie-breaks, and validation

Gross daily P&L is final marked equity minus $1,400. Replay does not force a sale
at the ending time; report ending exposure and realized/unrealized P&L separately.
The primary development score deducts 5 basis points from each recorded traded
notional and the same cost on the ending marked position as a closing-cost
allowance. This is a static-ledger sensitivity, not an executed sale, an estimate
of actual costs, or a replay with changed risk decisions. Also report gross and
the existing 0/1/2.5/10/25-bps and per-share sensitivities.

Select the highest unrounded summed primary score across eligible development
dates, requiring at least one entry. Break ties by higher worst-day score, lower
maximum daily drawdown, fewer entries, then C1, C2, C3, B1, R1. An eligible winner
can still lose money; report that plainly. Freeze source, ranking, and data hashes
before Friday. Verify a renamed final script reproduces the selected development
runs, then evaluate Friday without retuning. Friday must meet the same coverage
and boundary rule and actually complete warmup to count as strategy validation.

Only the root agent controls the app. Export each complete session before
starting another, retaining exact JSON numbers, pinned source/settings, candles,
indicators, decisions, risk overrides, fills, account snapshots, stream cursors,
and session identity. Independently verify Decimal accounting, OHLC aggregation,
warmup and gap handling, indicator values and actions, and candidate comparability.

## Deliverables

Create a fund/ticker-named experimental script for an eligible selected profile,
install it in the app's existing Strategies folder, retain reproducible research
and daily P&L, and remove only this experiment's temporary candidates. Restore
the user's starting app settings. Preserve unrelated working-tree changes.
Commit and push validated milestones under the standing authorization for
long-running autonomous work. Real-time Paper testing remains future work.
