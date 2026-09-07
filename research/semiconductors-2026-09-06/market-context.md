# SOXL and SOXX: fund structure and dated events

Both funds reference the NYSE Semiconductor Index (ICESEMIT). SOXX is the iShares
Semiconductor ETF, an ordinary equity index fund listed on Nasdaq without a daily
leverage target. SOXL is the Direxion Daily Semiconductor Bull 3X ETF, listed on
NYSE Arca and targeting 300% of the index's daily performance before fees and
expenses. SOXL's prospectus states that shorter or longer holding periods should
not be expected to return 300% of the benchmark's result.

SOXX is a traded fund, not the index itself. This experiment therefore uses each
ticker's own prices and fills; it never multiplies SOXX observations or profits
by three. Equal $500 cash positions do not match their underlying exposure or risk.
Daily reset accounts and fixed stops remain the protocol's common conditions,
not a claim that the resulting strategies have comparable risk.

Sources: [SOXX fund page](https://www.ishares.com/us/products/239705/ishares-us-technology-etf),
[SOXL February 27, 2026 summary prospectus](https://www.sec.gov/Archives/edgar/data/1424958/000119312526078540/d50601d497k.htm),
[Direxion fund page](https://www.direxion.com/product/daily-semiconductor-bull-bear-3x-etfs).

## SOXX corporate action

The August 21, 2026 SEC supplement announces a 3:1 forward split with record date
November 3, effective after the November 4 close, and split-adjusted trading
starting November 5. This announced action is outside the August 31-September 4
research window. Preserve the provider's split-adjusted history setting and exact
captured prices; do not manually apply a future split to this experiment.

Source: [SEC split supplement](https://www.sec.gov/Archives/edgar/data/1100663/000119312526361162/d133118d497.htm).

## Scheduled events

| Date | Event | Pacific time | Relation to requested session |
| --- | --- | --- | --- |
| September 1 | July JOLTS | 07:00 | During session |
| September 2 | Broadcom Q3 results | After close; call at 14:00 | Between Wednesday and Thursday sessions |
| September 3 | Revised Q2 Productivity and Costs | 05:30 | Before session |
| September 4 | August Employment Situation | 05:30 | Before session |

Sources: [Broadcom advance announcement](https://investors.broadcom.com/news-releases/news-release-details/broadcom-inc-announce-third-quarter-fiscal-year-2026-financial),
[Broadcom September 2 SEC filing](https://www.sec.gov/Archives/edgar/data/1730168/000173016826000076/avgo-20260902.htm),
[BLS 2026 calendar](https://www.bls.gov/schedule/2026/).

These are context annotations, not evidence of price causation. The price-only
scripts do not receive news, and event information does not trigger extra tuning
or change the frozen selection rules.
