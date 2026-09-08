# USO offline audit

`audit.py` imports the established low-price Decimal audit without changing it. The adapter scopes selection to USO and one candidate; ledger, OHLC aggregation, source identity, complete stream pagination, settings, fill/account reconciliation and warmup checks remain shared. Candidate formulas are independently checked by the existing indicator auditor.

From the repository root, using the bundled Python runtime:

```powershell
$python = 'C:/Users/abiem/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
& $python research/tools/uso/audit.py artifacts/uso-research/development --output artifacts/uso-research/audit-development
& $python research/tools/low-price/audit-indicators.py --directory artifacts/uso-research/development --manifest archive/research/uso-2026-09-06/candidates/manifest.json --output artifacts/uso-research/audit-development/indicators.json
```

Before inspecting Friday, save the development audit's `selections` array to `frozen-selection.json` with `selectionUsesFriday: false`. Keeping the full rows verifies scores, source hashes, dates and inputs to ranking, not only the winning name. Paths may change. Empty frozen selections are valid evidence that no candidate qualified.

After the frozen candidate's Friday Replay has been exported into the same raw directory:

```powershell
& $python research/tools/uso/audit.py artifacts/uso-research/development --output artifacts/uso-research/audit-final --include-friday --frozen-selection archive/research/uso-2026-09-06/frozen-selection.json
& $python research/tools/low-price/audit-indicators.py --directory artifacts/uso-research/development --manifest archive/research/uso-2026-09-06/candidates/manifest.json --output artifacts/uso-research/audit-final/indicators.json
```

The adapter requires at least two development days with at least 1,529 of 1,560 observations and exact 06:30/13:00 Pacific endpoints. All five candidates must share each development date's data; missing comparisons, inconsistent history or invalid exports block selection. A winning profile must enter at least once. Rank uses total 5-bps sensitivity, worst day, maximum daily drawdown, fewer entries and then C1/C2/C3/B1/R1 order. Friday is always excluded from ranking.

`audit.json`, `audit.md` and selected equity paths preserve missing history, marked open positions and gaps. A zero exit code means the exports passed their audit; check `selections`, `selectionPending` and `selectionShortfall` separately for selection eligibility. No app or broker calls occur. Raw files are never modified.

Run the adapter's synthetic selection and corruption tests with `python -m unittest discover -s research/tools/uso -p test_audit.py -v`. The original low-price audit tests remain available separately for the shared ledger implementation.
