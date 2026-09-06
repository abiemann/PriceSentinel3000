param([Parameter(Mandatory)][string] $RepositoryRoot)

# Keep archive and untagged builds honest; release packaging supplies its own versions.
$ErrorActionPreference = 'SilentlyContinue'
$packageVersion = '0.0.0-dev'
$windowsVersion = '0.0.0.0'
$displayVersion = '0.0.0-dev.unknown'
$versionPattern = 'v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)(?:\.(?<patch>0|[1-9]\d*))?(?<suffix>-[0-9A-Za-z][0-9A-Za-z.-]*)?'
$git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1

if ($git -and (Test-Path -LiteralPath (Join-Path $RepositoryRoot '.git'))) {
    $tags = @(& $git.Source -C $RepositoryRoot tag --merged HEAD 2>$null)
    if ($LASTEXITCODE -eq 0) {
        $tags = @($tags | Where-Object { $_ -cmatch "^$versionPattern`$" })
        if ($tags.Count -gt 0) {
            $arguments = @('-C', $RepositoryRoot, 'describe', '--tags', '--long', '--abbrev=12')
            foreach ($tag in $tags) { $arguments += @('--match', $tag) }
            $description = & $git.Source @arguments HEAD 2>$null
            $match = [regex]::Match([string]$description, "^(?<tag>$versionPattern)-(?<distance>\d+)-g[0-9a-f]+`$")
            if ($LASTEXITCODE -eq 0 -and $match.Success) {
                $major = $match.Groups['major'].Value
                $minor = $match.Groups['minor'].Value
                $patch = if ($match.Groups['patch'].Success) { $match.Groups['patch'].Value } else { '0' }
                $suffix = $match.Groups['suffix'].Value
                $displayVersion = $match.Groups['tag'].Value -creplace '^v', ''
                if ($match.Groups['distance'].Value -ne '0') {
                    $development = "dev.$($match.Groups['distance'].Value)"
                    $displayVersion += if ($suffix) { ".$development" } else { "-$development" }
                    $suffix += if ($suffix) { ".$development" } else { "-$development" }
                }
                $packageVersion = "$major.$minor.$patch$suffix"
                $windowsVersion = "$major.$minor.$patch.0"
            }
        }
    }
}

Write-Output "$packageVersion|$windowsVersion|$displayVersion"
