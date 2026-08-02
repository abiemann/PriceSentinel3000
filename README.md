# PriceSentinel 3000

Real-price monitoring, historical playback, and paper-first trade research
for a user-selected stock or ETF.

> [!WARNING]
> This project is experimental software, not financial advice. Trading involves
> substantial risk of loss. Validate every strategy in Paper Trader and Replay
> before considering live execution.

## Status

Stage 5 adds an auditable paper strategy and simulated execution engine to the
authenticated Robinhood data foundation:

- OFF / Replay / Paper Trader / LIVE rotary mode selection, with OFF at startup
- Paper Trader polls real Robinhood quotes at the configured interval, evaluates
  a deterministic reactionary strategy, and can never submit a real order
- Four-minute startup history and 45-second overlap reconciliation use real
  15-second Robinhood equity bars
- Replay accepts a ticker plus an exact local date/time and emits that historical
  15-second window as though each observation has just arrived
- Replay duration (1-480 minutes) and playback speed (1x-100x) are tunable
- A tunable 5-15 minute rolling buffer is analyzed as individual one-minute
  blocks and as a whole
- Bottom detection combines a meaningful decline, lingering or separated
  low-zone touches, a confirmed positive turn, and simple-average RSI(14)
- Peak detection combines open-position profit, repeated peak or pullback
  evidence, negative momentum, RSI context, and a five-minute profitable-stall exit
- Live-price paper buys fill at the observed ask and sells at the observed bid.
  Historical Replay has no bid/ask series, so its simulated fills use the bar
  close; no fill is generated from a stale closed-market quote
- Paper position sizing supports a fixed dollar amount or account percentage,
  plus unlimited quantity or a user-entered maximum-share limit
- Maximum entries, maximum daily loss, fixed/percentage stop loss, and a
  30-second re-entry cooldown are enforced before simulated execution
- The WPF chart always renders 15-second candlesticks. Replay preserves
  Robinhood's true OHLC bars, while Paper Trader aggregates incoming quotes into
  live candles; the chart also shows current price, bid/ask, and minute blocks
- The chart labels simulated BUY and SELL fills, while Session Status shows paper
  buying power, equity, position, realized/unrealized P&L, and entry count
- SQLite WAL journaling records sessions, observations, every strategy decision,
  paper orders, fills, position snapshots, and activities
- Separate selected and effective modes keep LIVE execution safely disarmed
- Startup silently restores a saved Robinhood session when possible; otherwise
  the welcome dialog offers EXIT or LOGIN before opening safely in OFF mode
- The first LIVE selection still shows the loss warning before entering disarmed LIVE
- Friendly status guidance distinguishes REAL TIME, MARKET CLOSED, REPLAY,
  AUTHORIZING, and OFFLINE states

Paper Trader and Replay now create simulated trades from real Robinhood prices.
The current thresholds are documented research defaults inferred from the example
charts; they are a premise to test, not evidence of profitability. LIVE remains
explicitly disarmed and no order-submission tool is called anywhere in the app.

## Paper Trader workflow

1. On the first startup, click **LOGIN** and complete Robinhood's hosted browser
   authorization. Being signed in is only the first step: finish the on-screen
   connection approval and wait for the PriceSentinel completion page.
   PriceSentinel never asks for or stores a Robinhood password. Later launches
   silently restore the encrypted saved session and open the workspace directly.
2. Select **Paper Trader**, enter a stock or ETF symbol and paper starting balance,
   configure the risk and timing settings, then click **Start Paper Trader**.
3. The app loads up to four minutes of real 15-second history, obtains the current
   quote, and then polls the current quote at the configured interval.
4. At each reconciliation interval, the app requests the full interval plus the
   configured overlap. Matching timestamps are verified, corrections replace old
   values, and missing bars are added to the ring buffer.
5. Each fresh quote evaluates the block/whole-buffer strategy. Confirmed entries
   and exits update only the in-memory paper account, then persist the decision,
   order, fill, and position snapshot to SQLite.
6. If the newest venue timestamp is old, the app says **MARKET CLOSED** and pauses
   strategy decisions and paper fills.

There is no generated-price fallback. If authorization, Robinhood, or the network
is unavailable, the session stops and reports the failure.

## Stage 5 research defaults

The first deterministic detector uses the supplied labeled screenshots as a
starting hypothesis:

- simple-average RSI period: 14 observations
- low/high touch-zone tolerance: 0.06%
- minimum pre-turn swing: 0.10%
- minimum 20-second reversal confirmation: 0.025%
- bottom RSI confirmation: at or below 48 and no longer falling
- minimum profitable peak exit: 0.04% before bid-side spread impact
- profitable-stall fallback: five minutes with non-positive momentum

These constants deliberately live in the strategy core and every decision stores
its confidence and human-readable evidence. Replay results should be used to tune
them later; they are not a promise that the labeled regions can be captured live.

## Replay workflow

1. After the required startup login, select **Replay** and enter the ticker,
   local date (`yyyy-MM-dd`), local start time (`HH:mm`), duration, and playback
   speed, then click **Start Replay**.
2. One bounded request loads actual 15-second Robinhood bars for precisely that
   start/end window, using extended-hours bounds.
3. The returned observations are replayed in source-time order. Each historical
   price enters the normal ring buffer as a newly observed event, with delays
   compressed by the selected speed.
4. Replay uses the same paper account, strategy, risk controls, fill model, chart
   markers, and journal as Paper Trader, making a historical run reproducible.

Replay does not depend on a previously recorded Paper Trader session and never
uses the former synthetic data.

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

At startup, a saved token is tried without permitting an interactive browser
redirect. A valid access token or refresh token opens the main window directly.
If the cache is missing, invalid, revoked, corrupt, or cannot be verified, the
welcome dialog appears and offers LOGIN or EXIT.

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

The preferences file contains ordinary UI values only. Robinhood credentials and
tokens are never written to it.

The journal uses normalized tables, indexed symbol/timestamp lookups, prepared
inserts, short transactions, and write-ahead logging. Broker passwords are never
stored in the database.

## Solution layout

~~~text
PriceSentinel3000.sln
src/
  PriceSentinel3000.App/             WPF desktop interface
  PriceSentinel3000.Core/            Market, paper-account, strategy, and risk models
  PriceSentinel3000.Infrastructure/  Robinhood MCP OAuth/data and SQLite adapters
tests/
  PriceSentinel3000.Core.Tests/      Deterministic market, persistence, and safety tests
~~~

Dependencies point inward: the application references Core and Infrastructure,
Infrastructure references Core, and Core remains independent of WPF, Robinhood,
and SQLite. The market-data and journal contracts retain an asset-class boundary
so a future crypto adapter can reuse the buffer and persistence engine.

## Development

Requirements:

- Visual Studio 2026 with the .NET desktop development workload
- .NET SDK 10.0.302 or a compatible .NET 10 patch
- A Robinhood account eligible for Agentic Trading access

Open `PriceSentinel3000.sln` in Visual Studio, or use:

~~~powershell
dotnet build PriceSentinel3000.sln
dotnet test PriceSentinel3000.sln
~~~

The workspace always starts OFF after the required Robinhood connection succeeds.
Accepting the LIVE warning makes LIVE the effective mode, but broker execution
remains disarmed until a later, separately reviewed implementation stage adds
account, risk, order-preview, and order-submission safeguards.
