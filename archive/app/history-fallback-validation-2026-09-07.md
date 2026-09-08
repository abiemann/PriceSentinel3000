# Historical Replay fallback validation — September 7, 2026

The reported NFLX August 24 no-history case now loads 390 genuine one-minute
OHLCV candles for 06:30–13:00 Pacific. No 15-second observations are invented.
The source interval remains visible in Session Status, SQLite, and MCP output.

## Provider verification

The authenticated Robinhood Agentic historical tool currently accepts
`15second`, `30second`, and `minute` within the two-minute ceiling. It has no
`2minute` interval; the next supported interval is `5minute`, which is excluded.
The exact August 24 range returned 1,560 interpolated 15-second bars, then 780
interpolated 30-second bars, then 390 genuine one-minute bars. Interpolated bars
do not count as usable data. No fixed retention period is assumed.

An independent comparison checked every recovered timestamp, open, high, low,
close, and volume against the raw one-minute API response. All 390 source
candles and all 390 finalized one-minute strategy candles matched exactly.
The first source candle begins at 13:30 UTC and becomes available at 13:31 UTC;
the last begins at 19:59 UTC and becomes available at 20:00 UTC.

## Automated checks

`dotnet build PriceSentinel3000.sln --configuration Release --no-restore --warnaserror`
passed with zero warnings and errors. All 557 solution tests passed:

| Project | Passed |
| --- | ---: |
| Core | 290 |
| Application | 58 |
| Infrastructure | 132 |
| App | 63 |
| Control | 14 |

Coverage includes fallback order, exact range filtering, incomplete/future bars,
empty and interpolated responses, provider errors, cancellation, mismatched
response intervals, duration-aware aggregation, source-close timing for both
Built-In and scripts, incompatible-script rejection before session creation,
close-only risk checks, chart interval compatibility, and journal migration and
round trips. Existing journal observations keep their 15-second default.

## Running-app MCP checks

Used the installed **Netflix (NFLX) Confirmation - experimental** script,
SHA-256 `3ec150e2efdbc454a28972250c53d18cb0c33970bc93f9536823913dc91317fe`,
without changing its source. Settings: $1,400 starting balance, $500 positions,
immediate settlement, unlimited entries, $50 daily loss limit, 1% stop loss,
one-minute script candles, and 06:30–13:00 Pacific.

| Check | Observed result |
| --- | --- |
| Request a 15-second script against recovered one-minute history | Startup rejected with an interval explanation; no session or fill created and the script interval stayed unchanged. |
| Pause after 84 one-minute source observations | Exactly 84 source/strategy candles exposed; warmup required one more candle. Querying later source records returned none. |
| Step once | Exactly 85 processed; indicators became ready at the expected 85-candle warmup boundary. |
| Resume with paced playback and pause after three more observations | Stopped at 88 with account and strategy state preserved. |
| Finish the full day | 390 sources, 390 strategy candles, 390 decisions, 14 fills, and a flat final position. |
| Repeat the same day entirely in fast mode | All 14 fill timestamps, prices, quantities and realized amounts matched; final account and all 200 retained decision records matched. Total decision counts were 390 in each run. |
| Independently check fill availability | Every fill timestamp equals an actual source candle close and every fill price equals that candle's close. |
| Capture the chart after fallback with a preferred 15-second display | Effective chart interval was 60 seconds; capture metadata recorded the actual source interval and fallback model. |
| Run NFLX September 4 as a recent-history control | 1,560 genuine 15-second source candles and 390 one-minute strategy candles; `IsFallback` was false and the chart returned to 15 seconds. |

The paused/stepped run has six extra activity journal entries, as expected from
its control actions. These do not change trading results. The August 24 runs
both ended at $1,398.1415676113, with realized P&L of -$1.8584323887; the September
4 control ended at $1,400.724905705. These are functional checks of unchanged
scripts and the app's existing fill model, not optimization or profitability
claims. Coarser source data cannot reproduce intrabar stop crossings or prices.

Native window inspection confirmed the additional Source data row fits in the
status panel. MCP chart inspection confirmed genuine source candles and the
recorded display interval. Replay remained simulated with broker execution
disabled throughout; no LIVE or open-market Paper test was performed.

Raw market-only responses and validation output are retained locally under
`artifacts/history-fallback-probe/` and `artifacts/history-fallback-validation/`.
The latter includes `mcp-validation.json`, `recent-history-control.json`,
`verify_evidence.py`, and successful `history-fallback-host` TRX files.
