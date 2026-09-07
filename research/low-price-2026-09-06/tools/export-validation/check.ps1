$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repository 'research/low-price-2026-09-06/tools/bin/Release/net10.0-windows/ReplayExport.dll'))
$type = $assembly.GetType('ReplayExport')
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$validate = $type.GetMethod('ValidateExport', $flags)
$save = $type.GetMethod('SaveNew', $flags)
$jobType = $assembly.GetType('ReplayExport+Job')
$checks = [Collections.Generic.List[object]]::new()
function Read-Element([string]$File) {
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($File))
    try { return $document.RootElement.Clone() } finally { $document.Dispose() }
}
function Check-Export($Element, [bool]$ShouldPass, [string]$Name, [string]$ExpectedHash = '') {
    $identity = $Element.GetProperty('job')
    $job = [Activator]::CreateInstance($jobType, [Reflection.BindingFlags]'Instance,Public,NonPublic', $null,
        [object[]]@($identity.GetProperty('symbol').GetString(), $identity.GetProperty('date').GetString(), $identity.GetProperty('profile').GetString(), $identity.GetProperty('strategyId').GetString(), $null), [Globalization.CultureInfo]::InvariantCulture)
    $settings = $Element.GetProperty('requestSettings')
    if (-not $ExpectedHash) { $ExpectedHash = $Element.GetProperty('status').GetProperty('strategy').GetProperty('sourceSha256').GetString() }
    $passed = $true; $message = $null
    try { $validate.Invoke($null, [object[]]@($Element, $job, $settings, $ExpectedHash)) | Out-Null }
    catch { $passed = $false; $message = $_.Exception.InnerException.Message }
    if ($passed -ne $ShouldPass) { throw "Unexpected result: $Name; $message" }
    $checks.Add([pscustomobject]@{name=$Name;passed=$true;rejection=$message})
}
$files = @(Get-ChildItem (Join-Path $repository 'artifacts/low-price-research/development') -Filter '*-C1.json' | Where-Object Name -NotLike 'unavailable-*')
if ($files.Count -ne 16) { throw "Expected the 16 completed C1 exports; found $($files.Count)." }
foreach ($file in $files) { Check-Export (Read-Element $file.FullName) $true "valid $($file.Name)" }
$fixture = Read-Element $files[0].FullName
foreach ($case in 'truncated','missing_record','wrong_session','failed_outcome','wrong_setting') {
    $node = [Text.Json.Nodes.JsonNode]::Parse($fixture.GetRawText())
    switch ($case) {
        'truncated' { $node['events']['pages'][0]['truncated'] = [Text.Json.Nodes.JsonNode]::Parse('true') }
        'missing_record' { $node['source']['records'].AsArray().RemoveAt(0) }
        'wrong_session' { $node['indicators']['sessionId'] = [Text.Json.Nodes.JsonNode]::Parse('"wrong"') }
        'failed_outcome' { $node['results']['outcome'] = [Text.Json.Nodes.JsonNode]::Parse('"ERROR"') }
        'wrong_setting' { $node['status']['settings']['startingBalance'] = [Text.Json.Nodes.JsonNode]::Parse('999') }
    }
    $document = [Text.Json.JsonDocument]::Parse($node.ToJsonString())
    try { Check-Export ($document.RootElement.Clone()) $false $case } finally { $document.Dispose() }
}
Check-Export $fixture $false 'changed script hash' ('0' * 64)
$number = '123.123456789012345678901234567890123456789'
$sentinel = [Text.Json.JsonDocument]::Parse('{"value":' + $number + '}')
$sentinelPath = Join-Path (Join-Path $repository 'artifacts') ('replay-export-number-' + [guid]::NewGuid().ToString('N') + '.json')
try { $save.Invoke($null, [object[]]@($sentinelPath.PSObject.BaseObject, $sentinel.RootElement.Clone())) | Out-Null } finally { $sentinel.Dispose() }
$roundtrip = Read-Element $sentinelPath
if ($roundtrip.GetProperty('value').GetRawText() -cne $number) { throw 'Numeric token changed during export.' }
$checks.Add([pscustomobject]@{name='exact long numeric token';passed=$true;rejection=$null})
[pscustomobject]@{generatedAtUtc=[datetimeoffset]::UtcNow.ToString('O');checks=$checks;appCalls=0} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'results.json')
Write-Output "PASS: $($checks.Count) offline checks; no app calls or binary rebuild."
