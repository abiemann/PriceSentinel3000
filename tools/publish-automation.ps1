param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\automation-publish')
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$projects = @(
    @{ Project = 'PriceSentinel3000.App'; Output = $publishDirectory },
    @{ Project = 'PriceSentinel3000.Control'; Output = (Join-Path $publishDirectory 'automation') }
)

foreach ($item in $projects) {
    $projectFile = Join-Path $projectRoot "src\$($item.Project)\$($item.Project).csproj"
    & dotnet publish $projectFile --configuration Release --runtime win-x64 `
        --self-contained true --output $item.Output `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) { throw "Publishing $($item.Project) failed." }
}

Write-Output "Desktop app: $(Join-Path $publishDirectory 'PriceSentinel3000.exe') --automation"
Write-Output "Control tool: $(Join-Path $publishDirectory 'automation\PriceSentinel3000.Control.exe')"
