<#
.SYNOPSIS  Builds, tests and publishes TigerCS.Api and TigerCS.Web into clean directories OUTSIDE the repository. Run on the build machine.
.EXAMPLE   .\Publish-TigerCS.ps1 -Output C:\build\TigerCS
#>
[CmdletBinding()] param([Parameter(Mandatory)][string]$Output, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if ((Resolve-Path -LiteralPath $Output -ErrorAction SilentlyContinue) -and ((Resolve-Path $Output).Path.StartsWith($repo.Path))) { throw 'Output must be outside the repository (the tracked publish\ folder is stale).' }
$commit = (git -C $repo rev-parse HEAD).Trim(); if ($LASTEXITCODE -or -not $commit) { throw 'Cannot read the git commit (publish from a clone, not a zip).' }
if (git -C $repo status --porcelain) { throw 'The working tree has uncommitted changes: publish only a committed revision, so GET /health reports exactly what was built.' }
$stamp = Get-Date -Format yyyyMMdd-HHmm
$root = Join-Path $Output $stamp
if (-not $SkipTests) { dotnet test "$repo\src\TigerCS.Tests" -c Release; if ($LASTEXITCODE) { throw 'Tests failed.' } }
foreach ($p in 'Api', 'Web') {
  dotnet publish "$repo\src\TigerCS.$p" -c Release -o "$root\$p" -p:SourceRevisionId=$commit; if ($LASTEXITCODE) { throw "Publish $p failed." }
}
# Gate: nothing environment-specific or secret may ship.
$bad = Get-ChildItem $root -Recurse -Include 'appsettings.Development.json', 'appsettings.UAT.json'
if ($bad) { throw "Environment-specific settings in output: $($bad.FullName -join ', ')" }
$secret = Select-String -Path "$root\*\appsettings*.json" -Pattern '"(Password|ClientSecret|SecretKey|ApiKey|SigningKey|OtpCodePepper)"\s*:\s*"[^"]+"'
if ($secret) { throw "Non-empty secret setting in published output (files: $(($secret.Path | Sort-Object -Unique) -join ', '))." }
foreach ($p in 'Api', 'Web') { if (-not (Test-Path "$root\$p\appsettings.Production.json") -or -not (Test-Path "$root\$p\web.config")) { throw "$p output is incomplete." } }
"commit: $commit`r`nbuilt:  $(Get-Date -Format s)" | Set-Content "$root\MANIFEST.txt"
Write-Host "Published and checked: $root  (commit $commit; after deployment GET /health on the Api AND the Web must report version x.y.z+$commit)"
