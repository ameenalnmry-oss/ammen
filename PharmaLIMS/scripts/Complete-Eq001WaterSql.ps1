[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit,
    [switch]$Apply
)
# SQL-only completion of the already-built b4 runtime. This never builds/copies binaries.
$ErrorActionPreference = 'Stop'
$recoveryCommit = $SourceCommit
$performSql = $Apply.IsPresent
$installedCommit = 'b4b3b0c3638628cac53d0af5a7f4455984dca724'
$originalProject = 'E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS'
function Read-CompletionSource([string]$Revision,[string]$Path) {
    if ($Revision -notmatch '^[a-fA-F0-9]{40}$' -or $Path -notmatch '^PharmaLIMS/[A-Za-z0-9_./-]+$' -or $Path.Contains('..')) { throw 'Unsafe source identity.' }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command git).Source
    $info.WorkingDirectory = $originalProject
    $info.Arguments = 'show "{0}:{1}"' -f $Revision,$Path
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false,$true)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw ('Cannot retrieve immutable completion source: '+$errors.GetAwaiter().GetResult()) }
        return $output.GetAwaiter().GetResult()
    }
    finally { $process.Dispose() }
}
function New-WaterCompletionBatch([string]$Stage) {
    if (-not $Stage.Contains('DECLARE @VerifiedIsolatedUat bit = 0;')) { throw 'Explicit UAT staging opt-in is missing.' }
    $tables = @('LIMS_NumberSequences','Samples','SampleTests','ElectronicSignatures','AuditTrail','Water_Plans','Water_PlanSamples','Water_PlanSampleTests','Water_PlanSampleAttempts')
    $orders = @('Id','SampleID','SampleTestID','SignatureID','AuditID','WaterPlanID','WaterPlanSampleID','WaterPlanSampleID,TestID','AttemptID')
    $capture = "SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000; BEGIN TRANSACTION;`r`n"
    $verify = "`r`nRAISERROR(N'PHASE: Water DDL completed; checking preserved rows.',0,1) WITH NOWAIT;`r`n"
    for ($i=0; $i -lt $tables.Count; $i++) {
        $table = $tables[$i]
        $capture += "RAISERROR(N'PHASE: Snapshot $table',0,1) WITH NOWAIT;`r`n"
        $capture += "DECLARE @Before$i binary(32)=HASHBYTES('SHA2_256',(SELECT * FROM dbo.[$table] WITH(HOLDLOCK) ORDER BY $($orders[$i]) FOR JSON PATH,INCLUDE_NULL_VALUES));`r`n"
        $verify += "IF @Before$i<>HASHBYTES('SHA2_256',(SELECT * FROM dbo.[$table] WITH(HOLDLOCK) ORDER BY $($orders[$i]) FOR JSON PATH,INCLUDE_NULL_VALUES)) THROW 56917,'Historical Water rows changed during staging.',1;`r`n"
    }
    $capture += "RAISERROR(N'PHASE: Preserved-row snapshots completed; staging Water boundary.',0,1) WITH NOWAIT;`r`n"
    $verify += "COMMIT; PRINT N'PASS: Water SQL boundary staged; existing business rows preserved.';`r`n"
    return $capture+$Stage.Replace('DECLARE @VerifiedIsolatedUat bit = 0;','DECLARE @VerifiedIsolatedUat bit = 1;')+$verify
}
function Invoke-CompletionSql([string]$InputFile,[string]$LogFile) {
    if ($InputFile.Contains('"') -or $LogFile.Contains('"')) { throw 'Unsafe SQL evidence path.' }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command sqlcmd.exe).Source
    $info.Arguments = '-S "lpc:.\SQLEXPRESS" -E -d "EQ001_UAT_AMEEN_20261024" -I -b -l 10 -t 60 -w 240 -f 65001 -i "{0}"' -f $InputFile
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit(90000)
        if (-not $finished) { $process.Kill(); $process.WaitForExit() }
        $text = $output.GetAwaiter().GetResult()+"`r`n"+$errors.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($LogFile,$text,[Text.UTF8Encoding]::new($false))
        Write-Host $text
        if (-not $finished) { throw "SQL completion process timed out; service remains stopped. Log: $LogFile" }
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}
# Execute only immutable installed deployment preflight WITHOUT -Apply. This verifies
# source bytes, private ACL, exact isolated target, TLS, pin, PFX and service identity.
$preflight = Read-CompletionSource $installedCommit 'PharmaLIMS/scripts/Deploy-Eq001WaterRegistration.ps1'
. ([scriptblock]::Create($preflight)) -SourceCommit $installedCommit
# Import only the pure corrected proof function from the reviewed recovery revision.
$reviewedDeploy = Read-CompletionSource $recoveryCommit 'PharmaLIMS/scripts/Deploy-Eq001WaterRegistration.ps1'
$tokens=$null; $parseErrors=$null
$tree=[Management.Automation.Language.Parser]::ParseInput($reviewedDeploy,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors) { throw 'Reviewed proof function parse failed.' }
$proof=@($tree.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-WaterStageResult'},$false))
if ($proof.Count -ne 1) { throw 'Reviewed committed SQL proof guard is missing.' }
. ([scriptblock]::Create($proof[0].Extent.Text))
if ($hostConfig.EnableWaterRegistrationUat -ne $true) { throw 'This completion is only for the already-enabled b4 Water runtime.' }
$originalRollback=Join-Path $privateRoot 'water-registration-rollback-142c0b87-417e-45fd-832b-081538e0841e'
Require-PrivateDirectory $originalRollback
foreach ($name in @('desktop','service')) {
    if (-not (Test-Path -LiteralPath (Join-Path $originalRollback $name) -PathType Container)) { throw 'Original protected runtime rollback is missing.' }
}
# Prove the already-installed host runtime still matches the original build output.
$buildOutput=Join-Path $project 'tools\PharmaLIMS.TrustedSigning.Host\bin\Release\net8.0-windows'
$verified=0
foreach ($file in @(Get-ChildItem -LiteralPath $buildOutput -File -Recurse -Force)) {
    if ($file.Name -in @('appsettings.json','hostsettings.json') -or $file.Extension -in @('.pfx','.key','.cer','.crt')) { continue }
    $relative=$file.FullName.Substring($buildOutput.Length).TrimStart('\')
    $target=Join-Path $serviceRuntime $relative
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
        (Get-Hash ([IO.File]::ReadAllBytes($target))) -ne (Get-Hash ([IO.File]::ReadAllBytes($file.FullName)))) { throw "Installed runtime drift: $relative" }
    $verified++
}
if ($verified -ne 184) { throw "Expected the previously verified 184 runtime files, found $verified." }
if (Get-Process PharmaLIMS -ErrorAction SilentlyContinue) { throw 'Close PharmaLIMS before SQL completion.' }
if ((Get-Service $serviceName).Status -ne 'Stopped') { throw 'Keep the signing service stopped before SQL completion.' }
$stage=Read-CompletionSource $installedCommit 'PharmaLIMS/scripts/EQ001_UAT_Water_Registration.sql'
$batch=New-WaterCompletionBatch $stage
Write-Host 'PASS: Installed b4 source/runtime, private rollback and exact UAT settings verified. SQL-only completion prepared.'
if (-not $performSql) { Write-Host 'Read-only check completed; no build, runtime copy, SQL or service changes.'; return }
$evidence=Join-Path $privateRoot ('water-sql-completion-'+[guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($evidence)
Require-PrivateDirectory $evidence
$sqlFile=Join-Path $evidence 'stage.sql'
$sqlLog=Join-Path $evidence 'stage.txt'
[IO.File]::WriteAllText($sqlFile,$batch,[Text.UTF8Encoding]::new($false))
try {
    $code=Invoke-CompletionSql $sqlFile $sqlLog
    Assert-WaterStageResult -ExitCode $code -LogText ([IO.File]::ReadAllText($sqlLog))
    $verification=@'
SET NOCOUNT ON;
IF DB_NAME()<>N'EQ001_UAT_AMEEN_20261024' OR @@SERVERNAME<>N'AMEEN\SQLEXPRESS'
 THROW 56918,'Unexpected verification database.',1;
IF OBJECT_ID(N'dbo.EQ001_UAT_WaterRegistration',N'P') IS NULL
 OR OBJECT_ID(N'dbo.EQ001_WaterRegistrationReceipts',N'U') IS NULL
 OR OBJECT_ID(N'dbo.EQ001_WaterLabelReceipts',N'U') IS NULL
 OR (SELECT COUNT(*) FROM sys.triggers WHERE is_disabled=0 AND
  ((name=N'TRG_EQ001_WaterRegistrationReceipts_AppendOnly' AND parent_id=OBJECT_ID(N'dbo.EQ001_WaterRegistrationReceipts')) OR
   (name=N'TRG_EQ001_WaterLabelReceipts_AppendOnly' AND parent_id=OBJECT_ID(N'dbo.EQ001_WaterLabelReceipts'))))<>2
 OR NOT EXISTS(SELECT 1 FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner')
  AND class=1 AND major_id=OBJECT_ID(N'dbo.EQ001_UAT_WaterRegistration') AND permission_name=N'EXECUTE' AND state=N'G')
 THROW 56918,'Water objects, append-only controls or exact service execute permission missing.',1;
PRINT N'PASS: Independent Water SQL metadata verification.';
'@
    $verifyFile=Join-Path $evidence 'verify.sql'
    $verifyLog=Join-Path $evidence 'verify.txt'
    [IO.File]::WriteAllText($verifyFile,$verification,[Text.UTF8Encoding]::new($false))
    $verifyCode=Invoke-CompletionSql $verifyFile $verifyLog
    $verificationText=[IO.File]::ReadAllText($verifyLog)
    if ($verifyCode -ne 0 -or 'PASS: Independent Water SQL metadata verification.' -cnotin @($verificationText -split '\r?\n' | ForEach-Object { $_.Trim() }) -or
        $verificationText -match '(?im)timeout expired|^\s*(Msg\s+\d+|Sqlcmd:\s*Error)') { throw 'Independent Water SQL metadata proof failed.' }
    if ((Get-Hash ([IO.File]::ReadAllBytes($desktopConfigFile))) -ne (Get-Hash $desktopBytes) -or
        (Get-Hash ([IO.File]::ReadAllBytes($hostConfigFile))) -ne (Get-Hash $hostBytes) -or
        (Get-Hash ([IO.File]::ReadAllBytes($pfxFile))) -ne $pfxHash) { throw 'Protected configuration/PFX bytes changed.' }
    Start-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    $ready=$false
    for ($i=0; $i -lt 25; $i++) {
        if ('\\.\pipe\PharmaLIMS_EQ001_AMEEN_UAT' -in @([IO.Directory]::GetFiles('\\.\pipe\'))) { $ready=$true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Expected UAT signing pipe did not appear.' }
    Write-Host "PASS: SQL commit and independent metadata verified; UAT service running. Evidence: $evidence"
    Write-Host 'No build, runtime copy, configuration or certificate changes. Actual standard-user Water registration remains required.'
}
catch {
    $failure=$_
    try { Stop-Service $serviceName -ErrorAction Stop } catch { Write-Warning 'Could not stop signing service; keep desktop closed.' }
    Write-Host "SQL completion stopped. Preserve evidence: $evidence"
    throw $failure
}
