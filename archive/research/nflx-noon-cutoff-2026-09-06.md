# NFLX: 06:30–12:00 Pacific, August 31–September 4, 2026

Follow-up to the [two start-time experiments](nflx-week-start-times-2026-09-06.md).
All five requested dates were attempted again through PriceSentinel MCP using
the same NFLXConfirmation script. Three Replay sessions completed; August 31
and September 1 again returned no usable history for the requested window.

## Daily P&L

Every run resets to $1,400 cash with fixed $500 positions. P&L is final equity
minus starting balance. All completed runs ended flat at noon, so P&L was
entirely realized; there was no forced liquidation. Costs are not modeled.

| Date | P&L, 06:30–12:00 | Entries / exits | Previous P&L, 06:30–13:00 |
| --- | ---: | ---: | ---: |
| Monday, Aug 31 | Unavailable | — | Unavailable |
| Tuesday, Sep 1 | Unavailable | — | Unavailable |
| Wednesday, Sep 2 | +$0.76 | 2 / 2 | +$1.06 |
| Thursday, Sep 3 | +$2.52 | 5 / 5 | +$2.97 |
| Friday, Sep 4 | +$0.72 | 3 / 3 | +$0.72 |
| **Available-day subtotal** | **+$4.00** | **10 / 10** | **+$4.76** |

Exact noon-cutoff P&L: $0.7605557115, $2.5192376360, and $0.7249057050;
subtotal $4.0046990525. Subtotals sum unrounded, independently reset daily
results and are not full-week or compounded returns. Missing dates are not
treated as zero P&L; their attempts produced no simulation session.

Ending at noon removed one later completed trade on Wednesday (+$0.3025718500)
and one on Thursday (+$0.4535284500) from the prior full-day results. Friday had
no later fills. The available-day difference is $0.7561003000 less P&L with the
noon cutoff. This is a timing comparison on previously tested dates, not a new
out-of-sample evaluation or a recommendation of an optimal trading window.

## Fixed conditions

- [Netflix (NFLX) Confirmation - experimental.thinkscript](<../../research/strategies/Netflix (NFLX) Confirmation - experimental.thinkscript>), unchanged
  SHA-256 `3ec150e2efdbc454a28972250c53d18cb0c33970bc93f9536823913dc91317fe`.
- EMA8/EMA21, RSI7, entry strength 50, exit strength 50; one-minute script bars.
- $1,400 fresh starting balance, fixed $500 positions, unrestricted quantity
  and entries, immediate paper settlement, 1% stop, $50 daily loss, 15-minute
  buffer, and existing host re-entry protections.
- A fresh Replay begins at 06:30 Pacific and ends at 12:00 Pacific. The normal
  85-candle warmup first completes at 07:55. Missing source observations restart
  warmup; there is no synthetic filling of gaps.
- Robinhood split-adjusted 15-second historical bars. The simulator uses sampled
  historical closes without spread, fees, slippage, or intrabar stop execution.
- Running app `1.2-dev.10`, runtime `thinkscript-subset-v1`, data model
  `completed-price-bars-v1`. No code, script inputs, or other settings changed.

## Data and checks

| Date | Source observations | Completed strategy bars | Missing source bars | Max equity drawdown |
| --- | ---: | ---: | ---: | ---: |
| Sep 2 | 1,317 | 327 | 3 | $0.8469438600 |
| Sep 3 | 1,320 | 330 | 0 | $1.7925069890 |
| Sep 4 | 1,320 | 330 | 0 | $1.3278243170 |

All successful source streams begin at 06:30 and end with the 11:59:45–12:00
candle. No source data closing after noon was included. Wednesday's missing
15-second bars begin at 08:33:15, 09:14:30, and 11:13:45 Pacific, matching the
earlier full-day data. Monday and Tuesday returned no usable history; the reason
for the provider's historical-data unavailability remains unconfirmed.

The fresh noon runs were compared against the earlier 06:30–13:00 runs through
noon, checking source prices, strategy candles, decisions, fills, and accounts.
Generated order IDs and wall-clock observation timestamps are run-specific.
All paginated source, strategy, and event records were retained before starting
another session; no research stream was truncated.

Independent review found no mismatches in any of the three runs and recomputed
every event's account state and fill accounting from the recorded prices.

Raw MCP exports, two unavailable-status responses, the protocol, analysis script,
and exact metrics are in the ignored `artifacts/nflx-noon-research` directory.
The app is left idle with Friday's completed noon-cutoff Replay and results.
