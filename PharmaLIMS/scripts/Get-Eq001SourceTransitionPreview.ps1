[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Get-Eq001Sha256([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Get-Eq001NormalizedSourceHash([byte[]]$Bytes) {
    $text = ([Text.UTF8Encoding]::new($false, $true)).GetString($Bytes).TrimStart([char]0xFEFF)
    $text = $text.Replace(([string][char]13 + [char]10), [string][char]10).TrimEnd([char[]]@([char]13,[char]10)) + [char]10
    return Get-Eq001Sha256 ([Text.Encoding]::UTF8.GetBytes($text))
}

function Get-Eq001RepositoryBytes([string]$Repository, [string]$Commit, [string]$Path) {
    if ($Commit -cnotmatch '^[a-f0-9]{40}$' -or $Path -cnotmatch '^PharmaLIMS/[A-Za-z0-9_./-]+$' -or
        $Path -match '(^|/)\.{1,2}(/|$)|//') { throw 'Unsafe immutable source object identity.' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = (Get-Command git -ErrorAction Stop).Source
    $info.WorkingDirectory = $Repository
    $info.Arguments = 'show "{0}:{1}"' -f $Commit, $Path
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    $buffer = New-Object IO.MemoryStream
    try {
        [void]$process.Start()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($buffer)
        $process.WaitForExit()
        $null = $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Cannot read exact Git object: $Path. Fetch the qualified commit in the original repository first." }
        return ,$buffer.ToArray()
    } finally { $buffer.Dispose(); $process.Dispose() }
}

function Get-Eq001VerifiedCohortPlan([byte[]]$Bytes) {
    if ((Get-Eq001Sha256 $Bytes) -cne 'f8725412e89eb76af5ad0ecb4efdb703172ea533fb8f2d4aedb203eabd9522af') {
        throw 'The immutable1935 source plan does not match its reviewed raw-byte SHA256.'
    }
    return (([Text.UTF8Encoding]::new($false, $true)).GetString($Bytes) | ConvertFrom-Json)
}

function Assert-Eq001NoReparsePoint([string]$Path) {
    $ancestor = [IO.Path]::GetFullPath($Path)
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'A source or receipt path contains a reparse point; no transition is qualified.'
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}

function Get-Eq001SafeSourcePath([string]$Project, [string]$Relative) {
    if ($Relative -cnotmatch '^[A-Za-z0-9_./-]+$' -or $Relative -match '(^|/)\.{1,2}(/|$)|//' -or
        [IO.Path]::IsPathRooted($Relative)) { throw 'Unsafe source metadata path.' }
    $root = [IO.Path]::GetFullPath($Project).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath((Join-Path $Project $Relative))
    if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path escapes the original project.' }
    Assert-Eq001NoReparsePoint $target
    return $target
}

function Get-Eq001SourceTransitionPreview($Plan, [Collections.IDictionary]$Candidates, [string]$Project,
    [string]$ReceiptPath, [string]$ReceiptSha256) {
    if ($Plan.InputCount -ne 357 -or @($Plan.Inputs).Count -ne 357 -or $Plan.OriginalBuildInputCount -ne 231 -or
        $Plan.MigrationCount -ne 102 -or $Plan.Version -cne '2026.10.19.306' -or
        $Plan.ArchiveSha256 -cne 'b9244b38b1d4cb72dbd0bacf913fb7e92e07802626057e5169a5c58d71b6309a') {
        throw 'Unexpected qualified cohort identity or counts.'
    }
    if (-not (Test-Path -LiteralPath $Project -PathType Container)) { throw 'Original source directory is missing.' }
    Assert-Eq001NoReparsePoint $Project
    Assert-Eq001NoReparsePoint $ReceiptPath
    if ($ReceiptSha256 -cnotmatch '^[a-f0-9]{64}$' -or -not (Test-Path -LiteralPath $ReceiptPath -PathType Leaf) -or
        (Get-Eq001Sha256 ([IO.File]::ReadAllBytes($ReceiptPath))) -cne $ReceiptSha256) {
        throw 'Original protected rollback receipt is missing or changed.'
    }
    # This is a new, closed review scope. It never expands an installer mode.
    $approved = @{
        'CultureMediaPreparation.xaml.Part2.cs'='update'; 'CultureMediaPreparation.xaml.Part3.cs'='update'
        'CultureMediaPreparation.xaml.cs'='update'; 'DatabaseHelper.EnvironmentalMonitoring.cs'='update'
        'DatabaseHelper.QualityEvents.cs'='update'; 'EMResultsEntry.xaml.Part3.cs'='update'
        'EMResultsEntry.xaml.cs'='update'; 'PRMQualityEventInvestigation.xaml.cs'='update'
        'ProductionRawMaterialResults.xaml.cs'='update'; 'QualityEventInvestigation.xaml.Part2.cs'='update'
        'QualityEventInvestigation.xaml.cs'='update'; 'ReportCertificate.xaml.cs'='update'
        'SystemPreflight.xaml.cs'='update'; 'DatabaseHelper.EmSourcePlan.cs'='update'
        'tools/PharmaLIMS.TrustedSigning/TrustedCallerMap.cs'='update'
        'tools/PharmaLIMS.TrustedSigning/WindowsTrustedPipeServer.cs'='update'
        'Services/MediaPreparationAmendmentSnapshot.cs'='add'
        'Infrastructure/QualityEventInvestigationConcurrencyGuard.cs'='add'
        'tools/PharmaLIMS.TrustedSigning/TrustedPipeConnectionPool.cs'='add'
    }
    if ($null -eq $Candidates -or $Candidates.Count -ne 19) { throw 'Exactly19 candidate source artifacts are required.' }
    foreach ($key in $Candidates.Keys) { if (-not $approved.ContainsKey([string]$key)) { throw 'An unreviewed candidate was supplied.' } }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $changes = [Collections.Generic.List[object]]::new()
    $originalCount = 0; $databaseCount = 0; $updateCount = 0; $addCount = 0
    foreach ($entry in $Plan.Inputs) {
        $relative = [string]$entry.Path
        $target = Get-Eq001SafeSourcePath $Project $relative
        if (-not $seen.Add($relative)) { throw 'Duplicate case-insensitive source metadata path.' }
        $database = $entry.OriginalStatus -ceq 'IMMUTABLE_SITE_DATABASE_CONTENT'
        $originalHash = if ($database) { [string]$entry.ExactSha256 } else { [string]$entry.OriginalRawSha256 }
        $existing = -not [string]::IsNullOrEmpty($originalHash)
        if ($existing) {
            if ($originalHash -cnotmatch '^[a-f0-9]{64}$' -or -not (Test-Path -LiteralPath $target -PathType Leaf) -or
                (Get-Eq001Sha256 ([IO.File]::ReadAllBytes($target))) -cne $originalHash) {
                throw "Original raw source bytes differ or are missing: $relative. No files changed."
            }
            $originalCount++
        } elseif (Test-Path -LiteralPath $target) { throw "A new helper target already exists: $relative. No files changed." }
        if ($database) { $databaseCount++; continue }
        $changed = -not $existing -or (-not [string]::IsNullOrEmpty([string]$entry.OriginalNormalizedSha256) -and
            [string]$entry.Sha256 -cne [string]$entry.OriginalNormalizedSha256)
        if (-not $changed) { continue }
        if (-not $approved.ContainsKey($relative) -or -not $Candidates.Contains($relative)) { throw "Unreviewed or missing changed source: $relative" }
        $operation = if ($existing) { 'update' } else { 'add' }
        if ($approved[$relative] -cne $operation -or [string]$entry.Sha256 -cnotmatch '^[a-f0-9]{64}$') {
            throw "The candidate operation or hash changed: $relative"
        }
        [byte[]]$candidate = $Candidates[$relative]
        if ((Get-Eq001NormalizedSourceHash $candidate) -cne [string]$entry.Sha256) { throw "Candidate source differs from the qualified build: $relative" }
        if ($existing) { $updateCount++ } else { $addCount++ }
        $changes.Add([pscustomobject]@{
            Path=$relative; Operation=$operation; OriginalRawSha256=$(if ($existing) { $originalHash } else { $null })
            CandidateRawSha256=(Get-Eq001Sha256 $candidate); CandidateNormalizedSha256=[string]$entry.Sha256
        })
    }
    if ($originalCount -ne 354 -or $databaseCount -ne 123 -or $changes.Count -ne 19 -or $updateCount -ne 16 -or $addCount -ne 3) {
        throw 'The preview is not the complete reviewed16-update/3-addition delta.'
    }
    $root = [IO.Path]::GetFullPath($Project).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar
    # Traverse one directory at a time: recursive enumeration can silently skip a
    # junction and falsely report no extra compiler inputs behind that link.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Project))
    while ($pending.Count) {
      foreach ($file in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
        $relative = $file.FullName.Substring($root.Length).Replace('\','/')
        if ($relative -match '(^|/)(bin|obj|\.git|\.vs)(/|$)' -or $relative -match '^tests(?:/|$)' -or
            ($relative -match '^tools/' -and $relative -notmatch '^tools/PharmaLIMS\.TrustedSigning(?:\.Host)?(?:/|$)')) { continue }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unlisted reparse path: $relative. No transition is qualified." }
        if ($file.PSIsContainer) { $pending.Push($file.FullName); continue }
        if (($file.Extension -in @('.cs','.xaml','.csproj','.props','.targets','.rsp') -or
            $file.Name -in @('global.json','NuGet.Config','nuget.config')) -and -not $seen.Contains($relative)) {
            throw "Unlisted build input: $relative. No transition is qualified."
        }
      }
    }
    if ((Get-Eq001Sha256 ([IO.File]::ReadAllBytes($ReceiptPath))) -cne $ReceiptSha256) { throw 'Rollback receipt changed during preview.' }
    return [pscustomobject]@{
        QualifiedSourceCommit='1935ec8ae4a52c3a0cd3cb63d08e2bd7952ed39f'; OriginalFilesVerified=$originalCount
        SourceChangeCount=$changes.Count; Updates=$updateCount; Additions=$addCount; UnchangedOriginalInputs=338
        CandidateArtifactsVerified=$changes.Count; OriginalReceiptUnchanged=$true; PreviewOnly=$true
        CandidateInstalled=$false; LocalUatPassed=$false; Changes=$changes.ToArray()
        Scope='Read-only point-in-time source transition preview. Not a transaction, installer, build, independent signoff or site UAT acceptance.'
    }
}

if ($env:COMPUTERNAME -ine 'AMEEN') { throw 'Run this preview only on the original AMEEN machine.' }
$project = 'E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS'
if (-not (Test-Path -LiteralPath $project -PathType Container)) { throw 'The original E: project is unavailable.' }
$commit = '1935ec8ae4a52c3a0cd3cb63d08e2bd7952ed39f'
$plan = Get-Eq001VerifiedCohortPlan (Get-Eq001RepositoryBytes $project $commit 'PharmaLIMS/scripts/site-cohort-inputs/AMEEN-cohort.json')
$candidates = @{}
foreach ($entry in $plan.Inputs) {
    if ($entry.OriginalStatus -ceq 'IMMUTABLE_SITE_DATABASE_CONTENT') { continue }
    if ([string]::IsNullOrEmpty([string]$entry.OriginalRawSha256) -or
        (-not [string]::IsNullOrEmpty([string]$entry.OriginalNormalizedSha256) -and [string]$entry.Sha256 -cne [string]$entry.OriginalNormalizedSha256)) {
        $candidates[[string]$entry.Path] = Get-Eq001RepositoryBytes $project $commit ('PharmaLIMS/' + $entry.Artifact)
    }
}
$parameters = @{
    Plan=$plan; Candidates=$candidates; Project=$project
    ReceiptPath='C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\source-water-analysis-rollback-a636fc38-f923-4a7f-ad17-c7a905596c16.json'
    ReceiptSha256='e71c9820185dacf70a988fd30d2f2af6814ecbbae9f945d6576f84ff450f81a2'
}
$preview = Get-Eq001SourceTransitionPreview @parameters
Write-Output ('Computer: ' + $env:COMPUTERNAME)
$preview | Select-Object -Property * -ExcludeProperty Changes | Format-List
$preview.Changes | Format-Table Path,Operation -AutoSize
