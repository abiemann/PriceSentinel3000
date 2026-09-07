# Candidate validation

This standalone .NET 10 console tool compiles the five frozen research candidates
against the repository's existing interpreter. It checks their manifest hashes,
exact conservative warmup, entry and exit decisions on synthetic prices, long-only
position handling, deterministic repeated evaluation, bounded indicator telemetry,
and evaluation at the maximum supported 4,096-bar history. It does not request market
data, control the app, place orders, or measure profitability.

From the repository root in PowerShell:

```powershell
dotnet restore research/low-price-2026-09-06/tools/candidate-validation/Validation.csproj --configfile research/low-price-2026-09-06/tools/candidate-validation/NuGet.Config
dotnet run --no-restore --project research/low-price-2026-09-06/tools/candidate-validation/Validation.csproj -- research/low-price-2026-09-06/candidates/validation-results.json
```

The project has no external package dependencies. If a sandbox cannot read the
user-level NuGet configuration, set `APPDATA` to a workspace-local temporary path
for these commands, for example:

```powershell
$env:APPDATA = Join-Path (Get-Location) 'artifacts/low-price-profile-validation/appdata'
```

Only source files and the report are retained in research. Build output is ignored.
