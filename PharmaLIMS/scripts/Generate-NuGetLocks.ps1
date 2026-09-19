[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projects = @(
    'PharmaLIMS.csproj',
    'tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj',
    'tests/PharmaLIMS.ProductionArtifactSmoke/PharmaLIMS.ProductionArtifactSmoke.csproj',
    'tools/PharmaLIMS.SecretProvisioning/PharmaLIMS.SecretProvisioning.csproj'
)
Push-Location $root
try {
    foreach ($project in $projects) {
        Write-Host "Generating NuGet lock for $project"
        dotnet restore $project --use-lock-file --force-evaluate
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $project" }
    }
    Write-Host 'NuGet lock generation completed. Review every generated packages.lock.json under change control, then commit them.'
    Write-Host 'Subsequent CI restore uses --locked-mode and will fail on dependency-graph drift.'
}
finally { Pop-Location }
