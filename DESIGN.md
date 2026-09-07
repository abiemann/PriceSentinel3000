# External strategy design

This specification incorporates decisions through September 7, 2026. Implementation
has been delivered in validated milestones. The [compatibility guide](docs/strategy-scripting.md)
identifies the implemented subset and resource limits; [research results](docs/strategy-research.md)
record the unchanged-source checks and original example fixtures. The existing compiled price-action strategy
remains enabled by default and appears as **Built-In**. Converting it to an
external script is future work.

V1 includes folder discovery, the shared selector, completed-candle interpretation,
session source/parameter provenance, per-session LIVE version approval, and one
original experimental example. Tests cover parser restrictions, indicator values,
host position/risk safeguards, history availability, immutable artifacts, WPF
selection, and sample seeding. Windows publication includes the original source.

## Product scope

- One shared strategy selector for Paper Trader, LIVE, and Replay.
- Built-In first, followed by compatible files in
  `%LOCALAPPDATA%\PriceSentinel3000\Strategies`.
- Users copy a text script into that folder and refresh the list. No project,
  build tool, manifest, or hand-written adapter is required.
- Ship one original external example alongside Built-In. Keep the default
  collection to one or two carefully tested strategies; more is not a goal.
- Existing thinkScript files are accepted unchanged when their syntax and data
  requirements fall within the published subset. Compatibility is established
  by parsing and validation, never by guessing what chart colors mean.
- A running session pins one exact strategy artifact and its timing settings.
  Folder edits take effect only on a later session.
- Script proposals remain subject to every host-owned risk and execution gate.

## Language and compatibility decision

The early specification proposed arbitrary C# source compiled with Roslyn. The
first implementation instead interprets a deliberately bounded subset of
thinkScript. This directly serves the requested existing-script workflow and
avoids granting source code the capabilities of the .NET runtime.

The interpreter parses source into a validated expression tree. It supports only
explicitly implemented arithmetic, Boolean expressions, prior-bar references,
inputs, definitions, indicator calculations, and order-signal declarations.
There is no arbitrary CLR execution, package loading, native interop, reflection,
filesystem, network, UI, process creation, clock access, or uncontrolled randomness.
Unknown syntax and functions are errors. Source size, syntax depth, node count,
history size, and evaluation work are bounded.

This constrained language is the execution boundary. A future arbitrary C#
plugin system would still require the separate restricted worker, OS-enforced
credential isolation, and compilation policies from the original proposal; a
normal child process or an assembly loader would not be sufficient. Arbitrary
`.strategy.cs` execution is outside this first implementation.

The compatibility guide must enumerate:

- Supported statements, operators, constants, functions, and order types.
- Supported price data and the exact indicator smoothing/initialization rules.
- Visual statements that are validated and ignored, with diagnostics.
- Host-controlled order price and size arguments that do not carry over from
  thinkorswim's hypothetical-fill model.
- Unsupported features, including external symbols, secondary aggregation,
  volume-dependent live signals without authoritative volume, future-dependent
  conditions, arbitrary loops, and unsupported recursive definitions.

The public compatibility tests use unchanged downloaded forum examples. Preserve
source URL, author/version when available, raw-source SHA-256, runtime version,
and findings. Public visibility is not a redistribution license: third-party
source without suitable permission stays outside the distributed application and
repository. Tests committed to the repository use original fixtures. A supported
parse is not evidence that a strategy is profitable or that simulated fills
match thinkorswim.

## Market data and timing

Paper Trader observes the same live Robinhood quote path as LIVE at the configured
poll interval. Its orders and account are simulated. Script support must not add
an artificial market-data delay or expose future information to Paper Trader.

Built-In retains its current quote-driven behavior. External bar-based scripts
use an explicit strategy candle interval, independent of the chart display
interval. The initial choices are 15, 30, 60, 120, and 300 seconds, defaulting to
60 seconds. A script receives completed bars only and evaluates once per newly
completed bar. A new quote can complete the preceding bar; it remains the
executable quote for current bid/ask risk and execution checks.

Warm-start history initializes indicators. Live bars are constructed from the
quotes actually observed, so OHLC is sampled at the poll interval rather than
being an exchange tick-complete record. Quote polling must be no slower than the
selected strategy interval. Volume-dependent programs are unavailable until the
feed can supply authoritative volume consistently. A missed period stays a gap;
chart-only synthetic flat candles never become strategy data.

Replay first requests 15-second history for the exact selected range. If there
are no usable, complete observations in that range, it retries at 30 seconds,
then one minute. These are the Robinhood MCP intervals currently supported within
the two-minute source limit: the provider does not accept two-minute requests,
and its next interval, five minutes, exceeds the limit. Null, interpolated,
incomplete, and out-of-range bars do not satisfy a request. Invalid prices,
authentication, transport, and malformed responses remain errors. Fallback
selects the first usable resolution for the entire run; it never widens the range, combines resolutions,
or fills gaps in strategy history.

Historical bars retain their actual duration and start timestamp. Their OHLC
becomes available at the source candle's close for both Built-In and scripts.
Replay evaluates risk and simulates fills at that close; it cannot reconstruct
intrabar prices, stop crossings, or the order of a candle's high and low. Coarser
history can therefore change strategy behavior and P&L. No smaller source bars
are synthesized.

The selected script interval must be an exact multiple of the source interval.
An incompatible interval blocks Replay before a session starts and is never
changed automatically. Chart intervals are restricted to compatible choices.
Session Status shows the actual source duration during and after Replay. The
user's preferred chart interval remains available for a later finer feed. Replay
never includes the next bar's close when evaluating a previous decision.
`open[-1]` may be recognized in an `AddOrder` execution-price argument as the
thinkScript next-bar convention; it is never accessible in the signal expression.
The host determines actual fills using its documented simulation or broker model.

Once a strategy bar has been finalized, its snapshot is immutable for the active
session. Delayed reconciliation can repair chart history without changing prior
script decisions or refiring a completed bar. The same observations in the same
order yield the same strategy proposals across modes, before their different
account, risk, and fill conditions are applied. Different sampling and available
history can legitimately produce different inputs and results.

Required warm-up comes from the compiled program and the selected interval,
within a documented maximum. Insufficient or interrupted history produces a
visible warming-up HOLD. It does not silently use the Built-In strategy.

## Strategy and host contract

The current `IPriceActionSignalEngine` seam can adapt the interpreter to both
execution engines without rewriting the built-in detector. Strategy input contains
only immutable completed price bars and a read-only long-position snapshot.
An external result requests BUY, SELL, or HOLD and supplies an explanatory state
and reason. Host-owned risk decisions remain evaluated independently on fresh
quotes even between script bar closes.

- BUY requests opening a long position. The host rejects repeated BUY while a
  position exists, respects entry limits and cooldowns, and owns sizing.
- SELL requests closing the current long position. It cannot open a short sale
  or exceed authoritative available shares.
- HOLD submits nothing and cannot suppress a host stop or daily-loss exit.
- Conflicting simultaneous entry and exit signals, malformed results, or runtime
  failures block script execution rather than selecting an arbitrary action.
- Scripts cannot submit/cancel broker orders, mutate account state, claim fills,
  or bypass regular-hours, tradability, review, stale-quote, or idempotency gates.

`AddOrder` defines executable intent only for supported long-position actions.
Display-only studies without explicit order conditions are diagnosed as lacking
an actionable strategy. Converting an indicator into a strategy requires the
user or author to define its trading rules; the app will not infer those rules.

## Catalog and selector

The catalog scans only the top level of the strategy folder, with bounded file
counts and per-file size. It accepts `.thinkscript`, `.ts`, and `.txt` text files.
Invalid UTF-8, unreadable files, unsupported programs, and incompatible source
formats receive filename and line diagnostics. Only compatible programs join
Built-In in the selector; diagnostics remain visible nearby.

Provide **Open Scripts Folder** and **Refresh** actions. Refresh when the selector
is opened and revalidate the exact selected file before session start. An absent
or incompatible selection blocks startup with guidance. It must not silently
fall back to Built-In. First launch and old preferences select Built-In.

The same list and selected ID apply to Paper, LIVE, and Replay. Selection and
strategy interval are editable while idle and locked during startup, a running
session, and paused Replay. Changing the chart interval remains independent.

Seed the single packaged original example when the user folder is initialized,
without overwriting an existing file or silently restoring a deliberately removed
example on every refresh. The packaged copy is also available with the app.

## Artifact identity, approval, and journal

Compile during discovery and re-read/revalidate at session start. Pin the actual
UTF-8 source bytes, source SHA-256, runtime version, selected interval, and default
input values for the lifetime of a session. Derived IDs identify files for
selection; source hashes identify exact behavior. Source changes never hot-reload
an active session.

Persist strategy provenance in the journal session settings, which all decisions,
orders, and fills already reference:

- Strategy ID and display name
- Source SHA-256 (or built-in application version identity)
- Interpreter/runtime version
- Input defaults and selected candle interval
- Data/simulation model version

Replay session settings also pin `ReplayHistory`: `SourceIntervalSeconds`,
`IsFallback`, `Availability` (`source-candle-close`), `ExecutionModel`
(`completed-source-candle-close`), and `IntrabarPricesAvailable` (`false`). SQLite
schema migration 4 stores each observation's source duration; existing rows
retain their original 15-second default. These metadata changes do not alter
Paper/LIVE data ingestion or introduce execution costs.

LIVE retains its existing explicit warning, arming, broker reconciliation, and
review flow. Before arming an external source, show the strategy name and exact
artifact identity and require review of that artifact. Editing a script invalidates
any remembered approval. Approval does not establish profitability; source execution
is still limited by the interpreter and every accepted action by the host.

## Failure behavior

Discovery diagnostics cannot crash the application or remove Built-In. Compilation
failure, changed/missing source, invalid interval, or insufficient data must remain
clear to the user. A runtime fault blocks new script entries and is journaled;
host risk checks remain active so a faulty script cannot disable position
protection. Restart requires a deliberate user action after diagnostics are
reviewed. No automatic substitution of another strategy is allowed.

## Original packaged strategy

Research popular public approaches such as Confirmation Candles for high-level
ideas. Develop original source with independently specified rules and parameters;
do not copy a forum script into the packaged example. Begin with a small set of
complementary price-only trend/momentum conditions, explicit long entry and exit,
and a manageable lookback. Document its assumptions and test both positive and
negative cases. Popularity and screenshots are not performance evidence.

Keep Built-In as the default. Label the original external example as experimental
until meaningful out-of-sample and Paper evidence supports stronger claims. The
existing compiled detector may be converted to a script in a later milestone
with decision-equivalence tests.

## Implementation milestones and verification

1. **Design:** document the compatibility subset, shared selector, original-source
   policy, data timing, and milestone acceptance criteria; commit and push.
2. **Runtime and host contract:** implement bounded parsing/evaluation, indicator
   semantics, compatibility diagnostics, and repeated-entry protection. Validate
   unchanged external examples locally plus original deterministic fixtures,
   future-data rejection, limits, and host risk tests; commit and push.
3. **Catalog and application:** add folder discovery, packaged sample seeding,
   Built-In default, shared selector, pinned artifacts, completed-bar ingestion,
   session provenance, and LIVE source review. Test actual WPF binding behavior,
   missing/edited files, locked settings, quote freshness, bar boundaries, history
   corrections, gap behavior, and simulated/broker-fake action paths; commit and push.
4. **Original example and delivery:** validate the example with deterministic
   positive/negative price sequences and compare repeated runs; document exact
   limitations and user workflow. Run the complete Release build/tests and a
   Windows publish check, independently review the final integration, then commit
   and push. No real broker order is part of automated validation.

Future work includes arbitrary C# plugins, secondary timeframes and external data,
volume-aware authoritative bars, richer indicator studies, and strategy pipelines.
Those capabilities must not be implied by a successful v1 compatibility check.

## Local automation and MCP control

Expose the running desktop application's Replay and Paper workflows through a
local control interface. This allows an assistant to reproduce the manual test
sequence without relying on timely mouse clicks. The existing Robinhood MCP
client remains the market-data/broker adapter; the new interface makes
PriceSentinel itself an MCP server through a small companion executable.

Launch the desktop app with `--automation` to opt in. A current-Windows-user-only
named pipe connects the companion to that exact app instance. The app owns the
pipe exclusively; a second instance cannot silently replace its target. An
explicit pipe name can isolate separate test instances. There is no listening
TCP port, automatic app launch, or credential exchange through this interface.
The visible window identifies that automation is enabled. Closing it stops the
server before disposing the session and journal.

The companion supports both MCP over standard input/output and a JSON command
line interface. Both transports use the same bounded request/response protocol.
Commands execute on the WPF dispatcher against the existing view model and
session engines, so automated actions update the visible controls and chart.
They do not create a second account or a separate strategy implementation.

Supported operations are status, strategy discovery/refresh, validated idle
configuration, start, pause, resume, one-observation step, explicit stop,
Replay run-to-end, and structured results. Configuration is checked before any
settings are applied. Startup returns an operation identifier promptly; callers
then inspect status for readiness, failure, pause, or completion. An accepted
start request is not a claim that historical data has finished loading.

Automation mutations are restricted to Replay and Paper. Reject them whenever
the selected, effective, or active session is LIVE, including attempts to stop
or change that session. Do not expose LIVE selection, risk acknowledgments,
arming, broker orders, login secrets, or arbitrary code/method invocation.
Existing host sizing, entry limits, stop loss, daily loss, source validation,
script pinning, and journal rules also apply to automated sessions.

Replay can pause after a requested number of additional observations or completed
strategy candles. Check the boundary inside the processing loop after the
observation, strategy evaluation, fills, and journal writes finish, before the
next observation. A step consumes exactly one source observation and pauses again
unless that was the final observation. Pause and Stop have explicit meanings,
independent of the overloaded button actions used in the interactive UI.

An optional fast Replay removes playback delays, preserving chronological source
events and their timestamps. It must still process every observation and yield
to the UI so status, pause, stop, and closing remain responsive. Paper continues
to use real market data at real-world speed. Historical-data loading and source
availability are separate from Replay pacing.

Structured results include the operation/session identity, progress counts,
strategy provenance, numeric paper account state, journal summary, and bounded
decision/fill records. Preserve the completed session's results for inspection.
Expose failures explicitly rather than treating a rejected or missing strategy
as a successful run. Repeatability comparisons use the same settings, source
history, and pinned strategy; MCP does not imply that separately fetched history
is identical or prove that WPF pixels render correctly.

Verification covers protocol framing/disconnects, duplicate ownership, invalid
configuration, LIVE rejection, startup cancellation, exact pause/step boundaries,
normal-versus-fast Replay equivalence, completed-result retention, and an actual
MCP client round trip. Use fake market/broker ports for automated tests; inspect
the connected visible app separately for the final control smoke test.

## MCP strategy research observability

Provide read-only `candles`, `indicators`, `events`, and `capture_chart` tools
against the same Replay/Paper session and app visual. Observations must describe
what the strategy could know at that point in market time. Never return future
loaded Replay history, synthesize missing indicator values, or reevaluate a
script solely to satisfy an inspection request.

Expose separate processed-source and finalized-strategy candle streams. Replay
source records preserve provider OHLC, actual source duration, and timestamps; Paper sampled
quotes must remain labeled as samples. Strategy candles preserve exact decimal
OHLC, start/end times, availability, bar version, and observation correlation at
the configured script interval. Forming chart candles are a separate visual
representation, and Built-In has no script candle stream.

Status, results, indicators, and chart captures include retained `replayHistory`
metadata. Source records report their actual interval and close availability.
These are additive protocol fields; inspection must not relabel completed
results when the next session's configuration changes.

Retain pinned input defaults and actual named definitions/plots evaluated by the
interpreter, with null values and availability reasons for unavailable
expressions. Bound the indicator list to 64 declarations with explicit total
count and truncation. Do not evaluate unused or unreached expressions for
diagnostics. Report required,
retained, and remaining warmup bars alongside the history version/start and
completed-bar count. Distinguish the latest evaluation from current warmup after
gaps, waiting observations, or host risk preemption. Keep Built-In indicator
readouts and chart RSI separate from external-script indicators.

Decision events connect the original strategy proposal, whether it was evaluated,
the final host decision, risk override, order, fill, and numeric account state.
Host risk checks can preempt evaluation; record that fact without inventing a
proposal. Embed script values only when an evaluation actually occurred, then
use evaluation and observation sequences to correlate subsequent events.

Research streams retain at most 4,096 records and 12 MiB each in memory, preserve
completed-session data, and reset for a new simulation. Page at most 100 records
and 600 KiB of record data per response. Return explicit cursor, retention, and
omission metadata rather than silently truncating. Accept an expected session
ID to reject comparisons that cross sessions. Existing SQLite journal retention
remains separate from these bounded research windows.

Optional chart captures render the actual WPF chart visual to a bounded PNG,
with session, capture time, visible range, display interval, and chart RSI
metadata. The default and maximum bounds are 1280 by 900 pixels, preserving
aspect ratio and reducing size further if required by the byte limit. Capture
only the chart, without credentials, account panels, or other desktop windows.
Return native MCP image content with separate metadata and no duplicate base64
payload. Visual checks complement exact numeric research and use the chart's
independent interval/indicator settings.

Verification must cover actual values and warmup transitions, no future-data
exposure, event correlation and risk preemption, pagination/retention and session
guards, and a native image round trip through an official MCP client. Compare
normal and fast Replay outcomes to ensure telemetry does not change behavior.
