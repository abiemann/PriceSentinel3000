# NFLX: two start times for August 31–September 4, 2026

Requested September 6, 2026: run the frozen NFLXConfirmation script for every
weekday of the last completed trading week, first from 06:30–13:00 Pacific,
then from 10:00–13:00 Pacific. All ten windows were attempted through the app's
MCP interface. Six runs completed; four returned no usable history.

## Daily P&L

Each run starts with a new $1,400 paper account and fixed $500 positions. P&L is
final equity minus starting balance. Every completed run ended flat, so these
figures are also the realized P&L. No spread, fees, or slippage are modeled.

| Date | Stage 1: 06:30–13:00 | Stage 2: 10:00–13:00 |
| --- | ---: | ---: |
| Monday, Aug 31 | Unavailable | Unavailable |
| Tuesday, Sep 1 | Unavailable | Unavailable |
| Wednesday, Sep 2 | +$1.06 | +$0.30 |
| Thursday, Sep 3 | +$2.97 | +$0.45 |
| Friday, Sep 4 | +$0.72 | -$0.70 |
| **Subtotal of available days** | **+$4.76** | **+$0.06** |

Subtotals use unrounded values. They sum independently reset daily results and
are not full-week or compounded returns. Stage 1's exact subtotal is
$4.7607993525; stage 2's is $0.06108554. No zero-profit result was substituted
for missing history.

Robinhood returned no usable NFLX 15-second observations for both requested
windows on August 31 and September 1. The app reported `Robinhood returned no
NFLX trades...`, created no simulation session, and supplied no P&L for those
attempts. The reason for that historical-data unavailability was not established;
there is no confirmed retention limit in this experiment. Calculating the two
missing dates requires a compatible historical source for those windows.

## Warmup and session interpretation

Stage 2 is a fresh app session starting at 10:00. Indicator history, cash,
positions, and prior-trade state reset. It does not preload morning candles.
NFLXConfirmation requires 85 completed one-minute candles before it can act.
Thus an uninterrupted 06:30 start first becomes eligible at 07:55, and an
uninterrupted 10:00 start at 11:25. Missing source bars reset this warmup.

Wednesday's stage 1 history has three missing 15-second observations, starting
at 08:33:15, 09:14:30, and 11:13:45 Pacific. Stage 2 includes the last of these
gaps, which occurs before initial warmup completes; its first eligible strategy
evaluation is delayed until 12:39. Thursday and Friday have no source gaps in
either requested window.

| Date | Stage | Source observations | Source gaps | First ready, Pacific | Entries / exits | Exact P&L | Max equity drawdown |
| --- | --- | ---: | ---: | --- | ---: | ---: | ---: |
| Sep 2 | 1 | 1,557 | 3 | 07:55 | 3 / 3 | +1.0631275615 | 0.8469438600 |
| Sep 2 | 2 | 719 | 1 | 12:39 | 1 / 1 | +0.3025718500 | 0.7261724400 |
| Sep 3 | 1 | 1,560 | 0 | 07:55 | 6 / 6 | +2.9727660860 | 1.7925069890 |
| Sep 3 | 2 | 720 | 0 | 11:25 | 1 / 1 | +0.4535284500 | 0.4831589754 |
| Sep 4 | 1 | 1,560 | 0 | 07:55 | 3 / 3 | +0.7249057050 | 1.3278243170 |
| Sep 4 | 2 | 720 | 0 | 11:25 | 1 / 1 | -0.6950147600 | 0.8213810800 |

Stage 1 placed 12 entries across available days; stage 2 placed three. This
comparison includes the consequences of fresh indicator warmup and reset
prior-trade state. It does not isolate a rule that merely disallows entries
before 10:00 while keeping indicators warmed from the morning.

Friday demonstrates the effect of prior-trade state. Both stages proposed BUY
at 11:26 at $79.135. Stage 1 had sold at 11:25 at $79.085, so the host blocked
re-entry: the price had moved only about 0.0632%, below its 0.10% requirement.
Stage 2 had no previous sale and took the trade, then sold at 11:29 at $79.025
for -$0.695014760. Both scripts produced the same entry signal at 11:26.

## Fixed settings and provenance

- Script: [Netflix (NFLX) Confirmation - experimental.thinkscript](<../../research/strategies/Netflix (NFLX) Confirmation - experimental.thinkscript>).
  No source or input changes were made during this experiment.
- Source SHA-256:
  `3ec150e2efdbc454a28972250c53d18cb0c33970bc93f9536823913dc91317fe`.
- Inputs: EMA lengths 8/21, RSI length 7, entry strength 50, exit strength 50.
- Symbol NFLX; starting balance $1,400; fixed $500 position sizing; unrestricted
  quantity and number of entries; immediate paper settlement; 1% position stop;
  $50 daily loss limit. Existing host re-entry protections remain active.
- One-minute strategy candles; 15-minute buffer; 15-second source OHLC candles.
  Fast Replay skips wall-clock delays without changing observation order.
- Local timezone: `Pacific Standard Time`, observing PDT (UTC−07:00) on all dates.
- Running app `1.2-dev.10`; script runtime `thinkscript-subset-v1`;
  data model `completed-price-bars-v1`; Robinhood split-adjusted history.

## Evidence and verification

The ignored `artifacts/nflx-week-research` folder contains six complete MCP JSON
exports, four unavailable-status responses, the recorded protocol, analysis
script, and metrics. Each successful export includes settings, pinned script
provenance, session ID, all processed source/strategy candles, indicator state,
decisions, host risk overrides, fills, and account snapshots. All pages were
captured before moving to the next simulation.

All successful windows end with a source candle closing at exactly 13:00
Pacific. Stage 1 begins at 06:30 and stage 2 at 10:00; no later candle was included.
The data shared by both stages on each date was checked for identical timestamp,
OHLC, and volume values. No retained research stream was truncated.

An independent review reconstructed every event's account state and every fill
from source prices, verified one-minute OHLC/volume from the source candles,
checked the pinned script and fixed settings, and confirmed both P&L subtotals.
No mismatches were found across the six completed runs.

The repeated stage 1 runs exactly reproduced the earlier NFLXConfirmation P&L
for September 2–4. Those dates were already used in the earlier research, so they
are not new out-of-sample days. The script remains experimental. The app is left
idle with Friday's completed stage 2 Replay and its retained chart/results.
