# Magnificent Seven: full-day script experiment

Requested September 6, 2026: create a named experimental script for each of the
Magnificent Seven and measure daily paper P&L for August 31 through September 4,
2026, from **06:30 to 13:00 Pacific**. Every run uses a fresh **$1,400 account and
fixed $500 positions**. All sessions run through the PriceSentinel MCP interface
in Replay mode; they place no real orders.

Seven company-named scripts were created and installed. The selected profiles
produce positive available-day totals for Microsoft, Alphabet, and NVIDIA, and
negative totals for Apple, Amazon, Meta, and Tesla. Friday was evaluated after
selection and did not change any script or threshold.

Robinhood returned no usable history for Monday or Tuesday for any of the seven
stocks. Those dates remain unavailable. The available-day totals therefore cannot
represent the entire requested week. The reason for the missing history was not
established.

## Daily P&L

Wednesday and Thursday are **fitted development results**: those two days were
used to select a profile separately for each stock. Friday is **held out** and
was evaluated only after all seven choices had been frozen. Dollar amounts below
sum unrounded daily equity changes before rounding totals for display.

| Stock | Mon Aug 31 | Tue Sep 1 | Wed Sep 2, fitted | Thu Sep 3, fitted | Fri Sep 4, held out | Available-day total |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| Apple (AAPL) | Unavailable | Unavailable | -$1.85 | -$0.80 | -$0.69 | **-$3.34** |
| Microsoft (MSFT) | Unavailable | Unavailable | +$1.02 | +$3.24 | -$0.34 | **+$3.92** |
| Alphabet (GOOGL) | Unavailable | Unavailable | -$0.12 | +$1.09 | +$0.08 | **+$1.05** |
| Amazon (AMZN) | Unavailable | Unavailable | -$0.04 | -$0.45 | -$0.77 | **-$1.26** |
| NVIDIA (NVDA) | Unavailable | Unavailable | +$4.58* | +$0.73 | +$0.57 | **+$5.88** |
| Meta (META) | Unavailable | Unavailable | -$0.19 | -$1.83 | +$0.21* | **-$1.82** |
| Tesla (TSLA) | Unavailable | Unavailable | -$0.51 | +$0.35 | -$0.72 | **-$0.88** |

\* NVIDIA on September 2 and Meta on September 4 end with open positions. Their
P&L includes the position marked at the final observed price. Every other
selected run ends flat. See the accounting discussion below.

The exact totals across the seven independently funded stocks are
**+$5.2009108446 for development** and **-$1.6496696446 for held-out Friday**,
giving **+$3.5512412000 across available days**. This aggregate combines fitted
and held-out results, and is not a return from a shared $1,400 portfolio. In
particular, the positive aggregate does not mean all seven scripts were
profitable or that Friday independently confirmed the development gains.

## Named scripts and selection

The [protocol](protocol.md) fixed four profiles before inspecting any MAG7
development prices or P&L. Only RSI entry and exit thresholds differ. All four
share our original EMA8/EMA21/RSI7 trend and pullback logic, and no candidate was
added after inspecting results.

For each stock, select the highest exact sum of available Monday–Thursday daily
P&L. Because Monday and Tuesday have no history, this means September 2 and 3.
Exact ties use lower maximum daily equity drawdown, then fewer entries, then the
predeclared order Baseline, Original, Patient exit, Stronger entry. Microsoft
Baseline and Stronger entry tie on all three measured criteria, so Baseline wins
by that final rule. The [frozen selection](frozen-selection.json) was recorded
before Friday was inspected; [the script manifest](script-manifest.json) records
the resulting filenames and source hashes.

The selection was committed as `b323fae`, and the named scripts as `ebcfa23`,
before Friday testing. No Friday outcome was used to revise either milestone.

Each link below opens that stock's final script. The app displays the filename
stem as its title, including the full company name and ticker. The user must
match the app's selected symbol to the script manually; these scripts do not
enforce a ticker or automatically switch symbols.

| Script | Selected profile | Entry / exit RSI | NFLX baseline, Wed–Thu | Selected, Wed–Thu | Change |
| --- | --- | ---: | ---: | ---: | ---: |
| [Apple (AAPL)](<../../../research/strategies/Apple (AAPL) Confirmation - experimental.thinkscript>) | Stronger entry | 55 / 50 | -$3.09 | -$2.66 | +$0.43 |
| [Microsoft (MSFT)](<../../../research/strategies/Microsoft (MSFT) Confirmation - experimental.thinkscript>) | Baseline | 50 / 50 | +$4.25 | +$4.25 | $0.00 |
| [Alphabet (GOOGL)](<../../../research/strategies/Alphabet (GOOGL) Confirmation - experimental.thinkscript>) | Stronger entry | 55 / 50 | +$0.63 | +$0.96 | +$0.34 |
| [Amazon (AMZN)](<../../../research/strategies/Amazon (AMZN) Confirmation - experimental.thinkscript>) | Original | 50 / 45 | -$1.36 | -$0.49 | +$0.87 |
| [NVIDIA (NVDA)](<../../../research/strategies/NVIDIA (NVDA) Confirmation - experimental.thinkscript>) | Original | 50 / 45 | +$4.31 | +$5.31 | +$1.01 |
| [Meta (META)](<../../../research/strategies/Meta (META) Confirmation - experimental.thinkscript>) | Original | 50 / 45 | -$2.11 | -$2.02 | +$0.09 |
| [Tesla (TSLA)](<../../../research/strategies/Tesla (TSLA) Confirmation - experimental.thinkscript>) | Stronger entry | 55 / 50 | -$1.37 | -$0.16 | +$1.21 |

The NFLX baseline uses entry RSI 50 and exit RSI 50; it is not the app's Built-In
strategy. Four stocks still lose money on the selected development profile.
Selection identifies the best of four tested profiles, not a global profit
maximum or a guarantee that every stock becomes profitable. Several stocks
select the same parameters, so these are seven named profiles of shared logic,
not seven unrelated strategies.

## Accounting and fixed conditions

Daily P&L is **final equity minus $1,400**, including any unrealized change in an
open position at the final observed price. Positions are not forcibly liquidated
at 13:00. Cash, holdings, and prior-trade state reset before the next daily run.
Totals are sums of independent daily experiments, without compounding or a
shared account across the seven stocks.

The selected NVIDIA profile ends September 2 with **2.228163 shares**, marked at
$224.40. That day's realized P&L is **+$4.581862851** and unrealized P&L is **$0**.
Zero unrealized P&L does not mean the account is flat; the last buy remains open
at its purchase price.

The selected Meta profile ends Friday with **0.810530 shares**, bought at $616.88
and marked at $616.75. Its realized P&L is **+$0.3119902764**, its unrealized P&L
is **-$0.1053689**, and its equity P&L is **+$0.2066213764**. These two open
positions are the only ending exposures in the 21 selected runs. Their values
are included in account equity, and no subsequent sale or overnight price change
is assumed.

All candidates and final scripts use these fixed conditions:

- Starting balance $1,400; fixed $500 position sizing; fractional quantities
  truncated to six decimal places by the host.
- No configured quantity or entry-count caps, immediate paper settlement, a 1%
  position stop, and a $50 daily loss limit. Existing host re-entry protections remain
  active; scripts cannot override them.
- EMA lengths 8 and 21; RSI length 7; completed one-minute strategy candles;
  a 15-minute buffer. Each fresh session needs 85 completed bars before it can
  act, making the first eligible evaluation **07:55 Pacific** on the complete
  development histories. Starting at 06:30 does not mean trading starts at 06:30.
- Robinhood historical 15-second OHLC candles, processed in timestamp order.
  Strategy bars become available at their ends; no future candle is used.
  Simulated fills occur at the observed source candle's closing price. Intrabar
  bid/ask spreads and execution uncertainty are not reconstructed.
- Fast Replay skips wall-clock delays without changing observation order.
  Local timezone America/Los_Angeles is PDT, UTC−07:00, on the requested dates.
- **Spread, fees, and slippage are not modeled.** These small gross simulated
  results cannot be treated as expected net trading returns.

Maximum daily drawdown is the largest observed decline from a running equity
peak within one daily account, starting from $1,400. It is measured at the
processed 15-second observations. The ranking appendix reports the larger
of the two development-day drawdowns, rather than summing them. Entries count
buys; an entry need not have a corresponding exit before the session ends.

## Evidence and verification

The 56 candidate development runs and fourteen no-history attempts passed the
independent audit with **zero invalid runs**. Each completed development run
contained **1,560 source observations, 390 strategy candles, and 1,560 events**,
covering exactly 06:30–13:00 with no source gaps. Source timestamps, OHLC, and
volume match across candidates for the same stock and date.

The [final audit](final-audit.json) covers **21 selected-script runs with zero
invalid runs**: fourteen development reruns and seven held-out Friday runs. The
fourteen development reruns using the final company-named files have **zero
behavioral differences** from the frozen winning candidates. The comparison
covers source candles, strategy candles, events, fills, and final account
values, excluding only changing observation wall-clock times and generated
order identifiers.

Friday is available for all seven stocks, but three histories each omit one
15-second candle. Those gaps remain in the experiment and reset the script's
85-bar warmup. All three gaps occurred while the selected account was flat;
strategy signals became eligible again only after warmup completed. All seven
runs still cover the requested start and final close.

| Stock, Friday | Missing source interval, Pacific | Source / strategy candles | First ready after gap, Pacific |
| --- | --- | ---: | --- |
| Microsoft (MSFT) | 11:15:30–11:15:45 | 1,559 / 389 | 12:41 |
| Amazon (AMZN) | 10:32:45–10:33:00 | 1,559 / 389 | 11:58 |
| Meta (META) | 09:53:45–09:54:00 | 1,559 / 389 | 11:19 |

The other four Friday histories contain 1,560 source candles and 390 strategy
candles with no gaps. Every selected run first becomes ready at 07:55 Pacific;
the table above records the additional warmup after a later gap. No artificial
candles are inserted to make these incomplete histories appear complete.

The audits check fixed settings and script hashes, profile inputs, complete
research streams, timestamp cutoffs, source availability, one-minute OHLCV
aggregation, warmup, fill sizing and prices, account reconstruction, and profile
ranking. Accounting uses decimal arithmetic with a tolerance of $0.000000001
only when checking exported values. Selection and reported totals use unrounded
values; displayed cents are not used to rank candidates.

Recorded runtime: app **1.2-dev.10**, script runtime **thinkscript-subset-v1**,
data model **completed-price-bars-v1**. Each raw export retains its session ID,
pinned source and settings, source and strategy candles, indicator values,
signals, host risk overrides, fills, and account snapshots. Raw exports are in
the local, Git-ignored `artifacts/mag7-research` directory. The committed frozen
selection and manifest retain the source/data hashes and selection provenance.

From the repository root, rerun the audits against those local exports:

```powershell
pwsh -NoProfile -File research/tools/mag7/audit-and-select.ps1 -Directory artifacts/mag7-research
pwsh -NoProfile -File research/tools/mag7/audit-selected.ps1 -Directory artifacts/mag7-research -FrozenSelection archive/research/mag7-2026-09-06/frozen-selection.json
```

These commands verify captured evidence; they do not fetch missing history or
replay the app. Repeating simulations requires the same data, pinned source,
runtime, and fixed settings. No known retention rule explains the unavailable
dates, and no synthetic prices substitute for them.

## Interpretation

Only two days informed parameter selection, and only Friday is independent of
that selection. Development improvements are expected when choosing among
candidates on those same dates. On Friday, three selected scripts produce a
positive equity change and four produce a loss; the combined Friday result is
negative. Those outcomes are retained without choosing another winner. Friday
was tested only for the selected scripts, so it does not measure their
improvement over every alternative out of sample. This experiment alone does
not establish a durable advantage for any stock or profile.

The full-day window is the user's requested condition. These MAG7 runs do not
compare other start or end times, so they do not establish that the full trading
day maximizes every stock's P&L. Real-time Paper testing and evaluation on more
untouched dates remain necessary before stronger conclusions are justified.

## Appendix: all development candidate rankings

Each row sums September 2 and 3 only. P&L and drawdown are in dollars; exact
values are shown so small differences and the Microsoft tie remain visible.
Within each stock, rows follow the frozen selection order.

| Stock | Rank | Profile | Entry / exit RSI | Exact development P&L | Maximum daily drawdown | Entries |
| --- | ---: | --- | ---: | ---: | ---: | ---: |
| AAPL | 1 | Stronger entry | 55 / 50 | -2.6565495185 | 2.399056535 | 8 |
| AAPL | 2 | Baseline | 50 / 50 | -3.0877565185 | 2.539508055 | 9 |
| AAPL | 3 | Original | 50 / 45 | -3.6874537085 | 3.221553845 | 10 |
| AAPL | 4 | Patient exit | 50 / 40 | -5.4466911497 | 3.510252765 | 11 |
| MSFT | 1 | Baseline | 50 / 50 | 4.2546736400 | 1.077647760 | 5 |
| MSFT | 2 | Stronger entry | 55 / 50 | 4.2546736400 | 1.077647760 | 5 |
| MSFT | 3 | Original | 50 / 45 | 1.5566522663 | 2.7094205387 | 8 |
| MSFT | 4 | Patient exit | 50 / 40 | 1.5042711863 | 2.8154596187 | 8 |
| GOOGL | 1 | Stronger entry | 55 / 50 | 0.9647477950 | 0.8240871435 | 6 |
| GOOGL | 2 | Baseline | 50 / 50 | 0.6277406161 | 0.8240871435 | 7 |
| GOOGL | 3 | Original | 50 / 45 | 0.0741762011 | 1.2466295685 | 7 |
| GOOGL | 4 | Patient exit | 50 / 40 | -1.0037007218 | 1.6096618085 | 9 |
| AMZN | 1 | Original | 50 / 45 | -0.4907935500 | 2.173375955 | 10 |
| AMZN | 2 | Patient exit | 50 / 40 | -0.6683780640 | 2.356738210 | 9 |
| AMZN | 3 | Stronger entry | 55 / 50 | -1.2376703650 | 1.671379065 | 8 |
| AMZN | 4 | Baseline | 50 / 50 | -1.3635100600 | 1.941874065 | 10 |
| NVDA | 1 | Original | 50 / 45 | 5.3139462660 | 2.457555549 | 8 |
| NVDA | 2 | Baseline | 50 / 50 | 4.3056797207 | 3.8906030743 | 10 |
| NVDA | 3 | Patient exit | 50 / 40 | 4.0682672685 | 3.7032345465 | 9 |
| NVDA | 4 | Stronger entry | 55 / 50 | 3.7413847195 | 3.8889814755 | 10 |
| META | 1 | Original | 50 / 45 | -2.0232068261 | 2.739396405 | 9 |
| META | 2 | Stronger entry | 55 / 50 | -2.1088467267 | 2.727960248 | 9 |
| META | 3 | Baseline | 50 / 50 | -2.1108081435 | 2.3991786848 | 10 |
| META | 4 | Patient exit | 50 / 40 | -2.9207572861 | 2.739396405 | 9 |
| TSLA | 1 | Stronger entry | 55 / 50 | -0.1619069618 | 2.0356833296 | 5 |
| TSLA | 2 | Baseline | 50 / 50 | -1.3730918022 | 2.4707149804 | 9 |
| TSLA | 3 | Original | 50 / 45 | -2.2025484828 | 2.259814880 | 9 |
| TSLA | 4 | Patient exit | 50 / 40 | -2.3009369928 | 2.259814880 | 9 |
