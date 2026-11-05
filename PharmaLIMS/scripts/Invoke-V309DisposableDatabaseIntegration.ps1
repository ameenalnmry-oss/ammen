[CmdletBinding()]
param(
    [switch]$RunRuntimeSmoke,
    [switch]$UseExternalDisposableSql,
    [string]$ExternalMasterConnectionString = '',
    [switch]$AcknowledgeCreatesAndDropsDatabases
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Disposable PharmaLIMS SQL integration requires Windows.'
}

$releaseValidation = Join-Path $PSScriptRoot 'Invoke-ReleaseValidation.ps1'
if (-not (Test-Path -LiteralPath $releaseValidation -PathType Leaf)) {
    throw 'Invoke-ReleaseValidation.ps1 is missing.'
}

$previousMaster = $env:PHARMALIMS_TEST_MASTER_CONNECTION_STRING
$previousDisposable = $env:PHARMALIMS_TEST_SQL_DISPOSABLE
$previousCleanUat = $env:PHARMALIMS_CREATE_CLEAN_UAT
$previousProbe = $env:PHARMALIMS_WINDOWS_RUNTIME_PROBE_DATABASE

try {
    # Never inherit retained-UAT or runtime-probe modes into this destructive integration runner.
    $env:PHARMALIMS_CREATE_CLEAN_UAT = $null
    $env:PHARMALIMS_WINDOWS_RUNTIME_PROBE_DATABASE = $null

    if ($UseExternalDisposableSql) {
        if (-not $AcknowledgeCreatesAndDropsDatabases) {
            throw 'UseExternalDisposableSql requires -AcknowledgeCreatesAndDropsDatabases.'
        }
        if ([string]::IsNullOrWhiteSpace($ExternalMasterConnectionString)) {
            throw 'ExternalMasterConnectionString is required when UseExternalDisposableSql is selected.'
        }

        try {
            $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ExternalMasterConnectionString)
        }
        catch {
            throw 'ExternalMasterConnectionString is not a valid SQL Server connection string.'
        }

        if ([string]::IsNullOrWhiteSpace([string]$builder.DataSource)) {
            throw 'External disposable SQL target must declare a Data Source.'
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$builder.AttachDBFilename)) {
            throw 'AttachDbFilename is not allowed for disposable SQL integration.'
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$builder.InitialCatalog) -and
            -not ([string]$builder.InitialCatalog).Equals('master',[StringComparison]::OrdinalIgnoreCase)) {
            throw 'External master connection must target master or omit Initial Catalog.'
        }

        $env:PHARMALIMS_TEST_MASTER_CONNECTION_STRING = $builder.ConnectionString
        $env:PHARMALIMS_TEST_SQL_DISPOSABLE = 'YES'
        Write-Host "Using explicitly acknowledged disposable SQL Server: $($builder.DataSource)"
    }
    else {
        # Default path is always LocalDB, even if the parent shell happened to contain
        # a PHARMALIMS_TEST_MASTER_CONNECTION_STRING from another task.
        $env:PHARMALIMS_TEST_MASTER_CONNECTION_STRING = $null
        $env:PHARMALIMS_TEST_SQL_DISPOSABLE = $null
        Write-Host 'Using Windows LocalDB MSSQLLocalDB for disposable DatabaseIntegration.'
    }

    $params = @{
        RunDatabaseIntegration = $true
        ValidateDeliveryPackage = $true
    }
    if ($RunRuntimeSmoke) {
        $params.RunRuntimeSmoke = $true
    }

    & $releaseValidation @params

    Write-Host ''
    Write-Host 'PASS: disposable PharmaLIMS database integration completed.'
    if ($RunRuntimeSmoke) {
        Write-Host 'PASS: Development WPF runtime smoke completed against the disposable migrated database.'
    }
    Write-Warning 'No existing UAT/Production database is an approved target for this runner.'
}
finally {
    $env:PHARMALIMS_TEST_MASTER_CONNECTION_STRING = $previousMaster
    $env:PHARMALIMS_TEST_SQL_DISPOSABLE = $previousDisposable
    $env:PHARMALIMS_CREATE_CLEAN_UAT = $previousCleanUat
    $env:PHARMALIMS_WINDOWS_RUNTIME_PROBE_DATABASE = $previousProbe
}
