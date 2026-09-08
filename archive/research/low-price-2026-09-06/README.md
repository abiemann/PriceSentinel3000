# Three stocks below $25: experimental Paper profiles

Research requested September 6, 2026 Pacific; completed before the September 8
Paper session. The frozen procedure selected **NU/B1, SOFI/B1, and AAL/C1**.
**None had a positive result after the 5-basis-point cost allowance on both
development days.** These are research candidates, not proven winning tickers.
NU was the only selection with a positive available-session total after that
allowance, and its Friday history did not support a valid strategy test.

## Scripts and intended use

| Company / symbol | Experimental script | Pattern | Required one-minute bars |
| --- | --- | --- | ---: |
| Nu Holdings / NU | [Nu Holdings (NU) Breakout](<../../../research/strategies/Nu Holdings (NU) Breakout - experimental.thinkscript>) | B1: prior-channel breakout | 84 |
| SoFi Technologies / SOFI | [SoFi Technologies (SOFI) Breakout](<../../../research/strategies/SoFi Technologies (SOFI) Breakout - experimental.thinkscript>) | B1: prior-channel breakout | 84 |
| American Airlines / AAL | [American Airlines (AAL) Confirmation](<../../../research/strategies/American Airlines (AAL) Confirmation - experimental.thinkscript>) | C1: trend-pullback confirmation | 85 |

NU and SOFI independently selected the same B1 formula. Their files are
byte-identical copies of that candidate, with different company filenames.
B1 enters when the close crosses above the previous 20-bar high while above
EMA21; it exits below the previous 10-bar low or EMA21. AAL uses EMA8/EMA21 and
RSI7, with entry and exit strength thresholds of 50. The host owns sizing and
risk controls. **Match the app's symbol manually: filenames do not enforce it.**
See the [script manifest](script-manifest.json) for exact hashes and strategy IDs.

## What was selected, and when

The [protocol](protocol.md) and five candidate scripts were frozen in commit
`9138edb` before inspecting this experiment's Replay P&L. The initial universe
was SOFI, F, RIVN, SNAP, NU, CCL, AAL, and PINS; see the dated
[price and liquidity screen](screening.md). NU is a U.S.-listed foreign issuer.
Friday's public price/volume data informed this screen, so Friday was held out
from strategy selection, not completely unseen in every respect.

All eight stocks returned explicit no-history responses for Monday August 31
and Tuesday September 1: **16 unavailable attempts, not 16 zero-return runs**.
All five candidates were run on each available Wednesday/Thursday history,
giving 80 development sessions. A stock needed two days with at least 1,529 of
1,560 expected source observations and exact 06:30/13:00 Pacific boundaries.

Only NU, SOFI, AAL, and CCL met that two-day requirement. Ford's Wednesday
history began at 06:30:15, failing the exact opening boundary despite 1,556
observations. SNAP had only one qualifying day. RIVN and PINS were below 98%
coverage on both days. Those histories remain in the audit and were not repaired.

Candidates were ranked by unrounded summed development P&L after 5 bps per side,
then worst day, daily drawdown, entry count, and the frozen candidate order.
Stock winners were ranked by the protocol's consistency tier and mean score.
All four eligible stock winners were tier 2; CCL/R1 ranked fourth. The selected
three and exact evidence hashes were [frozen](frozen-selection.json) and committed
in **`7d2be20` before any Friday Replay**. Friday did not change the selections.

## Daily gross P&L

Each independent daily account began with **$1,400**, used fixed **$500 positions**,
a 1% purchase-price stop, a $50 daily loss limit, immediate settlement, unlimited
entries, existing re-entry controls, and completed one-minute strategy candles.
The source was Robinhood's historical 15-second bars, 06:30–13:00 Pacific.
Gross P&L is final marked equity minus $1,400. Money below is USD; arithmetic
uses unrounded decimals, with the tables rounded to cents.

| Session | Evidence role | NU / B1 | SOFI / B1 | AAL / C1 |
| --- | --- | ---: | ---: | ---: |
| Mon Aug 31 | Unavailable history | unavailable | unavailable | unavailable |
| Tue Sep 1 | Unavailable history | unavailable | unavailable | unavailable |
| Wed Sep 2 | Fitted development | +4.37 | +3.07 | 0.00 |
| Thu Sep 3 | Fitted development | -0.59 | -0.76 | -0.67 |
| Fri Sep 4 | Held out; NU partial and never ready | 0.00* | -0.65 | -0.38 |
| Available three-session sum | Includes NU's partial observation | +3.77* | +1.67 | -1.05 |

The exact available gross sums are NU **3.773401670900**, SOFI
**1.669587644100**, and AAL **-1.051242432500**. These are sums of independently
reset daily accounts, not a five-day return, compounded result, or portfolio.

## Cost sensitivity and ending exposure

The primary score subtracts **5 bps on each recorded traded notional**, plus
5 bps on the ending position's marked value as a closing-cost allowance.
This is a static-ledger sensitivity, not an execution replay: spread, slippage,
fees, and altered risk decisions can change actual trade paths. The exact audit
also retains 0, 1, 2.5, 10, and 25 bps and $0.005/$0.01 per-share sensitivities.

| Symbol | Wed + Thu after 5 bps | Friday after 5 bps | Available-session total after 5 bps |
| --- | ---: | ---: | ---: |
| NU | +0.27 | 0.00* | +0.27* |
| SOFI | -0.69 | -3.64 | -4.33 |
| AAL | -2.67 | -0.88 | -3.55 |

Exact available totals after this allowance: NU **0.27151502349455**, SOFI
**-4.33124705112295**, AAL **-3.55071678625935**. NU's small positive total comes
from development; SOFI and AAL are weak experimental selections under these costs.

Replay does not force a sale at 13:00. Friday ended with SOFI holding
**27.404768 shares, marked at $499.17784912**, and AAL holding **38.066235 shares,
marked at $499.619334375**. Their gross totals include unrealized P&L; the cost
allowance is not a recorded closing fill. NU ended flat. All six selected
development sessions ended flat.

| Symbol | Largest daily equity drawdown, Wed–Fri | Friday entries | Friday source observations |
| --- | ---: | ---: | ---: |
| NU | $3.21 | 0* | 1,525 / 1,560* |
| SOFI | $4.13 | 6 | 1,560 / 1,560 |
| AAL | $5.32 | 1 | 1,551 / 1,560 |

**\* NU Friday is not validated.** Its 1,525 observations give **97.76% coverage**,
below the frozen 98% threshold. Thirty-two gaps repeatedly reset retained
history; it never reached its 84-bar warmup, made no entries, and recorded zero
P&L. That flat observation is not a profitable or successful held-out strategy
result. SOFI had complete Friday history; AAL met the coverage threshold with
nine gaps. Neither had positive Friday P&L after the assumed costs.

## Evidence and Tuesday follow-through

The existing MCP interface produced **89 successful Replay sessions**: 80
development, six named-script verification, and three Friday sessions. The
16 no-history attempts are separate. All **83 primary ledgers** passed exact
decimal accounting, source/strategy OHLC aggregation, stream completeness,
identity, warmup, and source-consistency checks. The independent indicator audit
passed all 83 runs, **9,647 ready evaluations and 61,519 indicator comparisons**.
The six named verification runs matched their candidate streams and accounts
and passed independent indicator checks. Audit integrity does not make NU's
partial Friday history adequate for performance validation.

Exact results are in [final-audit.json](final-audit.json); selection evidence is
in [frozen-selection.json](frozen-selection.json), with
[named-run comparisons](verification-comparison.json) and
[audit reproduction instructions](../../../research/tools/low-price/AUDIT-README.md).

For Tuesday's **operational Paper check**, NU is a useful focus for verifying
warmup with continuous real quotes. SOFI is an alternative with a complete
Friday historical session. Its CFO speaks **September 8 at 10:50 a.m. Pacific**
([company announcement](https://investors.sofi.com/news/news-details/2026/SoFi-to-Participate-in-Goldman-Sachs-Communacopia--Technology-Conference/default.aspx));
record that event context rather than attributing every move to the formula.
This choice concerns test coverage, not whether to invest in either company.

Follow the [Tuesday Paper acceptance checklist](tuesday-paper-checklist.md):
fresh quotes, correct symbol and mode, completed candles, natural warmup,
signals and host overrides, fills, account/exposure, incremental exports, and
stop/start identity. One visible session tests one stock at a time. Real-time
Paper testing and release publication remain pending; this small fitted sample
does not establish a durable advantage or guarantee profit.
