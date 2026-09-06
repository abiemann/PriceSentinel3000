# External strategy design

This specification incorporates the September 5, 2026 decisions. Implementation
is delivered in validated milestones; the release notes and compatibility guide
identify the implemented subset. The existing compiled price-action strategy
remains enabled by default and appears as **Built-In**. Converting it to an
external script is future work.

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

Historical 15-second bars have start timestamps. Their OHLC cannot be made
available before the end of that source bar. Replay honors that distinction and
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
