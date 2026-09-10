# PriceSentinel 3000

[![Windows CI](https://github.com/abiemann/PriceSentinel3000/actions/workflows/windows-ci.yml/badge.svg)](https://github.com/abiemann/PriceSentinel3000/actions/workflows/windows-ci.yml)

A Windows desktop application that connects to Robinhood Agentic Trading for
real-time price monitoring, historical playback, paper-first strategy research,
and guarded live execution of a user-selected stock or ETF.

![PriceSentinel 3000 showing a running historical Replay with candlesticks, RSI, trade markers, risk controls, and an activity journal](docs/images/pricesentinel3000-replay.png)

*Historical Replay mode using Robinhood price history and simulated paper fills.*

See [Architecture](docs/architecture.md) for the project boundaries, runtime
flows, and LIVE safety invariants. Security concerns should follow the private
reporting guidance in [Security](SECURITY.md).
Pending work is tracked in [TODO](TODO.md).

For assistant-driven Replay and Paper tests, see [Local app control](docs/automation.md).
The optional MCP/CLI companion controls the visible app and supports exact Replay
pause/step boundaries and fast playback.

## Retain high-resolution history

Open **Tools > Retain Hi-Res Data** to create download lists, import individual equities from Robinhood lists, and choose a daily collection time and time zone. New lists show **SAVE LIST**; existing lists show **UPDATE LIST** only while edits are unsaved. **EXPORT LISTS** backs up saved list names, symbols, and inclusion choices to JSON; it does not export candles or unsaved edits.

**DOWNLOAD GAPS NOW** and scheduled runs always queue every included equity for today, then work backward one date at a time. Older dates are checked against saved candles and recorded attempts before being added to the queue. Complete coverage and gaps already attempted twice are skipped. The first older regular trading day whose entire gap check returns no broker candles stops that equity's backward search, even when local candles are saved. Its daily JSON remembers the boundary for later normal runs while the other equities continue. Compatible saved candles are reused across regular, premarket, after-hours, and overnight trading. See the [market-data library guide](docs/market-data-library.md) for the stopping rule and request limits.

Each daily JSON keeps its candles, unavailable ranges, and per-range attempt counts together. Older gaps receive at most two normal attempts; **FORCED DOWNLOAD** bypasses that limit. Successfully saved ranges are removed from the attempt records. Today is always queued, with known unavailable ranges eligible again after 15 minutes. Errors count as attempts but do not prove that history is unavailable. **CLEAR** removes finished queue entries only when idle, preserving saved candles and their collection records.

The library stores one current 15-second file per equity and **Eastern market date** under year / numbered English month / ticker folders. Candle timestamps are UTC; daily grouping remains Eastern for consistent sharing. Compatible imported history participates in coverage checks and Replay. When returned 15-second candles overlap saved history, newer valid prices and positive volumes update the current file; zero, null, or empty values keep the saved values. Superseded snapshots remain available by exact hash in the archive. Gap downloads still skip complete coverage. Automatic collection requires the app to stay open and connected; copied history can be replayed offline.

In **LOCAL LIBRARY**, **RESCAN LIBRARY** shows processing progress and the total size of active candle files in MB. Library rows and the download table support primary sorting and Shift-click secondary sorting. Local-library coverage excludes market closures and future candles, so today's denominator stops at the latest completed candle. Click a library row for a timeline of that **Eastern date**, with its boundaries displayed as local dates and times. For example, September 10 Eastern spans September 9 at 21:00 through September 10 at 21:00 Pacific daylight time. Confirmed non-overnight equities show the 04:00–20:00 Eastern extended-hours span in local time. Click a 15-minute block for details; a selected black **Missing** block offers **DOWNLOAD** for that block and its connected missing neighbors. Green means complete, light green partial, striped gray market closed, and blue future time.

Replay keeps an existing Robinhood connection available for symbol autocomplete, including after playback from saved files. Local-only Replay can still run without connecting. Selecting OFF does not open a connection; background downloads can continue using an existing one.

Replay checks disk first and fills missing coverage from supported broker history. Complete finer candles can be aggregated alongside coarser gap fills to one Replay interval, with the native resolutions shown and preserved in the files. Choose **Replay from local files only (offline)** in the dashboard before START to prevent broker history requests.

In Replay, press **Enter** after entering a date or time, click **CHECK**, or choose a calendar date to check coverage before starting. Dark green means complete local 15-second data, light green means verified broker 15-second data, orange means 30–60-second data, and red means two-minute data. Neutral dates have details explaining unchecked, partial, or unavailable coverage.

Downloads continue when you close the retention window. The **Retain Hi-Res Data** button shows a compact progress bar and reopens the current download status. Nearby gaps are grouped into bounded requests to reduce broker calls. The status shows the current request range and progress checking that stock/date, separately from saved-data coverage. **Checking older history** keeps both progress bars visibly busy while local ticker/date checks find more work. **PAUSE DOWNLOADS** also stops between those checks, preserving the queue. Ready batches continue without a fixed pause; keep PriceSentinel open for background collection.

## Reviewer tour

A Robinhood Agentic Trading account is required to run the connected workspace,
but the solution builds and its deterministic tests run without Robinhood
credentials. For a quick code review:

1. Start with [Architecture](docs/architecture.md) for the dependency direction
   and safety boundaries.
2. Review `PriceSentinel3000.Core` for strategy, risk, paper-account, indicator,
   and chart calculations that do not depend on WPF or Robinhood.
3. Review `PriceSentinel3000.Application` for real-time ingestion, Replay pacing,
   and the guarded LIVE order lifecycle.
4. Review `PriceSentinel3000.Infrastructure` for Robinhood MCP/OAuth, DPAPI,
   SQLite, and JSON adapter implementations.
5. Review the WPF `Views`, `Styles`, and `Themes` folders for the modular desktop
   presentation, then run the boundary-specific test projects for executable
   safety examples.

> [!WARNING]
> This project is experimental software, not financial advice. Trading involves
> substantial risk of loss. Validate every strategy in Paper Trader and Replay
> before considering live execution.

## Install on Windows

Download the `PriceSentinel3000-<version>-win-x64-setup.exe` asset from the
[latest GitHub release](https://github.com/abiemann/PriceSentinel3000/releases/latest).
The current release is [1.3](https://github.com/abiemann/PriceSentinel3000/releases/tag/1.3).
The installer is self-contained for Windows x64, so it does not require a
separate .NET installation. It installs for the current Windows user without an
administrator prompt and creates a Start menu shortcut; a desktop shortcut is
optional.

Each release also includes a `SHA256SUMS.txt` file and build-provenance record.
The installer is currently unsigned, so Windows may show an **Unknown publisher**
or Microsoft Defender SmartScreen warning. The checksum verifies that a download
matches the GitHub release asset, but it is not a substitute for a trusted
publisher signature.

Upgrades and uninstalling preserve the journal, preferences, local strategies,
default candle library, and encrypted Robinhood session under `%LOCALAPPDATA%\PriceSentinel3000`. Delete that folder
manually only when you intentionally want to remove local PriceSentinel data and
saved authorization.

## Status

Release **1.3** includes single-symbol Replay, Paper Trader, guarded LIVE execution,
folder strategies, the local candle library, and optional MCP/CLI app control:

- OFF / Replay / Paper Trader / LIVE rotary mode selection, with OFF at startup
- Paper Trader polls real Robinhood quotes at the configured interval, evaluates
  the selected Built-In or folder script strategy, and can never submit a real order
- Warm-start history covers the chart window plus the RSI(14) lookback required
  by every selectable chart candle interval, expanding for a selected script's
  warmup when needed; configurable delayed-lookback
  reconciliation uses real 15-second Robinhood equity bars
- Chart candles keep the same width when changing display interval. With a
  15-minute buffer, the chart shows 60 candles: 15 minutes at 15 seconds per candle,
  or two hours at two minutes per candle. Time labels adjust to the visible span.
- Replay accepts a ticker plus an exact local date/time and checks saved history
  before provider history at 15 seconds, 30 seconds, then one minute. It preserves
  the source duration and can be paused, resumed, or stopped without losing the
  captured chart and paper-account state. In current source, selecting a different
  symbol while idle clears the old chart, prices, and paper-account display;
  completed journal records and structured session results remain available
- Replay local start/end range (up to 24 hours) and playback speed are tunable.
  Current source supports 1x-500x with a **MAX** button to select 500x.
  Speeds outside 1x-500x are set to the nearest limit when you leave the field or press START.
  The published 1.3 build supports 1x-100x
- Built-In analyzes a tunable 5-15 minute rolling buffer as individual one-minute
  blocks and as a whole, retaining at least 16 observations for RSI when slow
  polling would otherwise leave too little history. Scripts use completed candles
  at their selected strategy interval with their own indicator lookbacks
- Built-In bottom detection combines a meaningful decline, lingering or separated
  low-zone touches, a confirmed positive turn, and simple-average RSI(14)
- Built-In peak detection combines open-position profit, repeated peak or pullback
  evidence, negative momentum, RSI context, and a five-minute profitable-stall exit
- Live-price paper buys fill at the observed ask and sells at the observed bid.
  Historical Replay has no bid/ask series, so its simulated fills use the bar
  close; no fill is generated from a stale closed-market quote
- Paper sale proceeds can either settle immediately or use a simulated equity
  T+1 schedule. Delayed proceeds remain in paper-account equity but cannot fund
  another buy until 9:30 AM Eastern on the next weekday; exchange holidays are
  not yet modeled
- Paper position sizing supports a fixed dollar amount or account percentage,
  plus "As many as possible" or a user-entered "No more than" share limit
- Maximum entries, maximum daily loss, purchase-price percentage/total-position
  dollar stop loss, a 30-second re-entry cooldown, and a post-sell price deadband
  are enforced before simulated execution
- Daily entry counts and loss limits roll over at Eastern midnight while preserving
  positions and settlement state; LIVE baselines are persisted by account and date
- The WPF chart renders selectable 15-, 30-, 60-, or 120-second candlesticks.
  Replay preserves Robinhood's true OHLC observations while larger intervals
  aggregate them; display choices remain compatible with the source duration
  shown in Session Status.
  Paper Trader combines incoming quotes with reconciled history. The chart also
  shows current price and bid/ask
- The chart labels simulated BUY and SELL fills, while Session Status shows paper
  buying power, equity, position, realized/unrealized P&L, and entry count
- Optional RSI(14), minute-by-minute time labels, cursor crosshairs, and Auto or
  drag-adjusted Manual Y-axis scaling support visual inspection
- OFF collapses the configuration tiles under the rotary selector. Selecting
  Replay, Paper Trader, or an acknowledged LIVE mode reveals the relevant controls;
  LIVE retains polling/buffer controls while hiding Replay-only date and speed fields
- START commits and validates enabled inputs. Session settings remain fixed during
  startup and trading; STOP can cancel startup, and chart interval changes remain
  available while a session runs
- SQLite WAL journaling records sessions, observations, every strategy decision,
  paper orders, fills, position snapshots, activities, and idempotent LIVE order events
- LIVE enters disarmed, then **Start Live Trader** reconciles the agentic account,
  buying power, position, symbol tradability, existing orders, daily loss baseline,
  and daily entry count before it can arm
- If Robinhood already holds the selected symbol, LIVE shows the quantity, average
  purchase price, and current estimated sell price before doing anything. The user
  can request an immediate reviewed market sale, adopt the position and wait for a
  profitable strategy exit, or cancel startup without changing the position. New
  BUY orders remain locked out until a PriceSentinel SELL is fully filled and
  Robinhood independently confirms the symbol is flat with no open order
- A LIVE strategy decision must first pass local risk, arming, regular-hours,
  tradability, and fractional-share gates before its order intent reaches
  Robinhood review; missing/malformed review data, any non-empty broker alert,
  stale prices, excessive review-price drift, ambiguous acknowledgements, and
  duplicate/open orders fail closed
- LIVE uses regular-hours GFD market orders with a stable idempotency reference;
  STOP disarms immediately, requests cancellation, and briefly polls the broker
  because cancellation is asynchronous; closing the app with unresolved order
  state requires an explicit exit confirmation after a Robinhood warning
- Startup silently restores a saved Robinhood session when possible; otherwise
  the welcome dialog offers **LOGIN**, **USE OFFLINE**, or **EXIT**. The workspace
  always starts in OFF mode
- Accepting the LIVE loss warning is remembered in local preferences across
  restarts. Every LIVE session still requires **Start Live Trader** to arm
- A light-gray window edge and rounded corners separate the dark workspace from
  surrounding windows
- Friendly status guidance distinguishes REAL TIME, MARKET CLOSED, REPLAY,
  AUTHORIZING, and OFFLINE states

Paper Trader and Replay now create simulated trades from real Robinhood prices.
The Built-In thresholds are documented research defaults inferred from the example
charts; they are a premise to test, not evidence of profitability. LIVE can submit
real equity orders only after the warning is accepted and the user explicitly
starts a fully reconciled LIVE session.

## Shared strategy, guarded execution

Replay, Paper Trader, and LIVE use the strategy selected for the session.
**Built-In** evaluates rolling `MarketQuote` history and position context through
[`PriceActionSignalEngine`](src/PriceSentinel3000.Core/Strategy/PriceActionSignalEngine.cs),
producing `BOTTOM CONFIRMED` buys and `PEAK CONFIRMED` or `PROFIT STALLED` sells.
A compatible folder script instead uses
[`ThinkScriptSignalEngine`](src/PriceSentinel3000.Application/Strategies/ThinkScriptSignalEngine.cs)
to evaluate its pinned rules once per newly completed strategy candle after
warmup. The chart candle interval controls the display; a script's separate
candle interval controls its calculations.

Scripts can declare a [tested candle interval](docs/strategy-scripting.md#tested-candle-interval).
Selecting an annotated script applies that interval and displays it beside the
setting. Overrides show a warning; scripts without metadata are labeled unspecified.

What happens after a decision depends on the operating mode:

- [`PaperTradingEngine`](src/PriceSentinel3000.Core/PaperTrading/PaperTradingEngine.cs)
  applies the configured paper-account risk controls and creates simulated fills
  for Replay and Paper Trader. Their chart markers represent completed simulated
  fills, not merely an unexecuted strategy signal.
- [`LiveExecutionEngine`](src/PriceSentinel3000.Core/LiveTrading/LiveExecutionEngine.cs)
  applies corresponding limits against the authoritative Robinhood account,
  position, buying power, and available shares before creating a broker-neutral
  order intent.
- [`LiveOrderCoordinator`](src/PriceSentinel3000.Application/LiveTrading/LiveOrderCoordinator.cs)
  owns Robinhood review, idempotent placement, polling, reconciliation, and
  cancellation. A LIVE chart marker appears only after Robinhood reports an
  actual fill; a valid strategy signal can be blocked without producing a marker.

With the same selected strategy, settings, and equivalent available history,
Replay and LIVE use the same decision rules, but need not fill at the same price
or timestamp.
Replay uses historical bar closes, while LIVE uses fresh bid/ask data and adds
market-hours, tradability, broker-state, fractional-share, and pre-trade-review
gates. Executable examples live in
[`PriceActionSignalEngineTests`](tests/PriceSentinel3000.Core.Tests/Strategy/PriceActionSignalEngineTests.cs)
and
[`LiveOrderCoordinatorTests`](tests/PriceSentinel3000.Application.Tests/LiveTrading/LiveOrderCoordinatorTests.cs).

## Paper Trader workflow

1. On the first startup, click **LOGIN** and complete Robinhood's hosted browser
   authorization. Being signed in is only the first step: finish the on-screen
   connection approval and wait for the PriceSentinel completion page.
   PriceSentinel never asks for or stores a Robinhood password. Later launches
   silently restore the encrypted saved session and open the workspace directly.
2. Select **Paper Trader**, enter a stock or ETF symbol and paper starting balance,
   choose **Built-In** or a compatible script, configure the risk and timing
   settings, then click **Start Paper Trader**.
3. The app requests real 15-second warm-start history covering the widest chart
   view plus 28 minutes for chart RSI(14). A 15-minute buffer needs 148 minutes of
   chart history so switching to two-minute candles can show two hours. The
   request expands further when the script needs more warmup. Chart-only history
   does not extend the strategy's seed window: scripts retain the longer of the
   configured buffer plus 28 minutes and their required warmup plus two strategy
   candles. A script history requirement
   over 24 hours blocks startup. Available completed history seeds the script;
   it waits for enough consecutive candles before proposing trades. The app
   obtains the current quote and polls it at the configured interval.
4. At each reconciliation interval, the app requests the configured lookback
   window ending behind real time by the completion delay. This avoids treating a
   still-forming historical bar as final. Matching timestamps are verified,
   corrections replace old values, and missing bars are added to the ring buffer.
   One history request can run alongside quote polling, so slow reconciliation
   does not hold up delivery of fresh quotes.
5. Each fresh quote evaluates Built-In's block/whole-buffer rules, or supplies
   the selected script with completed strategy candles. Scripts propose an action
   only once per newly completed candle; host risk checks still run on fresh
   quotes. Entries and exits that pass host checks update the in-memory paper
   account, then persist the decision, order, fill, and position snapshot to SQLite.
   Execution uses the current quote's bid/ask and checks its age against the
   current clock. Reconciliation does not rewrite finalized script candles.
6. If the newest venue timestamp is old, the app says **MARKET CLOSED** and pauses
   strategy decisions and paper fills.

There is no generated-price fallback. If authorization, Robinhood, or the network
is unavailable, the session stops and reports the failure.

## LIVE workflow

1. Resolve any existing open order for the selected symbol; an open order blocks
   startup. An existing long position instead opens a confirmation dialog showing
   Robinhood quantity, average cost, and the current estimated sell-side price.
2. On the first **LIVE** selection, read the loss warning and choose **I AGREE**.
   Acceptance is saved in local preferences, so later selections skip this dialog.
   Canceling or closing it does not record acceptance. Entering LIVE always leaves
   execution disarmed; accepting the warning does not submit an order.
3. Configure conservative risk limits, then choose **Start Live Trader**. The app
   fetches the account from Robinhood instead of using the paper starting balance.
4. For an existing position, choose **Sell Now**, **Wait for the next profitable
   exit**, or **Cancel Live Start**. Immediate sale still goes through Robinhood
   review, idempotent placement, polling, and reconciliation. Monitoring treats
   the position as newly adopted and enters exit logic before any new BUY. It is
   blocked when the configured stop loss or daily-loss limit would liquidate the
   position immediately. Cancel submits nothing and leaves LIVE disarmed. If an
   exit remains pending, is only partially filled, or ends unsuccessfully, entry
   stays blocked and LIVE stops or remains disarmed for manual review. A completed
   exit releases the entry lock only after Robinhood also reports no remaining
   position and no open order.
5. LIVE arms only after account, balance, buying power, tradability, position,
   open-order, daily-entry, daily-loss, and any existing-position recovery checks
   succeed. Broker state and the quote are refreshed after the dialog so a changed
   position cannot be acted upon using stale confirmation. If the dialog spans
   Eastern midnight, startup must be repeated to confirm the new day's limits.
6. A strategy signal must pass the app's local risk gates before it can create an
   intent. That intent must also pass arming, regular-hours, tradability, and
   fractional-share gates before Robinhood reviews the exact order. The app records
   and displays the market-data disclosure and blocks every non-empty pre-trade
   alert before placement.
7. After submission, the app polls the broker order, blocks duplicate signals,
   records state transitions and fills, and refreshes the authoritative position.
8. **STOP** disarms the session and requests cancellation of a PriceSentinel order.
   Robinhood cancellation is asynchronous, so always confirm the final order and
   position in Robinhood. An order can fill while cancellation is in flight. If
   shutdown cannot confirm a terminal state, the app pauses closing and requires
   explicit confirmation before exiting.

LIVE daily-loss baselines are stored separately for each account and Eastern
trading date. A continuing session carries its last observed equity into the new
day's baseline. On upgrade, an older journal may contain today's LIVE sessions
without an account number. If no account-specific baseline already exists, LIVE
stays disarmed until the next Eastern day because that old baseline cannot be
safely attributed to the current account. Paper Trader and Replay remain available.

LIVE is experimental and not production-proven. The first market-hours validation
should use the smallest practical position, one maximum entry, and direct Robinhood
monitoring. Market orders prioritize speed but do not guarantee an execution price.

## Strategy research defaults

The compiled **Built-In** detector uses the supplied labeled screenshots as a
starting hypothesis. Folder scripts define their own entry, exit, and indicator
rules; host risk and re-entry controls still apply to every selection.

Built-In defaults:

- simple-average RSI period: 14 observations
- low/high touch-zone tolerance: 0.06%
- minimum pre-turn swing: 0.10%
- minimum 20-second reversal confirmation: 0.025%
- bottom RSI confirmation: at or below 48 and no longer falling
- minimum profitable peak exit: 0.04% before bid-side spread impact
- profitable-stall fallback: five minutes with non-positive momentum

The shared host requires at least 0.10% price movement in either direction from
the previous sell before re-entry, as well as its 30-second cooldown.

The Built-In constants live in the strategy core, and its decisions store
confidence and human-readable evidence. Replay results should be used to tune
them later; they are not a promise that the labeled regions can be captured live.

## Replay workflow

1. Connect to Robinhood, or continue offline for saved history, then select **Replay** and enter the ticker,
   local date (`yyyy-MM-dd`), local start/end times (`HH:mm`), and playback
   speed. Press **Enter**, click **CHECK**, or select a calendar date to inspect
   availability, then click **Start Replay**.
2. The check considers saved history and the broker for precisely that range and
   all available trading hours. Complete 15-second coverage takes
   priority over complete 30-second, one-minute, or imported two-minute coverage.
   Partial results are explicitly marked. START reuses the checked snapshot;
   starting without a check uses the existing local-first lookup. Offline mode
   uses saved files only. Null/interpolated bars are excluded and invalid prices
   remain errors. Compatible saved candles and broker gap fills can use different
   native resolutions; complete finer spans aggregate to one uniform Replay
   interval. Coarse candles are never split, and unresolved gaps remain visible.
3. The returned observations are replayed in source-time order. Each historical
   candle becomes available at its actual close, with delays compressed by the
   selected speed. Session Status labels the source duration, and the chart
   offers compatible display intervals.
4. **Pause** freezes playback while preserving the chart, buffer, strategy, and
   paper account. **Resume** continues with the next historical observation.
5. Replay uses the same selected strategy, paper account, risk controls, chart
   markers, and journal as Paper Trader. Its simulated fills use historical
   source closes, making a run reproducible from the captured observations.

The source limit is two minutes, but Robinhood MCP currently supports no
two-minute request; its next interval after one minute is five minutes and is
not used. An empty result means no usable history was returned at the supported
intervals, rather than a claim that the stock did not trade. Provider errors
remain visible and do not trigger a retry at a different resolution.

A script interval must be an exact multiple of the returned source interval.
For example, one-minute source candles can run a one-minute script, but cannot
run a 15-second script. An incompatible selection blocks startup without changing
the script interval. Select a compatible interval deliberately for a separate
experiment. Changing the chart interval only changes the display.

Both Built-In and scripts see a source candle at its close. Replay risk checks
and simulated fills use that close; the intervening prices and exact stop-loss
crossing time are unknown. Results from different source resolutions can differ.
The journal and MCP results preserve the actual duration and execution model.
Replay uses real provider history and does not depend on a previously recorded
Paper Trader session.

## Strategy scripts

The shared **Strategy** selector applies to Paper Trader, LIVE, and Replay.
**Built-In** remains selected by default and uses the existing compiled strategy.
One original example, **Original Confirmation - experimental**, is packaged with the app.

1. Click **SCRIPTS FOLDER** to open `%LOCALAPPDATA%\PriceSentinel3000\Strategies`.
2. Copy a thinkScript strategy into that folder as `.thinkscript`, `.ts`, or `.txt`.
3. Open the selector or click **REFRESH**. Compatible scripts appear; excluded files
   have diagnostics explaining the unsupported syntax or missing trading rules.
4. Select a script and candle interval, then test in Paper or Replay. The interval
   is independent of the chart display. Inputs use the defaults in the script;
   edit its `input` declarations before starting to change them.

Strategy diagnostics wrap inside a scrollable panel. Right-click the diagnostics
and choose **Copy** to copy the full text, including offscreen lines, or **Cancel**
to dismiss the menu. Diagnostics remain readable while session inputs are locked.

This release interprets a documented subset of thinkScript. It accepts explicit
long-only `AddOrder` strategies; chart studies do not acquire invented trading
rules. Volume, secondary timeframes, custom functions, shorting, and other unsupported
features are rejected. Two small public forum strategies have passed unchanged-source
compatibility checks. That does not imply every thinkScript works unchanged.
See the [compatibility guide](docs/strategy-scripting.md) and
[source research and compatibility checks](archive/research/strategy-sources-and-compatibility.md).

Repository script sources are collected in [research/strategies](research/strategies/README.md).
Current application guides are indexed in [docs](docs/README.md), and reusable
research tools in [research](research/README.md). Past app verification and stock
experiments share one [archive](archive/README.md), grouped by topic.

For scripts, Paper and LIVE evaluate completed price candles built from incoming quotes.
These sampled candles can differ from exchange tick candles. Initial history can
warm the script only when its source bars have completed; later history corrections
do not rewrite finalized script candles. Replay uses completed historical candles
and simulated close-price fills. Replay results can therefore differ from Paper/LIVE.

Session startup revalidates and freezes the source, SHA-256, runtime, input defaults,
and candle interval in the journal. Missing or incompatible selections block startup.
LIVE additionally asks for approval of that exact script version at every start.
All existing execution and risk controls apply. A script fault disables script
proposals for that session while host risk checks continue on fresh quotes.

The original sample is seeded once without overwriting user files. It is an
independently written example, not a claim of profitable performance.

## Authentication and local data

Robinhood is connected through the official Streamable HTTP MCP endpoint and OAuth
authorization flow. PriceSentinel dynamically registers as a native desktop client
and uses a loopback callback; there is no separate developer-app registration page.
The client pins Robinhood's currently supported MCP `2025-11-25` handshake instead
of probing the newer `server/discover` method.
The app allows up to five minutes for interactive browser authorization. While the
browser is open, the disabled LOGIN button reads
**WAITING FOR APPROVAL**; EXIT cancels the attempt. If authorization times out,
LOGIN becomes available for a clean retry.

At startup, a cached access token and dynamic client registration are tried without
permitting an interactive browser redirect. When present, a cached refresh token
can assist that reconnection. A verified cached connection opens the main window
directly. If the cache is missing, invalid, revoked, corrupt, or cannot be verified,
the welcome dialog offers **LOGIN**, **USE OFFLINE**, or **EXIT**. Offline access
supports saved-history Replay and local-library inspection without authorization.

The OAuth client cache format was updated with the MCP C# SDK 2.0 migration. The
first run after upgrading re-registers PriceSentinel once; this does not modify
authorization for Codex, Claude, or another MCP client.

Access/refresh tokens and dynamic client registration are encrypted with Windows
Data Protection API for the current Windows user at:

~~~text
%LOCALAPPDATA%\PriceSentinel3000\robinhood-tokens.dat
%LOCALAPPDATA%\PriceSentinel3000\robinhood-client.dat
~~~

The SQLite journal is stored at:

~~~text
%LOCALAPPDATA%\PriceSentinel3000\journal.db
~~~

Editable Paper Account, risk, timing, and Replay inputs are restored from:

~~~text
%LOCALAPPDATA%\PriceSentinel3000\preferences.json
~~~

The preferences file contains UI settings and the saved LIVE-warning acknowledgment.
Robinhood credentials and tokens are never written to it.

The journal uses normalized tables, indexed symbol/timestamp lookups, prepared
inserts, short transactions, and write-ahead logging. Broker passwords are never
stored in the database.

## Solution layout

~~~text
PriceSentinel3000.sln
src/
  PriceSentinel3000.App/             WPF presentation, composition, and workspace coordination
  PriceSentinel3000.Application/     Session timing, LIVE order workflow, and app-facing ports
  PriceSentinel3000.Control/         Optional MCP/CLI companion for local app control
  PriceSentinel3000.Core/            Market, paper-account, strategy, and risk models
  PriceSentinel3000.Infrastructure/  Robinhood MCP OAuth/data and SQLite adapters
tests/
  PriceSentinel3000.Core.Tests/            Deterministic domain and strategy tests
  PriceSentinel3000.Application.Tests/     Session and LIVE order workflow tests
  PriceSentinel3000.Infrastructure.Tests/  Robinhood, OAuth, preferences, and SQLite tests
  PriceSentinel3000.App.Tests/             WPF workflows with fake broker ports
  PriceSentinel3000.Control.Tests/         Companion CLI and MCP protocol tests
~~~

Dependencies point inward: App composes Application with Infrastructure,
Application provides UI-agnostic workflows against Core contracts,
Infrastructure implements external-data and storage ports, and Core remains
independent of WPF, Robinhood, and SQLite. The market-data and journal contracts
retain an asset-class boundary so a future crypto adapter can reuse the buffer
and persistence engine.

## Development

Requirements:

- Visual Studio 2026 with the .NET desktop development workload
- .NET SDK 10.0.302 or a newer .NET 10 feature band; `global.json` rolls forward
  within .NET 10 while excluding prerelease SDKs
- A Robinhood account eligible for Agentic Trading access for connected workflows;
  building, deterministic tests, and saved-history offline Replay need no credentials

Open `PriceSentinel3000.sln` in Visual Studio, or use:

~~~powershell
dotnet restore PriceSentinel3000.sln
dotnet build PriceSentinel3000.sln --configuration Release --no-restore
dotnet test PriceSentinel3000.sln --configuration Release --no-build
~~~

The Windows CI workflow runs the same Release build with warnings promoted to
errors and executes the complete test suite on every pull request and push to
`main`.
The September 10, 2026 source review for the **1.3 rebuild** includes daily gap
attempt records and candle revisions, persistent older-history stopping boundaries,
resumable discovery progress and pause handling, and Eastern-date coverage timelines
shown in local time. The current source passed all **1,495 tests** and a local
Release build with zero warnings or errors. Refreshed Windows CI, installer
packaging, checksums, and source-provenance verification were still pending at
this source review; their final results belong to the rebuilt release's workflow
and provenance record.

The initial 1.3 publication from `1bea13a` passed **1,404 tests**. Its historical
[Windows CI](https://github.com/abiemann/PriceSentinel3000/actions/runs/34422818384)
and [release packaging](https://github.com/abiemann/PriceSentinel3000/actions/runs/34422844466)
workflows succeeded, and its published checksums and source provenance were verified.
Those runs do not validate the rebuilt source. Open-market Paper testing and
interactive packaged-app smoke checks remain tracked in [TODO](TODO.md).

The app footer and MCP companion use the version embedded at build time.
GitHub release builds display their release tag without a leading `v`, including
any prerelease suffix (for example, `1.3` or `1.4.0-beta.1`). Local builds derive
their version from the nearest numeric Git tag; commits after that tag are
marked as development builds. Fetch new tags with `git fetch origin --tags`
before building locally. The installed app keeps its own build version when a
new release is published; installing that release updates the footer.

Publishing a GitHub release for a numeric tag such as `1.3` or `v1.3.0` starts the
Windows release workflow. It builds and tests the exact tagged source, creates a
self-contained x64 publish, compiles the Inno Setup installer, and attaches the
installer, SHA-256 checksums, and provenance to the release. An existing release
can also be packaged through the workflow's manual `tag` input; replacing assets
with the same names requires the explicit replacement option.

For an older tag that predates the packaging files, the workflow uses the exact
workflow commit for the installer definition and artwork while compiling only
the tagged application source. Both commits are recorded in the provenance
asset. The packaging workflow itself does not move or rewrite tags.

The workspace always starts OFF, whether a Robinhood connection is restored,
login succeeds, or **USE OFFLINE** is selected.
Accepting the LIVE warning makes LIVE the effective mode while broker execution
remains disarmed. **Start Live Trader** performs the broker preflight and arms the
session only when every check succeeds; confirmed signals can then submit real
orders to the connected Robinhood agentic account.

## License

PriceSentinel 3000 is available under the [MIT License](LICENSE).

Copyright (c) 2026 Alexander Biemann.
