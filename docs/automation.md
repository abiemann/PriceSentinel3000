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
Starting a data session uses the normal Robinhood connection flow and may require
the user's secure browser login. The control executable never opens the app or
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

The pipe protocol uses `strategies` internally; the command line accepts
`list_strategies`. Unknown commands, arguments, settings, and invalid values are
rejected. The complete candidate configuration is validated before applying
settings. Strategy compatibility is rechecked when starting the actual session.

`start` and `resume` accept Replay-only options:

- `pauseAfterObservations`: positive number of **additional** source observations.
- `pauseAfterStrategyBars`: positive number of **additional** completed script
  candles. Requires an external strategy; Built-In has no script candle counter.
- `fast`: remove Replay pacing when true; use the configured playback speed when
  false. `start` defaults to false; `resume` preserves pacing when omitted.

Specify at most one pause counter. For one-minute script candles and contiguous
15-second history, four observations form a completed script candle. Data gaps
can change that relationship. Counters are checked after the complete strategy,
paper-account, and journal update, before reading the next source observation.
If the requested boundary reaches or exceeds the available history, Replay
completes. Stepping the final observation also completes the session.

Fast Replay still processes every observation in order with its source timestamp.
It does not invent ticks, skip risk checks, or expose future bars to a script.
Historical loading still takes time. Paper Trader remains paced by real market
data; Replay pacing arguments are rejected in Paper mode.

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

## Testing limits

The automated suite uses fake market/broker ports, temporary journals, the actual
WPF dispatcher, named pipes, and an official MCP client. It checks exact Replay
boundaries, normal/fast equivalence, lifecycle cancellation, malformed requests,
LIVE rejection, and retained results without placing real orders.

MCP readouts validate session behavior. They do not prove that every WPF control
renders correctly. Keep a visual smoke test for chart drawing, clipping, labels,
and layout. Comparing runs also requires identical source history, settings, and
strategy hashes; separately fetched provider history may have changed.
