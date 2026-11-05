[CmdletBinding()]
param(
    [string]$ClientRuntime = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\client-v309',
    [string]$ServiceRuntime = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\service',
    [string]$EvidenceRoot = '',
    [switch]$RunProvenance,
    [switch]$RunDatabaseIntegration,
    [switch]$RunRuntimeSmoke,
    [switch]$RunCertificateStorePreflight,
    [string]$CertificateThumbprint = '',
    [string]$TrustedSigningServiceName = 'PharmaLIMS.TrustedSigning'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $EvidenceRoot = Join-Path $root "artifacts\release-readiness\$stamp"
}
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null

$steps = New-Object System.Collections.Generic.List[object]
$failure = $null

function Add-Step([string]$Name,[string]$Status,[string]$Detail) {
    $script:steps.Add([pscustomobject]@{
        name = $Name
        status = $Status
        detail = $Detail
        atUtc = [DateTime]::UtcNow.ToString('o')
    })
}


function Clear-ControlledWorkspaceGeneratedArtifacts {
    # This gate is designed to be re-runnable on an extracted controlled source
    # package. Previous build/test runs legitimately create local bin/obj/cache
    # directories that are prohibited from a delivery package. Remove only those
    # generated directories under the source root; never touch runtime or evidence.
    $artifactRoot = (Join-Path $root 'artifacts')
    $generatedNames = @('bin','obj','__pycache__','.pytest_cache')

    $generated = @(
        Get-ChildItem -LiteralPath $root -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $generatedNames -contains $_.Name -and
            -not $_.FullName.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object { $_.FullName.Length } -Descending
    )

    foreach ($directory in $generated) {
        if (Test-Path -LiteralPath $directory.FullName -PathType Container) {
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force
        }
    }

    Write-Host ("Controlled workspace cleanup removed {0} generated director{1}." -f `
        $generated.Count, $(if ($generated.Count -eq 1) { 'y' } else { 'ies' }))
}

function Invoke-Gate([string]$Name,[scriptblock]$Action) {
    Write-Host ''
    Write-Host "===== $Name ====="
    try {
        & $Action
        Add-Step $Name 'PASS' ''
    }
    catch {
        Add-Step $Name 'FAIL' $_.Exception.Message
        throw
    }
}

try {
    Invoke-Gate 'Controlled workspace cleanup' {
        Clear-ControlledWorkspaceGeneratedArtifacts
    }

    Invoke-Gate 'Controlled source/build validation' {
        $params = @{
            ValidateDeliveryPackage = $true
        }
        if ($RunDatabaseIntegration) { $params.RunDatabaseIntegration = $true }
        if ($RunRuntimeSmoke) {
            if (-not $RunDatabaseIntegration) {
                throw 'RunRuntimeSmoke requires RunDatabaseIntegration.'
            }
            $params.RunRuntimeSmoke = $true
        }
        & (Join-Path $PSScriptRoot 'Invoke-ReleaseValidation.ps1') @params
    }

    if ($RunCertificateStorePreflight) {
        Invoke-Gate 'Certificate-store readiness preflight' {
            if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
                throw 'CertificateThumbprint is required when RunCertificateStorePreflight is selected.'
            }
            & (Join-Path $PSScriptRoot 'Test-V309CertificateStoreReadiness.ps1') `
                -Thumbprint $CertificateThumbprint `
                -ServiceName $TrustedSigningServiceName `
                -ClientConfig (Join-Path $ClientRuntime 'appsettings.json') `
                -OutputDirectory $EvidenceRoot
        }
    }
    else {
        Add-Step 'Certificate-store readiness preflight' 'NOT_RUN' 'Use -RunCertificateStorePreflight -CertificateThumbprint <thumbprint>.'
    }

    if ($RunProvenance) {
        Invoke-Gate 'Installed runtime provenance' {
            & (Join-Path $PSScriptRoot 'Invoke-V309RuntimeProvenance.ps1') `
                -ClientRuntime $ClientRuntime `
                -ServiceRuntime $ServiceRuntime `
                -OutputDirectory $EvidenceRoot
        }
    }
    else {
        Add-Step 'Installed runtime provenance' 'NOT_RUN' 'Use -RunProvenance on the controlled Windows runtime.'
    }

    if (-not $RunDatabaseIntegration) {
        Add-Step 'Disposable database integration' 'NOT_RUN' 'Use -RunDatabaseIntegration only with LocalDB or an explicitly disposable SQL target.'
    }
    if (-not $RunRuntimeSmoke) {
        Add-Step 'WPF runtime smoke' 'NOT_RUN' 'Use -RunDatabaseIntegration -RunRuntimeSmoke on a disposable migrated database.'
    }
}
catch {
    $failure = $_
}
finally {
    $summary = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        application = 'PharmaLIMS'
        sourceRoot = $root
        clientRuntime = $ClientRuntime
        serviceRuntime = $ServiceRuntime
        status = if ($null -eq $failure) { 'PASS_WITH_OPEN_GATES' } else { 'FAIL' }
        steps = @($steps | ForEach-Object { $_ })
        note = 'PASS_WITH_OPEN_GATES is not GMP release approval. Functional UAT, controlled certificate cutover/rotation assessment and QA validation sign-off remain separate gates.'
    }
    $summaryPath = Join-Path $EvidenceRoot 'V309_RELEASE_READINESS_SUMMARY.json'
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    Write-Host ''
    Write-Host "Evidence directory : $EvidenceRoot"
    Write-Host "Summary            : $summaryPath"
}

if ($null -ne $failure) {
    throw $failure.Exception
}

Write-Host ''
Write-Host 'PASS: automated release-readiness gates completed for the selected scope.'
Write-Warning 'This does not authorize GMP release. Review all NOT_RUN gates and obtain QA validation sign-off.'
