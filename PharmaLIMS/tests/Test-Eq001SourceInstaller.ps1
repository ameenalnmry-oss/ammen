$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $projectRoot 'scripts/Install-Eq001AmeenSource.ps1'
$parseErrors = $null
$tokens = $null
$tree = [Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors) { throw ($parseErrors | Out-String) }

# Load only pure/helper functions, never the site installation body.
foreach ($function in $tree.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$emptyHash = Get-ByteHash ([byte[]]@())
if ($emptyHash -ne 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855') {
    throw 'Empty source snapshots must have the real SHA-256 digest, not a null hash.'
}
$lf = [Text.Encoding]::UTF8.GetBytes("source line`nsecond line`n")
$crlf = [Text.Encoding]::UTF8.GetBytes(([char]0xFEFF) + "source line`r`nsecond line`r`n`r`n")
if ((Get-SourceHash $lf) -ne (Get-SourceHash $crlf)) { throw 'Source normalization changed line-ending/BOM semantics.' }
if ((Get-SourceHash $lf) -eq (Get-SourceHash ([Text.Encoding]::UTF8.GetBytes("changed source`n")))) {
    throw 'A real source edit must change the prerequisite digest.'
}
$repoRoot = (& git -C $projectRoot rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0) { throw 'Git checkout required for blob preservation check.' }
$SourceCommit = (& git -C $repoRoot rev-parse HEAD)
$gitExecutable = (Get-Command git).Source
$bytes = Get-RepositoryBytes 'PharmaLIMS/AppConfig.cs'
$worktreeBytes = [IO.File]::ReadAllBytes((Join-Path $projectRoot 'AppConfig.cs'))
if ((Get-ByteHash $bytes) -ne (Get-ByteHash $worktreeBytes)) {
    throw 'Git object extraction must preserve exact bytes, including UTF-8 and BOM.'
}
$manifest = Get-Content (Join-Path $projectRoot 'scripts/Eq001-AmeenSourcePrerequisites.json') -Raw | ConvertFrom-Json
if ($manifest.OriginalProject -ne 'E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS') {
    throw 'Installer manifest must target only the original project.'
}
foreach ($entry in $manifest.Files) {
    if ($entry.ExpectedNormalizedSha256 -and $entry.ExpectedNormalizedSha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'Invalid prerequisite hash.'
    }
    if ($entry.Path -match '(^|/)\.\.(/|$)' -or $entry.Path -match '(^|/)(bin|obj|\.git)(/|$)') {
        throw 'Source install targets must not include traversal, build outputs or Git state.'
    }
}
[xml]$variant = Get-Content (Join-Path $projectRoot 'scripts/site-overlays/AMEEN-PharmaLIMS.csproj.txt') -Raw
if (@($variant.Project.PropertyGroup.Version | Where-Object { $_ -eq '2026.10.19.306' }).Count -ne 1) {
    throw 'Site project version must remain unchanged.'
}
$nativeSource = Get-Content (Join-Path $projectRoot 'scripts/site-overlays/Eq001ExclusiveSourceFile.cs.txt') -Raw
if (-not ('Eq001SiteSourceFile' -as [type])) { Add-Type -TypeDefinition $nativeSource -ErrorAction Stop }
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('eq001-source-lock-test-' + [guid]::NewGuid())
    [void][IO.Directory]::CreateDirectory($fixture)
    $target = Join-Path $fixture 'source.txt'
    $replacement = Join-Path $fixture 'editor-save.txt'
    $created = Join-Path $fixture 'new-source.txt'
    $handle = $null
    try {
        [IO.File]::WriteAllBytes($target,$lf)
        [IO.File]::WriteAllBytes($replacement,$crlf)
        $handle = [Eq001SiteSourceFile]::Open($target,$false)
        $blocked = $false
        try { $other = [IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite); $other.Dispose() }
        catch [IO.IOException] { $blocked = $true }
        if (-not $blocked) { throw 'An editor must not acquire a writer while installation owns the source.' }
        $blocked = $false
        try { [IO.File]::Replace($replacement,$target,$null) }
        catch [IO.IOException] { $blocked = $true }
        if (-not $blocked) { throw 'An atomic editor save must not replace the exclusively held source.' }
        $handle.WriteAll($crlf)
        $handle.WriteAll($lf)
        $handle.DeleteOnClose($true)
        $handle.DeleteOnClose($false)
        $handle.Dispose(); $handle = $null
        if ((Get-ByteHash ([IO.File]::ReadAllBytes($target))) -ne (Get-ByteHash $lf)) {
            throw 'Rollback must restore exact original bytes and cancel a pending source deletion.'
        }
        $handle = [Eq001SiteSourceFile]::Open($created,$true)
        $handle.WriteAll($crlf)
        $handle.DeleteOnClose($true)
        $handle.Dispose(); $handle = $null
        if (Test-Path -LiteralPath $created) { throw 'Rollback must delete a newly created source through its owned handle.' }
        Write-Host 'PASS: Windows exclusive writers, atomic-save exclusion, exact rollback and handle-owned deletion.'
    } finally {
        if ($handle) { $handle.Dispose() }
        Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
} else { Write-Host 'SKIP: native Windows source-lock and rollback execution requires Windows.' }
Write-Host 'PASS: installer syntax, empty snapshots, source drift hashes, exact Git blob extraction and site project identity. No installation was performed.'
