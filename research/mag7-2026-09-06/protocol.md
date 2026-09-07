# Magnificent Seven experiment protocol

Requested September 6, 2026. Run Apple (AAPL), Microsoft (MSFT), Alphabet Class A
(GOOGL), Amazon (AMZN), NVIDIA (NVDA), Meta (META), and Tesla (TSLA) for each weekday
from August 31 through September 4, 2026. Each session runs from 06:30 to 13:00
in the America/Los_Angeles timezone. These seven companies comprise the
[Magnificent Seven](https://www.roundhillinvestments.com/etf/mags/).

The candidate set and selection rule below were fixed before inspecting any
MAG7 development prices or results. One attempt to obtain AAPL history for
August 31 had already returned no history. No Friday data will be inspected
until selection is frozen.

| Profile | Entry RSI | Exit RSI |
| --- | ---: | ---: |
| Baseline (NFLX) | 50 | 50 |
| Original | 50 | 45 |
| Patient exit | 50 | 40 |
| Stronger entry | 55 | 50 |

All four profiles use the same original EMA8/EMA21/RSI7 trend and pullback logic,
completed one-minute candles, and an 85-bar warmup. Each run starts with a fresh
$1,400 account and uses fixed $500 positions, immediate settlement, unlimited
entries, a 1% position stop, a $50 daily loss limit, and a 15-minute buffer.
Existing host re-entry protections remain active. The script logic and candidate
parameter set will not be expanded after seeing results. Monday through Thursday
are development dates; Friday is held out.

Select a profile separately for each stock by the highest unrounded sum of daily
P&L across available development dates. Daily P&L is final account equity minus
$1,400. All candidates must use identical available dates and historical data.
Break exact ties by lower maximum daily equity drawdown, then fewer entries,
then the profile order: Baseline, Original, Patient exit, Stronger entry. Never
use Friday to select or tune a profile. Accounts reset daily, and any position
still open at the session's end is marked to market without a forced closing
trade. Spread, fees, and slippage are not modeled.

Only the coordinating agent controls the visible app through MCP. Retain each
run's status, pinned source and settings, source and strategy candles, indicator
values, events, fills, and account snapshots. Report missing history as
unavailable, never as zero P&L. Independently verify session cutoffs, data gaps
and warmup behavior, complete research streams, identical historical inputs
across candidates, account calculations, and profile selection.

After selection, create one compatible script file per stock with the company
name and ticker in its filename stem, which supplies the app's dropdown title.
Re-run each selected, named script on the development dates to confirm identical
behavior, then run it on Friday. Report the fitted development results and
Friday results separately. The best of four tested profiles is not a global
profit maximum.
