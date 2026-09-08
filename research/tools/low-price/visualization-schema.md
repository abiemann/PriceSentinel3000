# Audited comparison input

`build-visual.py` accepts `visualization-data.json` from the independent auditor.
It refuses to render before both the selection freeze and audit are confirmed.
No placeholder stock names, paths, or profits are supplied by this tool.

The top-level fields are:

- `schemaVersion`: `1`
- `selectionFrozen`: `true`, after matching the persisted frozen selection
- `auditPassed`: `true`, after independent ledger validation
- `initialEquity`: `1400`
- `positionSize`: `500`
- `costBpsPerSide`: `5`
- `costIncludesClosingAllowance`: `true`
- `timeZone`: `"America/Los_Angeles"`
- `missingDates`: `["2026-08-31", "2026-09-01"]`
- `stocks`: exactly three objects with `ticker`, `company`, and `profileId`
- `days`: the three sessions below, each containing a `series` object per stock

Each day has `date` and `phase`: September 2 and 3 use `"development"`;
September 4 uses `"heldout"`. Each series has `ticker`, `status` (`"complete"`
or `"partial"` for audited observed history below the eligibility threshold),
`grossPnl`, `costAdjustedPnl`, and `points`. The cost-adjusted endpoint is the
auditor's 5-bps score including its ending-closing-cost allowance.

Each series also retains `entries`, `sourceCount`, `expectedSourceCount`,
`firstReady` (an actual timestamp or `null`), and `endingExposure`. The visual
labels a never-ready strategy as unvalidated rather than interpreting its flat
zero P&L as successful performance. An incomplete source count and null readiness
produce a gaps-prevented-warmup label. Positive ending exposure is labeled as an
open position marked at the session close; it is not shown as a realized exit.

Each point contains `t` (an ISO timestamp with UTC offset) and `equity` (actual
observed account equity). Monetary values accept JSON numbers or decimal strings.
Timestamps must strictly increase; where an account stream has multiple records
at the same time, use its last actual observation for that timestamp. The final
point's equity less $1,400 must reconcile with the audited daily gross endpoint.

The generator plots gross P&L only. It retains actual endpoints and within-bucket
extrema if more than 1,000 observations occur in a path. Tooltip values interpolate
the drawn paths at the exact cursor time and are explicitly labeled interpolated.
The table reports the audited endpoints without interpolating them.

From the repository root, after the actual selected data is available:

```powershell
python research/tools/low-price/build-visual.py PATH_TO_AUDITED_VISUALIZATION_DATA_JSON ABSOLUTE_THREAD_VISUALIZATION_PATH_HTML
```

The sibling `comparison-template.html` is a literal template, not a completed
visualization. Browser checks at 736px and 360px in light and dark themes must be
performed on the generated fragment before presenting it.
