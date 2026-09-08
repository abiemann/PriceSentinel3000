# Strategy source research and compatibility checks

This research separates source compatibility from trading performance. The checks
use synthetic completed one-minute bars to verify compilation and BUY/SELL/HOLD
decisions. They are not market backtests, ThinkorSwim parity certification, or
evidence of profitability.

## Sources and selection

Research began with the requested [Strategies & Chart Setups category](https://usethinkscript.com/forums/strategies-chart-setups.5/).
Its [Confirmation Candles thread](https://usethinkscript.com/threads/confirmation-candles-indicator-for-thinkorswim.6316/#post-61246)
describes combining agreement among trend indicators. That general idea informed
the original example's trend and momentum confirmation. A large discussion or a
contributor's performance description is not a verified return record.

Two smaller strategies in the same site's **Questions** section provide bounded,
unchanged compatibility examples. They are not mislabeled as scripts from the
requested category.

| Source | Recorded version | Features checked | Recorded outcome |
| --- | --- | --- | --- |
| [Mocnarf1, SMA9 offset strategy](https://usethinkscript.com/threads/price-crossing-moving-average-for-thinkorswim.10161/#post-107994) | September 13, 2022, post 7 | Arithmetic within `Average`, 9/21-bar windows, boolean conditions, explicit long entry/exit orders, named order decorations | Accepted unchanged; synthetic entry and exit both matched; 21-bar warmup |
| [Pensar, SPY strategy](https://usethinkscript.com/threads/spy-trading-strategy-script-help.9886/#post-88914) | January 28, 2022, post 2 | 66-bar SMA, prior 3-bar low and 19-bar high, function history offsets, plots, explicit long entry/exit orders | Accepted unchanged; synthetic entry and exit both matched; 66-bar warmup |
| [Christopher84, C3_Max_v2](https://usethinkscript.com/threads/confirmation-candles-indicator-for-thinkorswim.6316/#post-61246) | Source header: created December 14, 2021; modified April 11, 2022 | An actual study from the requested category, with recursive state, switch/enum syntax, volume and unsupported indicators; no `AddOrder` calls | Rejected at line 108: `Unsupported character '{'.`; no trades inferred from colors, names, or plots |

The SPY source describes daily bars. Feeding its unchanged formulas minute bars
checks the interpreter's syntax and arithmetic; it does not reproduce the
author's intended daily trading system. The SMA9 source also gives no basis for a
claim that its results generalize across instruments or timeframes.

## Provenance and redistribution

The selected code blocks were extracted directly from downloaded public HTML.
Only presentation markup was removed and HTML entities decoded; script text and
line endings were otherwise left unchanged. Source URLs, authors, versions,
retrieval timestamps, byte counts, and SHA-256 hashes are recorded in
[research-sources.json](../../research/strategies/validation/research-sources.json).

No explicit redistribution license was found for these three posted code blocks.
Their unchanged copies and downloaded pages remain outside this repository at
`D:\Projects\PriceSentinel3000-script-research`. They are not bundled, seeded into
the user's strategy folder, or installed by the validation tool. Repository
fixtures contain original synthetic price data and provenance metadata only.

## Original example

[OriginalConfirmation.thinkscript](../../research/strategies/OriginalConfirmation.thinkscript)
was written independently for this project and uses the repository's MIT license.
It combines common moving-average and momentum concepts; it is not a translation
or rewrite of any forum script.

Its default entry requires all three conditions:

1. EMA8 is above EMA21, and EMA21 is not falling.
2. The latest completed close recovers above EMA8 after the previous close was at
   or below its EMA8.
3. RSI7 is at least 50 and rises from its previous value.

It exits an existing long when the close falls below EMA21, EMA8 falls below
EMA21, or RSI7 falls below 45. The exit may realize a loss. All periods count
completed bars. The defaults are readable starting values, not parameters fitted
to historical returns. Source comments explain each input and rule.

The subset runtime requires 85 completed bars at these defaults, including
explicit smoothing warmup and the prior-bar comparison. Until then it returns
`WARMING UP` without an entry proposal.

The script emits only long-entry and long-exit proposals. PriceSentinel retains
position sizing, execution prices, one-position enforcement, stop loss, daily
loss, and re-entry controls. It does not give the script account access.

## Semantics and limits

Native ThinkorSwim `AddOrder` represents an order on the next bar, with
`open[-1]` as the default price. PriceSentinel evaluates conditions on completed
bars and applies its own current execution rules; accepted source syntax does not
make the resulting fill report identical to ThinkorSwim's. See the official
[AddOrder reference](https://toslc.thinkorswim.com/center/reference/thinkScript/Functions/Others/AddOrder).

EMA initialization and available history also matter. Native ThinkorSwim uses
prefetch for exponential averages, and RSI defaults to Wilder's smoothing.
Matching formulas alone is insufficient to claim agreement at the start of a
short history window. See the official
[ExpAverage reference](https://toslc.thinkorswim.com/center/reference/thinkScript/Functions/Tech-Analysis/ExpAverage)
and [RSI reference](https://toslc.thinkorswim.com/center/reference/Tech-Indicators/studies-library/R-S/RSI).

V1 compatibility is deliberately limited. A study without explicit supported
orders does not become a strategy automatically. Unsupported syntax, volume
dependencies, multi-timeframe calls, recursion, and future-looking signal inputs
must be diagnosed rather than silently rewritten. A large script can require
several changes before it would fit the subset; these research copies were not
changed to make them pass.

## Reproduce the checks

The recorded run on September 6, 2026 UTC (September 5 Pacific), using
`thinkscript-subset-v1`, passed all four compilation expectations and all nine
synthetic decision fixtures. The original example compiled successfully and
matched five fixtures; the two compatible community sources matched two each.
[compatibility-results.json](../../research/strategies/validation/compatibility-results.json)
contains the measured results, source hashes, diagnostics, and exact run time.

The standalone [validation project](../../research/strategies/validation/StrategyExamples.csproj)
references the Core interpreter and reads
[validation-fixtures.json](../../research/strategies/validation/validation-fixtures.json). It has
no broker or market-data dependency. Each fixture supplies explicit synthetic
closes; OHLC values equal the close, volume is zero, and bars are one minute long.

From the repository root, run the original example checks:

```powershell
dotnet run --project research/strategies/validation/StrategyExamples.csproj
```

To also check the recorded third-party copies retained on this machine:

```powershell
dotnet run --project research/strategies/validation/StrategyExamples.csproj -- "D:\Projects\PriceSentinel3000-script-research"
```

The tool verifies each third-party source hash before compiling it. It reports
missing optional research copies as skipped when no directory is supplied; an
explicit directory with missing or modified files is an error. A nonzero exit
code indicates a failed expectation. The JSON output records runtime version,
source hashes, compilation diagnostics, required warmup, and per-fixture outcomes.

The original fixtures exercise warmup, a flat market, a confirmed pullback entry,
an exit during a downtrend, and refusal to open a short. The unchanged reference
fixtures check one entry and one exit for each compact strategy. These bounded
checks establish only the recorded behavior on those inputs.

The five original-example fixtures also run in the normal Core test suite and CI.
That test evaluates each fixture twice and checks the action, state, and reason
remain identical. Third-party sources remain optional local checks because their
code is not redistributed in this repository.
