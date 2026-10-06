[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path $PublishDirectory).Path

$hasPfx = -not [string]::IsNullOrWhiteSpace($env:PHARMALIMS_CODESIGN_PFX_BASE64)
$hasPassword = -not [string]::IsNullOrWhiteSpace($env:PHARMALIMS_CODESIGN_PFX_PASSWORD)
if (-not $hasPfx -and -not $hasPassword) {
    throw 'Production release requires Authenticode signing. Configure both PHARMALIMS_CODESIGN_PFX_BASE64 and PHARMALIMS_CODESIGN_PFX_PASSWORD; an unsigned artifact cannot be uploaded as a Production release.'
}
if ($hasPfx -ne $hasPassword) {
    throw 'Authenticode code signing is partially configured. PHARMALIMS_CODESIGN_PFX_BASE64 and PHARMALIMS_CODESIGN_PFX_PASSWORD must either both be configured or both be absent.'
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe was not found in the Windows SDK.' }

$pfxPath = Join-Path ([IO.Path]::GetTempPath()) ("PharmaLIMS-CodeSign-" + [Guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:PHARMALIMS_CODESIGN_PFX_BASE64))
    $timestampUrl = if ([string]::IsNullOrWhiteSpace($env:PHARMALIMS_TIMESTAMP_URL)) { 'http://timestamp.digicert.com' } else { $env:PHARMALIMS_TIMESTAMP_URL }
    $targets = @(
        (Join-Path $publish 'PharmaLIMS.exe'),
        (Join-Path $publish 'PharmaLIMS.dll')
    )
    foreach ($target in $targets) {
        if (-not (Test-Path $target)) { throw "Required first-party release binary is missing: $target" }
        & $signtool.FullName sign /fd SHA256 /td SHA256 /tr $timestampUrl /f $pfxPath /p $env:PHARMALIMS_CODESIGN_PFX_PASSWORD $target
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $target" }
        & $signtool.FullName verify /pa /all $target
        if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed: $target" }
    }
}
finally {
    Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
}
