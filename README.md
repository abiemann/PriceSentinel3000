# PriceSentinel 3000

Real-price monitoring, historical playback, and paper-first trade research
for a user-selected stock or ETF.

> [!WARNING]
> This project is experimental software, not financial advice. Trading involves
> substantial risk of loss. Validate every strategy in Paper Trader and Replay
> before considering live execution.

## Status

Stage 4 replaces the synthetic feed with authenticated Robinhood MCP market data:

- OFF / Replay / Paper Trader / LIVE rotary mode selection, with OFF at startup
- Paper Trader polls real Robinhood quotes at the configured interval and can
  never submit a real order
- Four-minute startup history and 45-second overlap reconciliation use real
  15-second Robinhood equity bars
- Replay accepts a ticker plus an exact local date/time and emits that historical
  15-second window as though each observation has just arrived
- Replay duration (1-480 minutes) and playback speed (1x-100x) are tunable
- A tunable 5-15 minute rolling buffer is analyzed as serial one-minute blocks
- A WPF price chart shows the real or replayed stream, current price, bid/ask,
  minute-block direction, close, and quote counts
- SQLite WAL journaling records sessions, observations, and activities, with
  reserved tables for decisions, paper orders, fills, positions, and risk events
- Separate selected and effective modes keep LIVE execution safely disarmed
- Startup is gated by a welcome dialog: EXIT closes the app, while LOGIN must
  establish Robinhood authorization before the workspace opens safely in OFF mode
- The first LIVE selection still shows the loss warning before entering disarmed LIVE
- Friendly status guidance distinguishes REAL TIME, MARKET CLOSED, REPLAY,
  AUTHORIZING, and OFFLINE states

Paper Trader is the safe account mode for real-price strategy testing and future
simulated fills. The automatic signal and paper-fill engine is the next stage, so
Stage 4 observes and journals prices but does not create trades yet. LIVE remains
explicitly disarmed and no order-submission tool is called anywhere in the app.

## Paper Trader workflow

1. At startup, click **LOGIN** and complete Robinhood's hosted browser
   authorization. PriceSentinel never asks for or stores a Robinhood password.
2. Select **Paper Trader**, enter a stock or ETF symbol and paper starting balance,
   configure the risk and timing settings, then click **Start Paper Trader**.
3. The app loads up to four minutes of real 15-second history, obtains the current
   quote, and then polls the current quote at the configured interval.
4. At each reconciliation interval, the app requests the full interval plus the
   configured overlap. Matching timestamps are verified, corrections replace old
   values, and missing bars are added to the ring buffer.
5. If the newest venue timestamp is old, the app says **MARKET CLOSED** instead of
   presenting the last price as a fresh real-time quote.

There is no generated-price fallback. If authorization, Robinhood, or the network
is unavailable, the session stops and reports the failure.

## Replay workflow

1. After the required startup login, select **Replay** and enter the ticker,
   local date (`yyyy-MM-dd`), local start time (`HH:mm`), duration, and playback
   speed, then click **Start Replay**.
2. One bounded request loads actual 15-second Robinhood bars for precisely that
   start/end window, using extended-hours bounds.
3. The returned observations are replayed in source-time order. Each historical
   price enters the normal ring buffer as a newly observed event, with delays
   compressed by the selected speed.

Replay does not depend on a previously recorded Paper Trader session and never
uses the former synthetic data.

## Authentication and local data

Robinhood is connected through the official Streamable HTTP MCP endpoint and OAuth
authorization flow. Access/refresh tokens and dynamic client registration are
encrypted with Windows Data Protection API for the current Windows user at:

~~~text
%LOCALAPPDATA%\PriceSentinel3000\robinhood-tokens.dat
%LOCALAPPDATA%\PriceSentinel3000\robinhood-client.dat
~~~

The SQLite journal is stored at:

~~~text
%LOCALAPPDATA%\PriceSentinel3000\journal.db
~~~

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
