# Sequential Replay exporter

This research-only console tool calls the existing PriceSentinel Control bridge
through the official MCP/stdio client. It does not launch the desktop app,
install or refresh scripts, alter application code, or restore settings. Only
the coordinating agent should invoke it while owning the visible app.

Build with the .NET 10 SDK and the cached `ModelContextProtocol.Core` 2.0.0:

```powershell
dotnet build research/low-price-2026-09-06/tools/ReplayExport.csproj -c Release
```

Run from the repository root, substituting the actual manifest and output paths:

```powershell
& './research/low-price-2026-09-06/tools/bin/Release/net10.0-windows/ReplayExport.exe' `
  --manifest './research/low-price-2026-09-06/jobs.json' `
  --control './artifacts/automation-publish/automation/PriceSentinel3000.Control.exe' `
  --output './artifacts/low-price-research'
```

`--pipe NAME` selects an explicitly configured app endpoint. Otherwise the
bridge uses its normal current-user endpoint. Run in the ordinary Windows user
context that owns the app, since its pipe rejects a separate sandbox identity.
`--help` performs no app calls. Paths are arguments; none are machine-specific
inside the program.

The manifest has shared settings and ordered jobs. Use the exact installed
strategy identifiers from `list_strategies`; this exporter never refreshes the
catalog. `sourceSha256` is optional but recommended for a frozen script.

```json
{
  "settings": {
    "startingBalance": 1400,
    "tradesSettleImmediately": true,
    "positionSizeBasis": "FixedAmount",
    "positionSizeValue": 500,
    "quantityLimitMode": "AsManyAsPossible",
    "maximumQuantity": 100,
    "unlimitedEntries": true,
    "maximumEntriesPerDay": 1,
    "maximumDailyLossBasis": "FixedAmount",
    "maximumDailyLossValue": 50,
    "stopLossBasis": "PurchasePriceDeclinePercentage",
    "stopLossValue": 1,
    "bufferMinutes": 15,
    "quotePollingSeconds": 5,
    "scriptBarIntervalSeconds": 60,
    "chartCandleIntervalSeconds": 15,
    "reconciliationSeconds": 45,
    "reconciliationLookbackSeconds": 900,
    "reconciliationCompletionDelaySeconds": 30,
    "replayTime": "06:30",
    "replayEndTime": "13:00",
    "replaySpeed": 100
  },
  "jobs": [
    {
      "symbol": "SOFI",
      "date": "2026-09-02",
      "profile": "original",
      "strategyId": "script:originalconfirmation.thinkscript"
    }
  ]
}
```

The symbol is an example, not a selection or price qualification. Shared
settings cannot contain `symbol`, `replayDate`, or `strategyId`, which come from
each job. Omitted shared settings inherit the app's current values. Freeze the
complete relevant settings in the research manifest. Replay times use the
computer's local timezone, so the coordinator must verify Pacific time first.

Before any mutation, the exporter checks the app process identity, rejects LIVE,
and requires an idle session. It reads status and the current strategy catalog
first. Each job configures Replay, starts once in fast mode, waits for the same
operation to complete, and exports the completed session before the next job.
Each call has a 45-second timeout; each complete job, including export, has a
180-second timeout. There is no fixed batch lifetime timeout. A timeout or
unknown failure stops the batch, records a read-only status inspection, and
never retries or stops a possibly active operation automatically.

Outputs:

- `initial-status.json`, created once and never overwritten, retains the first
  app settings for the coordinator's eventual restoration.
- A uniquely named manifest, initial status, strategy catalog, and raw JSON
  transport transcript for each batch.
- `SYMBOL-DATE-PROFILE.json` with `{job, requestSettings, status, results,
  indicators, source, strategy, events}`. Each stream contains `{records,pages}`;
  page metadata omits its duplicated records. `job.id` and `job.strategyId` both
  hold the installed identifier, preserving the previous MAG7 export convention.
- `unavailable-SYMBOL-DATE-PROFILE.json` only for an explicit no-history failure
  with an empty new operation. It contains the request and failed status, and
  never copies stale results from a previous run.
- `failed-SYMBOL-DATE-PROFILE-BATCH.json` for other job failures. Raw responses
  already collected remain in the batch transcript even if a full export could
  not be completed.

All numeric response tokens remain `JsonElement` values throughout capture and
serialization, with no conversion to binary floating point. Export validation
checks settings, pinned source hashes, session identity, terminal outcome,
contiguous sequences, omission/truncation flags, page cursors, and stream counts.
It does not substitute for an independent Decimal accounting/strategy audit.
It prints progress and record counts, without P&L.

Existing run files are rejected by default. `--resume` skips completed files
only after validating their job, exact requested settings, complete export, and
source hash against the current catalog. Existing unavailable files still
require omitting those jobs from a new manifest. Partial or invalid completed
files are rejected, never overwritten. Before continuing after an uncertain
failure, inspect the app and construct the next manifest deliberately.

## Independent offline checks

The Python standard-library indicator auditor reconstructs only the completed
bars retained at each evaluation, including history resets and capacity limits.
It recomputes EMA, Wilder RSI, means, prior channels, cross conditions, and
position-aware proposals for the five frozen profiles. Binary64 indicator
arithmetic follows the interpreter's documented numeric model; comparisons to
Decimal-parsed exports use an absolute tolerance of `1e-10`. Account and risk
accounting have a separate Decimal auditor in `audit.py`.

```powershell
python research/low-price-2026-09-06/tools/audit-indicators.py `
  --directory artifacts/low-price-research/development `
  --output research/low-price-2026-09-06/development-indicator-audit.json
python research/low-price-2026-09-06/tools/compare-replays.py `
  --reference artifacts/low-price-research/development `
  --verification artifacts/low-price-research/verification `
  --output research/low-price-2026-09-06/verification-comparison.json
```

`compare-replays.py` compares named-script verification runs with their matching
candidate filenames. It checks every source and strategy record, event,
indicator snapshot, pinned settings/source, decision, fill, and account. The
comparison removes only receipt wall times, generated order IDs after checking
their internal correlations, session IDs, and renamed strategy identity fields.

`export-validation/check.ps1` uses reflection against the built exporter to
accept all 16 saved C1 runs and reject corrupted completeness, identity,
settings, and hash fixtures. It also verifies exact serialization of a long
numeric token. `export-validation/check-indicators.py` accepts an unchanged
saved fixture and rejects a corrupt indicator and proposed action. Their
retained JSON results record 23 exporter checks and three indicator fixtures.
Neither check connects to the app.
