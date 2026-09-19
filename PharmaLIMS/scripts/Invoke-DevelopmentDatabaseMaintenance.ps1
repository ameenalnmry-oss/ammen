param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolProject = Join-Path $projectRoot "tools\PharmaLIMS.DatabaseMaintenance\PharmaLIMS.DatabaseMaintenance.csproj"

Write-Host "PharmaLIMS Development database maintenance" -ForegroundColor Cyan
Write-Host "This is an explicit schema/master-data maintenance action. Normal application startup and workflow screens do not run migrations."
Write-Host ""

dotnet run --project $toolProject --configuration $Configuration -- --confirm-development-maintenance
if ($LASTEXITCODE -ne 0) {
    throw "Development database maintenance failed with exit code $LASTEXITCODE."
}
