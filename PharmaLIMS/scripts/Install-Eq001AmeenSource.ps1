[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit,
    [switch]$Apply,
    [switch]$DesktopSessionOnly,
    [switch]$WaterRegistrationOnly,
    [switch]$WaterAnalysisOnly,
    [switch]$CurrentSiteOnly
)

$ErrorActionPreference = 'Stop'
if ($CurrentSiteOnly -and [Environment]::MachineName -ine 'AMEEN') { throw 'Current-site updates run only on AMEEN.' }
$currentSitePlanHash='da7ee6ca2f2a145284306aafaf7d73e063a601be47ef0918304f10d250d04830'
$parentReceiptHash='E71C9820185DACF70A988FD30D2F2AF6814ECBBAE9F945D6576F84FF450F81A2'
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

function Test-CurrentSiteInputInventory([string]$ProjectPath,$KnownPaths) {
    $known=@{}
    foreach($path in $KnownPaths){
        if($known.ContainsKey([string]$path)){throw 'Duplicate inventory path.'}
        $null=Get-CurrentSitePath $ProjectPath $path;$known[[string]$path]=$true
    }
    # Walk one level at a time; reject links before recursion can follow them.
    $pending=New-Object 'System.Collections.Generic.Queue[string]';$pending.Enqueue([IO.Path]::GetFullPath($ProjectPath))
    $root=[IO.Path]::GetFullPath($ProjectPath).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath ($pending.Dequeue()) -Force) {
            $relative=$item.FullName.Substring($root.Length).Replace('\','/')
            if ($relative -match '(^|/)(bin|obj|\.git|\.vs)(/|$)' -or $relative -match '^tests(/|$)' -or
                ($relative -match '^tools/' -and $relative -notmatch '^tools/PharmaLIMS\.TrustedSigning(?:\.Host)?(/|$)')) { continue }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unreviewed reparse path: $relative" }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName);continue }
            if (($item.Extension -in @('.cs','.xaml','.csproj','.props','.targets','.sql') -or
                $item.Name -in @('global.json','NuGet.Config','nuget.config','MigrationManifest.json')) -and -not $known.ContainsKey($relative)) { throw "Unlisted source/database input: $relative" }
        }
    }
}

function Get-CurrentSitePath([string]$ProjectPath,[string]$RelativePath) {
    if ($RelativePath -notmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or
        $RelativePath -match '(^|/)\.{1,2}(/|$)') { throw 'Unsafe current-site path.' }
    $root=[IO.Path]::GetFullPath($ProjectPath).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $target=[IO.Path]::GetFullPath((Join-Path $ProjectPath $RelativePath))
    if (-not $target.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes current project.' }
    $ancestor=$target
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Current-site paths cannot contain reparse points.' }
        }
        $ancestor=[IO.Path]::GetDirectoryName($ancestor)
    }
    return $target
}

function Test-CurrentSitePreservedInputs($Inputs,[string]$ProjectPath,$KnownPaths=$null) {
    if($null -ne $KnownPaths){Test-CurrentSiteInputInventory $ProjectPath $KnownPaths}
    foreach ($entry in $Inputs) {
        $target=Get-CurrentSitePath $ProjectPath $entry.Path
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
            (Get-ByteHash ([IO.File]::ReadAllBytes($target))) -cne $entry.ExpectedRawSha256) {
            throw "Current site bytes differ or are missing: $($entry.Path). No unreviewed source is accepted."
        }
    }
}

function Get-CurrentSiteSourceManifest($Plan,[string]$ProjectPath,[scriptblock]$ReadCandidate) {
    if ($Plan.InputCount -ne 370 -or @($Plan.Inputs).Count -ne 370 -or
        $Plan.OriginalBuildInputCount -ne 231 -or $Plan.CurrentSiteBuildInputCount -ne 243 -or
        $Plan.MigrationCount -ne 103 -or $Plan.Version -cne '2026.10.19.306' -or
        $Plan.ArchiveSha256 -cne 'b9244b38b1d4cb72dbd0bacf913fb7e92e07802626057e5169a5c58d71b6309a' -or
        @($Plan.AdditionalReviewInputs).Count -ne 10) { throw 'Unexpected current-site qualification identity.' }
    $updates=@('CultureMediaPreparation.xaml.Part2.cs','CultureMediaPreparation.xaml.Part3.cs','CultureMediaPreparation.xaml.cs',
        'DatabaseHelper.EnvironmentalMonitoring.cs','DatabaseHelper.QualityEvents.cs','EMResultsEntry.xaml.Part3.cs','EMResultsEntry.xaml.cs',
        'PRMQualityEventInvestigation.xaml.cs','ProductionRawMaterialResults.xaml.cs','QualityEventInvestigation.xaml.Part2.cs',
        'QualityEventInvestigation.xaml.cs','ReportCertificate.xaml.cs','SystemPreflight.xaml.cs','DatabaseHelper.EmSourcePlan.cs',
        'tools/PharmaLIMS.TrustedSigning/TrustedCallerMap.cs','tools/PharmaLIMS.TrustedSigning/WindowsTrustedPipeServer.cs')
    $adds=@('Services/MediaPreparationAmendmentSnapshot.cs','Infrastructure/QualityEventInvestigationConcurrencyGuard.cs',
        'tools/PharmaLIMS.TrustedSigning/TrustedPipeConnectionPool.cs')
    $allowed=@($updates+$adds)
    if (@($Plan.SourceUpdateScope).Count -ne 19 -or @(Compare-Object $allowed @($Plan.SourceUpdateScope.Path)).Count) { throw 'Current-site scope must match the nineteen reviewed source paths.' }
    $known=@{};$source=@{};$scope=@{};$retained=New-Object 'System.Collections.Generic.List[object]';$files=New-Object 'System.Collections.Generic.List[object]'
    foreach ($entry in $Plan.SourceUpdateScope) {
        if ($scope.ContainsKey([string]$entry.Path)) { throw 'Duplicate current-site update path.' }
        $scope[[string]$entry.Path]=$entry
    }
    foreach ($entry in $Plan.Inputs) {
        $path=[string]$entry.Path
        if ($known.ContainsKey($path)) { throw 'Duplicate current-site input path.' }
        $target=Get-CurrentSitePath $ProjectPath $path;$known[$path]=$true;$source[$path]=$entry
        $absent=[bool]$entry.CurrentSiteExpectedAbsent
        if ($absent -ne ($path -in $adds)) { throw 'Only the three new helpers can be absent.' }
        if ($absent) {
            if ((Test-Path -LiteralPath $target) -or $entry.CurrentSiteRawSha256) { throw "New helper already exists: $path" }
        } else {
            if ([string]$entry.CurrentSiteRawSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Missing current raw-byte witness.' }
            $witness=[pscustomobject]@{Path=$path;ExpectedRawSha256=$entry.CurrentSiteRawSha256}
            Test-CurrentSitePreservedInputs @($witness) $ProjectPath
            if (-not $scope.ContainsKey($path)) { $retained.Add($witness) }
        }
    }
    foreach ($entry in $Plan.AdditionalReviewInputs) {
        $path=[string]$entry.Path
        if ($known.ContainsKey($path) -or $path -notmatch '^scripts/[A-Za-z0-9_.-]+\.sql$' -or
            $entry.Execute -ne $false -or [string]$entry.ExactSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid read-only SQL sidecar.' }
        $null=Get-CurrentSitePath $ProjectPath $path;$known[$path]=$true
        $witness=[pscustomobject]@{Path=$path;ExpectedRawSha256=$entry.ExactSha256}
        Test-CurrentSitePreservedInputs @($witness) $ProjectPath;$retained.Add($witness)
    }
    Test-CurrentSiteInputInventory $ProjectPath @($known.Keys)
    foreach ($path in $allowed) {
        if (-not $source.ContainsKey($path)) { throw 'Reviewed update is absent from input graph.' }
        $entry=$scope[$path];$inputEntry=$source[$path];$add=$path -in $adds
        if ($entry.Operation -cne $(if ($add) {'add'} else {'update'}) -or
            [string]$entry.ExpectedCurrentRawSha256 -cne [string]$inputEntry.CurrentSiteRawSha256 -or
            $entry.CandidateNormalizedSha256 -cne $inputEntry.Sha256 -or
            $entry.Artifact -notmatch '^scripts/site-overlays/[A-Za-z0-9_.-]+\.txt$') { throw 'Current-site candidate contract mismatch.' }
        [byte[]]$candidate=& $ReadCandidate $entry.Artifact
        if ((Get-ByteHash $candidate) -cne $entry.CandidateRawSha256 -or
            (Get-SourceHash $candidate) -cne $entry.CandidateNormalizedSha256) { throw 'Reviewed candidate bytes differ.' }
        $files.Add([pscustomobject]@{Path=$path;SourcePath=('PharmaLIMS/'+$entry.Artifact);Remove=$false;
            ExpectedRawSha256=$inputEntry.CurrentSiteRawSha256;ExpectedNormalizedSha256=$inputEntry.CurrentSiteNormalizedSha256})
    }
    return [pscustomobject]@{OriginalProject=$ProjectPath;Files=$files.ToArray();PreservedInputs=$retained.ToArray();KnownPaths=@($known.Keys)}
}

function Assert-CurrentSiteRollbackAcl($Acl,[switch]$AllowSiteDirectoryTraversal) {
    $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-21-3887410152-3473494482-1737934645-1009')
    $readers=@('S-1-5-21-3887410152-3473494482-1737934645-1012',
        'S-1-5-21-3887410152-3473494482-1737934645-1013','S-1-5-21-3887410152-3473494482-1737934645-1014')
    if($Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Source rollback ownership is not protected.'}
    $raw=[Security.AccessControl.RawSecurityDescriptor]::new([byte[]]$Acl.GetSecurityDescriptorBinaryForm(),0)
    if($null -eq $raw.DiscretionaryAcl){throw 'Source rollback ACL cannot allow unrestricted access.'}
    if($AllowSiteDirectoryTraversal -and $Acl -isnot [Security.AccessControl.DirectorySecurity]){throw 'Traversal exception requires a directory ACL.'}
    $read=[int][Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [int][Security.AccessControl.FileSystemRights]::Synchronize
    foreach($rule in $Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $rule.IdentityReference.Value -in $trusted){continue}
        # These exact site actors can list/traverse only this directory. They
        # cannot write it or pass their read access to any rollback file.
        if($AllowSiteDirectoryTraversal -and $rule.IdentityReference.Value -in $readers -and
            [int]$rule.FileSystemRights -eq $read -and -not $rule.IsInherited -and
            $rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::None -and
            $rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None){continue}
        throw ('Source rollback ACL grants unexpected access: '+$rule.IdentityReference.Value)
    }
}

function Invoke-GuardedSourceChanges($Changes,[string]$BackupDirectory,[string]$Commit,
    [switch]$WaterAnalysisRecord,[switch]$CurrentSiteRecord,[scriptblock]$ValidatePreserved={},[scriptblock]$ValidateRollbackAcl={}) {
    $SourceCommit=$Commit
    $backupPath=$null;$applied=New-Object 'System.Collections.Generic.List[object]'
    try {
        foreach ($change in $Changes) {
            if ($change.WasPresent) {
                $change.Handle=if($CurrentSiteRecord){[Eq001SiteSourceFile]::OpenQualified($change.Target,$false)}else{[Eq001SiteSourceFile]::Open($change.Target,$false)}
                if ((Get-ByteHash ($change.Handle.ReadAll())) -cne $change.BeforeSha256) { throw "Source changed during preparation: $($change.Path)" }
            } elseif (Test-Path -LiteralPath $change.Target) { throw "New source target appeared: $($change.Path)" }
        }
        & $ValidatePreserved
        $pattern=if($CurrentSiteRecord){'source-current-site-rollback-{0}.json'}elseif($WaterAnalysisRecord){'source-water-analysis-rollback-{0}.json'}else{'source-rollback-{0}.json'}
        $backupPath=Join-Path $BackupDirectory ($pattern -f [guid]::NewGuid())
        $rollback=@($Changes|ForEach-Object{[pscustomobject]@{Path=$_.Path;WasPresent=$_.WasPresent;Remove=$_.Remove;
            BeforeSha256=$_.BeforeSha256;InstalledSha256=$_.AfterSha256;OriginalBase64=[Convert]::ToBase64String($_.BeforeBytes)}})
        $record=if($CurrentSiteRecord){[pscustomobject]@{Schema=2;SourceCommit=$Commit;SourcePlanSha256='da7ee6ca2f2a145284306aafaf7d73e063a601be47ef0918304f10d250d04830';
            ParentSourceReceiptCommit='97b347290360d8f8f90f6d29c56029af59298b24';
            ParentSourceReceiptSha256='E71C9820185DACF70A988FD30D2F2AF6814ECBBAE9F945D6576F84FF450F81A2';Files=$rollback}}
        elseif($WaterAnalysisRecord){[pscustomobject]@{Schema=1; SourceCommit=$SourceCommit; Files=$rollback}}else{$rollback}
        $bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Depth 6))
        $backup=[IO.FileStream]::new($backupPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{
            & $ValidateRollbackAcl $backupPath
            $backup.Write($bytes,0,$bytes.Length);$backup.Flush($true)
        }finally{$backup.Dispose()}
        Write-Host "Durable rollback source bytes saved before mutation: $backupPath"
        foreach($change in $Changes){
            if(-not $change.WasPresent){
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($change.Target))
                $change.Handle=if($CurrentSiteRecord){[Eq001SiteSourceFile]::OpenQualified($change.Target,$true)}else{[Eq001SiteSourceFile]::Open($change.Target,$true)}
            }
            $applied.Add($change)
            if($change.Remove){$change.Handle.DeleteOnClose($true)}else{
                $change.Handle.WriteAll($change.AfterBytes)
                if((Get-ByteHash ($change.Handle.ReadAll())) -cne $change.AfterSha256){throw "Source readback failed: $($change.Path)"}
            }
        }
        & $ValidatePreserved
    }catch{
        $failure=$_
        for($index=$applied.Count-1;$index -ge 0;$index--){
            $change=$applied[$index]
            try{
                if($change.WasPresent){$change.Handle.DeleteOnClose($false);$change.Handle.WriteAll($change.BeforeBytes)}
                else{$change.Handle.DeleteOnClose($true)}
            }catch{Write-Warning "Recovery failed for $($change.Path). Original bytes are in $backupPath"}
        }
        throw $failure
    }finally{foreach($change in $Changes){if($change.Handle){$change.Handle.Dispose()}}}
    return $backupPath
}

function Get-TargetPath([string]$RelativePath) {
    if ($CurrentSiteOnly) { return Get-CurrentSitePath $project $RelativePath }
    if ($RelativePath -notmatch '^[A-Za-z0-9_./-]+$' -or $RelativePath -match '(^|/)\.\.(/|$)') {
        throw 'Unsafe target path.'
    }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $project $RelativePath))
    if (-not $fullPath.StartsWith($project + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Target escapes original project.'
    }
    if ($WaterAnalysisOnly) {
        $ancestor = $fullPath
        while ($ancestor -and -not (Test-Path -LiteralPath $ancestor)) { $ancestor=[IO.Path]::GetDirectoryName($ancestor) }
        while ($ancestor) {
            $item=Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse points are not allowed in analysis source paths.' }
            $ancestor=[IO.Path]::GetDirectoryName($ancestor)
        }
    }
    return $fullPath
}

if (@($DesktopSessionOnly,$WaterRegistrationOnly,$WaterAnalysisOnly,$CurrentSiteOnly | Where-Object { $_ }).Count -gt 1) { throw 'Choose one source installation mode.' }
if ($CurrentSiteOnly) {
    $planBytes=Get-RepositoryBytes 'PharmaLIMS/scripts/site-cohort-inputs/AMEEN-cohort.json'
    if ((Get-ByteHash $planBytes) -cne $currentSitePlanHash) { throw 'Current-site source qualification hash differs.' }
    $plan=[Text.Encoding]::UTF8.GetString($planBytes) | ConvertFrom-Json
    $manifest=Get-CurrentSiteSourceManifest -Plan $plan -ProjectPath $project -ReadCandidate { param($path) Get-RepositoryBytes ('PharmaLIMS/'+$path) }
} else {
$manifestPath = if ($WaterAnalysisOnly) { 'PharmaLIMS/scripts/Eq001-WaterAnalysisSourcePrerequisites.json' } elseif ($WaterRegistrationOnly) { 'PharmaLIMS/scripts/Eq001-WaterSourcePrerequisites.json' } elseif ($DesktopSessionOnly) { 'PharmaLIMS/scripts/Eq001-SessionSourcePrerequisites.json' } else { 'PharmaLIMS/scripts/Eq001-AmeenSourcePrerequisites.json' }
$manifestBytes = Get-RepositoryBytes $manifestPath
$manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
}
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
if ($WaterRegistrationOnly) {
    $waterPaths = @(
        'NewSampleDialog.xaml.cs'
,        'NewSampleDialog.TrustedWater.cs'
,        'Services/TrustedApplicationSession.cs'
,        'Services/WaterRegistrationRecovery.cs'
,        'tools/PharmaLIMS.TrustedSigning/PharmaLIMS.TrustedSigning.csproj'
,        'tools/PharmaLIMS.TrustedSigning/WaterRegistrationRequest.cs'
,        'tools/PharmaLIMS.TrustedSigning/TrustedHostSettings.cs'
,        'tools/PharmaLIMS.TrustedSigning.Host/Program.cs'
,        'tools/PharmaLIMS.TrustedSigning.Host/TrustedApplicationEndpoint.cs'
,        'tools/PharmaLIMS.TrustedSigning.Host/TrustedWaterRegistrationStore.cs'
,        'scripts/EQ001_UAT_Water_Registration.sql'
,        'scripts/Deploy-Eq001WaterRegistration.ps1'
,        'Services/WaterNumericResultEvaluator.cs'
,        'Services/ControlledNumericValue.cs'
)
    if (@($manifest.Files).Count -ne $waterPaths.Count -or
        @(Compare-Object $waterPaths @($manifest.Files.Path)).Count -ne 0 -or
        @($manifest.Files | Where-Object { $_.Remove }).Count -ne 0) {
        throw 'Water source changes must match the reviewed allowlist.'
    }
}
if ($WaterAnalysisOnly) {
    $analysisPaths = @(
        'ElectronicSignature.xaml.cs',
        'ResultsEntry.xaml',
        'ResultsEntry.xaml.cs',
        'ResultsEntry.xaml.Part2.cs',
        'ResultsEntry.xaml.Part3.cs',
        'ResultsEntry.xaml.Part4.cs',
        'ResultsEntry.xaml.Part5.cs',
        'ResultsEntry.xaml.Part6.cs',
        'Services/LabEquipmentUsageService.cs',
        'Services/WaterEvidencePreflightSql.cs',
        'Services/WaterLesExecutionService.cs',
        'Services/WaterLesTemperatureContract.cs',
        'Services/WaterNumericResultEvaluator.cs',
        'Services/WaterRegistrationRecovery.cs',
        'Services/WaterReportNumericContract.cs',
        'Services/WaterResourceEvidenceService.cs',
        'Services/WaterResultSnapshotSql.cs',
        'Services/WaterResultValueContract.cs',
        'Services/WaterTestResourcePolicy.cs',
        'Services/WaterWorkflowEvidenceGuard.cs',
        'ResultsEntry.TrustedWaterWorkflow.cs',
        'Services/TrustedApplicationSession.cs',
        'Services/WaterWorkflowMutationPlan.cs',
        'Services/WaterWorkflowSnapshot.cs',
        'Services/WaterWorkflowScientific.cs',
        'Services/WaterWorkflowValidator.cs',
        'Services/WaterWorkflowValidator.Evidence.cs',
        'Services/WaterWorkflowRecovery.cs',
        'tools/PharmaLIMS.TrustedSigning/WaterWorkflowRequest.cs',
        'tools/PharmaLIMS.TrustedSigning/WaterWorkflowSqlContract.cs',
        'tools/PharmaLIMS.TrustedSigning/TrustedHostSettings.cs',
        'tools/PharmaLIMS.TrustedSigning.Host/TrustedWaterWorkflowStore.cs',
        'tools/PharmaLIMS.TrustedSigning.Host/TrustedApplicationEndpoint.cs',
        'tools/PharmaLIMS.TrustedSigning.Host/Program.cs',
        'scripts/EQ001_UAT_Water_Analysis.sql',
        'scripts/Deploy-Eq001WaterAnalysis.ps1',
        'ProductionRawMaterialSamples.xaml.cs',
        'ProductionRawMaterialSamples.TrustedRegistration.cs',
        'Services/PrmRegistrationRecovery.cs',
        'tools/PharmaLIMS.TrustedSigning/PrmRegistrationRequest.cs',
        'tools/PharmaLIMS.TrustedSigning.Host/TrustedPrmRegistrationStore.cs',
        'scripts/EQ001_UAT_Prm_Registration.sql'
    )
    if (@($manifest.Files).Count -ne $analysisPaths.Count -or
        @(Compare-Object $analysisPaths @($manifest.Files.Path)).Count -ne 0 -or
        @($manifest.Files | Where-Object { $_.Remove }).Count -ne 0) {
        throw 'Water analysis source changes must match the reviewed allowlist.'
    }
}
$changes = New-Object 'System.Collections.Generic.List[object]'
foreach ($entry in $manifest.Files) {
    $target = Get-TargetPath $entry.Path
    $exists = Test-Path -LiteralPath $target -PathType Leaf
    [byte[]]$before = @()
    [byte[]]$after = @()
    if ($exists) { $before = [IO.File]::ReadAllBytes($target) }
    if ($CurrentSiteOnly -and (([bool]$entry.ExpectedRawSha256 -ne $exists) -or
        ($exists -and (Get-ByteHash $before) -cne $entry.ExpectedRawSha256))) {
        throw "Current source bytes changed during preparation: $($entry.Path)"
    }
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
if ($CurrentSiteOnly) {
    Assert-CurrentSiteRollbackAcl -Acl $acl -AllowSiteDirectoryTraversal
} else {
if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) { throw 'UAT directory ownership is not protected.' }
foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
    if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $allowed) { throw 'UAT directory permissions are not private.' }
}
}
foreach ($process in @(Get-Process -Name PharmaLIMS -ErrorAction SilentlyContinue)) {
    if ($process.Path -and $process.Path.StartsWith($project + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Close the original application before changing its source.' }
}
$service = Get-Service -Name 'PharmaLIMS.TrustedSigning' -ErrorAction SilentlyContinue
if (-not $DesktopSessionOnly -and $service -and $service.Status -ne 'Stopped') { throw 'Do not change source while the signing service is running.' }

$parentReceiptPath=Join-Path $privateDirectory 'source-water-analysis-rollback-a636fc38-f923-4a7f-ad17-c7a905596c16.json'
if ($CurrentSiteOnly) {
    $null=Get-CurrentSitePath $privateDirectory ([IO.Path]::GetFileName($parentReceiptPath))
    if ((Get-ByteHash ([IO.File]::ReadAllBytes($parentReceiptPath))) -ine $parentReceiptHash) { throw 'Original source receipt changed; no source update allowed.' }
}
$nativeSource=Get-RepositoryBytes 'PharmaLIMS/scripts/site-overlays/Eq001ExclusiveSourceFile.cs.txt'
if ($CurrentSiteOnly -and (Get-ByteHash $nativeSource) -cne 'cee748672ba3ed7079290ccf3b40bf9ee9990e8961c5c2798575f7a639cd3a5c') { throw 'Qualified file helper hash differs.' }
if (-not ('Eq001SiteSourceFile' -as [type])) {
    Add-Type -TypeDefinition ([Text.Encoding]::UTF8.GetString($nativeSource)) -ErrorAction Stop
}
if ($CurrentSiteOnly -and -not [Eq001SiteSourceFile].GetMethod('OpenQualified')) { throw 'Open a fresh PowerShell session to load the qualified source helper.' }
$preservedCheck={}
$rollbackAclCheck={}
if ($CurrentSiteOnly) {
    $preservedCheck={
        Assert-CurrentSiteRollbackAcl -Acl (Get-Acl -LiteralPath $privateDirectory) -AllowSiteDirectoryTraversal
        Test-CurrentSitePreservedInputs $manifest.PreservedInputs $project $manifest.KnownPaths
        if ((Get-ByteHash ([IO.File]::ReadAllBytes($parentReceiptPath))) -ine $parentReceiptHash) { throw 'Parent receipt changed during source update.' }
    }
    $rollbackAclCheck={param($path) Assert-CurrentSiteRollbackAcl -Acl (Get-Acl -LiteralPath $path)}
}
$backupPath=Invoke-GuardedSourceChanges -Changes $changes -BackupDirectory $privateDirectory -Commit $SourceCommit `
    -WaterAnalysisRecord:$WaterAnalysisOnly -CurrentSiteRecord:$CurrentSiteOnly -ValidatePreserved $preservedCheck -ValidateRollbackAcl $rollbackAclCheck
Write-Host "Applied $($changes.Count) guarded source changes in the original project."
Write-Host "Rollback source bytes: $backupPath"
Write-Host 'No branch switch, service installation, database operation, certificate trust change or Production activation was performed.'
Write-Host 'Build and site UAT are still required; this is not deployment acceptance.'
