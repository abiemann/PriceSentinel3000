# Installed strategy replay: August 24–28, 2026

Executed all 14 stock-specific scripts installed in `C:\Users\abiem\AppData\Local\PriceSentinel3000\Strategies` through the running app’s MCP interface. OriginalConfirmation, Built-In, and the seed marker were excluded. No strategy source or input default was changed.

Each daily run starts independently at $1,400, with $500 fixed position sizing, fractional shares, immediate settlement, unlimited entries, a 1% purchase-price stop loss, and a $50 daily loss limit. The requested window is 06:30–13:00 America/Los_Angeles, with one-minute strategy candles and normal warmup from that day’s history. Positions and profits do not carry into the next daily run.

All 70 runs completed on app build 1.2-dev.29 using genuine one-minute source candles obtained through the historical fallback. Daily P&L is final equity minus $1,400, including any open position marked at the final source close. No intrabar prices were reconstructed.

| Stock / ETF | Mon 8/24 | Tue 8/25 | Wed 8/26 | Thu 8/27 | Fri 8/28 | Total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| NVDA | -$0.91 | +$1.52¹ | +$0.60 | +$8.96 | -$0.16 | **+$10.02** |
| SOXX | +$3.45 | -$1.23 | -$1.37 | +$2.61¹ | -$0.43 | **+$3.04** |
| NFLX | -$1.86 | +$3.35 | +$0.40 | +$0.53 | -$1.31 | **+$1.10** |
| GOOGL | +$0.41 | +$1.13 | +$0.33 | -$0.95 | -$0.49 | **+$0.42** |
| META | +$1.61 | +$1.15¹ | -$0.50 | +$0.40 | -$3.15 | **-$0.50** |
| AAL | -$2.14 | +$5.51¹ | -$2.80 | -$3.33 | +$1.54 | **-$1.22** |
| AAPL | +$1.28 | -$0.45 | +$0.44 | -$1.91 | -$1.10 | **-$1.75** |
| MSFT | +$0.20 | -$1.41 | -$1.06 | +$0.93 | -$3.21 | **-$4.55** |
| AMZN | -$0.64 | -$0.47 | -$0.41 | -$1.43 | -$2.65¹ | **-$5.60** |
| TSLA | -$0.47 | -$1.44 | -$0.12 | -$2.08 | -$1.76 | **-$5.88** |
| NU | -$3.95¹ | +$2.84¹ | +$0.35 | -$4.38 | -$5.08 | **-$10.21** |
| SOFI | -$2.79 | -$3.75¹ | -$1.76 | +$1.36¹ | -$3.83 | **-$10.76** |
| SOXL | -$2.32 | -$7.69 | +$3.71 | -$3.03 | -$2.19¹ | **-$11.52** |
| USO² | $0.00 | -$1.50 | +$3.26 | $0.00 | $0.00 | **+$1.76** |

¹ An open position remained at the final candle. Daily P&L includes its unrealized value; the app did not force a closing sale.

² USO had missing history on every day: 360, 388, 387, 375, and 374 usable candles out of 390. Its three $0 days had no fills. August 27 and 28 never accumulated the required 85 contiguous warmup candles; those zeros cannot establish breakeven performance. These incomplete-data results are not directly comparable with the other rows and are listed separately at the bottom.

Totals use unrounded daily amounts. This is simulated gross P&L using the existing fill model, with no extra fees or slippage assumption.

## Script identities

| Ticker | Installed file | SHA-256 |
| --- | --- | --- |
| AAL | American Airlines (AAL) Confirmation - experimental.thinkscript | `e5c76250cd82438ec3c2fa4f8c73230ac067d2292a6803ababf64cae48547abc` |
| AAPL | Apple (AAPL) Confirmation - experimental.thinkscript | `047bbc6be841e61c997ba140aff2f54393e048896aa6309c1eec30f6fc203de7` |
| AMZN | Amazon (AMZN) Confirmation - experimental.thinkscript | `7b537fcf3e16742c9a11148849f26acd0bb26c3c8303ce2c4b2fb5beee5f96a1` |
| GOOGL | Alphabet (GOOGL) Confirmation - experimental.thinkscript | `bfd3036a50998fb19fd8d962fe2253c3840411d605f26032e40b591b3504ee03` |
| META | Meta (META) Confirmation - experimental.thinkscript | `878200713f63946279b4ed89f1a5d4d11e5cc0818a724ee110cb8b45e42e4f16` |
| MSFT | Microsoft (MSFT) Confirmation - experimental.thinkscript | `c52cda0000392d5578a853b3ca8054e361a8d2234727f80407a955fc2f5bf87a` |
| NFLX | Netflix (NFLX) Confirmation.thinkscript | `3ec150e2efdbc454a28972250c53d18cb0c33970bc93f9536823913dc91317fe` |
| NU | Nu Holdings (NU) Breakout - experimental.thinkscript | `ddb3eef3af8aad0c5b6091cdea4a0e02d39e48fe2b857fc879124daa4fd16ad2` |
| NVDA | NVIDIA (NVDA) Confirmation - experimental.thinkscript | `06a75053b40bae04a41061aae22879063aa781da03cdc5826516f7be820e8b89` |
| SOFI | SoFi Technologies (SOFI) Breakout - experimental.thinkscript | `ddb3eef3af8aad0c5b6091cdea4a0e02d39e48fe2b857fc879124daa4fd16ad2` |
| SOXL | Direxion Semiconductor Bull 3X (SOXL) Mean Recovery - experimental.thinkscript | `66568d7690651cb1173007ccc4104a98b8af138cf74aef3dc297d20c2fc83a60` |
| SOXX | iShares Semiconductor (SOXX) Confirmation - experimental.thinkscript | `e5c76250cd82438ec3c2fa4f8c73230ac067d2292a6803ababf64cae48547abc` |
| TSLA | Tesla (TSLA) Confirmation - experimental.thinkscript | `cf842105d9ceb997d8721f181c6eddc0ce685749a3da9fcb39a21c5d02b64da6` |
| USO | United States Oil Fund (USO) Confirmation - experimental.thinkscript | `e5c76250cd82438ec3c2fa4f8c73230ac067d2292a6803ababf64cae48547abc` |

## Reproduction and evidence

Captured settings pin each script source and hash. Local evidence for every run is in `artifacts/installed-strategies-week-2026-08-24/TICKER-DATE.json`, including session/operation IDs, complete source candles, indicator snapshots, all fills, and the retained decision window. `manifest.json` pins the original installed catalog and common settings.

`audit-evidence.py` independently reconstructs cash, position cost, realized and unrealized P&L with Decimal arithmetic, and checks source timestamps, close availability, fill prices, settings, hashes, and coverage. The final audit passed 353,009 checks across all 70 runs and 806 fills, with no arithmetic, identity, or timing failures. Ten sessions ended with open positions. `audit-results.json` separates validation failures from data-coverage warnings. `summary.json` holds the exact daily and total P&L used in the table.
