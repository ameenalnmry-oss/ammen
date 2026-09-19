[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path $PublishDirectory).Path

if ([string]::IsNullOrWhiteSpace($env:PHARMALIMS_CODESIGN_PFX_BASE64)) {
    throw 'PHARMALIMS_CODESIGN_PFX_BASE64 is required for Production release signing.'
}
if ([string]::IsNullOrWhiteSpace($env:PHARMALIMS_CODESIGN_PFX_PASSWORD)) {
    throw 'PHARMALIMS_CODESIGN_PFX_PASSWORD is required for Production release signing.'
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
