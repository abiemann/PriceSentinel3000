# Strategy scripting: thinkscript-subset-v1

PriceSentinel3000 accepts a deliberately limited, price-only subset of thinkScript. Its built-in strategy remains the compiled default. External text can propose a long entry or a long exit through explicit `AddOrder` statements. The host controls execution, position sizing, entry limits, stop loss, daily loss limits, and broker access.

This interpreter is not the thinkorswim strategy engine. A script passing compatibility checks means its syntax is supported; it does not establish equivalent market data, fills, profitability, or live suitability.

## Loading a strategy

Use **Open Scripts Folder** to open `%LOCALAPPDATA%\PriceSentinel3000\Strategies`. Place UTF-8 `.thinkscript`, `.ts`, or `.txt` source files directly in that folder, then refresh the list while the session is stopped. Subdirectories are not searched. The filename supplies the display name. The folder supports at most 128 files, and each source file is limited to 256 KiB before decoding.

The bundled `OriginalConfirmation.thinkscript` is an original educational example using EMA trend, price recovery, and RSI conditions. It is separate from the compiled built-in default. Start with Paper/Replay to inspect its decisions; synthetic validation fixtures demonstrate language behavior and make no performance claim.

Edit numeric `input` defaults in the source file to change parameters. Boolean defaults use `yes`/`no`; source inputs such as `input price = close;` select a series. The current application uses the source defaults. The compiler API also accepts numeric overrides during evaluation and validates affected lengths and warmup again.

Refresh reads and compiles source without evaluating it. A session pins the compiled program, exact source, SHA-256 hash, runtime version, and defaults. Source changes on disk cannot change a running session. An unavailable or incompatible selected file blocks starting that selection. LIVE asks for approval of the selected file version and candle interval. Session provenance records those choices.

## Data and order timing

Periods count **completed strategy candles**, not calendar days. Select 15, 30, 60, 120, or 300 seconds; the default is 60 seconds. Warm-start candles are aggregated from available consecutive 15-second historical OHLC observations. Subsequent candles use sampled live quote prices, so their highs and lows describe observed samples and may differ from a venue's full trade feed. Replay uses its available historical OHLC candles.

An incomplete startup candle is excluded. Gaps reset the continuous history and restart warmup. Delayed chart corrections do not rewrite the frozen strategy history or retroactively create signals. The host evaluates each newly completed candle once, after its end is available to the triggering observation. Execution uses the current eligible quote and existing host rules.

Native thinkScript `AddOrder` models a next-bar order and defaults its price to `open[-1]`. In this application, `AddOrder` proposes an action after the completed signal bar; neither a future open nor the source's trade size sets an executable order. This difference is explicit in compiler warnings. See the [official AddOrder reference](https://toslc.thinkorswim.com/center/reference/thinkScript/Functions/Others/AddOrder).

- `BUY_TO_OPEN` and `BUY_AUTO`: propose Buy only when flat.
- `SELL_TO_CLOSE` and `SELL_AUTO`: propose Sell only while a long position exists.
- Short-opening/short-covering order types are incompatible. A sell while flat holds.
- Simultaneously true entry and exit conditions hold with a conflicting-signal explanation.
- Plots, labels, colors, and variable names never imply a trade. A study with no supported `AddOrder` is incompatible.

For example:

```thinkscript
input length = 9;
def mean = Average(close, length);
AddOrder(OrderType.BUY_TO_OPEN, close crosses above mean,
         name = "Close crossed above mean");
AddOrder(OrderType.SELL_TO_CLOSE, close crosses below mean,
         name = "Close crossed below mean");
```

## Supported expressions

Statements require semicolons. Identifiers and named parameters are case-insensitive. `#` and `//` introduce comments. Supported declarations are `input`, `def`, `plot`, `declare upper`, and `declare lower`. Forward references are allowed, but cycles and recursive definitions are rejected, including `def x = x[1] + 1;`.

Expressions support finite numeric literals; `yes`, `no`, `true`, `false`, and `Double.NaN`; parentheses; unary `+`, `-`, `!`, and `not`; arithmetic `+ - * / %`; comparisons `== != <> > < >= <= is is not`; `and`/`or` and `&&`/`||`; and `if condition then value else value`.

`open`, `high`, `low`, `close`, `hl2`, `hlc3`, and `ohlc4` are available with or without empty parentheses. A constant history offset such as `close[1]` or `Lowest(close, 3)[1]` reads that many supplied bars earlier. Offsets must be literal integers from 0 through 2048. Future references are rejected everywhere, including ignored annotations, except the exact `open[-1]` execution-price argument of `AddOrder`, which is validated as a special convention and never evaluated.

`a crosses above b`, `a crosses below b`, and `a crosses b` compare current and previous values; a previous equality can precede a crossing. `Crosses(a, b, CrossingDirection.ABOVE/BELOW/ANY)` is the equivalent function form. See the [official crosses reference](https://toslc.thinkorswim.com/center/reference/thinkScript/Reserved-Words/crosses).

All signal and indicator arithmetic uses bounded, deterministic double-precision evaluation. A condition is true only when finite and nonzero. Missing history, division by zero, overflow, and invalid numeric operations produce NaN. NaN is never truthy, including in comparisons and unary `not`; `IsNaN` can test it explicitly. An `if` with a NaN condition produces NaN. Logical `and` with a known false operand is false; `or` with a known true operand is true. Non-finite plot values are returned as null.

## Function reference

Positional arguments must precede named arguments. Unknown, duplicate, missing required, or excessive arguments are rejected. Lengths must be constant integers from 1 through 2048; they may depend on numeric inputs and constant expressions.

| Function | Supported parameters and defaults |
| --- | --- |
| `Average`, `ExpAverage`, `WildersAverage`, `Highest`, `Lowest`, `Sum` | `data`, `length = 12` |
| `MovingAverage` | `averageType`, `data`, `length = 12`; type is `AverageType.SIMPLE`, `.EXPONENTIAL`, or `.WILDERS` |
| `RSI` | `length = 14`, `over_bought = 70`, `over_sold = 30`, `price = close`, `averageType = AverageType.WILDERS`, `showBreakoutSignals = no`; only the RSI value is returned, also accessible as `RSI(...).RSI` |
| `Min`, `Max`, `Power` | `value1`, `value2` |
| `AbsValue`, `Sqrt`, `Sqr`, `Sign`, `IsNaN` | `value` |
| `Round` | `number`, `numberOfDigits = 2`; digits from 0 through 12; midpoint rounds away from zero |
| `TrueRange` | `high = high`, `close = close`, `low = low`; uses the previous close |
| `Crosses` | `data1`, `data2`, `direction = CrossingDirection.ANY` |

Simple averages and rolling extrema require a full finite window. EMA seeds from the first supplied finite value with alpha `2 / (length + 1)`. Wilder smoothing seeds with a full simple average and then uses alpha `1 / length`. Missing series data resets smoothing. RSI applies the chosen average to positive and negative price changes; a flat series returns 50, gains without losses 100, and losses without gains 0.

The runtime requires conservative warmup: a direct simple rolling function requires `length` bars, EMA `4 * length`, and Wilder `7 * length`; RSI needs an additional price-change bar. Dependencies, chained indicators, and history offsets add their own requirements. The original example requires 85 completed bars. The application additionally rejects a selected interval/lookback needing more than 24 hours of warm-start history.

thinkorswim can prefetch data beyond the visible history. This runtime uses only the fixed history supplied by the host, so smoothing seeds and results can differ. Its warmup makes that boundary explicit and does not promise native equivalence. See [ExpAverage](https://toslc.thinkorswim.com/center/reference/thinkScript/Functions/Tech-Analysis/ExpAverage), [RSI](https://toslc.thinkorswim.com/center/reference/Tech-Indicators/studies-library/R-S/RSI), and [past offset and prefetch](https://toslc.thinkorswim.com/center/reference/thinkScript/tutorials/Advanced/Chapter-12---Past-Offset-and-Prefetch).

## Ignored visual and execution arguments

`AddOrder(type, condition, price = open[-1], tradeSize = 1, tickColor = Color.MAGENTA, arrowColor = Color.MAGENTA, name = "Script signal")` accepts the long-only order types above. Only the condition proposes an action. Literal names become proposal reasons. Price, quantity, dynamic names, and colors are ignored with a warning; their expressions must still pass capability/history validation. Price and quantity arguments must be numeric expressions.

`plot` values are available in the evaluation result, but the application does not render custom script plots. Supported visual statements are checked for recognized targets, argument signatures, identifiers, and expression/history restrictions, then ignored with a warning:

- Plot methods: `SetDefaultColor(color)`, `AssignValueColor(color)`, `SetPaintingStrategy(paintingStrategy)`, `SetLineWeight(weight)`, `SetStyle(curve)`, `SetHiding(condition)`, `Hide()`, `HideBubble()`, `HideTitle()`.
- `AddLabel(visible, text, color = Color.RED)`.
- `AddCloud(data1, data2, color1 = Color.YELLOW, color2 = Color.RED, showBorder = no)`.
- `AssignPriceColor(color)`.
- `AddChartBubble(timeCondition, priceLocation, text, color = Color.RED, up = yes)`.

Common named `Color`, `PaintingStrategy`, and `Curve` constants are whitelisted; unknown constants and functions are rejected. Strings are supported only in these ignored annotations and order names. Newer visual options outside these signatures, custom RGB colors, and arbitrary plot methods are incompatible.

## Capability and resource boundaries

The implementation parses an AST and interprets a fixed whitelist of operations. It does not compile C#, load assemblies, invoke script-supplied methods, or expose CLR reflection, files, networking, environment variables, a clock, UI access, or order submission. It runs in the application process; the restriction comes from the absence of those capabilities in the language, not from executing arbitrary code in a separate OS sandbox.

Volume is deliberately unavailable because sampled live quotes do not provide authoritative candle volume. Secondary symbols, secondary aggregation periods, multi-timeframe studies, custom `script` blocks, loops, `fold`, `rec`, `switch`, recursive series, strategy-account functions, and unlisted studies/functions are rejected with source-line diagnostics. Full studies such as C3_Max require substantial unsupported features and cannot be treated as executable strategies by interpreting their colors.

| Bound | Limit |
| --- | --- |
| Source passed to the compiler | 128,000 characters |
| Tokens | 24,000 |
| Parsed expression nodes | 4,096 |
| Expression/declaration dependency nesting | 64 levels |
| Arguments per call | 16 |
| Indicator length / history offset | 2,048 |
| Supplied history / calculated warmup | 4,096 bars |
| Allocated numeric array values per evaluation | 2,000,000, approximately 16 MB plus bounded object overhead |
| Counted evaluation operations | 4,000,000 |

Limits apply in combination. A compatible script can exceed the operation or memory budget on a large history, particularly with many plots or long rolling windows. Such an evaluation returns `SCRIPT ERROR` and no proposal. The host latches that fault for the session while its own risk checks remain active. Stop, review, and restart after correcting the script. Compilation failures remain excluded from selection. Neither path silently replaces the selected script with the built-in strategy.
