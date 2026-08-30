# PriceSentinel 3000 architecture

PriceSentinel separates deterministic trading policy from Windows presentation,
session orchestration, and Robinhood-specific integrations. The result is a WPF
desktop application whose strategy and safety decisions can be tested without a
window, network connection, broker account, or SQLite database.

## Project boundaries

```mermaid
flowchart LR
    App[PriceSentinel3000.App<br/>WPF presentation and composition] --> Application[PriceSentinel3000.Application<br/>Use cases and ports]
    App --> Core[PriceSentinel3000.Core<br/>Domain, strategy, and risk]
    App --> Infrastructure[PriceSentinel3000.Infrastructure<br/>Robinhood and local-storage adapters]
    Infrastructure --> Application
    Application --> Core
    Infrastructure --> Core
```

| Project | Owns | Does not own |
| --- | --- | --- |
| `PriceSentinel3000.App` | WPF views, controls, view models, commands, dialogs, composition, mode routing, and workspace-state coordination | Deterministic strategy rules or broker protocol implementations |
| `PriceSentinel3000.Application` | Session cancellation lifetime, real-time ingestion timing, Replay pacing, LIVE order coordination, and app-facing ports | WPF controls, Robinhood payload parsing, SQLite, or encryption details |
| `PriceSentinel3000.Core` | Market and account models, candle aggregation, indicator/chart calculations, strategy decisions, paper fills, risk gates, and LIVE execution rules | WPF, networking, filesystem access, Robinhood, or SQLite |
| `PriceSentinel3000.Infrastructure` | Robinhood MCP/OAuth, broker response parsing, DPAPI-protected authentication state, SQLite journaling, and JSON preferences | Presentation behavior or trading strategy |

`Core` has no dependency on the other projects. `Application` depends only on
`Core`. `Infrastructure` implements ports declared by Core and Application and
translates external data into Core models. `App` is the composition root and is
the only production project that decides which concrete adapters satisfy each
port.

## Application orchestration

The presentation layer delegates focused long-running operations while the main
view model remains the overall workspace and mode orchestrator:

- `TradingSessionCoordinator` owns the cancellation lifetime for the single active
  session through explicit begin, cancel, and dispose operations.
- `RealtimeSessionRunner` owns warm-start history, quote polling, delayed-lookback
  reconciliation, and observation delivery shared by Paper Trader and LIVE.
- `ReplaySessionRunner` owns source-time playback pacing, including pause and
  resume without discarding session state. `MainViewModel` requests the bounded
  history before passing observations to the runner.
- `LiveOrderCoordinator` owns the guarded review, placement, polling,
  reconciliation, and cancellation lifecycle for a LIVE order.
- `IUserPreferencesStore` is an app-facing persistence port; the JSON
  implementation remains in Infrastructure.

`MainViewModel` remains responsible for bindable state and commands as well as
mode selection, validation, Replay loading, broker preflight queries, symbol
search, tradability projection, paper/LIVE account projection, journaling
coordination, and chart projection. Its implementation is grouped into session,
LIVE, existing-position recovery, paper, presentation, symbol-search, and
tradability partials so those responsibilities remain navigable without adding
artificial service layers.

Window shutdown is a two-phase workflow. `MainWindow` first awaits
`MainViewModel.PrepareForShutdownAsync`, which cancels the active data session,
awaits its command task, and attempts to cancel or reconcile retained LIVE order
context. If a terminal broker state cannot be confirmed, closing pauses behind an
explicit warning. After resolution or deliberate exit confirmation,
`MainViewModel.ShutdownAsync` disposes the broker and storage adapters without
synchronously blocking the WPF UI thread.

## Runtime data flow

```mermaid
flowchart TD
    Robinhood[Robinhood MCP] --> Adapter[Infrastructure adapter]
    Adapter --> Observation[Core market observations]
    Observation --> Buffer[Rolling strategy buffer]
    Buffer --> Strategy[Deterministic strategy and risk gates]
    Strategy --> Decision[Auditable decision]
    Decision --> Paper[Paper fill model]
    Decision --> Live[LiveOrderCoordinator]
    Live --> Review[Robinhood review and execution]
    Observation --> Chart[WPF chart projection]
    Observation --> Journal[SQLite journal]
    Decision --> Journal[SQLite journal]
    Paper --> Journal
    Review --> Journal
```

Chart candle selection is a presentation concern. The 15-, 30-, 60-, and
120-second display intervals do not change the strategy's source observations or
risk rules.

Paper Trader and LIVE share the real-time ingestion path, but not execution:

- Paper Trader sends strategy decisions only to the paper fill model.
- LIVE order review, placement, order polling, and cancellation reach the broker
  adapter through `LiveOrderCoordinator` after explicit arming and successful
  preflight. `MainViewModel` separately queries the broker port for account,
  position, tradability, and open-order state.
- Replay reads a bounded historical window and always uses simulated fills.

Symbol entry uses the `IInstrumentSearchSource` port for autocomplete suggestions.
Robinhood tradability capabilities and the Core `EquityMarketSessionEvaluator`
feed the `24HR` eligibility badge and the current `Tradable now` projection. Those
presentation states do not expand the broker-execution window described below.

## LIVE safety invariants

LIVE is intentionally fail-closed:

1. The application always starts in OFF, and selecting LIVE leaves execution
   disarmed.
2. The user must acknowledge the loss warning and explicitly start LIVE.
3. Account value, buying power, symbol tradability, the selected symbol's position,
   and its open orders are queried before arming. An open order for that symbol
   blocks LIVE startup.
4. An existing long position requires an explicit user choice: request an immediate
   reviewed sale, adopt it for profitable-exit monitoring, or cancel without an
   order. Quantity, average cost, available shares, and a fresh estimated sell price
   are shown before the choice. Broker state and price are refreshed afterward;
   changes fail closed. Adoption is blocked if the configured stop loss or daily
   loss would immediately liquidate the position, and shorts, missing cost basis,
   partially unavailable holdings, or quantities beyond broker order precision are
   not adopted. An inherited-position latch blocks every new entry until the app's
   matching SELL fully fills and a fresh broker snapshot confirms both a flat
   position and no open order. Rejection, partial fill, unexpected position changes,
   or an unresolved order keep execution stopped or disarmed for manual review.
5. Entry count is reconstructed from filled agentic BUY orders created since New
   York midnight. The daily-loss baseline comes from the first journaled LIVE
   starting balance for that trading day, falling back to the current portfolio
   value; it is not a separate broker daily-P&L query.
6. LIVE broker orders are limited to weekdays from 9:30 AM through 4:00 PM New
   York time and are submitted as `regular_hours` orders. A `24HR` eligibility
   badge or a positive `Tradable now` display does not widen that execution window.
7. Robinhood must review the exact intent before placement. Missing or malformed
   review data, any broker alert, stale pricing, or excessive review-price drift
   blocks the order.
8. Stable idempotency references and active-order reconciliation prevent duplicate
   placement while a submission result is uncertain.
9. STOP and application shutdown cancel the data session first. When retained or
   unresolved LIVE order context exists, including a submission with an uncertain
   placement response, they recover by stable client reference when necessary,
   request cancellation, and briefly poll broker state. If application shutdown
   cannot confirm a terminal broker state, closing pauses behind an explicit
   warning so the user can verify Robinhood or deliberately choose to exit anyway.
10. Replay and Paper Trader cannot submit a real broker order.

These rules span deterministic Core gates, the Application order coordinator, and
the view-model workflow below the visual controls; changing a button's visual
state does not bypass them.

## Persistence and credentials

All mutable application data lives under `%LOCALAPPDATA%\PriceSentinel3000`:

- OAuth tokens and dynamic client registration are encrypted with Windows DPAPI
  for the current user.
- The SQLite WAL journal records observations, decisions, simulated and LIVE order
  events and fills, plus paper-account position snapshots. LIVE broker position
  state is queried from Robinhood rather than persisted as a position snapshot.
- `SqliteJournalSchema` isolates schema creation and migrations from the journal's
  read/write operations.
- JSON preferences contain ordinary UI and research settings only.

Passwords are never requested or stored. Runtime databases, token files, and local
preferences are excluded from source control.

## Testing strategy

- `PriceSentinel3000.Core.Tests` uses deterministic observations to verify
  aggregation, strategy, paper-account, settlement, re-entry, and risk behavior.
- `PriceSentinel3000.Application.Tests` exercises session cancellation, Replay
  pause/resume and pacing, real-time reconciliation, and fail-closed LIVE order
  review, duplicate suppression, recovery, cancellation, and disposal.
- `PriceSentinel3000.Infrastructure.Tests` covers Robinhood payload parsing,
  authentication-state protection, preference persistence, and SQLite journaling
  without involving WPF.
- Application runners accept fakeable ports and `TimeProvider`, keeping orchestration
  tests deterministic and independent of Robinhood or the system clock.
- The Windows CI workflow restores, builds Release with warnings treated as errors,
  and runs the complete test suite on every pull request and push to `main`.

The architecture deliberately avoids a message bus, generic repository layer, and
framework-heavy mediator abstractions. Explicit coordinators and ports keep the
safety-critical flow visible to a reviewer and straightforward to test.
