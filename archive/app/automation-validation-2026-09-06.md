# MCP Replay validation — 2026-09-06

All ten test groups passed against the running desktop app at source commit
`963ed24`. App actions used the official C# MCP client and the published stdio
MCP server throughout. The run recorded 212 MCP calls, including inspection and
cleanup verification. All trades were simulated in Replay.

## Test configuration

- Symbol: SOFI
- History: September 4, 2026, 06:30–10:00 Pacific local time
- Source observations: 840 historical 15-second observations
- Strategy: OriginalConfirmation (experimental), one-minute strategy candles
- Starting paper balance: $1,400; fixed position amount: $500
- Baseline: unlimited entries, $50 daily loss limit, 1% purchase-price stop loss,
  immediate settlement, and fast Replay

Individual risk tests changed the relevant limit or stop-loss basis. Script
discovery tests used temporary copies of the original and a deliberately
incompatible future-dependent script.

## Results

| Test group | Result |
| --- | --- |
| Invalid configuration and start arguments | PASS: rejected without partial settings changes or an unintended session |
| Exact pause, locked settings, step, resume, and stop | PASS: paused at candle 98 / observation 392; stepped to 393; resumed to 395; stopped with account and session results retained |
| Repeatability, reset, and bounded results | PASS: two complete runs matched fills and account values; 840 decisions recorded, 200 recent decisions returned |
| Final-observation step | PASS: stepping from 839 to 840 completed without an extra observation or duplicate fill |
| One-entry cap | PASS: one entry, flat final position, equity $1,399.45 |
| Daily loss limit of $0.50 | PASS: explicit DailyLoss decision, no later entries, equity $1,399.45 |
| Total-position dollar stop of $0.25 | PASS: explicit StopLoss decision, flat final position, equity $1,399.59 |
| Purchase-price percentage stop of 0.25% | PASS: exited by the strategy's Sell signal; no StopLoss decision; equity $1,399.45 |
| Discovery, incompatible source, pinning, and startup revalidation | PASS: compatible copy listed; future-dependent source excluded; mid-session edits left the pinned session unchanged; the next start rejected the edited file without fallback |
| Recovery after script rejection | PASS: selecting the original strategy again reproduced the baseline fills |

Baseline and recovery finished with 210 completed strategy candles, three
entries, six fills, and a flat position. Final equity was $1,399.8715905779 and
realized P&L was -$0.1284094221, matching the earlier manual run at displayed
precision ($1,399.87 / -$0.13).

Temporary test scripts were removed after checking their ownership and contents.
The refreshed catalog contained Built-In and OriginalConfirmation. Initial
settings were restored, and the app was left idle in Replay with the completed
recovery chart available. No application source changes were needed for this run.

Detailed assertions, MCP transcripts, and per-scenario results were retained
locally under `artifacts/mcp-validation/`; generated artifacts are not tracked.
See [Local app control](../../docs/automation.md) for the supported tools and launch workflow.

## Remaining coverage and research needs

This run validates Replay behavior. Real-time Paper testing during an open market
remains outstanding, and these results do not establish strategy performance
across other periods.

The current MCP interface exposes settings, strategy provenance, account values,
counts, and recent decisions/fills. It does not expose OHLC candles or indicator
values. Future thinkScript evaluation would benefit from read-only candle and
indicator traces, including warmup state and signal timing. Chart images would
support separate visual checks of rendering, labels, and trade markers.
