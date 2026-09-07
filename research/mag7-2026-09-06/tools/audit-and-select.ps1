param(
    [string]$Directory = $PSScriptRoot,
    [string[]]$Symbols = @('AAPL','MSFT','GOOGL','AMZN','NVDA','META','TSLA'),
    [switch]$IncludeFriday,
    [hashtable]$SelectedProfiles
)
$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
if (-not ('Mag7ExactJson' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
public static class Mag7ExactJson {
    public static object Read(string path) {
        return Parse(File.ReadAllText(path));
    }
    public static object Parse(string text) {
        using var doc = JsonDocument.Parse(text);
        return Convert(doc.RootElement);
    }
    private static object Convert(JsonElement e) {
        switch (e.ValueKind) {
            case JsonValueKind.Object:
                var obj = new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in e.EnumerateObject()) obj.Add(p.Name, Convert(p.Value));
                return obj;
            case JsonValueKind.Array:
                var arr = new List<object>();
                foreach (var v in e.EnumerateArray()) arr.Add(Convert(v));
                return arr.ToArray();
            case JsonValueKind.Number: return e.GetDecimal();
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }
}
'@
}
function Read-Exact([string]$Path) { return [Mag7ExactJson]::Read($Path) }
function Ticks([string]$At) { return [datetimeoffset]::Parse($At, $culture).UtcTicks }
function Hash([string]$Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}
function Canonical-Logic([string]$Source) {
    $body = $Source -replace '(?m)#.*$', ''
    $body = $body -replace '(?im)^\s*input\s+\w+\s*=\s*[\d.]+\s*;', ''
    return $body -replace '\s+', ''
}
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:runFailures.Add($Message) }
}
function Check-Amount([decimal]$Expected, $Actual, [string]$Label) {
    # Decimal arithmetic is exact; tolerate only JSON export's sub-nanodollar rounding.
    Check ($null -ne $Actual -and [math]::Abs($Expected - [decimal]$Actual) -le 0.000000001d) $Label
}
$repoDirectory = [IO.DirectoryInfo]::new($PSScriptRoot)
while ($null -ne $repoDirectory -and -not (Test-Path -LiteralPath (Join-Path $repoDirectory.FullName 'research/strategies/NFLXConfirmation.thinkscript'))) {
    $repoDirectory = $repoDirectory.Parent
}
if ($null -eq $repoDirectory) { throw 'Cannot locate the repository containing the frozen NFLX strategy.' }
$repo = $repoDirectory.FullName
$logic = Canonical-Logic (Get-Content -Raw -LiteralPath (Join-Path $repo 'research/strategies/NFLXConfirmation.thinkscript'))
$profileOrder = @('baseline','original','patient','stronger')
$profileInputs = @{
    baseline = @{ entryStrength=50; exitStrength=50 }
    original = @{ entryStrength=50; exitStrength=45 }
    patient = @{ entryStrength=50; exitStrength=40 }
    stronger = @{ entryStrength=55; exitStrength=50 }
}
$developmentDates = @('2026-08-31','2026-09-01','2026-09-02','2026-09-03')
$auditRows = [Collections.Generic.List[object]]::new()
$missingRows = [Collections.Generic.List[object]]::new()
$identity = @{}
foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.json' | Sort-Object Name) {
    if ($file.Name -notmatch '^(?<missing>unavailable-)?(?<symbol>AAPL|MSFT|GOOGL|AMZN|NVDA|META|TSLA)-(?<date>2026-\d{2}-\d{2})-(?<profile>baseline|original|patient|stronger|selected)\.json$') { continue }
    $symbol = $Matches.symbol; $day = $Matches.date; $profile = $Matches.profile; $unavailable = [bool]$Matches.missing
    if ($symbol -notin $Symbols) { continue }
    if ($null -ne $SelectedProfiles) {
        if ($profile -ne 'selected') { continue }
        if (-not $SelectedProfiles.ContainsKey($symbol) -or $SelectedProfiles[$symbol] -notin $profileOrder) { throw "No valid frozen winner for $symbol." }
        $profile = $SelectedProfiles[$symbol]
    } elseif ($profile -eq 'selected') { continue }
    if ($day -eq '2026-09-04' -and -not $IncludeFriday) { continue }
    if ($day -notin ($developmentDates + '2026-09-04')) { continue }
    $run = Read-Exact $file.FullName
    $script:runFailures = [Collections.Generic.List[string]]::new()
    if ($unavailable) {
        $status = if ($run.ContainsKey('status')) { $run.status } elseif ($run.ContainsKey('result')) { $run.result } else { $run }
        Check ($status.operationState -eq 'failed' -and $null -eq $status.sessionId -and $status.totalObservations -eq 0) 'Unavailable status does not show a failed empty attempt.'
        Check ($status.operationError -like "Robinhood returned no $symbol trades*") 'Failure is not an explicit no-history response.'
        Check ($status.settings.symbol -eq $symbol -and $status.settings.replayDate -eq $day -and $status.settings.replayTime -eq '06:30' -and $status.settings.replayEndTime -eq '13:00') 'Unavailable request window/symbol mismatch.'
        $missingRows.Add([pscustomobject]@{symbol=$symbol;date=$day;profile=$profile;error=$status.operationError;failureCount=$runFailures.Count;failures=$runFailures.ToArray()})
        continue
    }
    $status = $run.status; $results = $run.results; $s = $status.settings
    Check ($status.operationState -eq 'completed' -and $results.outcome -eq 'COMPLETED' -and $results.mode -eq 'Replay' -and $status.effectiveMode -eq 'Replay') 'Session not a completed Replay.'
    Check ($status.sessionId -eq $results.sessionId -and $status.sessionId -eq $run.indicators.sessionId) 'Session identity mismatch.'
    $fixed = @{ symbol=$symbol;startingBalance=1400;tradesSettleImmediately=$true;positionSizeBasis='FixedAmount';positionSizeValue=500;quantityLimitMode='AsManyAsPossible';maximumQuantity=100;unlimitedEntries=$true;maximumEntriesPerDay=1;maximumDailyLossBasis='FixedAmount';maximumDailyLossValue=50;stopLossBasis='PurchasePriceDeclinePercentage';stopLossValue=1;bufferMinutes=15;quotePollingSeconds=5;scriptBarIntervalSeconds=60;chartCandleIntervalSeconds=15;reconciliationSeconds=45;reconciliationLookbackSeconds=900;reconciliationCompletionDelaySeconds=30;replayDate=$day;replayTime='06:30';replayEndTime='13:00';replaySpeed=100 }
    foreach ($key in $fixed.Keys) { Check ($s[$key] -eq $fixed[$key]) "Setting mismatch: $key." }
    $source = $results.settings.Strategy.Source
    $sourceHash = Hash $source
    Check ($sourceHash -eq $results.settings.Strategy.SourceSha256 -and $sourceHash -eq $status.strategy.sourceSha256) 'Pinned script hash mismatch.'
    Check ((Canonical-Logic $source) -ceq $logic) 'Script logic differs from frozen candidate family.'
    $expectedInputs = @{fastLength=8;slowLength=21;momentumLength=7;entryStrength=$profileInputs[$profile].entryStrength;exitStrength=$profileInputs[$profile].exitStrength}
    $inputMatches = [regex]::Matches($source, '(?im)^\s*input\s+(\w+)\s*=\s*([\d.]+)\s*;')
    Check ($inputMatches.Count -eq 5 -and $results.settings.Strategy.Inputs.Count -eq 5) 'Unexpected input count.'
    foreach ($match in $inputMatches) {
        $name = $match.Groups[1].Value
        Check ($expectedInputs.ContainsKey($name) -and [decimal]::Parse($match.Groups[2].Value,$culture) -eq $expectedInputs[$name] -and $results.settings.Strategy.Inputs[$name] -eq $expectedInputs[$name]) "Profile input mismatch: $name."
    }
    Check ($results.settings.Strategy.CandleIntervalSeconds -eq 60 -and $results.settings.Strategy.RuntimeVersion -eq 'thinkscript-subset-v1' -and $results.settings.Strategy.DataModel -eq 'completed-price-bars-v1') 'Runtime/data-model mismatch.'
    $enumMap = @{PositionSizeBasis=0;QuantityLimitMode=0;MaximumDailyLossBasis=0;StopLossBasis=1}
    foreach ($key in $fixed.Keys) {
        $expected = if ($enumMap.ContainsKey($key)) { $enumMap[$key] } else { $fixed[$key] }
        Check ($results.settings[$key] -eq $expected) "Pinned setting mismatch: $key."
    }
    Check ($results.settings.StrategyId -eq $s.strategyId -and $results.settings.Strategy.Id -eq $s.strategyId) 'Pinned strategy ID mismatch.'
    foreach ($kind in 'source','strategy','events') {
        $stream = $run[$kind]
        Check ($stream.pages.Count -gt 0 -and -not $stream.pages[-1].hasMore -and $stream.pages[-1].nextSequence -eq $stream.records.Count) "$kind final page incomplete."
        foreach ($page in $stream.pages) { Check (-not $page.truncated -and $page.sessionId -eq $status.sessionId -and $page.firstAvailableSequence -eq 1) "$kind truncated or wrong session." }
        for ($i=0; $i -lt $stream.records.Count; $i++) { Check ($stream.records[$i].sequence -eq $i+1 -and -not $stream.records[$i].omitted) "$kind sequence/omission at index $i." }
    }
    $src = $run.source.records; $events = $run.events.records; $bars = $run.strategy.records
    Check ($src.Count -gt 0 -and $src.Count -eq $events.Count -and $src.Count -eq $status.totalObservations -and $src.Count -eq $status.processedObservations -and $src.Count -eq $results.summary.quoteCount -and $bars.Count -eq $status.completedStrategyBars) 'Stream/status counts mismatch.'
    $start = Ticks "${day}T06:30:00-07:00"; $end = Ticks "${day}T13:00:00-07:00"
    $gaps = [Collections.Generic.List[object]]::new(); $sourceMap = @{}; $barEndMap = @{}; $signature = [Text.StringBuilder]::new()
    [long]$previousEnd = $start
    foreach ($obs in $src) {
        $at = Ticks $obs.startsAtUtc; $through = Ticks $obs.endsAtUtc
        Check ($at -ge $start -and $through -le $end -and ($through-$at) -eq [timespan]::TicksPerSecond*15 -and $at -ge $previousEnd) "Source window/order at $($obs.sequence)."
        Check ($at -eq (Ticks $obs.sourceTimestampUtc) -and $through -eq (Ticks $obs.availableAtUtc) -and $through -eq (Ticks $obs.evaluationTimestampUtc)) "Source availability timing at $($obs.sequence)."
        Check ($obs.intervalSeconds -eq 15 -and $obs.finalized -and $obs.kind -eq 'historical_candle' -and $obs.bid -eq 0 -and $obs.ask -eq 0 -and $obs.last -eq $obs.close) "Source model at $($obs.sequence)."
        Check ($obs.low -gt 0 -and $obs.high -ge [math]::Max([decimal]$obs.open,[decimal]$obs.close) -and $obs.low -le [math]::Min([decimal]$obs.open,[decimal]$obs.close)) "Source OHLC validity at $($obs.sequence)."
        if ($at -gt $previousEnd) { $gaps.Add([pscustomobject]@{fromUtc=[datetimeoffset]::new($previousEnd,[timespan]::Zero).ToString('O');throughUtc=[datetimeoffset]::new($at,[timespan]::Zero).ToString('O');missingBars=($at-$previousEnd)/([timespan]::TicksPerSecond*15)}) }
        $previousEnd=$through; $sourceMap[$at]=$obs
        [void]$signature.Append("$at|$through|")
        foreach ($key in 'open','high','low','close','last','bid','ask','volume') { [void]$signature.Append(([decimal]$obs[$key]).ToString('G29',$culture)).Append('|') }
        [void]$signature.AppendLine()
    }
    if ($previousEnd -lt $end) { $gaps.Add([pscustomobject]@{fromUtc=[datetimeoffset]::new($previousEnd,[timespan]::Zero).ToString('O');throughUtc=[datetimeoffset]::new($end,[timespan]::Zero).ToString('O');missingBars=($end-$previousEnd)/([timespan]::TicksPerSecond*15)}) }
    foreach ($bar in $bars) {
        $at=Ticks $bar.startsAtUtc; $through=Ticks $bar.endsAtUtc; $barEndMap[$through]=$bar
        $pieces=@(0..3 | ForEach-Object { $sourceMap[$at+$_*[timespan]::TicksPerSecond*15] })
        Check ($pieces.Count -eq 4 -and @($pieces | Where-Object {$null -eq $_}).Count -eq 0) "Incomplete source for strategy bar $($bar.sequence)."
        if (@($pieces | Where-Object {$null -eq $_}).Count -gt 0) { continue }
        [decimal]$high=$pieces[0].high;[decimal]$low=$pieces[0].low;[decimal]$volume=0
        foreach ($piece in $pieces) {$high=[math]::Max($high,[decimal]$piece.high);$low=[math]::Min($low,[decimal]$piece.low);$volume+=[decimal]$piece.volume}
        Check ($bar.open -eq $pieces[0].open -and $bar.close -eq $pieces[-1].close -and $bar.high -eq $high -and $bar.low -eq $low -and $bar.volume -eq $volume) "Strategy OHLCV at $($bar.sequence)."
        Check (($through-$at) -eq [timespan]::TicksPerMinute -and $through -eq (Ticks $bar.availableAtUtc) -and $bar.finalized) "Strategy timing at $($bar.sequence)."
    }
    [decimal]$cash=1400;[decimal]$quantity=0;[decimal]$average=0;[decimal]$realized=0;[decimal]$peak=1400;[decimal]$drawdown=0
    [int]$entries=0;[int]$fills=0;[int]$retained=0;[int]$completed=0; $firstReady=$null; $previousEnd=$start
    foreach ($event in $events) {
        $obs=$src[[int]$event.observationSequence-1]; $at=Ticks $obs.startsAtUtc; $through=Ticks $obs.endsAtUtc
        Check ($event.sequence -eq $event.observationSequence -and (Ticks $event.evaluatedAtUtc) -eq $through) "Event source/time at $($event.sequence)."
        if ($at -ne $previousEnd) {$retained=0};$previousEnd=$through
        if ($barEndMap.ContainsKey($through)) {$retained=[math]::Min(256,$retained+1);$completed++}
        if ($event.scriptEvaluation) {
            $evaluation=$event.scriptEvaluation
            Check ($evaluation.requiredWarmupBars -eq 85 -and $evaluation.retainedBars -eq $retained -and $evaluation.completedBarCount -eq $completed -and $evaluation.isWarmingUp -eq ($retained -lt 85)) "Warmup at event $($event.sequence)."
            if (-not $evaluation.isWarmingUp -and -not $firstReady) {$firstReady=$event.evaluatedAtUtc}
        }
        if ($event.fill) {
            $fill=$event.fill;$fills++;[decimal]$price=$fill.price;[decimal]$amount=$fill.quantity
            Check ($price -eq $obs.close -and $fill.orderId -eq $event.order.id -and (Ticks $fill.filledAtUtc) -eq $through) "Fill model at $($event.sequence)."
            if ($fill.side -eq 'Buy') {
                [decimal]$allocation=[math]::Min(500d,$cash)
                [decimal]$expectedQuantity=[math]::Floor(($allocation/$price)*1000000d)/1000000d
                Check ($quantity -eq 0 -and $amount -eq $expectedQuantity) "Buy sizing at $($event.sequence)."
                $cash-=$amount*$price;$quantity=$amount;$average=$price;$entries++
            } else {
                Check ($fill.side -eq 'Sell' -and $amount -eq $quantity) "Sell quantity at $($event.sequence)."
                [decimal]$profit=($price-$average)*$quantity
                Check-Amount $profit $fill.realizedProfitLoss "Fill realized at $($event.sequence)."
                $realized+=$profit;$cash+=$quantity*$price;$quantity=0;$average=0
            }
        }
        [decimal]$equity=$cash+$quantity*[decimal]$obs.last
        $expectedAccount=@{cash=$cash;buyingPower=$cash;equity=$equity;positionQuantity=$quantity;averagePrice=$average;marketValue=($quantity*[decimal]$obs.last);realizedProfitLoss=$realized;unrealizedProfitLoss=($quantity*([decimal]$obs.last-$average))}
        foreach ($key in $expectedAccount.Keys) {Check-Amount $expectedAccount[$key] $event.account[$key] "Account $key at event $($event.sequence)."}
        Check ($event.account.entriesToday -eq $entries) "Entry count at $($event.sequence)."
        $peak=[math]::Max($peak,$equity);$drawdown=[math]::Max($drawdown,$peak-$equity)
    }
    foreach ($key in $expectedAccount.Keys) {Check-Amount $expectedAccount[$key] $results.account[$key] "Final account $key."}
    Check ($results.summary.fillCount -eq $fills -and $results.fills.Count -eq [math]::Min($fills,[int]$results.recordLimit) -and $results.account.entriesToday -eq $entries) 'Final fill/entry count.'
    $dataHash=Hash $signature.ToString();$identityKey="$symbol/$day"
    if ($identity.ContainsKey($identityKey)) { Check ($identity[$identityKey] -eq $dataHash) 'Historical source differs across profiles.' } else {$identity[$identityKey]=$dataHash}
    $auditRows.Add([pscustomobject]@{symbol=$symbol;date=$day;profile=$profile;sessionId=$status.sessionId;sourceSha256=$sourceHash;dataSha256=$dataHash;sourceCount=$src.Count;strategyCount=$bars.Count;firstSource=$src[0].startsAtUtc;lastClose=$src[-1].endsAtUtc;firstReady=$firstReady;gapCount=$gaps.Count;gaps=$gaps.ToArray();pnl=($equity-1400d);realized=$realized;unrealized=$expectedAccount.unrealizedProfitLoss;endingQuantity=$quantity;entries=$entries;fills=$fills;maximumDrawdown=$drawdown;failureCount=$runFailures.Count;failures=$runFailures.ToArray()})
}
$selections=[Collections.Generic.List[object]]::new()
foreach ($symbol in $Symbols) {
    if ($null -ne $SelectedProfiles) { break }
    $rows=@($auditRows | Where-Object {$_.symbol -eq $symbol -and $_.date -in $developmentDates})
    $missing=@($missingRows | Where-Object {$_.symbol -eq $symbol -and $_.date -in $developmentDates})
    $available=@($rows.date | Sort-Object -Unique);$unavailable=@($missing.date | Where-Object {$_ -notin $available} | Sort-Object -Unique)
    $pending=[Collections.Generic.List[string]]::new()
    foreach ($day in $developmentDates) {
        if ($day -notin $available -and $day -notin $unavailable) {$pending.Add("$day history not attempted")}
        if ($day -in $available) {foreach ($profile in $profileOrder) {if (@($rows | Where-Object {$_.date -eq $day -and $_.profile -eq $profile}).Count -ne 1) {$pending.Add("$day/$profile missing or duplicate")}}}
    }
    $scoreRows=@(foreach ($profile in $profileOrder) {
        $candidate=@($rows | Where-Object profile -eq $profile)
        [decimal]$pnl=0;[decimal]$maxDrawdown=0;[int]$entries=0
        foreach ($r in $candidate) {$pnl+=$r.pnl;$maxDrawdown=[math]::Max($maxDrawdown,[decimal]$r.maximumDrawdown);$entries+=$r.entries}
        [pscustomobject]@{profile=$profile;days=$candidate.Count;pnl=$(if($candidate.Count -gt 0){$pnl}else{$null});maximumDailyDrawdown=$(if($candidate.Count -gt 0){$maxDrawdown}else{$null});entries=$entries;tieOrder=[array]::IndexOf($profileOrder,$profile)}
    })
    $ranked=@($scoreRows | Sort-Object @{Expression='pnl';Descending=$true},maximumDailyDrawdown,entries,tieOrder)
    $failures=@($rows | Where-Object failureCount -gt 0).Count + @($missing | Where-Object failureCount -gt 0).Count
    $ready=$pending.Count -eq 0 -and $failures -eq 0 -and $available.Count -gt 0
    $selections.Add([pscustomobject]@{symbol=$symbol;ready=$ready;availableDevelopmentDates=$available;unavailableDevelopmentDates=$unavailable;pending=$pending.ToArray();invalidRuns=$failures;winner=$(if($ready){$ranked[0].profile}else{$null});ranking=$ranked})
}
[pscustomobject]@{generatedAtUtc=[datetimeoffset]::UtcNow.ToString('O');selectionUsesFriday=$false;auditedRuns=$auditRows.Count;unavailableAttempts=$missingRows.Count;invalidRuns=@($auditRows | Where-Object failureCount -gt 0).Count + @($missingRows | Where-Object failureCount -gt 0).Count;runs=$auditRows.ToArray();unavailable=$missingRows.ToArray();selections=$selections.ToArray()} | ConvertTo-Json -Depth 15
