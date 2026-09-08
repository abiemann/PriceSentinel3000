# MCP telemetry validation — September 6, 2026

Validated the four new research tools against the running desktop app using an
official C# MCP client, MCP/stdio, the published Control companion, and the app's
user-scoped named pipe. All app configuration, Replay controls, numeric reads,
and chart captures in this validation went through MCP.

The app used real Robinhood historical SOFI observations for September 4, 2026,
06:30–10:00 Pacific. Orders were simulated in Replay. Strategy:
`OriginalConfirmation (experimental)`, SHA-256
`753badb011266ef7e7dcdc2bea8c39b4999b3d7ae23481873f510019b66a9676`.
The script used 60-second candles; the chart used 15-second candles.

| Check | Result |
| --- | --- |
| MCP discovery | All 14 tools discovered, including `candles`, `indicators`, `events`, and `capture_chart`. |
| Exact candles and availability | Paused after 84 strategy candles. Exactly 336 source observations were exposed; their OHLC aggregated exactly into those 84 candles. No later Replay observations appeared. |
| Warmup and cached indicators | At candle 84, required warmup was 85 and values were null. After one more completed candle, all eight named script definitions had values. Repeated reads left the snapshot unchanged. |
| Decision and fill correlation | All 840 baseline decision events were paged successfully. Each of the six fills linked to its order and original strategy proposal. |
| Stop-loss override | At observation 433, the host generated STOP LOSS, with no script evaluation or fabricated proposal. The exit order, actual fill, and resulting account were attached to that event. |
| Session identity | Requests using the previous session ID were rejected after a new simulation started. |
| Native chart images | Three actual chart captures returned native MCP image content with separate metadata. PNGs were decoded and visually inspected; candles, time/price labels, and chart controls rendered correctly. The running app had chart RSI switched off; automated WPF coverage separately verified RSI-on rendering. |

At candle 85 (07:54–07:55 Pacific), the exact strategy OHLC was
18.10 / 18.10 / 18.09 / 18.095. The retained values included:

- `fastTrend`: 18.1083510141198
- `slowTrend`: 18.1154784854074
- `strength`: 37.982496497841

The baseline ended flat after three entries and six fills, with equity
1399.8715905779 and realized P&L -0.1284094221. The $0.25 total-position
stop-loss case ended flat at 1399.586435075. Both exactly matched the earlier
MCP validation; a final repeated baseline matched again. These are verification
fixtures, not evidence of strategy profitability.

Regression validation passed with warnings treated as errors: 228 Core,
46 Application, 113 Infrastructure, 55 App, and 14 Control tests (456 total).
Coverage includes cached-only telemetry, risk preemption, no future candle
access, stale quotes, gap warmup resets, session preservation, 4,096-record and
12 MiB retention, byte-bounded pages, oversized records, and native image
transport. The Release publish completed successfully.

Local raw responses, MCP transcript, test driver, and PNG captures are under
the ignored `artifacts/mcp-validation` directory. The app was left idle with
the completed baseline chart and restored configuration. Research buffers are
bounded in-memory windows and do not survive an app restart; existing SQLite
journal retention is separate.
