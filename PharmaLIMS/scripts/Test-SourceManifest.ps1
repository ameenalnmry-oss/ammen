[CmdletBinding()]
param([string]$ManifestFile = (Join-Path $PSScriptRoot '..\SOURCE_MANIFEST_SHA256.txt'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$manifestPath = (Resolve-Path -LiteralPath $ManifestFile).Path
$root = Split-Path -Parent $manifestPath
$failures = @()
foreach ($line in Get-Content -LiteralPath $manifestPath) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
    if ($line -notmatch '^(?<Hash>[0-9a-fA-F]{64})  (?<Path>.+)$') { $failures += "Invalid line: $line"; continue }
    $file = Join-Path $root $Matches.Path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $failures += "Missing: $($Matches.Path)"; continue }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Matches.Hash.ToLowerInvariant()) { $failures += "Hash mismatch: $($Matches.Path)" }
}
if ($failures.Count) { $failures | ForEach-Object { Write-Error $_ }; throw 'Source manifest verification failed.' }
Write-Host 'Source manifest verification passed.'
