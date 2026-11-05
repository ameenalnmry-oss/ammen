[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$ProvenanceFile = '',
    [string]$ClientRuntime = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\client-v309',
    [string]$ServiceRuntime = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\service',
    [string]$ServiceName = 'PharmaLIMS.TrustedSigning',
    [string]$BackupRoot = 'C:\ProgramData\PharmaLIMS\Backups'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Controlled runtime deployment requires an elevated Administrator PowerShell.'
    }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-ProvenanceFile {
    if (-not [string]::IsNullOrWhiteSpace($ProvenanceFile)) {
        if (-not (Test-Path -LiteralPath $ProvenanceFile -PathType Leaf)) {
            throw "Provenance evidence file does not exist: $ProvenanceFile"
        }
        return (Resolve-Path -LiteralPath $ProvenanceFile).Path
    }

    $candidate = Get-ChildItem -LiteralPath (Join-Path $root 'artifacts\release-readiness') `
        -Filter 'V309_RUNTIME_PROVENANCE_*.json' -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw 'No V309 runtime provenance evidence was found. Run Invoke-V309ReleaseReadinessGate.ps1 -RunProvenance first.'
    }
    return $candidate.FullName
}

Assert-Administrator

$runningClient = Get-Process PharmaLIMS -ErrorAction SilentlyContinue
if ($null -ne $runningClient) {
    throw 'Close every running PharmaLIMS.exe before controlled deployment.'
}

foreach ($required in @($ClientRuntime,$ServiceRuntime,$BackupRoot)) {
    if (-not (Test-Path -LiteralPath $required -PathType Container)) {
        throw "Required deployment directory is missing: $required"
    }
}

$svc = Get-Service -Name $ServiceName -ErrorAction Stop
$provenancePath = Resolve-ProvenanceFile
$prov = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json

if ([string]$prov.status -ne 'DIFFERENT') {
    throw "Expected DIFFERENT provenance status before reconciliation; actual: $($prov.status)"
}

$manifest = Join-Path $root 'SOURCE_MANIFEST_SHA256.txt'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw 'SOURCE_MANIFEST_SHA256.txt is missing.'
}
$currentManifestHash = Get-Sha256 $manifest
if (-not $currentManifestHash.Equals(([string]$prov.sourceManifestSha256),[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Current source manifest does not match the source manifest attested by the selected provenance evidence.'
}

$expected = @(
    'client|PharmaLIMS.dll',
    'client|PharmaLIMS.TrustedSigning.dll',
    'service|PharmaLIMS.dll',
    'service|PharmaLIMS.TrustedSigning.dll',
    'service|PharmaLIMS.TrustedSigning.Host.dll'
)

$blocking = @($prov.comparisons | Where-Object { $_.blocking -eq $true })
if ($blocking.Count -ne $expected.Count) {
    throw "Controlled deployment requires exactly $($expected.Count) known executable hash mismatches; provenance contains $($blocking.Count) blocking differences."
}

$rows = @()
foreach ($row in $blocking) {
    $key = "$($row.kind)|$($row.path)"
    if ($expected -notcontains $key) {
        throw "Unexpected blocking provenance difference; deployment refused: $key / $($row.state)"
    }
    if ([string]$row.state -ne 'HASH_MISMATCH') {
        throw "Expected HASH_MISMATCH for controlled artifact $key; actual state: $($row.state)"
    }
    $rows += $row
}
foreach ($key in $expected) {
    if (-not ($rows | Where-Object { "$($_.kind)|$($_.path)" -eq $key })) {
        throw "Required controlled difference is absent from provenance evidence: $key"
    }
}

$clientBuild = [string]$prov.clientBuildDirectory
$serviceBuild = [string]$prov.serviceBuildDirectory
foreach ($required in @($clientBuild,$serviceBuild)) {
    if (-not (Test-Path -LiteralPath $required -PathType Container)) {
        throw "Controlled build staging directory referenced by provenance is missing: $required"
    }
}

$plan = New-Object System.Collections.Generic.List[object]
foreach ($row in $rows) {
    $buildRoot = if ([string]$row.kind -eq 'client') { $clientBuild } else { $serviceBuild }
    $runtimeRoot = if ([string]$row.kind -eq 'client') { $ClientRuntime } else { $ServiceRuntime }
    $source = Join-Path $buildRoot ([string]$row.path)
    $target = Join-Path $runtimeRoot ([string]$row.path)

    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Controlled build artifact is missing: $source"
    }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Installed runtime artifact is missing: $target"
    }

    $sourceHash = Get-Sha256 $source
    if (-not $sourceHash.Equals(([string]$row.buildSha256),[StringComparison]::OrdinalIgnoreCase)) {
        throw "Controlled build artifact changed after provenance capture: $source"
    }

    $runtimeHash = Get-Sha256 $target
    if (-not $runtimeHash.Equals(([string]$row.runtimeSha256),[StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed runtime changed after provenance capture: $target"
    }

    $plan.Add([pscustomobject]@{
        kind = [string]$row.kind
        path = [string]$row.path
        source = $source
        target = $target
        sourceSha256 = $sourceHash
        beforeSha256 = $runtimeHash
    })
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backup = Join-Path $BackupRoot "EQ001-UAT-v309-controlled-release-$stamp"
$evidenceDir = Join-Path $root "artifacts\controlled-deployment\$stamp"
New-Item -ItemType Directory -Path $backup -Force | Out-Null
New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null

$deployment = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    provenanceFile = $provenancePath
    applicationVersion = [string]$prov.applicationVersion
    sourceManifestSha256 = [string]$prov.sourceManifestSha256
    clientRuntime = $ClientRuntime
    serviceRuntime = $ServiceRuntime
    serviceName = $ServiceName
    backupDirectory = $backup
    status = 'PLANNED'
    files = @()
}

foreach ($item in $plan) {
    $backupTarget = Join-Path $backup (Join-Path $item.kind $item.path)
    $backupParent = Split-Path -Parent $backupTarget
    New-Item -ItemType Directory -Path $backupParent -Force | Out-Null
    Copy-Item -LiteralPath $item.target -Destination $backupTarget -Force

    $deployment.files += [ordered]@{
        kind = $item.kind
        path = $item.path
        sourceSha256 = $item.sourceSha256
        beforeSha256 = $item.beforeSha256
        afterSha256 = $null
        backupPath = $backupTarget
    }
}

$evidencePath = Join-Path $evidenceDir "V309_CONTROLLED_DEPLOYMENT_$stamp.json"
$deployment | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8

$serviceWasRunning = $svc.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running
$deploymentStarted = $false

try {
    if ($serviceWasRunning) {
        Write-Host "Stopping service $ServiceName..."
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        (Get-Service -Name $ServiceName).WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(30))
    }

    $deploymentStarted = $true
    foreach ($item in $plan) {
        if ($PSCmdlet.ShouldProcess($item.target,"Replace with controlled Release artifact")) {
            Copy-Item -LiteralPath $item.source -Destination $item.target -Force
        }
    }

    foreach ($item in $plan) {
        $after = Get-Sha256 $item.target
        if (-not $after.Equals($item.sourceSha256,[StringComparison]::OrdinalIgnoreCase)) {
            throw "Post-copy SHA-256 verification failed: $($item.target)"
        }
        foreach ($file in $deployment.files) {
            if ($file.kind -eq $item.kind -and $file.path -eq $item.path) {
                $file.afterSha256 = $after
                break
            }
        }
    }

    if ($serviceWasRunning) {
        Write-Host "Starting service $ServiceName..."
        Start-Service -Name $ServiceName -ErrorAction Stop
        (Get-Service -Name $ServiceName).WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Running,
            [TimeSpan]::FromSeconds(30))
    }

    $deployment.status = 'PASS'
    $deployment.completedAtUtc = [DateTime]::UtcNow.ToString('o')
    $deployment | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8

    Write-Host ''
    Write-Host 'PASS: controlled v309 executable artifacts deployed and SHA-256 verified.'
    Write-Host "Backup   : $backup"
    Write-Host "Evidence : $evidencePath"
    Write-Host "Service  : $((Get-Service -Name $ServiceName).Status)"
    Write-Warning 'No database/configuration/activity state was changed. Re-run the release-readiness gate with -RunProvenance before any release attestation.'
}
catch {
    $original = $_
    if ($deploymentStarted) {
        Write-Warning 'Controlled deployment failed. Restoring backed-up runtime artifacts.'
        try {
            if ((Get-Service -Name $ServiceName).Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
                Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
                (Get-Service -Name $ServiceName).WaitForStatus(
                    [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                    [TimeSpan]::FromSeconds(30))
            }
            foreach ($item in $plan) {
                $backupSource = Join-Path $backup (Join-Path $item.kind $item.path)
                if (Test-Path -LiteralPath $backupSource -PathType Leaf) {
                    Copy-Item -LiteralPath $backupSource -Destination $item.target -Force
                }
            }
            if ($serviceWasRunning) {
                Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
            }
        }
        catch {
            Write-Warning "Rollback encountered an additional error: $($_.Exception.Message)"
        }
    }

    $deployment.status = 'ROLLED_BACK_OR_FAILED'
    $deployment.failedAtUtc = [DateTime]::UtcNow.ToString('o')
    $deployment.failure = $original.Exception.Message
    $deployment | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    throw $original
}
