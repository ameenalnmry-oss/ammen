[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppPath,
    [switch]$NoBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'tests/PharmaLIMS.ProductionArtifactSmoke/PharmaLIMS.ProductionArtifactSmoke.csproj'
$app = (Resolve-Path $AppPath).Path

if ([string]::IsNullOrWhiteSpace($env:PHARMALIMS_PRODUCTION_SMOKE_CONNECTION_STRING)) {
    throw 'PHARMALIMS_PRODUCTION_SMOKE_CONNECTION_STRING is required. It must target a controlled pre-migrated SQL Server test database with Encrypt=true and TrustServerCertificate=false.'
}

Push-Location $root
try {
    if (-not $NoBuild) {
        dotnet restore $project
        if ($LASTEXITCODE -ne 0) { throw 'ProductionArtifactSmoke restore failed.' }
        dotnet build $project --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'ProductionArtifactSmoke build failed.' }
    }

    dotnet run --project $project --configuration Release --no-build --no-restore -- --app $app
    if ($LASTEXITCODE -ne 0) { throw 'Published Production artifact smoke failed.' }
}
finally {
    Pop-Location
}
