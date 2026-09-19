[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$RunDatabaseIntegration,
    [switch]$RunRuntimeSmoke,
    [switch]$ValidateProductionConfig,
    [switch]$SourceOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Assert-NativeExit([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with native exit code $LASTEXITCODE."
    }
}

function Start-ControlledLocalDbIfNeeded {
    if ($env:PHARMALIMS_TEST_MASTER_CONNECTION_STRING) {
        Write-Host 'External PHARMALIMS_TEST_MASTER_CONNECTION_STRING supplied; LocalDB startup skipped.'
        return
    }
    $isWindowsPlatform = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
    if (-not $isWindowsPlatform) {
        throw 'SQL integration/runtime smoke requires Windows LocalDB or PHARMALIMS_TEST_MASTER_CONNECTION_STRING.'
    }
    $localDb = Get-Command sqllocaldb -ErrorAction SilentlyContinue
    if (-not $localDb) {
        throw 'sqllocaldb was not found. Install SQL Server LocalDB or provide PHARMALIMS_TEST_MASTER_CONNECTION_STRING.'
    }
    $instances = @(sqllocaldb info)
    if ($instances -notcontains 'MSSQLLocalDB') {
        sqllocaldb create MSSQLLocalDB | Out-Null
        Assert-NativeExit 'Create MSSQLLocalDB'
    }
    sqllocaldb start MSSQLLocalDB | Out-Null
    Assert-NativeExit 'Start MSSQLLocalDB'
}

if ($SourceOnly -and ($RunDatabaseIntegration -or $RunRuntimeSmoke -or $ValidateProductionConfig)) {
    throw 'SourceOnly cannot be combined with runtime/database/production-configuration gates.'
}
if ($SkipBuild -and ($RunDatabaseIntegration -or $RunRuntimeSmoke)) {
    throw 'SkipBuild cannot be combined with DatabaseIntegration or RuntimeSmoke.'
}
if ($RunRuntimeSmoke -and -not $RunDatabaseIntegration) {
    throw 'RunRuntimeSmoke requires RunDatabaseIntegration; WPF startup smoke is not a substitute for SQL integration.'
}

Push-Location $root
try {
    $env:PYTHONDONTWRITEBYTECODE = '1'
    python -B scripts/validate_project.py --delivery-package
    Assert-NativeExit 'Delivery source validation'
    python -B -m unittest discover -s tests -p "test_*.py" -v
    Assert-NativeExit 'Python source-control regression suite'
    ./scripts/Test-SourceManifest.ps1

    if ($SourceOnly) {
        Write-Host 'Source-only checks passed. No .NET build, SQL execution, WPF runtime execution or release approval is implied.'
        return
    }

    if ($ValidateProductionConfig) {
        python -B scripts/validate_project.py --production-publish
        Assert-NativeExit 'Production configuration validation'
    }

    if ($SkipBuild) {
        Write-Warning 'Build skipped. This is not a completed release-validation gate.'
        return
    }

    # Restore once through the controlled projects. win-x64 assets are restored for the
    # Release build/publish path; RuntimeSmoke is built separately as Debug/Development.
    dotnet restore PharmaLIMS.csproj --runtime win-x64
    Assert-NativeExit 'WPF win-x64 restore'
    dotnet restore tools/PharmaLIMS.DatabaseMaintenance/PharmaLIMS.DatabaseMaintenance.csproj --runtime win-x64
    Assert-NativeExit 'DatabaseMaintenance restore'
    dotnet restore tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj
    Assert-NativeExit 'ReviewRegression restore'
    if ($RunDatabaseIntegration) {
        dotnet restore tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj
        Assert-NativeExit 'DatabaseIntegration restore'
    }
    if ($RunRuntimeSmoke) {
        dotnet restore tests/PharmaLIMS.RuntimeSmoke/PharmaLIMS.RuntimeSmoke.csproj
        Assert-NativeExit 'RuntimeSmoke restore'
    }

    dotnet build PharmaLIMS.csproj --configuration Release --runtime win-x64 --no-restore
    Assert-NativeExit 'WPF Release build'
    dotnet build tools/PharmaLIMS.DatabaseMaintenance/PharmaLIMS.DatabaseMaintenance.csproj --configuration Debug --runtime win-x64 --no-restore
    Assert-NativeExit 'DatabaseMaintenance build'
    dotnet build tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj --configuration Release --no-restore
    Assert-NativeExit 'ReviewRegression build'
    dotnet run --project tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj --configuration Release --no-build --no-restore
    Assert-NativeExit 'ReviewRegression execution'

    if ($RunDatabaseIntegration -or $RunRuntimeSmoke) {
        Start-ControlledLocalDbIfNeeded
    }

    if ($RunDatabaseIntegration) {
        dotnet build tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj --configuration Release --no-restore
        Assert-NativeExit 'DatabaseIntegration build'
        dotnet run --project tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj --configuration Release --no-build --no-restore
        Assert-NativeExit 'Disposable SQL Server integration executable'
    } else {
        Write-Warning 'SQL integration not requested. Use -RunDatabaseIntegration before release approval.'
    }

    if ($RunRuntimeSmoke) {
        # Debug/Development smoke is deliberate because LocalDB cannot satisfy the Production
        # certificate policy without weakening it. This gate verifies the WPF/XAML/DI/startup path
        # against a disposable migrated database, but it does NOT prove the published Production
        # artifact. Production release also requires Invoke-ProductionArtifactSmoke.ps1 against the
        # exact signed artifact and a trusted-TLS pre-migrated SQL Server test database.
        dotnet build tests/PharmaLIMS.RuntimeSmoke/PharmaLIMS.RuntimeSmoke.csproj --configuration Debug --no-restore
        Assert-NativeExit 'RuntimeSmoke Debug build'
        dotnet run --project tests/PharmaLIMS.RuntimeSmoke/PharmaLIMS.RuntimeSmoke.csproj --configuration Debug --no-build --no-restore
        Assert-NativeExit 'WPF RuntimeSmoke execution'
    } else {
        Write-Warning 'WPF RuntimeSmoke not requested. Use -RunDatabaseIntegration -RunRuntimeSmoke before release approval.'
    }

    if ($RunDatabaseIntegration -and $RunRuntimeSmoke) {
        Write-Host 'Automated source, Release build, SQL integration and Development WPF startup smoke gates passed.'
        Write-Warning 'Production artifact approval still requires signing plus Invoke-ProductionArtifactSmoke.ps1 against the exact published binary.'
        Write-Host 'Site-specific UAT, actual printing, operational load evidence and QA approval remain required.'
    }
}
finally {
    Pop-Location
}
