$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($name in @('Sign-PublishedArtifact.ps1', 'New-ResolvedSbom.ps1')) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root "scripts/$name"), [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) { throw "PowerShell syntax failure in $name" }
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('PharmaLIMS-SignGuard-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$savedPfx = $env:PHARMALIMS_CODESIGN_PFX_BASE64
$savedPassword = $env:PHARMALIMS_CODESIGN_PFX_PASSWORD
try {
    foreach ($partial in @($false, $true)) {
        $env:PHARMALIMS_CODESIGN_PFX_BASE64 = ''
        $env:PHARMALIMS_CODESIGN_PFX_PASSWORD = if ($partial) { 'Fixture-only-no-certificate' } else { '' }
        $blocked = $false
        try { & (Join-Path $root 'scripts/Sign-PublishedArtifact.ps1') -PublishDirectory $fixture }
        catch {
            $expected = if ($partial) { 'partially configured' } else { 'Production release requires Authenticode signing' }
            if (-not $_.Exception.Message.Contains($expected)) { throw }
            $blocked = $true
        }
        if (-not $blocked) { throw 'An unsigned Production artifact passed the signing gate.' }
    }
    Write-Host 'PASS absent and partial signing configuration block Production release; signing/SBOM scripts parse successfully.'
}
finally {
    $env:PHARMALIMS_CODESIGN_PFX_BASE64 = $savedPfx
    $env:PHARMALIMS_CODESIGN_PFX_PASSWORD = $savedPassword
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
