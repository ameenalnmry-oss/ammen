[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)]
    [string]$Thumbprint,
    [string]$ServiceConfig = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\service\hostsettings.json',
    [string]$ClientConfig = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\client-v309\appsettings.json',
    [string]$ServiceName = 'PharmaLIMS.TrustedSigning',
    [string]$BackupRoot = 'C:\ProgramData\PharmaLIMS\Backups',
    [string]$EvidenceRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Certificate-store cutover requires an elevated Administrator PowerShell.'
    }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-JsonPropertyValue([object]$Object,[string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Set-JsonPropertyValue([object]$Object,[string]$Name,[object]$Value) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
        return
    }
    $property.Value = $Value
}

function Get-ProtectedConfigSnapshot([object]$Config) {
    $certificateFields = @(
        'CertificatePath',
        'CertificatePassword',
        'CertificateThumbprint',
        'CertificateStoreName'
    )

    $snapshot = [ordered]@{}
    foreach ($property in $Config.PSObject.Properties) {
        if ($certificateFields -notcontains $property.Name) {
            $snapshot[$property.Name] = $property.Value
        }
    }
    return ($snapshot | ConvertTo-Json -Depth 20 -Compress)
}

Assert-Administrator

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Certificate-store cutover requires Windows.'
}

$normalized = ($Thumbprint -replace '\s','').ToUpperInvariant()
if ($normalized.Length -ne 40 -or $normalized -notmatch '^[0-9A-F]{40}$') {
    throw 'Thumbprint must be exactly 40 hexadecimal characters after whitespace normalization.'
}

foreach ($required in @($ServiceConfig,$ClientConfig)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required configuration file is missing: $required"
    }
}
if (-not (Test-Path -LiteralPath $BackupRoot -PathType Container)) {
    throw "Backup root is missing: $BackupRoot"
}

$service = Get-Service -Name $ServiceName -ErrorAction Stop
$serviceWasRunning = $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\certificate-cutover'
}
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backup = Join-Path $BackupRoot "EQ001-UAT-v309-certificate-store-$stamp"
New-Item -ItemType Directory -Path $backup -Force | Out-Null

$serviceConfigBackup = Join-Path $backup 'hostsettings.json'
Copy-Item -LiteralPath $ServiceConfig -Destination $serviceConfigBackup -Force
$beforeHash = Get-Sha256 $ServiceConfig

# Read-only preflight must succeed before any service/configuration change.
& (Join-Path $PSScriptRoot 'Test-V309CertificateStoreReadiness.ps1') `
    -Thumbprint $normalized `
    -ServiceName $ServiceName `
    -ClientConfig $ClientConfig `
    -OutputDirectory $EvidenceRoot

$readiness = Get-ChildItem -LiteralPath $EvidenceRoot -Filter 'V309_CERTIFICATE_STORE_READINESS_*.json' -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $readiness) {
    throw 'Certificate-store readiness evidence was not created.'
}
$ready = Get-Content -LiteralPath $readiness.FullName -Raw | ConvertFrom-Json
if ([string]$ready.status -ne 'READY_FOR_CONTROLLED_CUTOVER') {
    throw "Certificate-store readiness status is not acceptable: $($ready.status)"
}
if (([string]$ready.thumbprintSha1).ToUpperInvariant() -ne $normalized) {
    throw 'Certificate-store readiness evidence does not match the requested thumbprint.'
}
if ([string]$ready.clientPinState -ne 'MATCH') {
    throw 'Client TLS pin must match the selected certificate before cutover.'
}
if ([string]$ready.servicePrivateKeyReadAccess -ne 'PASS') {
    throw 'Service private-key ACL readiness was not proven before cutover.'
}

$config = Get-Content -LiteralPath $ServiceConfig -Raw | ConvertFrom-Json
$preserved = Get-ProtectedConfigSnapshot $config

# Idempotent success if the service is already on the exact requested store certificate.
$alreadyConfigured =
    [string]::IsNullOrWhiteSpace([string](Get-JsonPropertyValue $config 'CertificatePath')) -and
    [string]::IsNullOrWhiteSpace([string](Get-JsonPropertyValue $config 'CertificatePassword')) -and
    (([string](Get-JsonPropertyValue $config 'CertificateThumbprint') -replace '\s','').ToUpperInvariant() -eq $normalized) -and
    ([string](Get-JsonPropertyValue $config 'CertificateStoreName') -ieq 'My')

$cutover = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    serviceName = $ServiceName
    serviceConfig = $ServiceConfig
    clientConfig = $ClientConfig
    requestedThumbprint = $normalized
    certificateSha256 = [string]$ready.certificateSha256
    readinessEvidence = $readiness.FullName
    backupDirectory = $backup
    beforeConfigSha256 = $beforeHash
    afterConfigSha256 = $null
    serviceWasRunning = $serviceWasRunning
    status = if ($alreadyConfigured) { 'ALREADY_CONFIGURED' } else { 'PLANNED' }
}
$evidencePath = Join-Path $EvidenceRoot "V309_CERTIFICATE_STORE_CUTOVER_$stamp.json"
$cutover | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8

if ($alreadyConfigured) {
    if ($serviceWasRunning -and (Get-Service -Name $ServiceName).Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        throw 'Trusted-signing service is not running even though store configuration already matches.'
    }
    $cutover.afterConfigSha256 = $beforeHash
    $cutover.status = 'PASS_ALREADY_CONFIGURED'
    $cutover.completedAtUtc = [DateTime]::UtcNow.ToString('o')
    $cutover | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    Write-Host 'PASS: trusted-signing service already uses the requested LocalMachine\My certificate.'
    Write-Host "Evidence : $evidencePath"
    exit 0
}

$changed = $false
try {
    if ($serviceWasRunning) {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        (Get-Service -Name $ServiceName).WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(30))
    }

    Set-JsonPropertyValue $config 'CertificatePath' ''
    Set-JsonPropertyValue $config 'CertificatePassword' ''
    Set-JsonPropertyValue $config 'CertificateThumbprint' $normalized
    Set-JsonPropertyValue $config 'CertificateStoreName' 'My'

    if ($PSCmdlet.ShouldProcess($ServiceConfig,'Switch trusted-signing TLS certificate source to LocalMachine\My')) {
        # Set-Content updates the existing protected file in place so its ACL remains attached.
        $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ServiceConfig -Encoding UTF8
        $changed = $true
    }

    $verify = Get-Content -LiteralPath $ServiceConfig -Raw | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace([string](Get-JsonPropertyValue $verify 'CertificatePath')) -or
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonPropertyValue $verify 'CertificatePassword')) -or
        (([string](Get-JsonPropertyValue $verify 'CertificateThumbprint') -replace '\s','').ToUpperInvariant() -ne $normalized) -or
        ([string](Get-JsonPropertyValue $verify 'CertificateStoreName') -ine 'My')) {
        throw 'Post-write certificate configuration verification failed.'
    }

    $afterPreserved = Get-ProtectedConfigSnapshot $verify
    if ($preserved -cne $afterPreserved) {
        throw 'Certificate cutover changed a protected non-certificate setting.'
    }

    Start-Service -Name $ServiceName -ErrorAction Stop
    (Get-Service -Name $ServiceName).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    Start-Sleep -Seconds 2
    if ((Get-Service -Name $ServiceName).Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        throw 'Trusted-signing service did not remain Running after certificate-store cutover.'
    }

    # Repeat read-only validation after service startup.
    & (Join-Path $PSScriptRoot 'Test-V309CertificateStoreReadiness.ps1') `
        -Thumbprint $normalized `
        -ServiceName $ServiceName `
        -ClientConfig $ClientConfig `
        -OutputDirectory $EvidenceRoot

    $cutover.afterConfigSha256 = Get-Sha256 $ServiceConfig
    $cutover.status = 'PASS'
    $cutover.completedAtUtc = [DateTime]::UtcNow.ToString('o')
    $cutover | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8

    Write-Host ''
    Write-Host 'PASS: trusted-signing certificate source cut over to LocalMachine\My.'
    Write-Host "Certificate SHA-256 : $($ready.certificateSha256)"
    Write-Host "Backup              : $backup"
    Write-Host "Evidence            : $evidencePath"
    Write-Host "Service             : $((Get-Service -Name $ServiceName).Status)"
    Write-Warning 'The legacy PFX file was not deleted and no certificate was rotated. Complete the approved secret/certificate exposure assessment separately before closing H-02.'
}
catch {
    $original = $_
    Write-Warning 'Certificate-store cutover failed. Restoring the exact protected host configuration.'

    try {
        if ((Get-Service -Name $ServiceName).Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            (Get-Service -Name $ServiceName).WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(30))
        }

        if ($changed -and (Test-Path -LiteralPath $serviceConfigBackup -PathType Leaf)) {
            $bytes = [IO.File]::ReadAllBytes($serviceConfigBackup)
            [IO.File]::WriteAllBytes($ServiceConfig,$bytes)
        }

        if ($serviceWasRunning) {
            Start-Service -Name $ServiceName -ErrorAction Stop
            (Get-Service -Name $ServiceName).WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(30))
        }
    }
    catch {
        Write-Warning "Rollback encountered an additional error: $($_.Exception.Message)"
    }

    $cutover.status = 'ROLLED_BACK_OR_FAILED'
    $cutover.failedAtUtc = [DateTime]::UtcNow.ToString('o')
    $cutover.failure = $original.Exception.Message
    $cutover | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    throw $original
}
