# Candidate validation

This standalone .NET 10 console tool compiles the five frozen research candidates
against the repository's existing interpreter. It checks their manifest hashes,
exact conservative warmup, entry and exit decisions on synthetic prices, long-only
position handling, deterministic repeated evaluation, bounded indicator telemetry,
and evaluation at the maximum supported 4,096-bar history. It does not request market
data, control the app, place orders, or measure profitability.

From the repository root in PowerShell:

```powershell
dotnet restore research/tools/low-price/candidate-validation/Validation.csproj --configfile research/tools/low-price/candidate-validation/NuGet.Config
New-Item -ItemType Directory -Force artifacts/low-price-profile-validation | Out-Null
dotnet run --no-restore --project research/tools/low-price/candidate-validation/Validation.csproj -- artifacts/low-price-profile-validation/results.json
```

The project has no external package dependencies. If a sandbox cannot read the
user-level NuGet configuration, set `APPDATA` to a workspace-local temporary path
for these commands, for example:

```powershell
$env:APPDATA = Join-Path (Get-Location) 'artifacts/low-price-profile-validation/appdata'
```

Source files remain in research. The recorded [validation report](../../../../archive/research/low-price-2026-09-06/candidates/validation-results.json) is in the research archive; new generated reports go under ignored `artifacts/`, as in the command above. Build output is ignored.
