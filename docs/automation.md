# Local app control

PriceSentinel can expose its visible desktop workspace to an assistant through
MCP or a JSON command line tool. Both control the same running app, session,
chart, risk controls, and journal. This is separate from the app's existing
Robinhood MCP client.

Automation supports **Replay and Paper Trader**. It cannot select, arm, stop,
or modify LIVE sessions, submit broker orders, or read login credentials.

## Build and open

From a checkout with the .NET 10 SDK:

```powershell
.\tools\publish-automation.ps1
Start-Process .\artifacts\automation-publish\PriceSentinel3000.exe -ArgumentList '--automation'
$control = '.\artifacts\automation-publish\automation\PriceSentinel3000.Control.exe'
& $control --command status
```

The app opens in OFF with **Automation enabled (Replay / Paper)** in its title.
This launch mode permits inspection and configuration without logging in first.
Starting a connected data session may require the user's secure browser login.
Replay with usable local history or explicit library pins works without login. The control executable never opens the app or
authenticates a broker itself.

Normal launches do not expose automation. Only one app can own the default
endpoint for the current Windows user. For an explicitly separate instance,
launch the app with `--automation --automation-pipe MyTestInstance` and pass
`--pipe MyTestInstance` to the companion. Other instances and Windows users are
not selected automatically. Closing the app closes its endpoint.

The published companion is included in the `automation` subfolder of new Windows
release packages. An older app that is already running must be reopened from an
updated build to gain these controls; installing/configuring the companion alone
cannot add a server to an old process.

## MCP connection

Register the companion executable as a local **stdio** MCP server, passing
`--mcp`. For Codex, for example, substitute the absolute published path:

```powershell
codex mcp add pricesentinel -- 'D:\Projects\PriceSentinel3000\artifacts\automation-publish\automation\PriceSentinel3000.Control.exe' --mcp
```

Codex supports local stdio MCP servers and shares their configuration among its
local clients. See the [official Codex MCP documentation](https://developers.openai.com/codex/mcp)
for configuration and connection management. The desktop app must be open with
automation enabled before tools can inspect or change it. The CLI below is also
usable immediately, including before a client has refreshed its MCP tool list.

## Commands and tool names

All command arguments are JSON objects. Optional arguments may be omitted.
MCP tools expose the same inputs as named parameters.

| Command/tool | Purpose |
| --- | --- |
| `status` | Read operation state, app mode, startup/running/paused flags, progress, settings, and strategy identity. |
| `list_strategies` | Read compatible strategies and diagnostics; `refresh: true` rescans while idle. |
| `configure` | Apply `mode` (`Replay` or `PaperTrader`) and a partial `settings` object while idle. |
| `start` | Start the configured session and return its operation identity promptly. |
| `pause` | Pause a running Replay. |
| `resume` | Resume paused Replay, optionally setting a new pause boundary and pacing. |
| `step` | Process exactly one additional source observation in paused Replay. |
| `stop` | Stop the simulated session, including startup or paused Replay. |
| `run_to_end` | Remove pause boundaries and playback delays for the active Replay. |
| `results` | Read current/last simulated session identity, outcome, settings/provenance, numeric account, journal summary, and bounded recent decisions/fills. |
| `candles` | Page exact processed source observations or finalized strategy candles, with UTC timestamps and availability times. |
| `indicators` | Read actual strategy indicator values, evaluation identity, and current warmup state. |
| `events` | Page strategy proposals, host risk overrides, decisions, fills, and numeric account state in processing order. |
| `library_datasets` | Page validated local dataset metadata; pass the returned `catalogHash` on subsequent pages. No session or login required. |
| `library_candles` | Page exact immutable local candles by `datasetHash`, with decimal strings, nullable volume and provenance. Maximum 100 rows per page. |
| `capture_chart` | Return the actual simulation chart as a PNG image with its visible range, display interval, RSI, and session metadata. |

The pipe protocol uses `strategies` internally; the command line accepts
`list_strategies`. Unknown commands, arguments, settings, and invalid values are
rejected. The complete candidate configuration is validated before applying
settings. Strategy compatibility is rechecked when starting the actual session.

Strategy descriptors include nullable `testedCandleIntervalSeconds` from the
script's [interval metadata](strategy-scripting.md#tested-candle-interval).
When `configure` selects a different annotated script and omits
`scriptBarIntervalSeconds`, it applies the declared interval. An explicit interval
overrides that default; keeping the same script preserves the current interval.
`status` reports `testedCandleIntervalSeconds` and `scriptIntervalWarning` for
Paper/Replay configuration. Session provenance records both the tested and actual
candle intervals. A mismatch warns without blocking a deliberate experiment.

`start` and `resume` accept Replay-only options:

- `pauseAfterObservations`: positive number of **additional** source observations.
- `pauseAfterStrategyBars`: positive number of **additional** completed script
  candles. Requires an external strategy; Built-In has no script candle counter.
- `fast`: remove Replay pacing when true; use the configured playback speed when
  false. `start` defaults to false; `resume` preserves pacing when omitted.

Specify at most one pause counter. For one-minute script candles and contiguous
15-second history, four observations form a completed script candle; one-minute
source history needs one observation. Read the actual source interval instead
of assuming 15 seconds. Data gaps can change that relationship. Counters are checked after the complete strategy,
paper-account, and journal update, before reading the next source observation.
If the requested boundary reaches or exceeds the available history, Replay
completes. Stepping the final observation also completes the session.

Fast Replay still processes every observation in order with its source timestamp.
It does not invent ticks, skip risk checks, or expose future bars to a script.
Historical loading still takes time. Paper Trader remains paced by real market
data; Replay pacing arguments are rejected in Paper mode.

Replay tries `15second`, `30second`, then `minute` history for the exact requested
range, advancing only when no complete, usable bars remain. Null/interpolated
bars are discarded. Invalid prices and provider errors propagate, and the first
usable resolution is kept for the entire run without combining resolutions or filling strategy
gaps. The two-minute source limit does not imply a two-minute API request:
Robinhood MCP currently offers one minute followed by five minutes, so fallback
stops at one minute. A script interval that is not an exact multiple of the
source interval blocks startup; automation never changes that interval for you.

`status`, `results`, `indicators`, and `capture_chart` expose retained
`replayHistory` metadata. The same object is stored as `ReplayHistory` in journal
session settings, with fields `SourceIntervalSeconds`, `IsFallback`,
`Availability: "source-candle-close"`,
`ExecutionModel: "completed-source-candle-close"`, and
`IntrabarPricesAvailable: false`. These fields are additive to protocol version
1. Match the session ID before comparing retained results with a new operation.
Chart choices remain compatible with the actual source duration, including after
completion; a preferred smaller chart interval can be used with a later finer feed.

## Example: pause, step, and finish

```powershell
& $control --command list_strategies --arguments '{"refresh":true}'
& $control --command configure --arguments '{"mode":"Replay","settings":{"symbol":"SOFI","strategyId":"script:originalconfirmation.thinkscript","scriptBarIntervalSeconds":60,"replayDate":"2026-09-04","replayTime":"06:30","replayEndTime":"10:00","startingBalance":1400,"positionSizeBasis":"FixedAmount","positionSizeValue":500,"unlimitedEntries":true,"maximumDailyLossBasis":"FixedAmount","maximumDailyLossValue":50,"stopLossBasis":"PurchasePriceDeclinePercentage","stopLossValue":1}}'
& $control --command start --arguments '{"pauseAfterStrategyBars":98,"fast":true}'
& $control --command status
# Inspect status until paused is true (or the operation fails/completes).
& $control --command step
& $control --command results
& $control --command run_to_end
# Inspect status until the operation completes, then collect results.
& $control --command results
```

Replay dates/times use the computer's local time zone, just like the UI. Read the
strategy list instead of assuming a copied script has a particular identifier.
Configuration changes are saved to the app's normal preferences.

A successful `start` response means the request was accepted; it does not mean
the connection/history load succeeded. Inspect `operationState`,
`operationError`, and `starting`/`running`/`paused` in subsequent status responses.
Results keep the last simulated session's identity, so check the session ID when
comparing a failed new startup with a previous completed run. Up to 200 recent
decision/fill records are returned; the SQLite journal retains the full history.

The CLI prints one structured JSON response and returns exit code 0 on success,
1 on app/transport failure, or 2 for malformed command line input. Errors contain
an error code and message. A timed-out mutation may have reached the app: inspect
status before retrying. The transport uses one bounded request per connection,
a 30-second request timeout, and current-user-only Windows named pipes.
On Windows, a coding sandbox may run under a separate user identity. It must use
the ordinary Windows user context to connect to the desktop app; the bridge does
not relax its pipe permissions for a sandbox account.

## Research data and chart images

The four research tools are read-only. They observe the same Replay or Paper
session that the app processes; requesting data never runs the strategy again.
Prices and indicator values are JSON numbers at their stored decimal precision,
not rounded display strings. Timestamps include UTC offsets. Replay's loaded but
unprocessed future history is never included.

`candles` accepts `kind` (`strategy`, the default, or `source`), `afterSequence`
(default `0`), `limit` (default `50`, range `1`–`100`), and optional `sessionId`.
The two streams have different meanings:

- **Source:** Replay records contain the provider's OHLC, original source start,
  and actual `intervalSeconds`. `endsAtUtc`, `availableAtUtc`, and
  `evaluationTimestampUtc` use the source candle's actual end for both scripts
  and Built-In. Risk checks and simulated fills occur at that close; they do not
  infer intrabar price paths or stop crossings. Paper records are
  labeled `sampled_quote`; they do not claim to be authoritative OHLC candles or
  have a completed candle end time. Bid/ask and freshness remain explicit.
- **Strategy:** Finalized candles use the selected script interval and contain
  exact open, high, low, close, volume, start/end times, bar version, and the
  source observation sequence that made them available. The stream never includes
  a forming script candle. Built-In does not use script candles, so this stream
  is empty for Built-In.

`events` accepts the same paging arguments except `kind`. Each event links to
the source `observationSequence` and, when applicable, `evaluationSequence`.
`strategyProposal` is the actual decision from the strategy adapter before host
risk handling; `decision` is the final host decision. `riskOverride` explains
host intervention. A risk check can preempt the strategy entirely, in which case
`strategyEvaluated` is false and no proposal is invented. `scriptEvaluated`
distinguishes a new script calculation from waiting for another completed bar.
Only the event that actually ran the script embeds `scriptEvaluation`; waiting
events can reference its evaluation sequence. Orders, fills, and account state
remain attached to their actual processing event.

`indicators` accepts optional `sessionId` and returns the pinned strategy
identity, the latest actual evaluation, and `currentWarmup`. For scripts, warmup
reports required/retained/remaining bars, readiness, monotonic completed-bar
count, bar version, and the retained history start. A gap can reset the history
used for warmup even though the completed-bar count continues increasing. Input
defaults are included in the pinned strategy identity. Named script values cover
definitions/plots actually evaluated by the interpreter; an unused or unreached
expression is not calculated just for inspection. The indicator list is limited
to 64 declarations and reports its total count and truncation. Values carry
`available`, `unavailable`, `warming_up`, or `not_evaluated` state; unavailable
values stay null.
The pinned metadata includes the source hash, runtime, inputs, and candle
interval; the full source remains in `results` instead of being duplicated here.
An indicator snapshot exceeding 900 KiB returns warmup/timing metadata with
`omitted: true` and an explanation.
The latest evaluation can precede the newest source observation or current
warmup state when the engine is waiting or host risk handling preempts it.
Built-In reports its own RSI/momentum values and warmup state separately.

Both page tools return `records`, `nextSequence`, `hasMore`,
`firstAvailableSequence`, `truncated`, and `sessionId`. Pass `nextSequence` as
the next request's `afterSequence` and keep the same session ID. A mismatched
session ID fails instead of mixing runs. Each stream retains at most 4,096
records and 12 MiB in memory; pages also have a 600 KiB record-data limit, so a
page may contain fewer than `limit` records. An individual oversized record is
replaced with an explicit `omitted` placeholder. `truncated` means the requested
cursor predates the retained window. Completion preserves the window; a new
simulation replaces it. These pages are not an unbounded journal export.
Pause Replay while collecting a stable comparison across tools.

For example, inspect a paused run using the same retained session ID:

```powershell
$sessionId = (& $control --command results | ConvertFrom-Json).result.sessionId
$pageArgs = @{ kind = 'strategy'; afterSequence = 0; limit = 50; sessionId = $sessionId } | ConvertTo-Json -Compress
& $control --command candles --arguments $pageArgs
$readArgs = @{ sessionId = $sessionId } | ConvertTo-Json -Compress
& $control --command indicators --arguments $readArgs
& $control --command events --arguments $readArgs
```

`capture_chart` accepts optional `maxWidth` and `maxHeight`. Their default and
maximum values are 1280 and 900 pixels; both must be positive. The app preserves
aspect ratio and may reduce the image further to meet its transport byte limit.
The capture contains the actual chart visual, including its independently
selected candle interval and RSI panel, rather than surrounding account panels
or desktop windows. Metadata identifies the capture time, session, symbol,
visible time range, chart interval, RSI period/value, and dimensions.

MCP returns a native image content block and separate structured metadata; it
does not duplicate the base64 image in its text or structured response. The JSON
CLI returns the PNG as base64 in `result.data` with `result.mimeType`. Chart RSI
can use a different period and interval from the script's indicators, and the
image can include forming/display candles. Use `candles` and `indicators` for
exact strategy inputs; use captures to check drawing, clipping, and labels.

## Testing limits

Real-time Paper Trader validation during an open market remains outstanding;
passing historical Replay checks does not complete it. The remaining release
checks are tracked in [TODO](../TODO.md#next-release-verification).

The [MCP telemetry validation](../archive/app/automation-telemetry-validation-2026-09-06.md)
records exact candle aggregation, indicator warmup, event correlation, host
risk overrides, and native chart captures against the running app.

The [September 7 fallback verification](../archive/app/history-fallback-validation-2026-09-07.md)
retrieved 390 real one-minute NFLX candles
for August 24, 2026, 06:30–13:00 Pacific, where finer history was unavailable.
Automated coverage verifies source-close timing, compatible aggregation,
incompatible-script rejection, source-duration provenance, and migration of
existing journal observations with their 15-second default.

The [September 6 MCP Replay validation](../archive/app/automation-validation-2026-09-06.md)
records ten passing test groups against the running desktop app, including risk
exits, exact Replay boundaries, source pinning, and startup recovery.

The automated suite uses fake market/broker ports, temporary journals, the actual
WPF dispatcher, named pipes, and an official MCP client. It checks exact Replay
boundaries, normal/fast equivalence, lifecycle cancellation, malformed requests,
LIVE rejection, and retained results without placing real orders.

MCP readouts validate session behavior. They do not prove that every WPF control
renders correctly. Keep a visual smoke test for chart drawing, clipping, labels,
and layout. Comparing runs also requires identical source history, settings, and
strategy hashes; separately fetched provider history may have changed.

See [Local market-data library](market-data-library.md) for folder sharing, collection setup and offline Replay. Library tools accept no filesystem paths and may be read while LIVE is selected; they cannot trade or modify saved files.
