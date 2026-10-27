[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit,
    [switch]$Apply,
    [switch]$DesktopSessionOnly
)

$ErrorActionPreference = 'Stop'
$project = 'E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS'
$privateDirectory = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024'
if (-not (Test-Path -LiteralPath $project -PathType Container)) { throw 'Original project not found.' }
$repoRoot = (& git -C $project rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0) { throw 'Original Git checkout not found.' }
$resolved = (& git -C $repoRoot rev-parse --verify "$SourceCommit^{commit}")
if ($LASTEXITCODE -ne 0 -or $resolved -ne $SourceCommit) { throw 'Exact source commit must already be fetched.' }
$gitExecutable = (Get-Command git -ErrorAction Stop).Source

function Get-RepositoryBytes([string]$Path) {
    if ($Path -notmatch '^PharmaLIMS/[A-Za-z0-9_./-]+$' -or $Path -match '(^|/)\.\.(/|$)') {
        throw 'Unsafe source object path.'
    }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $gitExecutable
    $info.WorkingDirectory = $repoRoot
    $info.Arguments = 'show "{0}:{1}"' -f $SourceCommit, $Path
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    $buffer = New-Object System.IO.MemoryStream
    try {
        [void]$process.Start()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($buffer)
        $process.WaitForExit()
        $message = $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Cannot read source object: $Path. $message" }
        return ,$buffer.ToArray()
    } finally { $buffer.Dispose(); $process.Dispose() }
}

function Get-ByteHash([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Get-SourceHash([byte[]]$Bytes) {
    $text = [Text.Encoding]::UTF8.GetString($Bytes).TrimStart([char]0xFEFF)
    $text = $text.Replace("`r`n", "`n").TrimEnd([char[]]"`r`n") + "`n"
    return Get-ByteHash ([Text.Encoding]::UTF8.GetBytes($text))
}

function Get-TargetPath([string]$RelativePath) {
    if ($RelativePath -notmatch '^[A-Za-z0-9_./-]+$' -or $RelativePath -match '(^|/)\.\.(/|$)') {
        throw 'Unsafe target path.'
    }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $project $RelativePath))
    if (-not $fullPath.StartsWith($project + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Target escapes original project.'
    }
    return $fullPath
}

$manifestPath = if ($DesktopSessionOnly) { 'PharmaLIMS/scripts/Eq001-SessionSourcePrerequisites.json' } else { 'PharmaLIMS/scripts/Eq001-AmeenSourcePrerequisites.json' }
$manifestBytes = Get-RepositoryBytes $manifestPath
$manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
if ($manifest.OriginalProject -ne $project) { throw 'Source manifest targets a different project.' }
if ($DesktopSessionOnly) {
    $desktopPaths = @('App.xaml.cs','Login.xaml','Login.xaml.cs','MainWindow.xaml','MainWindow.xaml.cs',
        'Services/SessionTransitionCoordinator.cs','Services/SwitchedProcessEnvironment.cs','Services/WindowsUserSwitchService.cs')
    if (@($manifest.Files).Count -ne $desktopPaths.Count -or
        @(Compare-Object $desktopPaths @($manifest.Files.Path)).Count -ne 0 -or
        @($manifest.Files | Where-Object { $_.Remove }).Count -ne 0) {
        throw 'Desktop-only source changes must match the reviewed session feature allowlist.'
    }
}
$changes = New-Object 'System.Collections.Generic.List[object]'
foreach ($entry in $manifest.Files) {
    $target = Get-TargetPath $entry.Path
    $exists = Test-Path -LiteralPath $target -PathType Leaf
    [byte[]]$before = @()
    [byte[]]$after = @()
    if ($exists) { $before = [IO.File]::ReadAllBytes($target) }
    if (-not $entry.Remove) { $after = Get-RepositoryBytes $entry.SourcePath }
    if ($entry.Remove -and -not $exists) { continue }
    if (-not $entry.Remove -and $exists -and (Get-SourceHash $before) -eq (Get-SourceHash $after)) { continue }
    if ($exists) {
        if (-not $entry.ExpectedNormalizedSha256 -or (Get-SourceHash $before) -ne $entry.ExpectedNormalizedSha256) {
            throw "Local source drift detected; nothing was changed: $($entry.Path)"
        }
    } elseif ($entry.ExpectedNormalizedSha256) {
        throw "Expected original source is missing; nothing was changed: $($entry.Path)"
    }
    $changes.Add([pscustomobject]@{
        Path=$entry.Path; Target=$target; WasPresent=$exists; Remove=[bool]$entry.Remove
        BeforeBytes=[byte[]]$before; AfterBytes=[byte[]]$after
        BeforeSha256=(Get-ByteHash $before); AfterSha256=(Get-ByteHash $after)
        Handle=$null
    })
}

Write-Host "Source checks passed for exact commit $SourceCommit. Pending file changes: $($changes.Count)."
if (-not $Apply) { Write-Host 'Read-only check completed. No source files were changed.'; return }
if ($changes.Count -eq 0) { Write-Host 'Requested source already matches. Nothing changed.'; return }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'An Administrator session is required to create the protected rollback record.'
}
if (-not (Test-Path -LiteralPath $privateDirectory -PathType Container)) { throw 'Protected UAT directory not found.' }
$allowed = @('S-1-5-18','S-1-5-32-544','S-1-5-21-3887410152-3473494482-1737934645-1009')
$acl = Get-Acl -LiteralPath $privateDirectory
if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) { throw 'UAT directory ownership is not protected.' }
foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
    if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $allowed) { throw 'UAT directory permissions are not private.' }
}
foreach ($process in @(Get-Process -Name PharmaLIMS -ErrorAction SilentlyContinue)) {
    if ($process.Path -and $process.Path.StartsWith($project + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Close the original application before changing its source.' }
}
$service = Get-Service -Name 'PharmaLIMS.TrustedSigning' -ErrorAction SilentlyContinue
if (-not $DesktopSessionOnly -and $service -and $service.Status -ne 'Stopped') { throw 'Do not change source while the signing service is running.' }

if (-not ('Eq001SiteSourceFile' -as [type])) {
    $nativeSource = Get-RepositoryBytes 'PharmaLIMS/scripts/site-overlays/Eq001ExclusiveSourceFile.cs.txt'
    Add-Type -TypeDefinition ([Text.Encoding]::UTF8.GetString($nativeSource)) -ErrorAction Stop
}
$backupPath = $null
$applied = New-Object 'System.Collections.Generic.List[object]'
try {
    # Hold every existing target exclusively through commit or rollback.
    foreach ($change in $changes) {
        if ($change.WasPresent) {
            $change.Handle = [Eq001SiteSourceFile]::Open($change.Target,$false)
            if ((Get-ByteHash ($change.Handle.ReadAll())) -ne $change.BeforeSha256) {
                throw "Source changed during preparation: $($change.Path)"
            }
        } elseif (Test-Path -LiteralPath $change.Target) {
            throw "A new source target appeared during preparation: $($change.Path)"
        }
    }

    # Save only touched source bytes, not another project or database copy.
    $backupPath = Join-Path $privateDirectory ('source-rollback-{0}.json' -f [guid]::NewGuid())
    $rollback = @($changes | ForEach-Object {
        [pscustomobject]@{ Path=$_.Path; WasPresent=$_.WasPresent; Remove=$_.Remove
            BeforeSha256=$_.BeforeSha256; InstalledSha256=$_.AfterSha256
            OriginalBase64=[Convert]::ToBase64String($_.BeforeBytes) }
    })
    $backupBytes = [Text.Encoding]::UTF8.GetBytes(($rollback | ConvertTo-Json -Depth 6))
    $backupStream = [IO.FileStream]::new($backupPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $backupStream.Write($backupBytes,0,$backupBytes.Length); $backupStream.Flush($true) }
    finally { $backupStream.Dispose() }
    Write-Host "Durable rollback source bytes saved before mutation: $backupPath"

    foreach ($change in $changes) {
        if (-not $change.WasPresent) {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($change.Target))
            $change.Handle = [Eq001SiteSourceFile]::Open($change.Target,$true)
        }
        # Track before the first live mutation, including a write that fails partway.
        $applied.Add($change)
        if ($change.Remove) { $change.Handle.DeleteOnClose($true) }
        else {
            $change.Handle.WriteAll($change.AfterBytes)
        }
    }
} catch {
    $failure = $_
    for ($index=$applied.Count-1; $index -ge 0; $index--) {
        $change=$applied[$index]
        try {
            if ($change.WasPresent) {
                $change.Handle.DeleteOnClose($false)
                $change.Handle.WriteAll($change.BeforeBytes)
            } else { $change.Handle.DeleteOnClose($true) }
        } catch { Write-Warning "Recovery failed for $($change.Path). Original bytes are in $backupPath" }
    }
    throw $failure
} finally {
    foreach ($change in $changes) { if ($change.Handle) { $change.Handle.Dispose() } }
}
Write-Host "Applied $($changes.Count) guarded source changes in the original project."
Write-Host "Rollback source bytes: $backupPath"
Write-Host 'No branch switch, service installation, database operation, certificate trust change or Production activation was performed.'
Write-Host 'Build and site UAT are still required; this is not deployment acceptance.'
