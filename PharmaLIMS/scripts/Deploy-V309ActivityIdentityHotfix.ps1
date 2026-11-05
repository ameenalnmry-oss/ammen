[CmdletBinding()]
param(
    [string]$ServiceConfig = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\service\hostsettings.json',
    [string]$ClientRuntime = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\client-v309',
    [string]$ServiceName = 'PharmaLIMS.TrustedSigning'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from Administrator PowerShell.'
}

$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $project 'Database\MigrationManifest.json'
$migrationRelative = 'Migrations/20261031_001_Activity_Signing_Primary_Identity_Authority.sql'
$migrationPath = Join-Path (Join-Path $project 'Database') $migrationRelative
$hostProject = Join-Path $project 'tools\PharmaLIMS.TrustedSigning.Host\PharmaLIMS.TrustedSigning.Host.csproj'
$clientProject = Join-Path $project 'PharmaLIMS.csproj'
$hostBuild = Join-Path $project 'tools\PharmaLIMS.TrustedSigning.Host\bin\Release\net8.0-windows'
$clientBuild = Join-Path $project 'bin\Release\net8.0-windows7.0'
$serviceRuntime = Split-Path -Parent $ServiceConfig

foreach ($required in @($ServiceConfig,$manifestPath,$migrationPath,$hostProject,$clientProject,$ClientRuntime,$serviceRuntime)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required UAT path is missing: $required" }
}

if (Get-Process PharmaLIMS -ErrorAction SilentlyContinue) {
    throw 'Close every running PharmaLIMS.exe before deploying this UAT hotfix.'
}

$settings = Get-Content -LiteralPath $ServiceConfig -Raw | ConvertFrom-Json
if ($settings.DeploymentMode -ne 'UAT' -or $settings.ProductionActivationApproved -ne $false) {
    throw 'Refusing to deploy to a non-UAT or production-activated trusted host.'
}
if ([string]::IsNullOrWhiteSpace([string]$settings.ConnectionString) -or
    [string]::IsNullOrWhiteSpace([string]$settings.ActivitySigningConnectionString)) {
    throw 'Primary and ActivitySigning connection strings are required.'
}

$primary = [System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$settings.ConnectionString)
$activity = [System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$settings.ActivitySigningConnectionString)
if ($primary['Initial Catalog'] -notlike 'EQ001_UAT_*' -and
    $primary['Initial Catalog'] -notlike 'PharmaLIMS_EQ001_UAT_*') {
    throw "Primary trusted DB is not an isolated EQ001 UAT database: $($primary['Initial Catalog'])"
}
if ($activity['Initial Catalog'] -notlike 'PharmaLIMS_UAT_*' -and
    $activity['Initial Catalog'] -notlike 'PharmaLIMS_Integration_*' -and
    $activity['Initial Catalog'] -notlike 'EQ001_UAT_*' -and
    $activity['Initial Catalog'] -notlike 'PharmaLIMS_EQ001_UAT_*') {
    throw "ActivitySigning DB is not an allowed isolated UAT database: $($activity['Initial Catalog'])"
}
if ($primary['Data Source'] -ne $activity['Data Source']) {
    throw 'Primary and ActivitySigning databases must remain on the same controlled SQL Server.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$entry = @($manifest.migrations | Where-Object { $_.versionKey -eq '20261031_001' })
if ($entry.Count -ne 1) { throw 'MigrationManifest must contain exactly one 20261031_001 entry.' }
$entry = $entry[0]
if ([string]$entry.databaseScope -ne 'ActivitySigning') { throw '20261031_001 must be scoped to ActivitySigning.' }

$actualHash = (Get-FileHash -LiteralPath $migrationPath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedHash = ([string]$entry.sha256).ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "Migration checksum mismatch. Expected $expectedHash, actual $actualHash."
}

Write-Host "Primary DB : $($primary['Initial Catalog'])"
Write-Host "Activity DB: $($activity['Initial Catalog'])"
Write-Host "Migration  : 20261031_001 / $actualHash"

Write-Host "`nBuilding trusted host..."
dotnet build $hostProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Trusted host build failed; runtime was not changed.' }

Write-Host "`nBuilding WPF client..."
dotnet build $clientProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'WPF client build failed; runtime was not changed.' }

if (-not (Test-Path -LiteralPath (Join-Path $hostBuild 'PharmaLIMS.TrustedSigning.Host.exe'))) {
    throw 'Trusted host Release output is missing.'
}
if (-not (Test-Path -LiteralPath (Join-Path $clientBuild 'PharmaLIMS.exe'))) {
    throw 'WPF Release output is missing.'
}

# Apply and ledger the ActivitySigning migration atomically. Existing matching
# ledger evidence is accepted; checksum drift is rejected.
$sqlConnection = [System.Data.SqlClient.SqlConnection]::new([string]$settings.ActivitySigningConnectionString)
try {
    $sqlConnection.Open()
    $check = $sqlConnection.CreateCommand()
    $check.CommandText = @"
IF OBJECT_ID(N'dbo.LIMS_SchemaVersions',N'U') IS NULL
    THROW 56893,'Controlled migration ledger is missing from the ActivitySigning database.',1;
SELECT MigrationChecksum
FROM dbo.LIMS_SchemaVersions
WHERE VersionKey=N'20261031_001';
"@
    $existing = $check.ExecuteScalar()
    if ($null -ne $existing -and $existing -ne [DBNull]::Value) {
        $existingHash = ([string]$existing).ToLowerInvariant()
        if ($existingHash -ne $expectedHash) {
            throw "ActivitySigning ledger checksum mismatch for 20261031_001: $existingHash"
        }
        Write-Host "Migration already applied with matching ledger checksum."
    }
    else {
        $transaction = $sqlConnection.BeginTransaction()
        try {
            $apply = $sqlConnection.CreateCommand()
            $apply.Transaction = $transaction
            $apply.CommandTimeout = 300
            $apply.CommandText = [IO.File]::ReadAllText($migrationPath)
            [void]$apply.ExecuteNonQuery()

            $record = $sqlConnection.CreateCommand()
            $record.Transaction = $transaction
            $record.CommandText = @"
MERGE dbo.LIMS_SchemaVersions WITH (HOLDLOCK) AS target
USING (SELECT @VersionKey AS VersionKey) AS source
ON target.VersionKey=source.VersionKey
WHEN MATCHED THEN UPDATE SET
    Description=@Description,
    MigrationChecksum=@Checksum,
    ApplicationVersion=@ApplicationVersion
WHEN NOT MATCHED THEN INSERT
    (VersionKey,Description,MigrationChecksum,ApplicationVersion)
    VALUES(@VersionKey,@Description,@Checksum,@ApplicationVersion);
"@
            [void]$record.Parameters.Add('@VersionKey',[System.Data.SqlDbType]::NVarChar,100)
            [void]$record.Parameters.Add('@Description',[System.Data.SqlDbType]::NVarChar,500)
            [void]$record.Parameters.Add('@Checksum',[System.Data.SqlDbType]::NVarChar,128)
            [void]$record.Parameters.Add('@ApplicationVersion',[System.Data.SqlDbType]::NVarChar,50)
            $record.Parameters['@VersionKey'].Value = [string]$entry.versionKey
            $record.Parameters['@Description'].Value = [string]$entry.description
            $record.Parameters['@Checksum'].Value = $expectedHash
            $record.Parameters['@ApplicationVersion'].Value = [string]$manifest.applicationVersion
            [void]$record.ExecuteNonQuery()

            $transaction.Commit()
            Write-Host 'Migration 20261031_001 applied and ledgered.'
        }
        catch {
            try { $transaction.Rollback() } catch {}
            throw
        }
        finally {
            $transaction.Dispose()
        }
    }
}
finally {
    if ($null -ne $sqlConnection) { $sqlConnection.Dispose() }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = "C:\ProgramData\PharmaLIMS\Backups\EQ001-UAT-v309-primary-identity-$stamp"
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
Copy-Item -LiteralPath $serviceRuntime -Destination (Join-Path $backupRoot 'service') -Recurse -Force
Copy-Item -LiteralPath $ClientRuntime -Destination (Join-Path $backupRoot 'client-v309') -Recurse -Force
Write-Host "Backup: $backupRoot"

$wasRunning = (Get-Service $ServiceName).Status -eq 'Running'
try {
    if ($wasRunning) {
        Stop-Service $ServiceName -Force
        (Get-Service $ServiceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    }

    foreach ($file in Get-ChildItem -LiteralPath $hostBuild -File -Recurse) {
        if ($file.Name -in @('hostsettings.json','appsettings.json') -or
            $file.Extension -in @('.pfx','.p12','.key','.cer','.crt')) { continue }
        $relative = $file.FullName.Substring($hostBuild.Length).TrimStart('\')
        $target = Join-Path $serviceRuntime $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }

    foreach ($file in Get-ChildItem -LiteralPath $clientBuild -File -Recurse) {
        if ($file.Name -eq 'appsettings.json' -or
            $file.Extension -in @('.pfx','.p12','.key','.cer','.crt')) { continue }
        $relative = $file.FullName.Substring($clientBuild.Length).TrimStart('\')
        $target = Join-Path $ClientRuntime $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }

    if ($wasRunning) {
        Start-Service $ServiceName
        (Get-Service $ServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    }
}
catch {
    $failure = $_
    try {
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        Copy-Item -Path (Join-Path $backupRoot 'service\*') -Destination $serviceRuntime -Recurse -Force
        Copy-Item -Path (Join-Path $backupRoot 'client-v309\*') -Destination $ClientRuntime -Recurse -Force
        if ($wasRunning) {
            Start-Service $ServiceName
            (Get-Service $ServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
        }
    }
    catch {
        Write-Warning 'Automatic binary rollback was incomplete. Preserve the backup and inspect service state.'
    }
    throw $failure
}

Write-Host ''
Write-Host 'PASS: v309 primary-identity activity-signing hotfix deployed.'
Write-Host "Service      : $((Get-Service $ServiceName).Status)"
Write-Host "Primary DB   : $($primary['Initial Catalog'])"
Write-Host "Activity DB  : $($activity['Initial Catalog'])"
Write-Host "Migration    : 20261031_001"
Write-Host "Backup       : $backupRoot"
Write-Host ''
Write-Host 'Next: run the client under the Windows account mapped to qa. Complete Change Password if prompted, then Approve Activity 10 once.'
