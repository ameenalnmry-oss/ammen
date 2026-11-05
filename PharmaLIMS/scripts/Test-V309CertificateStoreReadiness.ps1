[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Thumbprint,
    [string]$ServiceName = 'PharmaLIMS.TrustedSigning',
    [string]$ClientConfig = '',
    [string]$OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'


function Resolve-ServiceAccountSid([string]$StartName) {
    if ([string]::IsNullOrWhiteSpace($StartName)) {
        throw 'Windows service identity is empty.'
    }

    $account = $StartName.Trim()
    if ($account -ieq 'LocalSystem') {
        $account = 'NT AUTHORITY\SYSTEM'
    }
    elseif ($account.StartsWith('.\', [StringComparison]::Ordinal)) {
        $account = "$env:COMPUTERNAME\$($account.Substring(2))"
    }

    try {
        return ([Security.Principal.NTAccount]::new($account)).Translate(
            [Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        throw "Unable to resolve trusted-signing service identity to a SID: $StartName"
    }
}

function Resolve-MachinePrivateKeyPath(
    [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {

    $privateKey = $null
    try {
        $privateKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
        if ($null -eq $privateKey) {
            $privateKey = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($Certificate)
        }
        if ($null -eq $privateKey) {
            throw 'Only RSA or ECDSA machine private keys are supported by the controlled certificate cutover.'
        }

        if ($privateKey -is [Security.Cryptography.RSACng]) {
            $name = $privateKey.Key.UniqueName
            return Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$name"
        }
        if ($privateKey -is [Security.Cryptography.ECDsaCng]) {
            $name = $privateKey.Key.UniqueName
            return Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$name"
        }
        if ($privateKey -is [Security.Cryptography.RSACryptoServiceProvider]) {
            $name = $privateKey.CspKeyContainerInfo.UniqueKeyContainerName
            return Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$name"
        }

        throw "Unsupported machine private-key provider: $($privateKey.GetType().FullName)"
    }
    finally {
        if ($null -ne $privateKey) { $privateKey.Dispose() }
    }
}

function Assert-ServicePrivateKeyReadAccess(
    [string]$KeyPath,
    [string]$ServiceSid) {

    if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) {
        throw "Machine private-key file was not found: $KeyPath"
    }

    $acl = Get-Acl -LiteralPath $KeyPath
    $readMask = [int64][Security.AccessControl.FileSystemRights]::Read
    $fullMask = [int64][Security.AccessControl.FileSystemRights]::FullControl
    $allowed = $false

    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
        try {
            $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        }
        catch {
            continue
        }
        if ($sid -ne $ServiceSid) { continue }

        $rights = [int64]$rule.FileSystemRights
        if (($rights -band $readMask) -ne 0 -or ($rights -band $fullMask) -eq $fullMask) {
            $allowed = $true
            break
        }
    }

    if (-not $allowed) {
        throw 'The trusted-signing Windows service identity does not have explicit or inherited read access to the selected certificate private key.'
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Certificate-store readiness requires Windows.'
}

$normalized = ($Thumbprint -replace '\s','').ToUpperInvariant()
if ($normalized.Length -ne 40 -or $normalized -notmatch '^[0-9A-F]{40}$') {
    throw 'Thumbprint must be exactly 40 hexadecimal characters after whitespace normalization.'
}

$service = Get-CimInstance Win32_Service -Filter ("Name='{0}'" -f ($ServiceName -replace "'","''"))
if ($null -eq $service) {
    throw "Windows service was not found: $ServiceName"
}

$store = [Security.Cryptography.X509Certificates.X509Store]::new(
    [Security.Cryptography.X509Certificates.StoreName]::My,
    [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)

$certificate = $null
try {
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    $matches = $store.Certificates.Find(
        [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
        $normalized,
        $false)

    if ($matches.Count -ne 1) {
        throw "Expected exactly one matching certificate in LocalMachine\My; found $($matches.Count)."
    }

    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($matches[0])
}
finally {
    $store.Close()
}

try {
    $now = [DateTime]::UtcNow
    if (-not $certificate.HasPrivateKey) {
        throw 'Certificate does not have an accessible private key in the current administrative context.'
    }
    if ($now -lt $certificate.NotBefore.ToUniversalTime() -or $now -gt $certificate.NotAfter.ToUniversalTime()) {
        throw 'Certificate is outside its validity period.'
    }

    $ekuExtension = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        Select-Object -First 1
    if ($null -eq $ekuExtension) {
        throw 'Certificate has no Enhanced Key Usage extension; explicit Server Authentication EKU is required for this controlled cutover.'
    }

    $eku = [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$ekuExtension
    $serverAuth = @($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.1' })
    if ($serverAuth.Count -ne 1) {
        throw 'Certificate does not contain the Server Authentication EKU.'
    }

    $serviceSid = Resolve-ServiceAccountSid $service.StartName
    $privateKeyPath = Resolve-MachinePrivateKeyPath $certificate
    Assert-ServicePrivateKeyReadAccess -KeyPath $privateKeyPath -ServiceSid $serviceSid

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $pin = (($sha256.ComputeHash($certificate.RawData) | ForEach-Object { $_.ToString('x2') }) -join '').ToUpperInvariant()
    }
    finally {
        $sha256.Dispose()
    }

    $clientPin = $null
    $clientPinState = 'NOT_CHECKED'
    if (-not [string]::IsNullOrWhiteSpace($ClientConfig)) {
        if (-not (Test-Path -LiteralPath $ClientConfig -PathType Leaf)) {
            throw "Client configuration was not found: $ClientConfig"
        }
        $json = Get-Content -LiteralPath $ClientConfig -Raw | ConvertFrom-Json
        if ($null -eq $json.TrustedSigning) {
            throw 'Client configuration has no TrustedSigning section.'
        }
        $clientPin = ([string]$json.TrustedSigning.ServerCertificateSha256 -replace '\s','').ToUpperInvariant()
        if ($clientPin.Length -ne 64 -or $clientPin -notmatch '^[0-9A-F]{64}$') {
            throw 'Client TrustedSigning.ServerCertificateSha256 is not a valid 64-hex SHA-256 pin.'
        }
        if ($clientPin -ne $pin) {
            throw 'Client TLS pin does not match the selected LocalMachine certificate.'
        }
        $clientPinState = 'MATCH'
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts\certificate-readiness'
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $output = Join-Path $OutputDirectory "V309_CERTIFICATE_STORE_READINESS_$stamp.json"

    $result = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        serviceName = $service.Name
        serviceStartName = $service.StartName
        serviceSid = $serviceSid
        serviceState = $service.State
        privateKeyPath = $privateKeyPath
        servicePrivateKeyReadAccess = 'PASS'
        store = 'LocalMachine\My'
        thumbprintSha1 = $normalized
        certificateSha256 = $pin
        subject = $certificate.Subject
        issuer = $certificate.Issuer
        serialNumber = $certificate.SerialNumber
        notBeforeUtc = $certificate.NotBefore.ToUniversalTime().ToString('o')
        notAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('o')
        hasPrivateKey = $certificate.HasPrivateKey
        serverAuthenticationEku = $true
        clientPinState = $clientPinState
        clientPin = $clientPin
        status = 'READY_FOR_CONTROLLED_CUTOVER'
        note = 'Read-only preflight verified the selected certificate, client pin and filesystem ACL read access for the configured Windows service identity. Controlled service startup remains the final operational proof.'
    }

    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding UTF8

    Write-Host "PASS: certificate-store preflight passed."
    Write-Host "Service identity : $($service.StartName)"
    Write-Host "Certificate      : $($certificate.Subject)"
    Write-Host "Service key ACL  : PASS ($serviceSid)"
    Write-Host "SHA-256 pin      : $pin"
    Write-Host "Client pin       : $clientPinState"
    Write-Host "Evidence         : $output"
}
finally {
    if ($null -ne $certificate) { $certificate.Dispose() }
}
