[CmdletBinding()]
param(
    [switch]$SkipPython,
    [switch]$SkipBuild,
    [switch]$SkipDatabaseIntegration,
    [switch]$RunRuntimeSmoke
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($SkipPython) {
    throw 'SkipPython is no longer supported. Source validation is a mandatory release gate.'
}

Write-Warning 'scripts/run_release_validation.ps1 is retained only as a compatibility entry point. The authoritative runner is scripts/Invoke-ReleaseValidation.ps1.'
$params = @{}
if ($SkipBuild) { $params.SkipBuild = $true }
if (-not $SkipDatabaseIntegration) { $params.RunDatabaseIntegration = $true }
if ($RunRuntimeSmoke) { $params.RunRuntimeSmoke = $true }
& (Join-Path $PSScriptRoot 'Invoke-ReleaseValidation.ps1') @params
