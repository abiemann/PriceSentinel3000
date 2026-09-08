# Three stocks below $25: experiment protocol

Requested September 6, 2026 Pacific. Find three promising experimental stock
profiles in the $3.50-$25 range for Tuesday September 8 Paper testing. Research
uses the last completed trading week, August 31 through September 4, 2026, and
the existing app without adding application features or placing real orders.

## Frozen universe and candidate rules

The initial universe is SOFI, F, RIVN, SNAP, NU, CCL, AAL, and PINS. Public dated
daily histories put their September 4 closes inside the requested price band
and their average daily share volumes above ten million during the week. The
screen considers liquidity and several business sectors; it is not a search of
every listed stock. Nu is a U.S.-listed foreign issuer. The stock list, five
candidate scripts, and selection rules will be committed before new Replay
P&L is inspected. No replacements or extra parameter trials will be introduced
after seeing development results.

Five original, price-only candidates run on each eligible stock:

| ID | Pattern | Fixed parameters |
| --- | --- | --- |
| C1 | Confirmed trend pullback, NFLX baseline | EMA 8/21, RSI 7, entry 50, exit 50 |
| C2 | Confirmed trend pullback, original exit | EMA 8/21, RSI 7, entry 50, exit 45 |
| C3 | Stronger momentum confirmation | EMA 8/21, RSI 7, entry 55, exit 50 |
| B1 | Prior-channel breakout with trend filter | Prior 20-bar high, EMA 21, prior 10-bar low exit |
| R1 | Oversold recovery toward the mean | RSI 7 crossing above 35 below SMA 20; exit at SMA 20 or RSI below 25 |

Exact source hashes, inputs, and compiler warmup requirements are retained in
the candidate manifest. No unsupported studies or copied community code are
used. The host controls sizing and every risk override. Candidate warmup differs
by formula and is included in the experiment rather than bypassed.

## Fixed sessions and evidence

Attempt every Monday-Thursday development date for all eight stocks. Robinhood
previously returned no Monday or Tuesday history in the MAG7 experiment; retry
these dates for this universe and record actual availability. No-history
responses are unavailable, never zero profit. Run all five candidates on each
available development date using identical source data. Retain partial histories
and their gap diagnostics, but a stock requires at least two development days,
each with at least 98% of the expected 1,560 observations and the exact requested
start/end boundaries, to qualify for the three final selections.

Each daily Replay covers 06:30-13:00 Pacific (UTC-07:00 on these dates), using
completed one-minute strategy candles and the provider's 15-second source bars.
Start each day with a fresh $1,400 paper account, fixed $500 positions, immediate
settlement, no entry or share-count cap, a 1% position stop, a $50 daily loss
limit, 15-minute buffer, 5-second quote polling, and existing re-entry controls.
Reconciliation settings remain 45/900/30 seconds. Fast Replay changes pacing
only. No future bars, fabricated gaps, or warmup preloading are allowed.

Only the coordinating agent controls the visible app. Use its MCP interface and
export each complete session before starting another: exact raw status/results,
pinned source/settings, source and strategy candles, indicators, all paged
events, risk overrides, fills, and account snapshots. Check process/operation/
session identity and fail on truncated streams, unexplained failures, or source
changes between candidates. Preserve raw JSON numeric precision. Reconstruct
cash, quantity, realized/unrealized P&L, fills, OHLC aggregation, and drawdown
independently using decimal arithmetic.

## Objective, selection, and validation

Daily gross P&L is final equity minus $1,400, including any open position marked
at the final observed price. Replay does not force liquidation at 13:00 and does
not model spread, fees, or slippage. Report ending exposure separately.

The primary development score subtracts a fixed **5 basis points per side**
from recorded traded notional. Also subtract the same assumed cost on the ending
position's marked notional as a closing-cost allowance. This is a static-ledger
cost sensitivity, not a simulated liquidation or an execution prediction; costs
can change real risk decisions and trade paths. Retain gross P&L and sensitivity
at 0, 1, 2.5, 5, 10, and 25 bps, plus $0.005 and $0.01 per share per side.

For each stock, choose the candidate with the highest unrounded summed primary
score across its eligible development dates. Require at least one entry across
those dates. Break ties by higher worst-day primary score, lower maximum daily
equity drawdown, fewer entries, then candidate order C1, C2, C3, B1, R1. All five
candidates must have the same dates and source hashes; incomplete comparisons
cannot produce a winner.

Rank stock winners in two tiers: first those with a positive primary score on
every eligible development day, then all other eligible winners. Within each
tier sort by mean daily primary score, higher worst-day primary score, lower
maximum daily drawdown, then ticker alphabetically. Select the first three and
publish their full development evidence. If fewer than three meet the first
tier, explicitly label the weaker selections. If fewer than three stocks meet
data/activity eligibility, report the shortfall rather than inventing results.
The best tested candidate is not a global maximum or a proven market advantage.

Commit the selected tickers, formulas, thresholds, source/data hashes, and
development ranking before running Friday. Create one company/ticker-named
experimental file per winner, verify its development behavior matches the
selected candidate, and then run September 4 without changing the selection.
Friday is held out from script tuning and P&L ranking; its public daily prices
and volumes were already used for universe screening, so it is not a completely
unseen market sample. Do not replace losers or retune after Friday.

## Deliverables and Tuesday

Install the three final scripts in the user's existing strategy folder and
remove only temporary candidates owned by this experiment. Supply a daily P&L
and drawdown report, gross/cost comparison, exact audit/manifest, and an
interactive comparison of the selected equity paths. Include Tuesday's
company-event context and a concise Paper acceptance checklist covering real
quotes, candle timestamps, warmup, signals, risk overrides, fills, and stop/start.
Tuesday real-time testing and release publication remain future joint work.

Commit and push validated research milestones under the user's standing
authorization. Preserve earlier uncommitted script-naming and NFLX report edits.
