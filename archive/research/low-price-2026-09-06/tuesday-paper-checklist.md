# Tuesday September 8: real-time Paper acceptance

Status: **pending**. The research sessions are historical Replay tests. Tuesday
uses incoming real quotes and simulated money. No release has been cut for this
research milestone.

## Before the regular session

- Open the intended build with `--automation`; record its version and process ID.
- Refresh the strategy catalog and verify the three installed filenames and
  source hashes against the final script manifest. Match the selected symbol to
  the company named in the script; the filename does not enforce that symbol.
- Confirm the computer is on Pacific time. Keep the tested $1,400 starting
  balance, $500 position amount, 1% stop, $50 daily loss limit, immediate
  settlement, one-minute strategy interval, and five-second quote poll settings.
- Run short Replay checks of each script's discovery, pinned source, indicators,
  and complete exports. These checks do not replace real-time Paper testing.

## During 06:30-13:00 Pacific

- Select **Paper Trader**, then start a fresh session. Confirm effective mode
  `PaperTrader`, broker execution disabled, and a new session identity. Do not
  use fast Replay, stepping, or historical replay timestamps as Paper evidence.
- Inspect live source timestamps and receipt times through MCP. Confirm fresh
  quotes advance with wall-clock time and the selected ticker; record any stale
  quotes, missing polls, or market-data disconnections.
- Verify each one-minute strategy candle closes only after its end time. Compare
  OHLC against that session's observed quotes. Confirm the required warmup from
  the installed profile and that gaps restart warmup when appropriate.
- Check EMA/RSI/channel or mean values as applicable, then any proposed signals,
  host risk overrides, simulated fills, quantities, and account equity. A day
  without an entry is valid observation, not a demonstrated fill-path test.
- Verify the market/tradability display against the current time and broker
  response, including the transition at 13:00 Pacific. A preserved Replay chart
  must not make the live status claim the historical session is currently open.
- Page and save source candles, strategy candles, and events incrementally during
  the session, pinning its session ID and keeping each stream's next cursor.
  Check truncation, sequence continuity, and page completion each time. At a
  five-second poll interval a full day can exceed the 4,096-record retention
  limit; byte limits can discard records sooner. A final-only export is not
  enough. Capture final results and remaining pages before starting another run.
  Record gaps, indicator state, risk overrides, fills, final exposure, and any
  unexpected journal messages. Stop/start should establish a fresh session and
  account according to the configured starting balance.

The current interface controls one visible app session at a time. A full-day
Paper run on one selected stock does not simultaneously validate the other two.
Use additional sequential Paper sessions if needed and explicitly record which
profiles have received real-time coverage. Do not shorten or fabricate warmup
to force trades before the session ends. Forced stop-loss, entry-cap, and
daily-loss scenarios belong in deterministic Replay tests if the real market
does not naturally exercise them.

## Release decision

Review the actual Paper evidence and any unresolved defects together. Require
the intended source changes, relevant tests, release build, version provenance,
and packaged script discovery to pass before tagging the next release. Historical
P&L is research evidence; real-time operational correctness is a separate gate.
Tuesday testing and release publication remain pending until performed.
