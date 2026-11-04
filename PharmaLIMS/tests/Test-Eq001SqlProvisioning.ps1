[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Exercise the actual provisioning section without invoking Windows services or
# Chocolatey. Real SQL installation is qualified separately by Windows UAT.
$source = Get-Content (Join-Path $RepositoryRoot 'scripts/Invoke-Eq001WindowsUat.ps1') -Raw
$start = $source.IndexOf('    # SQL Express provisioning contract')
if ($start -lt 0) { $start = $source.IndexOf("    if (-not (Get-Service -Name 'MSSQL`$SQLEXPRESS'") }
$end = $source.IndexOf('    $instanceRoot =', $start)
if ($start -lt 0 -or $end -le $start) { throw 'SQL provisioning section was not found.' }
$block = $source.Substring($start, $end - $start).Replace(
    '[System.Security.Principal.WindowsIdentity]::GetCurrent().Name', "'TEST\runner'")
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('eq001-sql-contract-' + [Guid]::NewGuid().ToString('N'))
$savedTemp = $env:TEMP
$savedProgramFiles = $env:ProgramFiles
$stamp = 'mock-sql-regression'
$results = $testRoot
$script:installed = $false
$script:registered = $true
$script:mode = 'success'
$script:summaryPath = Join-Path $testRoot 'Summary.txt'
$script:configurationPath = $null

function Get-Service {
    param($Name, $ErrorAction)
    if ($script:installed) { [pscustomobject]@{ Name = 'MSSQL$SQLEXPRESS'; Status = 'Running' } }
}
function Get-ItemProperty {
    param($Path, $ErrorAction)
    if ($script:installed -and $script:registered) { [pscustomobject]@{ SQLEXPRESS = 'MSSQL16.SQLEXPRESS' } }
}
function choco {
    $configurationArgument = @($args | Where-Object { $_ -like '*ConfigurationFile:*' })
    if ($configurationArgument.Count -ne 1) {
        throw 'SQL provisioning did not explicitly request an SQLENGINE/SQLEXPRESS configuration file.'
    }
    $script:configurationPath = ($configurationArgument[0] -split 'ConfigurationFile:', 2)[1].Trim('"', "'")
    $configuration = Get-Content $script:configurationPath -Raw
    foreach ($setting in @('FEATURES="SQLENGINE"', 'INSTANCENAME="SQLEXPRESS"',
            'INSTANCEID="SQLEXPRESS"', 'SQLSYSADMINACCOUNTS="TEST\runner"')) {
        if (-not $configuration.Contains($setting)) { throw "Missing explicit setup setting: $setting" }
    }
    if (-not ($args -contains '--version=2022.16.0.20260305')) { throw 'SQL package version is not pinned.' }
    if (-not ($args -contains '--force')) { throw 'Stale package metadata can skip the missing-instance installation.' }
    if ($script:mode -in @('success', 'missing-registry')) { $script:installed = $true }
    $global:LASTEXITCODE = 0
}
function Start-Sleep {
    param($Seconds)
    throw 'SQL contract test must not wait or install a real server.'
}
function Get-ChildItem {
    param($Path, $Filter, $ErrorAction, $Recurse)
    [pscustomobject]@{ FullName = $script:summaryPath }
}

try {
    New-Item $testRoot -ItemType Directory -Force | Out-Null
    $env:TEMP = $testRoot
    $env:ProgramFiles = $testRoot
    Set-Content $script:summaryPath -Value @('Final result: Failed',
        'Exit message: password=FakeSecret42 token=FakeToken42', 'SQLSVCPASSWORD: AlsoFakeSecret')
    & ([scriptblock]::Create($block))
    if (-not $script:installed) { throw 'Installation contract was not exercised.' }
    if (Test-Path $script:configurationPath) { throw 'Temporary SQL configuration was retained after success.' }
    Write-Host 'PASS explicit named SQL engine installation and registration'

    foreach ($mode in @('missing-service', 'missing-registry')) {
        $script:installed = $false
        $script:registered = $false
        $script:mode = $mode
        # Advance the clock past the real condition deadline without sleeping.
        $expiredBlock = $block.Replace('AddSeconds(60)', 'AddSeconds(-1)')
        try {
            & ([scriptblock]::Create($expiredBlock))
            throw 'Missing registration unexpectedly succeeded.'
        }
        catch {
            if ($_.Exception.Message -notlike '*without registering both*within 60 seconds*') { throw }
            $diagnostic = Join-Path $results 'sql-provisioning.txt'
            if (-not (Test-Path $diagnostic)) { throw 'Missing registration did not preserve diagnostics.' }
            $text = Get-Content $diagnostic -Raw
            if ($text -match 'FakeSecret|FakeToken|AlsoFakeSecret') { throw 'Diagnostic exposed a credential.' }
            if ($text -notmatch 'password=\[redacted\] token=\[redacted\]') { throw 'Redaction was not exercised.' }
            if (Test-Path $script:configurationPath) { throw 'Temporary SQL configuration was retained after failure.' }
            Write-Host "PASS $mode fails at bounded deadline with sanitized evidence and config cleanup"
        }
    }
}
finally {
    $env:TEMP = $savedTemp
    $env:ProgramFiles = $savedProgramFiles
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
