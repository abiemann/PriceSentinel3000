# NFLX Replay practice — September 6, 2026

Nine complete Replay runs compared Built-In, OriginalConfirmation, and one
experimental NFLXConfirmation profile. The candidate improved net equity and
maximum equity drawdown on both development days and the untouched September 4
holdout. This is preliminary evidence for an earlier momentum exit, not proof
that NFLX requires unique logic or that the script will remain profitable.

The candidate is [Netflix (NFLX) Confirmation - experimental.thinkscript](<../../research/strategies/Netflix (NFLX) Confirmation - experimental.thinkscript>).
It was originally named `NFLXConfirmation.thinkscript`; the repository filename
was standardized without changing the source bytes or the recorded hash.
It is installed in this computer's Scripts Folder for practice and is not added
to the app's bundled defaults. No application or Built-In logic changed.

## Experiment

All configuration, historical Replay execution, candles, indicators, signals,
risk overrides, fills, and native chart captures used the exposed PriceSentinel
MCP tools. Each run requested NFLX from 06:30 through 13:00 Pacific, using
Robinhood split-adjusted 15-second historical bars.

- Development dates: September 2 and 3, 2026.
- Holdout: September 4. Its data and results were first loaded after freezing
  the candidate. No parameter grid or subsequent retuning was performed.
- Every day independently starts with $1,400 and a fixed $500 position size,
  immediate settlement, unlimited entries, a 1% position stop, and a $50 daily
  loss limit. The history buffer is 15 minutes.
- Both scripts use completed one-minute candles and the same 85-bar warmup.
- The sole executable change from OriginalConfirmation is `exitStrength = 50`
  instead of `45`. EMA8/EMA21, RSI7, entry threshold 50, and all entry conditions
  remain identical. The host retains all risk and re-entry restrictions.

The hypothesis came from development trades that gave back favorable movement
before exit. Moving the RSI exit threshold toward 50 tests an earlier response
to weakening momentum without changing entry conditions or warmup.

## Results

Amounts are USD. Net is final equity minus the starting balance, including
unrealized P&L. Drawdown is the largest drop from a prior equity high, sampled at
every processed source observation. Entries/exits count actual fills. Each row
is a separately reset account; these are not a continuously compounded backtest.

| Date / role | Strategy | Net | Max drawdown | Entries / exits | Winning / losing exits | End position |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Sep 2 / development | Built-In | -0.58 | 5.35 | 10 / 10 | 9 / 1 | Flat |
| Sep 2 / development | OriginalConfirmation | +0.37 | 1.39 | 3 / 3 | 2 / 1 | Flat |
| Sep 2 / development | NFLXConfirmation | +1.06 | 0.85 | 3 / 3 | 2 / 1 | Flat |
| Sep 3 / development | Built-In | +1.74 | 4.79 | 11 / 10 | 10 / 0 | 6.028090 shares |
| Sep 3 / development | OriginalConfirmation | +2.31 | 2.03 | 6 / 6 | 2 / 4 | Flat |
| Sep 3 / development | NFLXConfirmation | +2.97 | 1.79 | 6 / 6 | 2 / 4 | Flat |
| Sep 4 / holdout | Built-In | -15.79 | 16.21 | 11 / 11 | 7 / 4 | Flat |
| Sep 4 / holdout | OriginalConfirmation | -0.06 | 2.12 | 3 / 3 | 1 / 2 | Flat |
| Sep 4 / holdout | NFLXConfirmation | +0.72 | 1.33 | 3 / 3 | 1 / 2 | Flat |

Built-In's September 3 net includes -$1.53716295 of unrealized loss; its realized
gain alone was $3.28148171. Replay does not automatically liquidate at the end.
Built-In had one STOP LOSS exit on September 2 and four on September 4. Neither
script reached a host stop or daily-loss exit in these runs. Host RISK BLOCKED
events for Built-In / Original / NFLX were 6/2/3, 7/3/5, and 12/1/2 respectively
on September 2, 3, and 4; these were not executed entries.

## An exit we can inspect

On the holdout, both scripts bought 6.307954 shares at $79.265 at 09:18 Pacific.
At 09:29, RSI7 was 49.4861298497654. The completed 09:28–09:29 candle had
OHLC 79.44 / 79.44 / 79.40 / 79.405 and volume 28,307. EMA8 was
79.4377512767026 and EMA21 was 79.3663447476859; warmup was complete with 179 bars.

NFLXConfirmation exited at $79.405, realizing $0.88311356. OriginalConfirmation
held until 09:30 and exited at $79.34, realizing $0.47309655. This isolates an
actual effect of the changed RSI threshold; the host did not override this exit.

The candidate's full holdout ended flat at equity $1,400.724905705. A separate
illustrative replay was paused immediately after that first exit for visual
inspection. Its partial account is therefore different from the full-day row.
The chart's RSI14 on 15-second candles is independent of the script's RSI7 on
one-minute candles; use MCP indicator values when interpreting script decisions.

## Limits and next decision

September 2 supplied 1,557 observations, with missing bars beginning at 08:33:15,
09:14:30, and 11:13:45 Pacific. Each gap reset script warmup. Both scripts were
flat at the resets, and only about 94 of the session's 390 minutes were available
for warmed-up script decisions. Reduced drawdown versus Built-In on that date
partly reflects this reduced participation. Candidate versus Original remains
comparable because both used the same data, warmup, and entry logic. September 3
and 4 each supplied all 1,560 observations without gaps.

Built-In evaluates 15-second observations with simple RSI14 and only 15 initial
observations of warmup. Its legacy Replay fill timestamps use source candle
starts; scripts use availability at completed candle ends. This comparison is
between complete strategies, not an isolated comparison of RSI settings.

Replay fills use sampled historical closes without spread, fees, or slippage;
stops are evaluated at source observations, not within a historical candle.
As a sensitivity illustration, a hypothetical cost of 0.03% of each buy and sell
notional would subtract about $0.90 from the candidate's $0.72 holdout gain.
This is an assumed cost scenario, not an estimate of actual broker costs.

Retain this as an experimental profile. Test the frozen version on more untouched
NFLX dates and other symbols before assigning ticker-specific defaults. A shared
strategy with per-symbol parameters is enough for this experiment. It has not
yet shown that the exit change is specific to NFLX. Real-time Paper testing
during an open market remains outstanding.

## Verification and provenance

Every source/event page was retained before starting the next run. No stream was
truncated. Timestamp/OHLC/volume sequences matched across all three variants on
each date. An independent review reconstructed cash, positions, equity,
drawdown, and turnover at every observation from the recorded fills and closes.
All nine completed runs matched. The installed candidate was accepted by the
app's compatibility checker with informational smoothing/AddOrder notes only.

- Running app: `1.2-dev.10`, host provenance
  `1.2-dev.10+96d27a13bded99f86f2c19fcbb5f40009804dc85`.
- Runtime: `thinkscript-subset-v1`; data model: `completed-price-bars-v1`.
- Original source SHA-256:
  `753badb011266ef7e7dcdc2bea8c39b4999b3d7ae23481873f510019b66a9676`.
- Candidate source SHA-256:
  `3ec150e2efdbc454a28972250c53d18cb0c33970bc93f9536823913dc91317fe`.

Local raw MCP exports, frozen protocol, metrics, analysis script, and PNG captures
are in the ignored `artifacts/nflx-research` directory. Pinned source/settings and
session IDs are included in each full-run JSON export. Broker history is kept
local rather than added to repository source control.
