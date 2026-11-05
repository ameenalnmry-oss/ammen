[CmdletBinding()]
param(
    [string]$ServiceConfig = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\service\hostsettings.json',
    [string]$TargetDatabase = 'PharmaLIMS_UAT_20261001',
    [string]$ServiceName = 'PharmaLIMS.TrustedSigning'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from Administrator PowerShell.'
}
if ($TargetDatabase -notmatch '^(EQ001_UAT_|PharmaLIMS_EQ001_UAT_|PharmaLIMS_UAT_|PharmaLIMS_Integration_)[A-Za-z0-9_]*$') {
    throw 'TargetDatabase is not an explicitly isolated UAT database.'
}

$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hostProject = Join-Path $project 'tools\PharmaLIMS.TrustedSigning.Host\PharmaLIMS.TrustedSigning.Host.csproj'
$hostBuild = Join-Path $project 'tools\PharmaLIMS.TrustedSigning.Host\bin\Release\net8.0-windows'
$devExample = Join-Path $project 'appsettings.Development.example.json'
$devLocal = Join-Path $project 'appsettings.Development.json'

if (-not (Test-Path -LiteralPath $ServiceConfig -PathType Leaf)) { throw "Trusted host settings not found: $ServiceConfig" }
if (-not (Test-Path -LiteralPath $hostProject -PathType Leaf)) { throw 'Trusted signing host project is missing.' }
if (-not (Test-Path -LiteralPath $devExample -PathType Leaf)) { throw 'Development settings example is missing.' }

$service = Get-CimInstance Win32_Service -Filter ("Name='" + $ServiceName.Replace("'","''") + "'")
if ($null -eq $service) { throw "Windows service '$ServiceName' is not installed." }
if ($service.StartName -notmatch '^\.\\[A-Za-z0-9_.-]+$') { throw 'Trusted signing service must run under a dedicated local Windows account.' }
$serviceAccount = $env:COMPUTERNAME + '\' + $service.StartName.Substring(2)


function Get-ConfiguredTrustedSigningCertificate($Config) {
    $path = [string]$Config.CertificatePath
    $thumbprint = ([string]$Config.CertificateThumbprint -replace '\s','').ToUpperInvariant()
    $usesFile = -not [string]::IsNullOrWhiteSpace($path)
    $usesStore = -not [string]::IsNullOrWhiteSpace($thumbprint)

    if ($usesFile -eq $usesStore) {
        throw 'Trusted signing configuration must select exactly one certificate source.'
    }

    if ($usesStore) {
        if ($thumbprint.Length -ne 40 -or $thumbprint -notmatch '^[0-9A-F]{40}$') {
            throw 'Trusted signing CertificateThumbprint is invalid.'
        }
        if ([string]$Config.CertificateStoreName -ine 'My') {
            throw 'Trusted signing certificate-store mode requires LocalMachine\My.'
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$Config.CertificatePassword)) {
            throw 'CertificatePassword must be empty in certificate-store mode.'
        }

        $store = [Security.Cryptography.X509Certificates.X509Store]::new(
            [Security.Cryptography.X509Certificates.StoreName]::My,
            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
        try {
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            $matches = $store.Certificates.Find(
                [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                $thumbprint,
                $false)
            if ($matches.Count -ne 1) {
                throw "Expected exactly one trusted-signing certificate in LocalMachine\My; found $($matches.Count)."
            }
            return [Security.Cryptography.X509Certificates.X509Certificate2]::new($matches[0])
        }
        finally {
            $store.Close()
        }
    }

    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new()
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    $cert.Import($path,[string]$Config.CertificatePassword,$flags)
    return $cert
}

$hostConfig = Get-Content -LiteralPath $ServiceConfig -Raw | ConvertFrom-Json
if ($hostConfig.DeploymentMode -ne 'UAT' -or $hostConfig.ProductionActivationApproved -ne $false) {
    throw 'Refusing to modify a non-UAT or production-activated trusted signing host.'
}
if ([string]::IsNullOrWhiteSpace([string]$hostConfig.ConnectionString)) { throw 'Primary trusted-host connection is missing.' }

$primary = [System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$hostConfig.ConnectionString)
if (-not $primary['Integrated Security'] -or -not $primary['Encrypt'] -or $primary['TrustServerCertificate']) {
    throw 'Primary trusted-host SQL transport is not Windows-authenticated, encrypted and certificate-validated.'
}
if ($primary['Initial Catalog'] -notlike 'EQ001_UAT_*' -and $primary['Initial Catalog'] -notlike 'PharmaLIMS_EQ001_UAT_*') {
    throw 'The existing primary trusted service target is not the expected isolated EQ001 UAT database.'
}

$activity = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($primary.ConnectionString)
$activity['Initial Catalog'] = $TargetDatabase
$activity['Application Name'] = 'PharmaLIMS.TrustedActivitySigning'
$activityConnectionString = $activity.ConnectionString

Write-Host "Primary trusted endpoint remains: $($primary['Data Source']) / $($primary['Initial Catalog'])"
Write-Host "Activity signing endpoint will be: $($activity['Data Source']) / $($activity['Initial Catalog'])"
Write-Host "Service identity: $serviceAccount"

# Verify target schema and provision only the permissions required for activity reauthentication/signing.
$sql = @'
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() NOT LIKE N'PharmaLIMS_UAT[_]%'
   AND DB_NAME() NOT LIKE N'PharmaLIMS_Integration[_]%'
   AND DB_NAME() NOT LIKE N'EQ001_UAT[_]%'
   AND DB_NAME() NOT LIKE N'PharmaLIMS_EQ001_UAT[_]%'
 THROW 56880, 'Activity signing provisioning is UAT only.', 1;

IF OBJECT_ID(N'dbo.Users',N'U') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentSignerEligibility',N'V') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage',N'P') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipment_AssertTrustedBoundary',N'P') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipment_ReadActivitySignerCredential',N'P') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipment_RecordActivityAuthFailure',N'P') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipment_CommitActivityAuthSuccess',N'P') IS NULL
 THROW 56881, 'Required v309 equipment signing schema is missing. Run controlled database maintenance first.', 1;

DECLARE @serviceLoginName sysname=@ServiceLogin;
IF SUSER_ID(@serviceLoginName) IS NULL
 THROW 56882, 'Dedicated trusted signing Windows login is not provisioned at SQL Server level.', 1;
IF NOT EXISTS(SELECT 1 FROM sys.server_principals WHERE name=@serviceLoginName AND type IN(N'U',N'G'))
 THROW 56883, 'Trusted signing service identity must be a Windows principal.', 1;
IF EXISTS(
 SELECT 1 FROM sys.server_role_members m
 JOIN sys.server_principals r ON r.principal_id=m.role_principal_id
 JOIN sys.server_principals s ON s.principal_id=m.member_principal_id
 WHERE s.name=@serviceLoginName AND r.name IN(N'sysadmin',N'securityadmin',N'serveradmin',N'dbcreator')
) THROW 56884, 'Trusted signing service identity has elevated SQL server roles.', 1;

BEGIN TRANSACTION;
BEGIN TRY
 IF USER_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
 BEGIN
   DECLARE @create nvarchar(max)=N'CREATE USER [PharmaLIMS_TrustedEquipmentSigner] FOR LOGIN '+QUOTENAME(@serviceLoginName)+N';';
   EXEC sys.sp_executesql @create;
 END;

 IF NOT EXISTS(
   SELECT 1 FROM sys.database_principals p
   WHERE p.name=N'PharmaLIMS_TrustedEquipmentSigner' AND p.type=N'U' AND p.sid=SUSER_SID(@serviceLoginName)
 ) THROW 56885, 'Trusted signer database user is not mapped to the configured Windows service login.', 1;

 GRANT SELECT ON OBJECT::dbo.MicroEquipmentSignerEligibility TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_AssertTrustedBoundary TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_ReadActivitySignerCredential TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_RecordActivityAuthFailure TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_CommitActivityAuthSuccess TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT VIEW SECURITY DEFINITION TO [PharmaLIMS_TrustedEquipmentSigner];

 -- v309: keep the service least-privileged. Password material can only be read
 -- through the owner-executed, trusted-origin authentication bridge above.
 DENY SELECT ON OBJECT::dbo.Users (PasswordHash,PasswordHashNew,PasswordSalt)
   TO [PharmaLIMS_TrustedEquipmentSigner];
 DENY UPDATE ON OBJECT::dbo.Users TO [PharmaLIMS_TrustedEquipmentSigner];

 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
'@

$adminConnection = [System.Data.SqlClient.SqlConnection]::new($activityConnectionString)
try {
    $adminConnection.Open()
    $command = $adminConnection.CreateCommand()
    $command.CommandText = $sql
    $command.CommandTimeout = 60
    [void]$command.Parameters.Add('@ServiceLogin',[System.Data.SqlDbType]::NVarChar,256)
    $command.Parameters['@ServiceLogin'].Value = $serviceAccount
    [void]$command.ExecuteNonQuery()
}
finally {
    if ($null -ne $adminConnection) { $adminConnection.Dispose() }
}

# Build the v309 trusted host before touching the installed service runtime.
dotnet build $hostProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Trusted signing host build failed. Installed service was not changed.' }
if (-not (Test-Path -LiteralPath $hostBuild -PathType Container)) { throw 'Trusted signing host build output is missing.' }

$serviceRuntime = Split-Path -Parent $ServiceConfig
$privateRoot = Split-Path -Parent $serviceRuntime
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$rollback = Join-Path $privateRoot ("v309-activity-signing-rollback-" + $stamp)
New-Item -ItemType Directory -Path $rollback -Force | Out-Null
Copy-Item -LiteralPath $ServiceConfig -Destination (Join-Path $rollback 'hostsettings.json') -Force
Copy-Item -LiteralPath $serviceRuntime -Destination (Join-Path $rollback 'service') -Recurse -Force
$hadDevLocal = Test-Path -LiteralPath $devLocal -PathType Leaf
if ($hadDevLocal) {
    Copy-Item -LiteralPath $devLocal -Destination (Join-Path $rollback 'appsettings.Development.json') -Force
}

# Compute the exact TLS server-certificate SHA-256 pin used by the desktop client.
# This supports both the legacy PFX compatibility mode and the preferred
# LocalMachine\My thumbprint mode.
$cert = Get-ConfiguredTrustedSigningCertificate $hostConfig
try {
    if (-not $cert.HasPrivateKey -or $cert.NotAfter -le (Get-Date)) {
        throw 'Configured trusted-signing certificate is missing its private key or is expired.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $pin = [BitConverter]::ToString($sha.ComputeHash($cert.RawData)).Replace('-','') }
    finally { $sha.Dispose() }
}
finally { $cert.Dispose() }

# Create a local Development configuration; the example remains source-controlled and secret-free.
$dev = Get-Content -LiteralPath $devExample -Raw | ConvertFrom-Json
$dev.Database.Database = $TargetDatabase
$trusted = [pscustomobject]@{
    Enabled = $false
    ActivitySigningEnabled = $true
    PipeName = [string]$hostConfig.PipeName
    ServerCertificateSha256 = $pin
}
$dev | Add-Member -NotePropertyName TrustedSigning -NotePropertyValue $trusted -Force
$dev | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $devLocal -Encoding UTF8

$wasRunning = (Get-Service $ServiceName).Status -eq 'Running'
try {
    if ($wasRunning) {
        Stop-Service $ServiceName -Force
        (Get-Service $ServiceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    }

    # Refresh host binaries but never overwrite the protected configuration/certificate.
    foreach ($file in Get-ChildItem -LiteralPath $hostBuild -File -Recurse) {
        if ($file.Name -in @('appsettings.json','hostsettings.json') -or $file.Extension -in @('.pfx','.key','.cer','.crt')) { continue }
        $relative = $file.FullName.Substring($hostBuild.Length).TrimStart('\')
        $target = Join-Path $serviceRuntime $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }

    $hostConfig | Add-Member -NotePropertyName ActivitySigningConnectionString -NotePropertyValue $activityConnectionString -Force
    # Write in place so the existing protected ACL remains intact.
    $hostConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ServiceConfig -Encoding UTF8

    Start-Service $ServiceName
    (Get-Service $ServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
}
catch {
    $failure = $_
    try {
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        Copy-Item -Path (Join-Path $rollback 'service\*') -Destination $serviceRuntime -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $rollback 'hostsettings.json') -Destination $ServiceConfig -Force
        if ($hadDevLocal) {
            Copy-Item -LiteralPath (Join-Path $rollback 'appsettings.Development.json') -Destination $devLocal -Force
        } else {
            Remove-Item -LiteralPath $devLocal -Force -ErrorAction SilentlyContinue
        }
        if ($wasRunning) {
            Start-Service $ServiceName
            (Get-Service $ServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
        }
    } catch { Write-Warning 'Automatic service rollback was incomplete; preserve the rollback directory.' }
    throw $failure
}

Write-Host ''
Write-Host 'PASS: v309 trusted activity signing configured with least-privilege reauthentication bridge.'
Write-Host "Trusted service primary DB preserved: $($primary['Initial Catalog'])"
Write-Host "Activity signing DB: $TargetDatabase"
Write-Host "Desktop debug settings: $devLocal"
Write-Host "Rollback: $rollback"
Write-Host 'Rebuild/run the WPF Debug application, then review Activity 10. Do not recreate the calibration.'
