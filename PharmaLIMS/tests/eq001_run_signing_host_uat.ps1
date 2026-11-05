# Runs ONLY under the isolated UAT dedicated Windows signing identity.
# No production connection, SQL permission cutover, or historical record change.
param([switch]$Rehearsal)
$ErrorActionPreference = 'Stop'
$expected = 'AMEEN\PharmaLIMSSigner'
$actual = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if (-not [string]::Equals($actual,$expected,[StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing trusted signing host under unauthorized process account: $actual"
}
$root = 'E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS'
$dll = Join-Path $root 'tools\PharmaLIMS.TrustedSigning\bin\Debug\net8.0-windows\PharmaLIMS.TrustedSigning.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Compiled signer host is missing or unreadable for dedicated account.' }
$dotnetExe = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { throw 'Global .NET host is missing or unreadable for dedicated account.' }
$env:PHARMALIMS_SIGNING_ISOLATED_UAT='ENABLED'
if ($Rehearsal) { $env:PHARMALIMS_SIGNING_UAT_REHEARSAL='ENABLED' }
else { Remove-Item Env:PHARMALIMS_SIGNING_UAT_REHEARSAL -ErrorAction SilentlyContinue }
$env:PHARMALIMS_TRUSTED_SIGNING_SQL='Server=.\SQLEXPRESS;Database=PharmaLIMS_UAT_20261001;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
$env:PHARMALIMS_SIGNING_REVIEWER_GROUP_SID='S-1-5-21-3887410152-3473494482-1737934645-1010'
$env:PHARMALIMS_SIGNING_APPROVER_GROUP_SID='S-1-5-21-3887410152-3473494482-1737934645-1011'
# Check the credential-launched service's real token, including nested groups.
# The service identity must not also be a Windows reviewer or approver.
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
foreach ($sid in @($env:PHARMALIMS_SIGNING_REVIEWER_GROUP_SID,$env:PHARMALIMS_SIGNING_APPROVER_GROUP_SID)) {
    if ($principal.IsInRole([Security.Principal.SecurityIdentifier]::new($sid))) {
        throw 'Dedicated service identity belongs to a signing authorization group.'
    }
}
# Do not use LOCALAPPDATA/USERPROFILE from a credential-launched process.
# Start-Process -Credential can retain the parent environment paths. The
# controlled launcher captures stdout/stderr under its own protected profile.
Write-Output "EQ-001: Windows UAT service identity: $actual"
Write-Output 'EQ-001: accepting localhost Windows-authenticated UAT requests only.'
& $dotnetExe $dll --serve
if ($LASTEXITCODE -ne 0) { throw "Trusted signer host stopped with exit code $LASTEXITCODE" }
