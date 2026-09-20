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

    $isWindowsPlatform =
        [System.Environment]::OSVersion.Platform -eq
        [System.PlatformID]::Win32NT

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

if (
    $SourceOnly -and
    (
        $RunDatabaseIntegration -or
        $RunRuntimeSmoke -or
        $ValidateProductionConfig
    )
) {
    throw 'SourceOnly cannot be combined with runtime/database/production-configuration gates.'
}

if (
    $SkipBuild -and
    (
        $RunDatabaseIntegration -or
        $RunRuntimeSmoke
    )
) {
    throw 'SkipBuild cannot be combined with DatabaseIntegration or RuntimeSmoke.'
}

if (
    $RunRuntimeSmoke -and
    -not $RunDatabaseIntegration
) {
    throw 'RunRuntimeSmoke requires RunDatabaseIntegration; WPF startup smoke is not a substitute for SQL integration.'
}

Push-Location $root

try {
    $env:PYTHONDONTWRITEBYTECODE = '1'

    # ============================================================
    # SOURCE / DELIVERY VALIDATION
    # ============================================================

    python -B scripts/validate_project.py --delivery-package
    Assert-NativeExit 'Delivery source validation'

    python -B -m unittest discover -s tests -p "test_*.py" -v
    Assert-NativeExit 'Python source-control regression suite'

    ./scripts/Test-SourceManifest.ps1

    if ($SourceOnly) {
        Write-Host 'Source-only checks passed. No .NET build, SQL execution, WPF runtime execution or release approval is implied.'
        return
    }

    # ============================================================
    # OPTIONAL PRODUCTION CONFIGURATION VALIDATION
    # ============================================================

    if ($ValidateProductionConfig) {
        python -B scripts/validate_project.py --production-publish
        Assert-NativeExit 'Production configuration validation'
    }

    if ($SkipBuild) {
        Write-Warning 'Build skipped. This is not a completed release-validation gate.'
        return
    }

    # ============================================================
    # RID-SPECIFIC RESTORE + BUILD
    #
    # IMPORTANT:
    # Build all win-x64 projects immediately after their RID restore.
    #
    # Non-RID ProjectReference restores can rewrite:
    #
    #   PharmaLIMS\obj\project.assets.json
    #
    # and remove:
    #
    #   net8.0-windows7.0/win-x64
    #
    # which causes NETSDK1047 during the Release build.
    # ============================================================

    Write-Host 'Restoring PharmaLIMS win-x64 dependency graph...'

    dotnet restore `
        PharmaLIMS.csproj `
        --runtime win-x64

    Assert-NativeExit 'WPF win-x64 restore'

    Write-Host 'Building PharmaLIMS Release win-x64...'

    dotnet build `
        PharmaLIMS.csproj `
        --configuration Release `
        --runtime win-x64 `
        --no-restore

    Assert-NativeExit 'WPF Release build'

    Write-Host 'Restoring DatabaseMaintenance win-x64 dependency graph...'

    dotnet restore `
        tools/PharmaLIMS.DatabaseMaintenance/PharmaLIMS.DatabaseMaintenance.csproj `
        --runtime win-x64

    Assert-NativeExit 'DatabaseMaintenance restore'

    Write-Host 'Building DatabaseMaintenance win-x64...'

    dotnet build `
        tools/PharmaLIMS.DatabaseMaintenance/PharmaLIMS.DatabaseMaintenance.csproj `
        --configuration Debug `
        --runtime win-x64 `
        --no-restore

    Assert-NativeExit 'DatabaseMaintenance build'

    # ============================================================
    # NON-RID REVIEW REGRESSION RESTORE / BUILD / EXECUTION
    #
    # These operations occur only after the RID-specific builds
    # above are complete, so they cannot invalidate the Release
    # win-x64 assets before that build.
    # ============================================================

    Write-Host 'Restoring ReviewRegression dependency graph...'

    dotnet restore `
        tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj

    Assert-NativeExit 'ReviewRegression restore'

    Write-Host 'Building ReviewRegression...'

    dotnet build `
        tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj `
        --configuration Release `
        --no-restore

    Assert-NativeExit 'ReviewRegression build'

    Write-Host 'Running ReviewRegression...'

    dotnet run `
        --project tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj `
        --configuration Release `
        --no-build `
        --no-restore

    Assert-NativeExit 'ReviewRegression execution'

    # ============================================================
    # DATABASE INTEGRATION RESTORE
    # ============================================================

    if ($RunDatabaseIntegration) {
        Write-Host 'Restoring DatabaseIntegration dependency graph...'

        dotnet restore `
            tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj

        Assert-NativeExit 'DatabaseIntegration restore'
    }

    # ============================================================
    # RUNTIME SMOKE RESTORE
    #
    # RuntimeSmoke references PharmaLIMS without a RID and may
    # rewrite the main project.assets.json. This is intentionally
    # delayed until AFTER the Release win-x64 build has completed.
    # ============================================================

    if ($RunRuntimeSmoke) {
        Write-Host 'Restoring RuntimeSmoke dependency graph...'

        dotnet restore `
            tests/PharmaLIMS.RuntimeSmoke/PharmaLIMS.RuntimeSmoke.csproj

        Assert-NativeExit 'RuntimeSmoke restore'
    }

    # ============================================================
    # CONTROLLED LOCAL SQL SERVER
    # ============================================================

    if (
        $RunDatabaseIntegration -or
        $RunRuntimeSmoke
    ) {
        Start-ControlledLocalDbIfNeeded
    }

    # ============================================================
    # DATABASE INTEGRATION BUILD / EXECUTION
    # ============================================================

    if ($RunDatabaseIntegration) {
        Write-Host 'Building DatabaseIntegration...'

        dotnet build `
            tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj `
            --configuration Release `
            --no-restore

        Assert-NativeExit 'DatabaseIntegration build'

        Write-Host 'Running disposable SQL Server integration validation...'

        dotnet run `
            --project tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj `
            --configuration Release `
            --no-build `
            --no-restore

        Assert-NativeExit 'Disposable SQL Server integration executable'
    }
    else {
        Write-Warning 'SQL integration not requested. Use -RunDatabaseIntegration before release approval.'
    }

    # ============================================================
    # WPF RUNTIME SMOKE
    # ============================================================

    if ($RunRuntimeSmoke) {
        # Debug/Development smoke is deliberate because LocalDB
        # cannot satisfy the Production certificate policy without
        # weakening it.
        #
        # This gate validates the WPF/XAML/DI/startup path against
        # a disposable migrated database.
        #
        # It does NOT prove the exact signed Production artifact.
        # Production approval still requires:
        #
        #   Invoke-ProductionArtifactSmoke.ps1
        #
        # against the exact signed artifact and a trusted-TLS,
        # pre-migrated SQL Server test database.

        Write-Host 'Building RuntimeSmoke Debug/Development gate...'

        dotnet build `
            tests/PharmaLIMS.RuntimeSmoke/PharmaLIMS.RuntimeSmoke.csproj `
            --configuration Debug `
            --no-restore

        Assert-NativeExit 'RuntimeSmoke Debug build'

        Write-Host 'Running WPF RuntimeSmoke...'

        dotnet run `
            --project tests/PharmaLIMS.RuntimeSmoke/PharmaLIMS.RuntimeSmoke.csproj `
            --configuration Debug `
            --no-build `
            --no-restore

        Assert-NativeExit 'WPF RuntimeSmoke execution'
    }
    else {
        Write-Warning 'WPF RuntimeSmoke not requested. Use -RunDatabaseIntegration -RunRuntimeSmoke before release approval.'
    }

    # ============================================================
    # FINAL AUTOMATED GATE SUMMARY
    # ============================================================

    if (
        $RunDatabaseIntegration -and
        $RunRuntimeSmoke
    ) {
        Write-Host 'Automated source, Release build, SQL integration and Development WPF startup smoke gates passed.'

        Write-Warning 'Production artifact approval still requires signing plus Invoke-ProductionArtifactSmoke.ps1 against the exact published binary.'

        Write-Host 'Site-specific UAT, actual printing, operational load evidence and QA approval remain required.'
    }
}
finally {
    Pop-Location
}