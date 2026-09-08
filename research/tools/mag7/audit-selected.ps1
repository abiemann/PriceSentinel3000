param(
    [string]$Directory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../artifacts/mag7-research')),
    [string]$FrozenSelection,
    [string[]]$Symbols = @('AAPL','MSFT','GOOGL','AMZN','NVDA','META','TSLA')
)
$ErrorActionPreference = 'Stop'
if (-not $FrozenSelection) { $FrozenSelection = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../archive/research/mag7-2026-09-06/frozen-selection.json')) }
# Only string identities are read here; all accounting below uses the exact parser.
$frozen = Get-Content -Raw -LiteralPath $FrozenSelection | ConvertFrom-Json -DateKind String
$mapping = @{}
foreach ($selection in $frozen.selections) {
    if ($selection.symbol -notin $Symbols) { continue }
    if ($selection.winner -notin @('baseline','original','patient','stronger')) { throw "Invalid frozen winner for $($selection.symbol)." }
    if ($mapping.ContainsKey($selection.symbol)) { throw "Duplicate frozen selection: $($selection.symbol)." }
    $mapping[$selection.symbol] = $selection.winner
}
foreach ($symbol in $Symbols) { if (-not $mapping.ContainsKey($symbol)) { throw "Frozen selection missing $symbol." } }
$auditText = & (Join-Path $PSScriptRoot 'audit-and-select.ps1') -Directory $Directory -Symbols $Symbols -IncludeFriday -SelectedProfiles $mapping
$audit = [Mag7ExactJson]::Parse(($auditText -join [Environment]::NewLine))
$developmentDates = @('2026-08-31','2026-09-01','2026-09-02','2026-09-03')
$comparisons = [Collections.Generic.List[object]]::new()
foreach ($row in $audit.runs) {
    if ($row.date -notin $developmentDates) { continue }
    $symbol=$row.symbol; $day=$row.date; $winner=$mapping[$symbol]
    $selectedFile = Join-Path $Directory "$symbol-$day-selected.json"
    $candidateFile = Join-Path $Directory "$symbol-$day-$winner.json"
    $errors = [Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $candidateFile)) {
        $errors.Add('Winning development candidate export is missing.')
    } else {
        $selected=[Mag7ExactJson]::Read($selectedFile); $candidate=[Mag7ExactJson]::Read($candidateFile)
        foreach ($kind in 'source','strategy','events') {
            $left=$selected[$kind].records; $right=$candidate[$kind].records
            if ($left.Count -ne $right.Count) { $errors.Add("$kind record counts differ."); continue }
            for ($i=0; $i -lt $left.Count; $i++) {
                if ($kind -eq 'source') {
                    $left[$i].Remove('observedAtUtc') | Out-Null
                    $right[$i].Remove('observedAtUtc') | Out-Null
                } elseif ($kind -eq 'events') {
                    if ($left[$i].order) {$left[$i].order.Remove('id') | Out-Null}
                    if ($right[$i].order) {$right[$i].order.Remove('id') | Out-Null}
                    if ($left[$i].fill) {$left[$i].fill.Remove('orderId') | Out-Null}
                    if ($right[$i].fill) {$right[$i].fill.Remove('orderId') | Out-Null}
                }
                $a=$left[$i] | ConvertTo-Json -Compress -Depth 30
                $b=$right[$i] | ConvertTo-Json -Compress -Depth 30
                if ($a -cne $b) {$errors.Add("$kind differs at sequence $($i+1).")}
            }
        }
        if (($selected.results.account | ConvertTo-Json -Compress) -cne ($candidate.results.account | ConvertTo-Json -Compress)) {$errors.Add('Final accounts differ.')}
    }
    $comparisons.Add([pscustomobject]@{symbol=$symbol;date=$day;frozenWinner=$winner;matched=$errors.Count -eq 0;failureCount=$errors.Count;failures=$errors.ToArray()})
}
$summaries = [Collections.Generic.List[object]]::new()
foreach ($symbol in $Symbols) {
    $selection=@($frozen.selections | Where-Object symbol -eq $symbol)[0]
    $development=@($audit.runs | Where-Object {$_.symbol -eq $symbol -and $_.date -in $developmentDates})
    $friday=@($audit.runs | Where-Object {$_.symbol -eq $symbol -and $_.date -eq '2026-09-04'})
    [decimal]$developmentPnl=0
    foreach ($row in $development) {$developmentPnl+=[decimal]$row.pnl}
    $summaries.Add([pscustomobject]@{
        symbol=$symbol
        frozenWinner=$mapping[$symbol]
        expectedDevelopmentDates=$selection.availableDevelopmentDates
        unavailableDevelopmentDates=$selection.unavailableDevelopmentDates
        auditedDevelopmentDates=@($development | ForEach-Object {$_.date} | Sort-Object)
        developmentPnl=$(if($development.Count){$developmentPnl}else{$null})
        fridayPnl=$(if($friday.Count -eq 1){$friday[0].pnl}else{$null})
        fridayAvailable=$friday.Count -eq 1
    })
}
[pscustomobject]@{
    frozenSelectionFile=(Resolve-Path -LiteralPath $FrozenSelection).Path
    selectionUsesFriday=$false
    auditedSelectedRuns=$audit.auditedRuns
    unavailableAttempts=$audit.unavailableAttempts
    invalidRuns=$audit.invalidRuns
    mismatchedDevelopmentRuns=@($comparisons | Where-Object {-not $_.matched}).Count
    runs=$audit.runs
    unavailable=$audit.unavailable
    developmentComparisons=$comparisons.ToArray()
    summaries=$summaries.ToArray()
} | ConvertTo-Json -Depth 15
