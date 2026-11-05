[CmdletBinding()]
param(
    [string]$ClientRuntime = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\client-v309',
    [string]$ServiceRuntime = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\service',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild,
    [string]$ClientBuildDirectory = '',
    [string]$ServiceBuildDirectory = '',
    [switch]$FailOnDebugSymbolDifference
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$clientProject = Join-Path $root 'PharmaLIMS.csproj'
$hostProject = Join-Path $root 'tools\PharmaLIMS.TrustedSigning.Host\PharmaLIMS.TrustedSigning.Host.csproj'
$sourceManifest = Join-Path $root 'SOURCE_MANIFEST_SHA256.txt'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts\provenance'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

foreach ($required in @($clientProject,$hostProject,$sourceManifest,$ClientRuntime,$ServiceRuntime)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required provenance input is missing: $required"
    }
}

# The attestation is meaningful only if the controlled source set is intact.
& (Join-Path $PSScriptRoot 'Test-SourceManifest.ps1') -ManifestFile $sourceManifest
if ($LASTEXITCODE -ne 0) { throw 'Source manifest verification failed.' }

$projectXml = [xml](Get-Content -LiteralPath $clientProject -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Project version is missing.' }

$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetVersion)) {
    throw 'dotnet SDK version could not be determined.'
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$isolatedRoot = Join-Path $OutputDirectory "controlled-build-$stamp"

if (-not $SkipBuild) {
    if (-not [string]::IsNullOrWhiteSpace($ClientBuildDirectory) -or
        -not [string]::IsNullOrWhiteSpace($ServiceBuildDirectory)) {
        throw 'ClientBuildDirectory/ServiceBuildDirectory are accepted only with -SkipBuild.'
    }

    $ClientBuildDirectory = Join-Path $isolatedRoot 'client'
    $ServiceBuildDirectory = Join-Path $isolatedRoot 'service'
    New-Item -ItemType Directory -Path $ClientBuildDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $ServiceBuildDirectory -Force | Out-Null

    Push-Location $root
    try {
        Write-Host 'Building WPF client into isolated provenance staging...'
        & dotnet build $clientProject -c Release --no-incremental --nologo -o $ClientBuildDirectory
        if ($LASTEXITCODE -ne 0) { throw 'WPF provenance build failed.' }

        Write-Host 'Building trusted host into isolated provenance staging...'
        & dotnet build $hostProject -c Release --no-incremental --nologo -o $ServiceBuildDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Trusted host provenance build failed.' }
    }
    finally { Pop-Location }
}
else {
    if ([string]::IsNullOrWhiteSpace($ClientBuildDirectory) -or
        [string]::IsNullOrWhiteSpace($ServiceBuildDirectory)) {
        throw '-SkipBuild requires both -ClientBuildDirectory and -ServiceBuildDirectory.'
    }
    $ClientBuildDirectory = [IO.Path]::GetFullPath($ClientBuildDirectory)
    $ServiceBuildDirectory = [IO.Path]::GetFullPath($ServiceBuildDirectory)
}

foreach ($required in @($ClientBuildDirectory,$ServiceBuildDirectory)) {
    if (-not (Test-Path -LiteralPath $required -PathType Container)) {
        throw "Expected controlled Release staging directory is missing: $required"
    }
}

$buildCommands = @(
    "dotnet build PharmaLIMS.csproj -c Release --no-incremental --nologo -o `"$ClientBuildDirectory`"",
    "dotnet build tools\PharmaLIMS.TrustedSigning.Host\PharmaLIMS.TrustedSigning.Host.csproj -c Release --no-incremental --nologo -o `"$ServiceBuildDirectory`""
)

function Get-RelativePath([string]$Base,[string]$Full) {
    $baseFull = [IO.Path]::GetFullPath($Base).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Full)
    if (-not $fullPath.StartsWith($baseFull,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside expected base: $Full"
    }
    return $fullPath.Substring($baseFull.Length).Replace('\','/')
}

function Test-ExcludedRuntimeFile([string]$Relative,[string]$Kind) {
    $name = [IO.Path]::GetFileName($Relative)
    $extension = [IO.Path]::GetExtension($Relative).ToLowerInvariant()

    # Site/runtime configuration is intentionally preserved across deployments
    # and is therefore evidence, not a byte-for-byte build artifact.
    if ($Kind -eq 'client' -and $name -eq 'appsettings.json') { return $true }
    if ($Kind -eq 'service' -and $name -in @('hostsettings.json','appsettings.json')) { return $true }

    # PharmaLIMS.TrustedSigning is a class-library dependency. Its standalone
    # .deps.json is emitted by `dotnet build -o` but is not required beside
    # either consuming executable (PharmaLIMS or PharmaLIMS.TrustedSigning.Host),
    # each of which owns the authoritative runtime dependency graph.
    if ($name -eq 'PharmaLIMS.TrustedSigning.deps.json') { return $true }

    # Private keys/certificates are provisioned separately and must never be
    # pulled into a provenance build comparison.
    if ($extension -in @('.pfx','.p12','.key','.cer','.crt')) { return $true }
    return $false
}

function New-ComparisonRow(
    [string]$Kind,
    [string]$Path,
    [string]$State,
    [string]$BuildHash,
    [string]$RuntimeHash,
    [bool]$Blocking
) {
    return [pscustomobject]@{
        kind = $Kind
        path = $Path
        state = $State
        blocking = $Blocking
        buildSha256 = $BuildHash
        runtimeSha256 = $RuntimeHash
    }
}

function Compare-Tree([string]$BuildRoot,[string]$RuntimeRoot,[string]$Kind) {
    $rows = New-Object System.Collections.Generic.List[object]
    $expected = @{}

    foreach ($file in Get-ChildItem -LiteralPath $BuildRoot -File -Recurse) {
        $relative = Get-RelativePath $BuildRoot $file.FullName
        if (Test-ExcludedRuntimeFile $relative $Kind) { continue }
        $expected[$relative] = $true

        $target = Join-Path $RuntimeRoot $relative.Replace('/',[IO.Path]::DirectorySeparatorChar)
        $expectedHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $isDebugSymbol = $file.Extension.Equals('.pdb',[StringComparison]::OrdinalIgnoreCase)
        $debugBlocking = $isDebugSymbol -and $FailOnDebugSymbolDifference.IsPresent

        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            $state = if ($isDebugSymbol) { 'DEBUG_SYMBOL_MISSING_RUNTIME' } else { 'MISSING_RUNTIME' }
            $rows.Add((New-ComparisonRow $Kind $relative $state $expectedHash $null ([bool](-not $isDebugSymbol -or $debugBlocking))))
            continue
        }

        $actualHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -eq $expectedHash) {
            $rows.Add((New-ComparisonRow $Kind $relative 'MATCH' $expectedHash $actualHash $false))
            continue
        }

        $state = if ($isDebugSymbol) { 'DEBUG_SYMBOL_HASH_MISMATCH' } else { 'HASH_MISMATCH' }
        $rows.Add((New-ComparisonRow $Kind $relative $state $expectedHash $actualHash ([bool](-not $isDebugSymbol -or $debugBlocking))))
    }

    # Flag executable/code extras in runtime that are not part of the isolated
    # controlled build. Non-code deployment evidence is intentionally ignored.
    foreach ($file in Get-ChildItem -LiteralPath $RuntimeRoot -File -Recurse) {
        $relative = Get-RelativePath $RuntimeRoot $file.FullName
        if (Test-ExcludedRuntimeFile $relative $Kind) { continue }
        if ($expected.ContainsKey($relative)) { continue }

        $lowerName = $file.Name.ToLowerInvariant()
        $isDebugSymbol = $file.Extension.Equals('.pdb',[StringComparison]::OrdinalIgnoreCase)
        $isCodeArtifact = $file.Extension.ToLowerInvariant() -in @('.dll','.exe') -or
            $lowerName.EndsWith('.deps.json',[StringComparison]::Ordinal) -or
            $lowerName.EndsWith('.runtimeconfig.json',[StringComparison]::Ordinal)

        if ($isDebugSymbol) {
            $rows.Add((New-ComparisonRow $Kind $relative 'UNEXPECTED_RUNTIME_DEBUG_SYMBOL' $null `
                (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() `
                ([bool]$FailOnDebugSymbolDifference.IsPresent)))
            continue
        }
        if (-not $isCodeArtifact) { continue }

        $rows.Add((New-ComparisonRow $Kind $relative 'UNEXPECTED_RUNTIME_CODE' $null `
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() $true))
    }

    return $rows.ToArray()
}

$comparisons = @()
$comparisons += Compare-Tree $ClientBuildDirectory $ClientRuntime 'client'
$comparisons += Compare-Tree $ServiceBuildDirectory $ServiceRuntime 'service'

$differences = @($comparisons | Where-Object { $_.state -ne 'MATCH' })
$blockingFailures = @($comparisons | Where-Object { $_.blocking })
$nonBlockingDifferences = @($differences | Where-Object { -not $_.blocking })
$status = if ($blockingFailures.Count -eq 0) { 'MATCH' } else { 'DIFFERENT' }

$gitCommit = $null
try {
    $gitCommit = (& git -C $root rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0) { $gitCommit = $null }
}
catch { $gitCommit = $null }

$output = Join-Path $OutputDirectory "V309_RUNTIME_PROVENANCE_$stamp.json"

$result = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    application = 'PharmaLIMS'
    applicationVersion = $version
    sourceRoot = $root
    sourceManifestSha256 = (Get-FileHash -LiteralPath $sourceManifest -Algorithm SHA256).Hash.ToLowerInvariant()
    gitCommit = $gitCommit
    dotnetSdkVersion = $dotnetVersion
    os = [Environment]::OSVersion.VersionString
    buildCommands = $buildCommands
    isolatedBuildRoot = if($SkipBuild){$null}else{$isolatedRoot}
    clientBuildDirectory = [IO.Path]::GetFullPath($ClientBuildDirectory)
    clientRuntimeDirectory = [IO.Path]::GetFullPath($ClientRuntime)
    serviceBuildDirectory = [IO.Path]::GetFullPath($ServiceBuildDirectory)
    serviceRuntimeDirectory = [IO.Path]::GetFullPath($ServiceRuntime)
    status = $status
    blockingDifferenceCount = $blockingFailures.Count
    nonBlockingDifferenceCount = $nonBlockingDifferences.Count
    totalDifferenceCount = $differences.Count
    failOnDebugSymbolDifference = [bool]$FailOnDebugSymbolDifference.IsPresent
    comparisons = @($comparisons)
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding UTF8

Write-Host ''
Write-Host "Runtime provenance status : $status"
Write-Host "Compared files            : $($comparisons.Count)"
Write-Host "Blocking differences      : $($blockingFailures.Count)"
Write-Host "Non-blocking differences  : $($nonBlockingDifferences.Count)"
Write-Host "Evidence                  : $output"

if ($differences.Count -gt 0) {
    $differences |
        Sort-Object -Property @{Expression='blocking';Descending=$true},kind,path |
        Select-Object kind,path,state,blocking,buildSha256,runtimeSha256 |
        Format-Table -AutoSize
}

if ($blockingFailures.Count -gt 0) {
    throw 'Installed runtime does not match the controlled isolated Release staging artifacts. Preserve the evidence; reconcile or deploy the exact controlled package before release attestation.'
}

if ($nonBlockingDifferences.Count -gt 0) {
    Write-Warning 'Executable/runtime code matches, but non-blocking debug-symbol differences were recorded. Preserve the evidence.'
}

Write-Host 'PASS: installed client/service executable code matches the controlled isolated Release staging artifacts.'
