# USO script research: insufficient historical data

Requested September 6, 2026: research a United States Oil Fund (USO) script for
August 31 through September 4, using the same 06:30-13:00 Pacific experiment as
our earlier stocks. **No candidate qualified.** The available development data
never let any of the five frozen candidates finish warmup or place a trade.
The recorded flat accounts cannot establish profitability or rank the formulas.

## Research starter

[United States Oil Fund (USO) Confirmation - experimental](<../../../research/strategies/United States Oil Fund (USO) Confirmation - experimental.thinkscript>)
is an unchanged copy of our original C1 baseline, supplied for future research.
It is **not optimized or performance-validated for USO**. Its
[manifest](script-manifest.json) records `UNVALIDATED_RESEARCH_STARTER` and
`selectedByPerformance: false`. Match the app's symbol to **USO** manually.

The formula uses EMA8/EMA21 and RSI7 on completed one-minute candles. It proposes
a long entry when price recovers above EMA8, with EMA8 above a non-declining
EMA21 and RSI7 at least 50 and improving. It proposes an exit below EMA21, when EMA8 falls below
EMA21, or when RSI7 falls below 50. The host applies sizing, re-entry restrictions,
stop loss and daily loss controls. The app requires 85 uninterrupted completed
one-minute bars before evaluating entry/exit conditions.

This named file has the same source hash as C1:
`e5c76250cd82438ec3c2fa4f8c73230ac067d2292a6803ababf64cae48547abc`.
Its two development verification runs exactly match the original C1 exports.
Matching runs with no activity verifies reproducibility, not trading performance.

## What was tested

The [protocol](protocol.md), five original formulas and inputs were frozen in
commit `de74456` before USO Replay inspection. C1/C2/C3 compare trend-pullback
confirmation thresholds; B1 tests a prior-channel breakout; R1 tests oversold
recovery toward a moving average. No forum script was copied, and no formula or
warmup was changed to fit incomplete USO data.

All five ran on Wednesday and Thursday, for ten development sessions. The app
reported no usable historical observations for Monday and Tuesday. The frozen minimum
was two development dates with at least 1,529/1,560 source observations, exact
session boundaries, matching history across all candidates, and at least one
entry from a selected profile. No date met the coverage rule and no script traded.
The [frozen selection](frozen-selection.json) therefore contains an empty array.

The [availability decision](availability-decision.md) preserves that rule and
declares unchanged C1 as Friday's diagnostic reference before fetching Friday.
The empty selection and verified starter were committed in `1900dce` before
the Friday run.
Friday is a data/warmup check, not a held-out test of a selected winning script.

## Daily evidence

Every Replay starts a fresh $1,400 account with fixed $500 positions, immediate
settlement, unlimited entries, a 1% purchase-price stop, a $50 daily loss limit,
and existing host re-entry guards. Source observations are historical 15-second
OHLC bars. Completed one-minute script bars must come from the supplied history.
Missing periods are not filled or treated as known prices.

| Date | Usable source observations | Coverage | Gap resets | Longest uninterrupted minute-bar history | Observed account P&L |
| --- | ---: | ---: | ---: | ---: | --- |
| Mon Aug 31 | Unavailable | unavailable | unavailable | unavailable | Unavailable |
| Tue Sep 1 | Unavailable | unavailable | unavailable | unavailable | Unavailable |
| Wed Sep 2 | 1,369 / 1,560 | 87.76% | 155 | 23 bars | $0.00; never ready |
| Thu Sep 3 | 1,247 / 1,560 | 79.94% | 215 | 30 bars | $0.00; never ready |
| Fri Sep 4, C1 diagnostic | 1,092 / 1,560 | 70.00% | 287 | 11 bars | $0.00; never ready |

All five candidates share Wednesday and Thursday's outcomes and source history.
Only the unchanged C1 reference ran on Friday, after recording that no candidate
qualified. Every completed account ended flat at exactly $1,400, with no orders
or fills. All available histories have exact requested first/final boundaries,
but fail the coverage criterion internally. Friday produced 122 completed
one-minute candles in total; its maximum usable contiguous history was 11.

**There is no weekly strategy return or winning candidate to report.** The $0
account observations must not be compared as active-strategy performance with
our NVIDIA, Netflix, or other research results.

## Why the data cannot answer the P&L question

The gap handler resets retained strategy history when the next source interval
does not begin at the preceding interval's end. The total number of candles over
the day therefore differs from the uninterrupted history available for warmup.
Wednesday produced 243 completed one-minute candles in total, but its longest
uninterrupted stretch was only 23; Thursday produced 185, with a maximum of 30.
The shortest candidate requires 51 bars, while the others require 84 or 85.

Zero trades means gross P&L, realized/unrealized P&L, equity drawdown and all
turnover-based cost sensitivities remain zero. Those are account observations,
not evidence of a profitable or low-risk strategy. Unavailable dates have no P&L.
The cause of the missing source intervals has not been established; this research
does not infer that USO was illiquid or that a specific service lost records.
The adapter discards null bars and bars marked interpolated, then sorts and
deduplicates timestamps. These Replay exports contain accepted observations, so
they cannot distinguish omitted provider records from bars excluded by that
filtering. Zero volume alone is not a rejection rule. See the
[history parser](../../../src/PriceSentinel3000.Infrastructure/MarketData/RobinhoodMarketDataParser.cs)
and [script candle series](../../../src/PriceSentinel3000.Application/Strategies/ScriptBarSeries.cs).

## Fund and event context

USO obtains exposure through oil futures and is not an oil-company stock or a
direct holding of physical crude. Its daily NAV objective, contract rolls and
market price can behave differently from spot oil. We therefore use USO's own
share history. [USCF fund overview](https://www.uscfinvestments.com/uso).

The EIA petroleum report was scheduled Wednesday September 2 at 07:30 Pacific,
within the requested session. Even uninterrupted C1 history starting at 06:30
would only finish warmup at 07:55; this experiment does not evaluate an entry
strategy for the immediate release. The full [market context](market-context.md)
links dated EIA and BLS sources and distinguishes event timing from price causation.

## Evidence and next step

Ledger and warmup checks passed for all 11 primary runs (ten development and one
Friday diagnostic); all development candidate source sequences match on each
date. Two additional named-script verification runs matched C1 exactly, giving
13 completed Replays plus two unavailable attempts. The independent indicator audit records
zero ready evaluations and zero available indicator-value comparisons. This
confirms withheld evaluation during warmup, not indicator or signal behavior on
an active USO session. The five byte-identical formulas retain their earlier
40-case synthetic validation, and the USO/shared audit tools passed 24 tests.

Reports: [development ledger](development-audit.json),
[development indicator/warmup audit](development-indicator-audit.json),
[final ledger and availability audit](final-audit.json),
[final indicator/warmup audit](final-indicator-audit.json),
[named-source equivalence](verification-comparison.json),
[verification warmup audit](verification-indicator-audit.json),
and [reproduction commands](../../../research/tools/uso/README.md). Exact raw MCP exports remain local
under the ignored `artifacts/uso-research` directory. App version: `1.2-dev.17`;
runtime: `thinkscript-subset-v1`; data model: `completed-price-bars-v1`.
The final audit rechecked all frozen development export, source and data hashes;
Friday did not change the empty selection or research starter.

The named starter is installed in the user's Strategies folder and catalogued
as compatible without errors. The five temporary USO candidates were removed
after source-hash checks, and the previous idle Replay settings were restored.
Earlier unrelated working-tree changes remain untouched. No app feature, live
broker execution, or release tag was changed.

USO needs sufficiently continuous history or a fresh real-time Paper recording
before script selection and P&L comparisons are meaningful. A future test should
keep the starter frozen and export live observations incrementally. These results
do not displace NVIDIA as the previously recommended Tuesday Paper candidate.
