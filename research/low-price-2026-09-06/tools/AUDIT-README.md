# Decimal audit tools

`audit.py` uses Python 3 standard-library `Decimal` throughout accounting and
ranking. It reads JSON fractions directly as decimals, rejects duplicate JSON
properties and non-finite numbers, and serializes decimal output fields as
strings. Source files and app state are never modified. The decimal context is
60 significant digits; ledger comparisons use exact equality, without a money
tolerance. Repeating ratios such as coverage and mean score use that context.

From the repository root:

```powershell
& 'C:\Users\abiem\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' research/low-price-2026-09-06/tools/test_audit.py
& 'C:\Users\abiem\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' research/low-price-2026-09-06/tools/audit.py artifacts/low-price-research/development --output research/low-price-2026-09-06
```

Outputs are `audit.json`, `audit.md`, `selected-equity.json`, and
`visualization-data.json`. Output files are replaced on each invocation; use a
different output folder to preserve a prior audit or selection freeze. The
auditor returns 1 for invalid exports and 0 for a valid audit. A successful exit
does **not** imply three eligible stocks: check `selectionPending`,
`selectionShortfall`, and each stock's `problems`.

After persisting the actual selection, `--frozen-selection FILE` verifies its
ordered `selections` array of `{symbol, profile}` against the current protocol
result. This is the only way the visual adapter sets `selectionFrozen:true`.
Use `--include-friday` to audit held-out files; Friday never enters eligibility,
candidate scoring, or stock ranking. The operator must freeze the selection
before obtaining Friday results.

The raw export contract is `{job,status,results,indicators,source,strategy,events}`,
optionally with `requestSettings`. `job` carries symbol, date, profile, strategyId,
and sourceSha256. Each stream contains complete `records` and all pagination
metadata in `pages`. Failed no-history attempts retain `job` and `status`; their
filenames start with `unavailable-`. A `failed-` runner export is an audit failure.
Candidate filenames are `SYMBOL-YYYY-MM-DD-C1.json` (likewise C2/C3/B1/R1).
Manifest/status/transcript support files do not enter ranking.

Named final-script exports may use different strategy IDs when the job, status,
results, and indicator snapshots agree and the source remains byte-identical to
the frozen candidate. Put repeated development verification exports in another
folder so they cannot duplicate development observations. The main auditor can
audit that folder's ledgers, but incomplete five-candidate comparisons will not
produce selections. Stream-by-stream equivalence of a renamed verification run
is a separate comparison against its original development export.

At least one explicit no-history attempt establishes unavailable history for a
stock/date. Every available date requires all five candidates and matching source
signatures. Valid partial dates are retained but scored only with at least 1,529
source observations and exact requested first-start/final-end boundaries. Missing
observations reset retained warmup; they are never filled. A candidate needs at
least one entry over its eligible development dates. All ties use unrounded
decimal scores and the fixed protocol order.

`audit-indicators.py` is the independent formula/action audit. Both audits must
pass before accepting a research milestone. `auditPassed` in the visual adapter
describes this ledger audit; the coordinator also checks the independent report.
No tool validates live-market executions or predicts profitability.
