# SOXL and SOXX script research

Research requested September 6, 2026 for August 31-September 4, 06:30-13:00
Pacific. The frozen procedure selected **SOXL / R1 oversold mean recovery**.
Its three available sessions earned **+$6.24 gross**, but **-$8.26** under the
assumed 5-bps cost allowance. **SOXX had no eligible selection**, because
only one development date met the data-coverage requirement.

## Scripts

| Fund | Named script | Status | One-minute warmup |
| --- | --- | --- | ---: |
| SOXL | [Direxion Semiconductor Bull 3X (SOXL) Mean Recovery - experimental](<../../../research/strategies/Direxion Semiconductor Bull 3X (SOXL) Mean Recovery - experimental.thinkscript>) | R1, selected among five frozen hypotheses | 51 bars |
| SOXX | [iShares Semiconductor (SOXX) Confirmation - experimental](<../../../research/strategies/iShares Semiconductor (SOXX) Confirmation - experimental.thinkscript>) | Unvalidated C1 starter; no winner | 85 bars |

SOXL's original formula proposes a long entry when RSI7 crosses above 35 while
price remains below SMA20. It proposes an exit on recovery to SMA20 or when RSI7
falls below 25. The host retains sizing, re-entry restrictions, stop loss and
daily-loss controls. A countertrend recovery can fail during a persistent decline.

SOXX's starter proposes entry when price recovers above EMA8, EMA8 is above a
non-declining EMA21, and RSI7 is at least 50 and improving. It proposes exit below
EMA21, when EMA8 falls below EMA21, or when RSI7 falls below 50. C1 was the fallback
specified before testing, not a winner inferred from limited SOXX results.

Both files are byte-identical to their original frozen candidate sources. They
are compatible with PriceSentinel's documented thinkScript subset, not a claim
of complete thinkorswim equivalence. The [script manifest](script-manifest.json)
pins source hashes and inputs. Match the app symbol to the file's ticker manually;
the filename does not enforce or automatically switch the symbol.

## Experiment and selection

Commit `dd062a4` froze the [protocol](protocol.md), candidate source and settings
before new SOXL/SOXX Replay prices or P&L were inspected. Five original formulas
cover trend-pullback confirmation with three entry/exit threshold configurations, a prior-channel
breakout, and oversold mean recovery. No community script was copied and no
candidate was added or retuned after inspecting results.

The app reported no usable history for Monday or Tuesday for either fund. All
five candidates ran on each available Wednesday/Thursday history, for twenty
development runs plus four unavailable attempts. Each fund independently needed
two dates with at least 1,529 of 1,560 observations and exact session boundaries,
matching source data across candidates, and an active selected profile.

Rank eligible candidates by exact summed development P&L after a 5-bps allowance
on recorded traded notional plus the same allowance on ending marked exposure.
Tie-break by worst daily score, maximum daily drawdown, fewer entries and frozen
profile order. SOXL qualified; SOXX had only one qualifying date. The
[frozen selection](frozen-selection.json) contains SOXL/R1 only and separately
labels SOXX/C1 as an unselected diagnostic. The selection, source files and named
verification results were committed in `df9058c` before Friday's runs. Friday is
excluded from selection and did not change either script.

| SOXL profile | Wednesday + Thursday gross | After 5-bps allowance | Entries |
| --- | ---: | ---: | ---: |
| R1, oversold mean recovery | +$5.31 | -$2.69 | 16 |
| B1, prior-channel breakout | -$0.36 | -$5.86 | 11 |
| C3, stronger confirmation | -$3.74 | -$10.74 | 14 |
| C2, confirmation with patient exit | -$3.61 | -$11.61 | 16 |
| C1, confirmation baseline | -$7.77 | -$17.76 | 20 |

Every SOXL candidate had a negative summed primary score. R1 is the
highest-scoring candidate under this protocol, not a proven profitable strategy
or global maximum.
Its exact development gross is $5.314150986600 and primary score -$2.6885053620680.

SOXX's C1/C2/C3/B1 profiles never completed warmup during development. R1, whose
warmup is shorter, made one Thursday trade for +$0.755815165000 gross and
+$0.2554376265525 after the allowance. That isolated observation does not meet
the two-day requirement and did not replace the predeclared C1 starter.

## Daily account results

Each fund/day starts a new $1,400 account with fixed $500 positions, immediate
settlement, unlimited entries, a 1% purchase-price stop, a $50 daily-loss limit,
one-minute strategy bars and existing re-entry guards. Historical source data
consist of Robinhood's split-adjusted 15-second OHLC observations. Fast Replay
changes pacing only. No warmup is preloaded and absent periods are not filled.

| Date / evidence role | SOXL R1 gross P&L | SOXL entries | SOXX C1 starter gross P&L | SOXX entries |
| --- | ---: | ---: | ---: | ---: |
| Mon Aug 31, unavailable | Unavailable | unavailable | Unavailable | unavailable |
| Tue Sep 1, unavailable | Unavailable | unavailable | Unavailable | unavailable |
| Wed Sep 2, development | -$1.88 | 10 | $0.00* | 0 |
| Thu Sep 3, development | +$7.20 | 6 | $0.00* | 0 |
| Fri Sep 4, SOXL held out / SOXX diagnostic | +$0.93 | 13 | +$0.80 | 1 |
| Sum of available independent accounts | +$6.24 | 29 | +$0.80* | 1 |

**\* SOXX's development zeros are inactive accounts:** C1 never completed warmup
on those days. They are not active-strategy performance results. Friday did allow
warmup and one completed trade, but this diagnostic does not retroactively select
C1 or validate a weekly SOXX strategy. All six reported accounts ended flat;
their gross P&L is entirely realized and no final liquidation was simulated.

Tables round to cents only after calculations with exact unrounded values. These
totals are not a full-week return or a shared/compounded $1,400 portfolio.

| Session | SOXL R1 after assumed 5 bps | SOXX C1 after assumed 5 bps |
| --- | ---: | ---: |
| Wednesday | -$6.88 | $0.00; never ready |
| Thursday | +$4.19 | $0.00; never ready |
| Friday | -$5.57 | +$0.30 |
| Available-account sum | **-$8.26** | +$0.30; one active day only |

Exact gross sums are SOXL $6.239657288500 and SOXX $0.801761740000. Exact cost
sensitivity sums are SOXL -$8.26346097227395 and SOXX $0.301360986550. SOXL's
Friday gross was $0.925506301900 and Friday cost score -$5.57495561020595; SOXX's
Friday figures were $0.801761740000 and $0.301360986550. No costs or returns are
assigned to the unavailable Monday or Tuesday sessions.

SOXL's largest equity drawdown on each day was $6.813202069900, $1.652251901600,
and $5.217074593500 respectively. All 29 exits were script sell signals, with no
stop-loss or daily-loss exit. The host blocked seven reentries under its 0.10%
price-movement rule. SOXX's Friday drawdown was $1.062575800000, with one script
exit and one host reentry block. Gross equity drawdown excludes the assumed costs.

## Data and interpretation limits

SOXL's development histories contain 1,558 and 1,559 observations, with two and
one gap resets respectively. SOXX's contain 1,502 and 1,537 observations, with
53 and 22 gap resets. SOXX's longest uninterrupted completed-minute histories
were 80 and 79 bars, insufficient for C1's 85-bar warmup. The total number of
completed candles during a day is not the retained contiguous warmup history.

SOXL Friday supplied all 1,560 observations and 390 completed minutes with no
gaps; R1 first became ready at 07:21 Pacific and had 340 ready evaluations. SOXX
Friday supplied 1,555 observations and 385 minutes, with five gaps and a maximum
of 177 uninterrupted completed minutes. C1 first became ready at 07:55 and had
94 ready evaluations. Later gaps reset it, so it ended with only 68 retained
bars and warmup incomplete. That final state does not erase its earlier ready
evaluations or completed trade. Friday met the data/warmup gates for SOXL's
holdout and for SOXX's diagnostic, but cannot supply SOXX's missing development day.

The adapter drops null and interpolated source bars before Replay, then sorts
and deduplicates timestamps. Accepted exports alone cannot distinguish provider
omissions from excluded interpolated/null records. Zero volume alone is not a
rejection rule. These counts do not establish a liquidity problem or a particular
provider failure. Each gap resets retained strategy history under the current
host. No artificial candles or shortened warmup conceal the shortfall.

Gross P&L is final marked equity minus $1,400. Replay does not automatically sell
at 13:00; ending positions must be disclosed. Cost sensitivity is a deduction
from an unchanged ledger, not a slippage simulation, predicted fee or recorded
closing order. It does not recalculate risk decisions. Source-close fills and
stops sampled at source observations do not model spread or within-bar execution.
Daily results are independently reset accounts, not a five-day or compounded
portfolio return. Monday and Tuesday's missing history is never assigned zero.

SOXL targets 300% of a semiconductor index's **daily** performance before fees;
SOXX has no such leverage target. Neither intraday nor multiday SOXL returns
should be assumed to equal three times SOXX returns. Equal $500 positions are
not equal-risk positions. [SOXL prospectus](https://www.sec.gov/Archives/edgar/data/1424958/000119312526078540/d50601d497k.htm),
[SOXX fund page](https://www.ishares.com/us/products/239705/ishares-us-technology-etf).

SOXX's announced 3:1 split takes effect in November, outside this window. Broadcom
reported after Wednesday's close; its call was at 14:00 Pacific. Friday's jobs
report was before the session. These are context annotations, not causal claims
or inputs to the scripts. See the primary sources in [market context](market-context.md).

## Verification and next step

Development ledgers, source consistency, timestamps, source-to-strategy OHLC,
warmup, account arithmetic and identity checks passed all twenty runs. The
independent formula/action audit checked 2,398 ready evaluations and 15,277
indicator values without a failure. Each named script was rerun on both
development histories and matched the frozen candidate exactly, including
settings, source/strategy candles, events, fills, account and indicator values.
The four verification runs passed 573 ready evaluations and 2,292 indicator
comparisons. SOXX's development verification did not exercise active signals or
fills; its Friday diagnostic provided the first such evidence for the starter.

The scripts reuse the five original formulas' forty-case synthetic validation.
The semiconductor audit wrapper passed eight tests for independent qualification,
corruption, Friday exclusion, activity requirements and complete frozen evidence.
Exact reports: [development audit](development-audit.json),
[development indicator audit](development-indicator-audit.json),
[verification comparison](verification-comparison.json),
[verification indicator audit](verification-indicator-audit.json),
[final ledger and availability audit](final-audit.json),
[final indicator/action audit](final-indicator-audit.json),
and [reproduction instructions](../../../research/tools/semiconductors/README.md).

The final audit passed all 22 primary completed runs and four unavailable
attempts with no invalid exports. Independent formulas/actions passed 2,832
ready evaluations and 17,389 indicator comparisons. Including the four matching
named verification sessions gives **26 completed Replays plus four unavailable
attempts**. All 24 frozen development evidence files, source/data hashes,
settings and scores were reverified unchanged after Friday.

Raw MCP exports remain local in the ignored `artifacts/semiconductor-research`
directory. Runtime: app `1.2-dev.17`, `thinkscript-subset-v1`,
`completed-price-bars-v1`. Real-time Paper testing and a release remain future
work. This small fitted sample does not establish a durable trading advantage.

Both named scripts are installed and compatible with no catalog errors. The
five temporary candidates were removed after source-hash checks, the user's
initial idle Replay settings were restored, and all pre-existing scripts and
unrelated working-tree changes were preserved. See [installation verification](installation-verification.json).

SOXL's positive gross sum is sensitive to its high turnover; a future hypothesis
should be tested on new dates before claiming improved results. SOXX needs more
usable development history or continuous real-time Paper observations. Neither
result displaces NVIDIA as the previously recommended Tuesday Paper candidate.
