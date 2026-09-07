# Semiconductor fund audit

This adapter reuses the exact Decimal ledger audit from `low-price-2026-09-06` and the source-gap warmup diagnostic from `uso-2026-09-06`. Those tools and the application remain unchanged. SOXL and SOXX qualify independently; one fund's insufficient coverage or isolated invalid export does not suppress a valid winner for the other fund. Shared identity corruption marks every affected export.

From the repository root:

```powershell
$python = 'C:/Users/abiem/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
& $python research/semiconductors-2026-09-06/tools/audit.py artifacts/semiconductor-research/development --output artifacts/semiconductor-research/audit-development
& $python research/low-price-2026-09-06/tools/audit-indicators.py --directory artifacts/semiconductor-research/development --manifest research/semiconductors-2026-09-06/candidates/manifest.json --output artifacts/semiconductor-research/audit-development/indicators.json
```

Each fund needs at least two development dates with at least 1,529 of 1,560 source bars and exact 06:30/13:00 Pacific endpoints. All five candidates must use identical source data on every available development date, and the winning candidate must enter at least once. Order is total 5-bps sensitivity, worst day, maximum daily drawdown, fewer entries, then C1/C2/C3/B1/R1. The sensitivity includes a closing-cost allowance on any ending exposure; no forced sale is assumed.

Before Friday, save the development report as `development-audit.json` with LF endings and create `frozen-selection.json` containing these exact fields from the report: `selections`, `developmentEvidence`, `protocolSha256`, `candidateManifestSha256`, and `selectionUsesFriday: false`. Include `developmentAuditSha256` computed from that saved report, the UTC declaration time and protocol commit. `developmentEvidence` pins every completed and unavailable development export's raw file hash, candidate hash, data hash and scoring metrics. Empty selections remain fully protected by these evidence checks.

After Friday, use:

```powershell
& $python research/semiconductors-2026-09-06/tools/audit.py artifacts/semiconductor-research/development --output artifacts/semiconductor-research/audit-final --include-friday --frozen-selection research/semiconductors-2026-09-06/frozen-selection.json
```

Friday never enters ranking. A fund without a qualifying winner may receive a separately declared, unchanged C1 diagnostic run, which must remain explicitly unvalidated. `selected-equity.json` only contains eligible selections. A ledger pass is not a performance or data-coverage pass. The exit code reports invalid exports; inspect `stocks`, `selections` and `selectionShortfall` to judge eligibility. All adapter outputs use UTF-8 without BOM and LF line endings; preserve numeric text when copying older tool outputs.

Run synthetic adapter tests with `python -m unittest discover -s research/semiconductors-2026-09-06/tools -p test_audit.py -v`. These tests do not access market data or the running application.
