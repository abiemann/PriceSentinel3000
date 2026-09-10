# External strategy design

This specification describes the implementation selected for the [release 1.3](https://github.com/abiemann/PriceSentinel3000/releases/tag/1.3) rebuild,
reviewed September 10, 2026. Future work is labeled separately. The [compatibility guide](docs/strategy-scripting.md)
identifies the implemented subset and resource limits; [source research](archive/research/strategy-sources-and-compatibility.md)
records the unchanged-source checks and original example fixtures. The existing compiled price-action strategy
remains enabled by default and appears as **Built-In**. Converting it to an
external script is future work.

Release 1.3 includes folder discovery, the shared selector, completed-candle interpretation,
session source/parameter provenance, per-session LIVE version approval, and one
original experimental example. Tests cover parser restrictions, indicator values,
host position/risk safeguards, history availability, immutable artifacts, WPF
selection, and sample seeding. Windows publication includes the original source.
Local MCP control, research telemetry, and source-duration-aware Replay fallback
are also implemented. Outstanding runtime checks and follow-ups are tracked in
[TODO.md](TODO.md). The local market-data library, automatic collection, and
Replay availability calendar described below are also implemented.

## Product scope

- One selected symbol per session, with a shared strategy selector for Paper Trader,
  LIVE, and Replay. Concurrent portfolio trading remains proposed in [TODO](TODO.md).
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

## Desktop presentation

The main window has a one-pixel light-gray edge and rounded restored corners;
maximized corners are square. The border does not intercept input or change
window dragging and resizing. Current source supports paced Replay from 1x to
500x, clamping entered speeds to that range when committed. **MAX** beside
the speed input selects 500x and follows the existing session
configuration lock. **CHECK** sits to the left of the Replay availability guidance.
These controls are changes after the published 1.3 rebuild. Local Replay now also
preserves an existing Robinhood connection for autocomplete instead of treating
local history as a disconnect. Disconnected local playback requires no login;
selecting OFF opens no connection and leaves shared background downloads available.
Changing the symbol while idle clears the retained chart, prices, source display,
and simulated account totals. The header follows the selected symbol immediately;
completed journal records and structured Replay results retain their original symbol.
Selected table rows use a green background without
cell focus outlines. Compact download status uses the same layout across the
retention window's tabs, keeping tab positions stable as status text changes.

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

The documented `CountSince(condition, reset)` extension retains bounded counter
state across successive evaluations and rolling history windows in one session.
Reset takes priority; missing inputs and data gaps clear the count. Checkpoints
are invalidated by changed programs or inputs, corrected history, or rewinds, and
failed evaluations do not advance them. This extension does not enable arbitrary
recursive definitions or shared state between sessions.

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

Optional `# PriceSentinel: tested-candle-seconds=60` metadata records the author's
reference interval. Selecting a different annotated script applies that interval;
a deliberate override shows mismatch guidance. Refresh and app restart preserve
saved overrides. Missing or malformed metadata leaves the tested interval
unspecified rather than excluding a compatible script. Declared and actual
intervals are retained in session provenance; the annotation does not change
active sessions or prove compatibility with a particular history source.

Warm-start history initializes indicators. Live bars are constructed from the
quotes actually observed, so OHLC is sampled at the poll interval rather than
being an exchange tick-complete record. Quote polling must be no slower than the
selected strategy interval. Volume-dependent programs are unavailable until the
feed can supply authoritative volume consistently. A missed period stays a gap;
chart-only synthetic flat candles never become strategy data.

Replay checks compatible saved 15-, 30-, 60-, and 120-second history first for the
exact selected range. Remaining gaps can be requested from Robinhood at 15 seconds,
then 30 seconds and one minute. The provider has no two-minute request; imported
two-minute history is supported, while five-minute sources exceed the limit.
Null, interpolated, incomplete, and out-of-range bars do not satisfy a request.
Invalid prices, authentication, transport, and malformed responses remain errors.

Compatible saved pieces and broker gap fills can contain different native
resolutions. Complete finer spans aggregate to a uniform Replay interval;
coarse candles are never split, overlaps never count twice, and unresolved gaps
remain visible. Files retain their native candles and exact source provenance.
Availability checks and START share this composition. The local-only option
skips broker requests. See [Replay lookup](#replay-lookup-and-script-analysis-access)
for source selection and prepared-snapshot reuse.

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

## Local market-data library

Implemented in release 1.3. The [user guide](docs/market-data-library.md) covers setup, scheduling, portable files, offline Replay and MCP access. History is separate from the SQLite journal, credentials and repository research. Sampled quote rows are not treated as finalized historical candles.

### Download lists and daily collection UI

**Tools > Retain Hi-Res Data** opens a reusable modeless window; main trading controls stay available. Its clock reflects saved automatic-download state, not unsaved edits. The three tabs contain named equity lists, a global daily schedule and download queue, and local-library inspection.

Manual lists accept pasted symbols. Connected resolution validates equities and resolves company names; offline identities are checked when downloading. Per-member and per-list inclusion control the collection union independently of strategy selection and trade eligibility. Robinhood imports validate response completeness and preview an editable local snapshot. Refresh preserves exclusions and previews membership changes before saving. New lists show **SAVE LIST**; existing lists show **UPDATE LIST** only for unsaved changes. The button animates into view while respecting Windows animation preferences. **EXPORT LISTS** is hidden without saved lists and exports saved names, symbols, and inclusion choices, excluding private remote IDs, candles, and unsaved edits. Removing lists never deletes candles.

The collector persists settings, queue membership, progress, and a shared date frontier in `collection-state.json`. Scheduled runs and **DOWNLOAD GAPS NOW** always queue today for the saved included equities, through a captured completed 15-second boundary. Once that date is processed, older dates are prechecked against compatible saved files and daily attempt metadata before queueing. Complete days and capped gaps require no queue row or broker request. A fresh run replaces prior availability rows for those equities; explicit manual work and saved history are preserved. There is no rolling lookback cutoff.

After the published 1.3 rebuild, collection also persists an ordered active selection of at most eight unfinished stock/date jobs. Pending retries retain their slots, and pause or restart resumes the same selection. A finished, removed, or disabled automatic job releases its slot; pending jobs fill available slots in newest-date and queue order. The collector dispatches only ready jobs within that selection and waits for their retry deadlines when none are ready. The existing serial broker request pacing, per-tick request budget, market calendar, and shared date frontier still apply.

For each older date, the oldest eligible missing range is checked in windows of up to one hour. Empty results advance through that date's gaps. Returned candles are saved immediately; once availability is established, remaining gaps use batches of up to six hours. Nearby gaps can be grouped when intervening saved coverage is at most five minutes. Requests stay inside trading windows and the captured cutoff, and never bridge an exhausted range during normal collection. Returned overlaps accept newer valid price and volume corrections while retaining saved values for zero or missing fields. Revision-only responses are saved; identical responses do not create redundant files. Retention has no coarse-candle fallback.

A shared frontier finishes all queued equities for a date before moving backward. Eligible productive older days can receive a second gap pass, but no third normal attempt. Broker availability is separate from saved coverage: an equity leaves discovery after a complete older regular-day gap pass returns no broker candles, even if saved candles remain. A daily JSON boundary preserves this decision across restarts and later normal runs. Validated positive broker responses clear it; an empty retry cannot erase availability already established earlier in the same run. On resume, completed unproductive checks in an older saved queue are reconciled before any broker requests, and superseded pending discovery jobs are removed without affecting explicit manual work. Force can recheck the boundary once and stops again if it remains unproductive. The run ends when no member equities remain. Today and overnight-only Sunday or holiday dates never establish this boundary. Errors and connection failures are not unavailable observations; an exhausted failed job holds the frontier until a new or explicit retry. This stopping rule does not prove every earlier date is unavailable.

Optional `collection` metadata in each canonical daily JSON stores scopes by broker, symbol, instrument, date, session, adjustment identity, and interval. Compact adjacent ranges retain attempt counts capped at two and successful unavailable observations, including missing portions of partial responses. Optional UTC timestamps record the broker-unavailable discovery boundary and the latest actual positive broker response; merging copied records cannot resurrect a boundary cleared by newer broker evidence. Existing local candles do not clear this marker. Dispatch is recorded before the provider call. Saved candle spans clear their corresponding attempts and unavailable records. Today uses a 15-minute unavailable-range cooldown rather than the historical attempt cap. **FORCED DOWNLOAD** bypasses both restrictions while preserving request pacing. A zero-candle daily file can retain unavailable history before the first valid candle arrives. Metadata writes are atomic, preserve the candle hash, and do not create candle archives; older JSON without collection metadata remains valid.

Transport failures and timeouts receive up to two total attempts per batch, separated by five seconds. Dispatches count toward older gaps' two-attempt cap but do not establish unavailable evidence; Force is needed to recheck capped gaps. Lost connections leave pending work; expired authorization may require explicit reconnecting. **CLEAR** removes finished queue entries and the discovery frontier only while no download or queued work remains, preserving daily files, continuity, and the schedule. **PAUSE DOWNLOADS** cancels the current request and checks cancellation between local ticker/date prechecks, preserving queued work until resumed or restart.

Collection requests only genuine completed 15-second candles in the provider's `24_5` scope. Expected windows exclude weekends, holidays, and early-close closures. The saved schedule zone follows daylight saving time; skipped times run at the first valid minute and repeated times run once at the earlier occurrence. The app must stay open and connected; background calls cannot initiate interactive authorization. Collection while the app is closed remains future work.

The app-owned view model drains ready batches continuously, yielding between them while preserving serial request spacing. Retry deadlines and connection waits are distinct from the idle scheduler's check interval. Closing the modeless window leaves collection running; app shutdown cancels and awaits it, preserving pending jobs. Updates preserve table selection and scrolling. The progress bar includes partly checked days; saved coverage remains separate from request progress. Each older-date precheck publishes `CheckingOlderHistory` with its actual ticker and date before local reads. The activity line reserves space for a busy spinner, respecting Windows animation settings. Main and header progress bars become indeterminate during those checks, including when all retained rows are terminal, then resume queue progress or completion. Between planning batches, the active discovery run keeps the working state visible. Queue counts distinguish partials, unavailable data, and failures. **Equity**, **Date**, and numeric **State** support primary and Shift-click secondary sorting. A stable **[i]** at the start of the status line opens an information popup that remains open while scrolling, with outside-click, Escape, and close-button dismissal.

### Portable folder layout

The default root is `%LOCALAPPDATA%\PriceSentinel3000\MarketData`, selectable in the UI:

```text
MarketData/
  README.md
  2026/
    08 - August/
      NFLX/
        2026-08-24.15s.json
    09 - September/
      SOXL/
        2026-09-04.15s.json
```

Create only needed folders. Month names are invariant English; daily grouping uses America/New_York while candles preserve exact UTC starts, ends and source-close availability. Schema 1 stores self-contained UTF-8 JSON with invariant decimal strings, nullable unknown volume, source/instrument identity, actual interval, adjustment metadata, fetched time, requested/covered ranges, gaps and a canonical full SHA-256 hash. The generated root README documents the schema. Unknown volume does not imply complete OHLCV.

Copies of files or whole folders work without the original journal, sidecars, private list IDs or credentials. Each equity/Eastern date has one current `YYYY-MM-DD.15s.json` file. Compatible 15-second sections are atomically consolidated into it, including imported regular/extended files. Identical overlaps are deduplicated. Newer fetched candles replace saved prices and positive volumes, including lower positive values; zero/null/empty fields keep the saved values, and empty responses never delete candles. Incomplete new price bars without saved counterparts are omitted. A field-wise fallback that would produce inconsistent OHLC keeps the saved price bar together. Incoming saves win equal fetch times; legacy consolidation resolves ties by the lexicographically lowest hash. Timing, duplicate, negative-value and provenance validation remain mandatory. Superseded snapshots and original hashes remain in `.archive/<hash>.json` for exact-hash access. Coarser native files can still retain `.rev-<hash>.json` revisions. Replay queries retain their existing revision policy and explicit pins for unresolved or coarser revisions; the library table has no pin editor. Different providers/instruments/adjustment identities cannot merge implicitly. Robinhood's unversioned split-adjustment basis is disclosed; fetched timestamps are not invented adjustment epochs.

Library scans cache bounded validated metadata and revalidate new or changed files. Reads and merges still validate selected candle contents and hashes. Queue state is local, while unavailable ranges and attempt counts travel with each daily JSON. Operational collection metadata is excluded from `DatasetHash`, so updating an attempt does not invalidate Replay references.

### Local-library coverage and timeline

**RESCAN LIBRARY** reports **Processing** and percentage complete, then displays the size of active candle files in decimal MB, right-aligned with the table. Archived snapshots and supporting files are excluded. Full scan notices live in a bounded details panel. The library supports primary and Shift-click secondary sorting, preserving the chosen order across rescans and reopening within the app session.

Library rows use Eastern dates. Day coverage counts unique saved eligible native candles against expected completed trading slots at that native interval, excluding market closures and future candles. Today's denominator advances with the clock; the **Candles** column still counts saved coverage for the whole date. Mixed intervals or incompatible identities show unavailable totals rather than misleading combined counts.

After the published 1.3 rebuild, download-table **State** uses the same completed-time rule as Local library: daily jobs divide unique saved completed 15-second candles by expected completed trading slots through the current coverage check. Both saved spans and expected windows are clipped to the last completed candle; market closures and future candles are excluded. Targeted timeline jobs remain scoped to their selected range. Per-job validated metadata is retained in memory and refreshed during collection, initial saved-queue loading, and explicit library rescans. Rescans and collection updates share the collector gate, preventing an older scan from replacing a newer download result. The existing 15-second clock refresh recalculates displayed percentages from cached metadata without scanning disk or saving queue state. Display time never changes a job's captured request cutoff. Failed and unavailable attempts retain the **0%** indicator while existing files remain intact; unknown coverage or a range with no completed candles displays **--**. Request progress measures checked work separately.

Clicking a library row opens a timeline anchored to that Eastern calendar date, with explicit local dates and times for its boundaries. Confirmed 24-hour equities show the full Eastern day, respecting daylight-saving transitions; other confirmed equities show 04:00–20:00 Eastern converted to local time. Unknown eligibility uses the full Eastern day with an explanation. The selected date is not reinterpreted as a local calendar date, and adjacent Eastern dates are not joined to build a local day. For example, September 10 Eastern spans September 9 at 21:00 through September 10 at 21:00 Pacific daylight time, keeping completed overnight candles visible before Pacific midnight. Each 15-minute block is green for complete, light green for partial, black for missing, striped gray for market closed, or blue for future time. Existing calendar rules classify market closures and early closes within the displayed span. Future and closed slots do not count as missing. Timeline counts can still differ from the library row because the timeline uses confirmed session eligibility and native 15-second candles, while the row uses the sessions and native intervals represented in saved files. Click outside, press Escape, or use the close button to dismiss the dialog.

A selected block shows counts and times in a fixed selected-block panel. Selecting **Missing** offers **DOWNLOAD** at its top right for the block and touching missing neighbors, stopping at partial, complete, closed, or future blocks. The action retries remembered empty ranges only within that completed span and splits local time into the appropriate Eastern daily files. It does not start older-day discovery. The button is disabled while other queued or downloading work remains. Completion refreshes coverage while retaining selection; status stays inside the panel at bottom left and clears on a different selection, including a late result from the previous block.

### Replay lookup and script-analysis access

Explicit hashes take precedence and never permit silent substitution. Otherwise Replay checks all local 15/30/60/120-second sources first. The default CompatibleCoverage policy combines same-identity revisions when overlapping candles agree; conflicting prices or volume still require a revision choice. Remaining gaps trigger a bounded request spanning the missing coverage at supported broker intervals 15, 30 and 60 seconds. The adapter has no native 120-second request. Availability and START share this composer and preserve exact checked source hashes; native downloads are archived only at START. Composition backtracks within each target bar to choose a complete partition of genuine whole candles, preferring finer data and local sources on ties. It aggregates complete partitions to one uniform Replay interval, maximizes covered duration, and uses the finest interval on coverage ties. Missing constituents remain gaps; coarse candles are never split or double-counted. Native files stay unchanged and provenance identifies native versus Replay intervals. The **Replay from local files only (offline)** checkbox is in dashboard Replay settings before START; it defaults off, invalidates prepared availability when changed, and skips broker history requests. The welcome screen's **USE OFFLINE** action opens saved-history Replay and local inspection without authentication.

Before START, Enter in the date/start/end fields, CHECK, or selecting a calendar date checks the exact dashboard range. Complete local 15-second coverage is dark green; verified broker 15-second coverage is light green; 30/60-second coverage is orange; actual 120-second coverage is red. Partial, unchecked, unavailable, and conflicting data remain neutral with details. Opening a month scans local metadata without broker requests for every day. Selected-date checks use the existing connection, preserve the checked provider candles in memory, and prefer complete coverage before partial results. START archives that exact prepared snapshot or rereads the pinned local files. Ticker, range, library, and selection-policy changes invalidate preparation. Broker calendar results expire after five minutes; source availability is verified rather than inferred from a fixed retention age. Starting without a check retains the direct lookup behavior above.

One uniform Replay interval is used per run, with existing strategy-interval compatibility and completed-bar aggregation. Native sources can have different genuine intervals; missing price groups remain gaps and no finer prices are synthesized. Exact source hashes, native and Replay resolutions, coverage and source provenance are retained in journal/MCP results. The read-only `library_datasets` and `library_candles` tools use the same validated reader without an active session or connection; catalog pagination detects changes and candle pages are pinned by immutable hash. No MCP library tool accepts arbitrary filesystem paths.

Deterministic tests cover copying data, UTC/Eastern boundaries, non-Gregorian display cultures, partial coverage, unknown volume, corrupt files, revisions, concurrent writes, schedule recovery, imported exclusions and offline Replay/MCP. Finer source data can change risk exits without improving strategy profits. Empirical resolution comparisons must freeze the script, its interval, risk controls and evaluation period; broader profit research is a separate task.

## Strategy and host contract

The `ThinkScriptSignalEngine` adapter implements `IPriceActionSignalEngine` for
both execution engines while preserving the built-in detector. Strategy input contains
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
Built-In in the selector. Diagnostics wrap inside a bounded scrollable panel,
without a hover tooltip. Right-click **Copy** copies the complete diagnostic text,
including offscreen lines; **Cancel** closes the menu. The panel remains usable
while session inputs are locked.

The UI provides **SCRIPTS FOLDER** and **REFRESH** actions. Refresh when the selector
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
- Input defaults, selected candle interval, and optional declared tested interval
- Data/simulation model version

Replay session settings also pin `ReplayHistory`: `SourceIntervalSeconds`,
`IsFallback`, `Availability` (`source-candle-close`), `ExecutionModel`
(`completed-source-candle-close`), and `IntrabarPricesAvailable` (`false`). SQLite
schema migration 4 stores each observation's source duration; existing rows
retain their original 15-second default. These metadata changes do not alter
Paper/LIVE data ingestion or introduce execution costs.

The general LIVE loss warning records acceptance in local `preferences.json`
through `LiveRiskAcknowledged`; subsequent selections skip that dialog. Cancel or
close does not record acceptance. The flag survives restarts while those
preferences remain, and automation cannot set it. Every app launch still starts
OFF, and entering LIVE leaves execution disarmed until an explicit **Start Live
Trader** completes broker reconciliation and the existing arming checks.

External scripts separately require approval of the exact pinned source and
candle interval at every LIVE session start. The saved general warning acceptance
does not replace this review. Source edits take effect only through a new pinned
artifact and review. Approval does not establish profitability; the interpreter
and every host risk, broker review, and execution gate remain in force.

## Failure behavior

Discovery diagnostics cannot crash the application or remove Built-In. Compilation
failure, changed/missing source, invalid interval, or insufficient data must remain
clear to the user. A runtime fault blocks new script entries and is journaled;
host risk checks remain active so a faulty script cannot disable position
protection. Restart requires a deliberate user action after diagnostics are
reviewed. No automatic substitution of another strategy is allowed.

## Original packaged strategy

The single packaged `OriginalConfirmation.thinkscript` displays as **Original
Confirmation - experimental**. Other local work-in-progress scripts remain outside
release packaging unless explicitly included.

Public approaches such as Confirmation Candles can inform high-level ideas.
Develop original source with independently specified rules and parameters;
do not copy a forum script into the packaged example. Begin with a small set of
complementary price-only trend/momentum conditions, explicit long entry and exit,
and a manageable lookback. Document its assumptions and test both positive and
negative cases. Popularity and screenshots are not performance evidence.

Keep Built-In as the default. Label the original external example as experimental
until meaningful out-of-sample and Paper evidence supports stronger claims. The
existing compiled detector may be converted to a script in a later milestone
with decision-equivalence tests.

## Implementation milestones and verification

The runtime, catalog, host integration, original example, local history library,
and MCP companion are shipped in 1.3. Their validation covers:

1. **Runtime:** bounded parsing and evaluation, indicator semantics, explicit
   compatibility diagnostics, completed-bar timing, gap resets, counter state,
   repeated-entry protection, and host risk gates.
2. **Application integration:** folder discovery and one-time sample seeding,
   Built-In defaults, tested intervals, pinned artifacts, provenance, per-start
   LIVE script review, saved general warning acceptance, and scrollable diagnostics.
3. **Data and automation:** portable files, revisions, queues, empty-range indexing,
   recovery and retries, local-time coverage, offline Replay, native-resolution
   composition, exact stepping, bounded telemetry, and fake-broker isolation.
4. **Delivery:** the September 10 rebuild source passed all 1,495 tests and a local
   Release build with zero warnings or errors. This includes regressions for daily
   gap attempts and candle corrections, persisted empty-day boundaries and resume,
   discovery progress and pause handling, and Eastern-date timelines in local time.
   Refreshed CI, packaging, checksums, and provenance verification were pending at
   this source review. The initial 1.3 publication from `1bea13a` passed 1,404 tests;
   its successful workflows and verified assets are historical validation, not
   evidence for the rebuilt source. No real broker order is part of automated validation.

Open-market Paper Trader testing and an interactive packaged-1.3 smoke check
remain outstanding in [TODO](TODO.md#release-13-validation-and-remaining-runtime-checks).
Automated builds, tests, and published artifacts do not complete those checks.

Future work includes arbitrary C# plugins, secondary timeframes and external data,
volume-aware authoritative bars, richer indicator studies, and strategy pipelines.
Those capabilities must not be implied by a successful v1 compatibility check.

## Local automation and MCP control

The implemented local control interface exposes the running desktop application's
Replay and Paper workflows. This allows an assistant to reproduce the manual test
sequence without relying on timely mouse clicks. The existing Robinhood MCP
client remains the market-data/broker adapter; the companion makes
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

The implemented read-only `candles`, `indicators`, `events`, and `capture_chart`
tools inspect the same Replay/Paper session and app visual. Observations must describe
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
