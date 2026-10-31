[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit,
    [switch]$Apply,
    [switch]$IncludePrmRegistration
)
$ErrorActionPreference = 'Stop'
$project = 'E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS'
$privateRoot = 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024'
$desktop = Join-Path $project 'bin\Release\net8.0-windows7.0'
$serviceRuntime = Join-Path $privateRoot 'service'
$hostConfigFile = Join-Path $serviceRuntime 'hostsettings.json'
$desktopConfigFile = Join-Path $desktop 'appsettings.json'
$serviceName = 'PharmaLIMS.TrustedSigning'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($identity.Name -ne 'AMEEN\Elite' -or
    -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Use Administrator PowerShell under AMEEN\Elite.'
}
function Get-Hash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','') }
    finally { $sha.Dispose() }
}
function Get-NormalizedHash([byte[]]$Bytes) {
    $value = [Text.Encoding]::UTF8.GetString($Bytes).TrimStart([char]0xFEFF).Replace("`r`n","`n").TrimEnd([char[]]"`r`n") + "`n"
    Get-Hash ([Text.Encoding]::UTF8.GetBytes($value))
}
function Read-GitBytes([string]$Path,[string]$Revision=$SourceCommit) {
    if ($Revision -notmatch '^[a-fA-F0-9]{40}$') { throw 'Build source revision must be an immutable full commit.' }
    if (-not $script:verifiedRevisions) { $script:verifiedRevisions=@{} }
    if (-not $script:verifiedRevisions.ContainsKey($Revision)) {
        $verified=git -C $project rev-parse --verify "$Revision^{commit}"
        if ($LASTEXITCODE -ne 0 -or $verified -cne $Revision) { throw 'Exact reviewed build-input commit is unavailable.' }
        $script:verifiedRevisions[$Revision]=$true
    }
    if ($Path -notmatch '^PharmaLIMS/[A-Za-z0-9_./-]+$' -or $Path -match '(^|/)\.\.(/|$)') { throw 'Unsafe repository path.' }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command git).Source
    $info.WorkingDirectory = $project
    $info.Arguments = 'show "{0}:{1}"' -f $Revision,$Path
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $buffer = [IO.MemoryStream]::new()
    try {
        [void]$process.Start()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($buffer)
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw ('Exact Git source retrieval failed: ' + $errors.GetAwaiter().GetResult()) }
        return ,$buffer.ToArray()
    }
    finally { $buffer.Dispose(); $process.Dispose() }
}
function Require-PrivateDirectory([string]$Path) {
    $allowed = @('S-1-5-18','S-1-5-32-544','S-1-5-21-3887410152-3473494482-1737934645-1009')
    $acl = Get-Acl -LiteralPath $Path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) { throw 'Unexpected private directory owner.' }
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $allowed) { throw 'UAT directory is not private.' }
    }
}
function Get-DbOption($Builder,[string[]]$Names) {
    foreach ($name in $Names) { if ($Builder.ContainsKey($name)) { return [string]$Builder[$name] } }
    return ''
}
Require-PrivateDirectory $privateRoot
Require-PrivateDirectory $serviceRuntime
$resolved = git -C $project rev-parse --verify "$SourceCommit^{commit}"
if ($LASTEXITCODE -ne 0 -or $resolved -ne $SourceCommit) { throw 'Fetch the exact reviewed source commit first.' }
$manifest = [Text.Encoding]::UTF8.GetString((Read-GitBytes 'PharmaLIMS/scripts/Eq001-WaterAnalysisSourcePrerequisites.json')) | ConvertFrom-Json
if ($manifest.OriginalProject -ne $project) { throw 'Unexpected source manifest project.' }
foreach ($entry in $manifest.Files) {
    if ($entry.Path -notmatch '^[A-Za-z0-9_./-]+$' -or $entry.Path -match '(^|/)\.\.(/|$)') { throw 'Unsafe source target.' }
    $target = Join-Path $project $entry.Path
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
        (Get-NormalizedHash ([IO.File]::ReadAllBytes($target))) -ne (Get-NormalizedHash (Read-GitBytes $entry.SourcePath))) {
        throw "Installed source does not match the reviewed Water analysis commit: $($entry.Path)"
    }
}
$desktopBytes = [IO.File]::ReadAllBytes($desktopConfigFile)
$settings = [Text.Encoding]::UTF8.GetString($desktopBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
if ($settings.Database.Server -ne 'tcp:AMEEN,1433' -or $settings.Database.Database -ne 'EQ001_UAT_AMEEN_20261024' -or
    $settings.Database.IntegratedSecurity -ne $true -or $settings.Database.Encrypt -ne $true -or
    $settings.Database.TrustServerCertificate -ne $false -or $settings.TrustedSigning.Enabled -ne $true -or
    $settings.TrustedSigning.PipeName -ne 'PharmaLIMS_EQ001_AMEEN_UAT' -or
    $settings.TrustedSigning.ServerCertificateSha256 -ne 'C891077332E7F06F1E2570A8A8093ADDD09D7083FC1DD36897D4E0BB887D8EE4' -or
    $settings.Runtime.ApplyStartupDatabaseUpdates -ne $false) { throw 'Unexpected desktop UAT settings.' }
$hostBytes = [IO.File]::ReadAllBytes($hostConfigFile)
$hostConfig = [Text.Encoding]::UTF8.GetString($hostBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
$dbOptions = [System.Data.Common.DbConnectionStringBuilder]::new()
$dbOptions.set_ConnectionString($hostConfig.ConnectionString)
if ($hostConfig.DeploymentMode -ne 'UAT' -or $hostConfig.ProductionActivationApproved -ne $false -or
    $hostConfig.PipeName -ne 'PharmaLIMS_EQ001_AMEEN_UAT' -or
    (Get-DbOption $dbOptions @('Server','Data Source')) -ne 'tcp:AMEEN,1433' -or
    (Get-DbOption $dbOptions @('Database','Initial Catalog')) -ne 'EQ001_UAT_AMEEN_20261024' -or
    (Get-DbOption $dbOptions @('Integrated Security','Trusted_Connection')) -notin @('True','SSPI') -or
    (Get-DbOption $dbOptions @('Encrypt')) -notin @('True','Mandatory','Strict') -or
    (Get-DbOption $dbOptions @('TrustServerCertificate','Trust Server Certificate')) -ne 'False') { throw 'Unexpected private service target or TLS settings.' }
$pfxFile = $hostConfig.CertificatePath
$pfxHash = Get-Hash ([IO.File]::ReadAllBytes($pfxFile))
$service = Get-CimInstance Win32_Service -Filter "Name='PharmaLIMS.TrustedSigning'"
if ($null -eq $service -or $service.StartName -ne '.\PharmaLIMSSigner' -or
    $service.PathName -ne ('"' + (Join-Path $serviceRuntime 'PharmaLIMS.TrustedSigning.Host.exe') + '" --service --config "' + $hostConfigFile + '"')) {
    throw 'Unexpected signing service identity or executable configuration.'
}

# A deployment contains only these qualified project outputs. Dependencies must already
# match the one clean build; an unexpected dependency change stops before SQL/copy.
$runtimeNames = @('PharmaLIMS.dll','PharmaLIMS.exe','PharmaLIMS.deps.json','PharmaLIMS.runtimeconfig.json',
 'PharmaLIMS.TrustedSigning.dll','PharmaLIMS.TrustedSigning.Host.dll','PharmaLIMS.TrustedSigning.Host.exe',
 'PharmaLIMS.TrustedSigning.Host.deps.json','PharmaLIMS.TrustedSigning.Host.runtimeconfig.json')
function Assert-NoReparsePoint([string]$Path) {
    $item=Get-Item -LiteralPath $Path -Force
    while ($item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse points are not allowed in deployment paths.' }
        $item=if ($item.PSIsContainer) { $item.Parent } else { $item.Directory }
    }
}
function Write-DurableBytes([string]$Path,[byte[]]$Bytes) {
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($Bytes,0,$Bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}
function Backup-Tree([string]$Source,[string]$Target) {
    [void][IO.Directory]::CreateDirectory($Target)
    $inventory=@()
    foreach ($file in @(Get-ChildItem -LiteralPath $Source -Recurse -Force)) {
        Assert-NoReparsePoint $file.FullName
        if ($file.PSIsContainer) { continue }
        $relative=$file.FullName.Substring($Source.Length).TrimStart('\')
        $copy=Join-Path $Target $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy))
        $bytes=[IO.File]::ReadAllBytes($file.FullName)
        Write-DurableBytes $copy $bytes
        $hash=Get-Hash $bytes
        if ((Get-Hash ([IO.File]::ReadAllBytes($copy))) -ne $hash) { throw 'Durable runtime rollback verification failed.' }
        $inventory += [pscustomobject]@{ Path=$relative; Sha256=$hash }
    }
    Write-DurableBytes ($Target+'.inventory.json') ([Text.Encoding]::UTF8.GetBytes(($inventory|ConvertTo-Json -Depth 5)))
}
function Get-WaterWorkflowStage([string]$Template,[string]$ContractSource) {
    $marker='/*__WATER_WORKFLOW_SNAPSHOT_SQL__*/'
    if (($Template.Split(@($marker),[StringSplitOptions]::None)).Count -ne 2) { throw 'Exactly one Water snapshot marker is required.' }
    # The reviewed C# raw literal is deliberately unindented: no C# escapes or
    # source execution. Reject a changed declaration shape rather than guessing.
    $matches=[regex]::Matches($ContractSource,'(?s)public const string SnapshotQuery = """\r?\n(.*?)\r?\n""";')
    if ($matches.Count -ne 1) { throw 'Canonical Water SnapshotQuery raw literal could not be extracted.' }
    $query=$matches[0].Groups[1].Value
    $ownerQuery=$query.Replace('{LOCK}','WITH (UPDLOCK,HOLDLOCK)').Replace("'","''")
    return $Template.Replace($marker,$ownerQuery)
}
function New-WaterAnalysisBatch([string]$Stage,[string]$PrmStage='') {
    if (($Stage.Split(@('DECLARE @VerifiedIsolatedUat bit = 0;'),[StringSplitOptions]::None)).Count -ne 2) { throw 'Explicit isolated UAT stage guard is required.' }
    $prmObjects=''
    if ($PrmStage) {
        if (($PrmStage.Split(@('DECLARE @VerifiedIsolatedUat bit = 0;'),[StringSplitOptions]::None)).Count -ne 2) { throw 'Exactly one independent PRM isolated UAT guard is required.' }
        # Both reviewed templates share their outer variable names. Rename only
        # these stage-local identifiers so both boundaries commit atomically.
        $PrmStage=$PrmStage.Replace('@VerifiedIsolatedUat','@VerifiedPrmIsolatedUat').Replace('@Procedure','@PrmProcedure').Replace('@Trigger','@PrmTrigger')
        $PrmStage=$PrmStage.Replace('DECLARE @VerifiedPrmIsolatedUat bit = 0;','DECLARE @VerifiedPrmIsolatedUat bit = 1;')
        $prmObjects="INSERT #WaterAllowedObjects VALUES(N'dbo.EQ001_UAT_PrmRegistration'),(N'dbo.EQ001_PrmRegistrationReceipts');"
    }
    # Snapshot every existing table, including historic receipts, resources, LES,
    # equipment, QE, plans, signatures and audit. HOLDLOCK retains protection
    # through the OUTER commit; the template's nested commit is not completion.
    $capture=@'
SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000;
IF DB_NAME()<>N'EQ001_UAT_AMEEN_20261024' OR @@SERVERNAME<>N'AMEEN\SQLEXPRESS'
 THROW 57000,'Exact isolated UAT required.',1;
BEGIN TRANSACTION;
CREATE TABLE #WaterBefore(ObjectId int PRIMARY KEY,RowHash varbinary(32) NOT NULL);
CREATE TABLE #WaterAllowedObjects(TableName nvarchar(128) NOT NULL);
INSERT #WaterAllowedObjects VALUES(N'dbo.EQ001_UAT_WaterWorkflow'),(N'dbo.EQ001_WaterWorkflowReceipts');
/*__OPTIONAL_PRM_OBJECTS__*/
SELECT class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type,state INTO #WaterPermissions
FROM sys.database_permissions p WHERE NOT EXISTS(SELECT 1 FROM #WaterAllowedObjects a WHERE p.class=1 AND p.major_id=OBJECT_ID(a.TableName));
DECLARE @ObjectId int,@Table nvarchar(517),@Sql nvarchar(max),@Hash varbinary(32);
DECLARE before_cursor CURSOR LOCAL FAST_FORWARD FOR
 SELECT object_id,QUOTENAME(SCHEMA_NAME(schema_id))+N'.'+QUOTENAME(name)
 FROM sys.tables WHERE is_ms_shipped=0 ORDER BY object_id;
OPEN before_cursor;
FETCH NEXT FROM before_cursor INTO @ObjectId,@Table;
WHILE @@FETCH_STATUS=0
BEGIN
 SET @Sql=N'SELECT @h=HASHBYTES(''SHA2_256'',ISNULL((SELECT (SELECT t.* FOR XML RAW,BINARY BASE64) AS [Row] FROM '+@Table+N' t WITH(HOLDLOCK) ORDER BY (SELECT t.* FOR XML RAW,BINARY BASE64) COLLATE Latin1_General_100_BIN2 FOR XML PATH('''')),N''''));';
 EXEC sys.sp_executesql @Sql,N'@h varbinary(32) OUTPUT',@h=@Hash OUTPUT;
 INSERT #WaterBefore VALUES(@ObjectId,@Hash);
 FETCH NEXT FROM before_cursor INTO @ObjectId,@Table;
END;
CLOSE before_cursor; DEALLOCATE before_cursor;
SELECT N'BEFORE' Phase,OBJECT_SCHEMA_NAME(ObjectId)+N'.'+OBJECT_NAME(ObjectId) TableName,CONVERT(varchar(64),RowHash,2) RowHash FROM #WaterBefore ORDER BY ObjectId;
'@
    # XML retains binary values and every column; ordering each complete row by
    # its binary-collated serialization makes heap/duplicate-row scans deterministic.
    $verify=@'
DECLARE @AfterHash varbinary(32),@BeforeHash varbinary(32);
DECLARE after_cursor CURSOR LOCAL FAST_FORWARD FOR
 SELECT b.ObjectId,QUOTENAME(SCHEMA_NAME(t.schema_id))+N'.'+QUOTENAME(t.name),b.RowHash
 FROM #WaterBefore b JOIN sys.tables t ON t.object_id=b.ObjectId ORDER BY b.ObjectId;
OPEN after_cursor;
FETCH NEXT FROM after_cursor INTO @ObjectId,@Table,@BeforeHash;
WHILE @@FETCH_STATUS=0
BEGIN
 SET @Sql=N'SELECT @h=HASHBYTES(''SHA2_256'',ISNULL((SELECT (SELECT t.* FOR XML RAW,BINARY BASE64) AS [Row] FROM '+@Table+N' t WITH(HOLDLOCK) ORDER BY (SELECT t.* FOR XML RAW,BINARY BASE64) COLLATE Latin1_General_100_BIN2 FOR XML PATH('''')),N''''));';
 EXEC sys.sp_executesql @Sql,N'@h varbinary(32) OUTPUT',@h=@AfterHash OUTPUT;
 SELECT N'AFTER' Phase,@Table TableName,CONVERT(varchar(64),@AfterHash,2) RowHash;
 IF @AfterHash<>@BeforeHash THROW 57040,'Existing clinical/configuration/history rows changed during analysis staging.',1;
 FETCH NEXT FROM after_cursor INTO @ObjectId,@Table,@BeforeHash;
END;
CLOSE after_cursor; DEALLOCATE after_cursor;
IF EXISTS(SELECT class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type,state FROM #WaterPermissions
 EXCEPT SELECT class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type,state FROM sys.database_permissions)
 OR EXISTS(SELECT class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type,state FROM sys.database_permissions
 WHERE NOT EXISTS(SELECT 1 FROM #WaterAllowedObjects a WHERE sys.database_permissions.class=1 AND sys.database_permissions.major_id=OBJECT_ID(a.TableName))
 EXCEPT SELECT class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type,state FROM #WaterPermissions)
 THROW 57045,'Existing database permission boundary changed during analysis staging.',1;
IF @@TRANCOUNT<>1 THROW 57041,'Unexpected SQL stage transaction nesting.',1;
COMMIT;
PRINT N'PASS: Water analysis outer transaction committed; all existing rows preserved.';
'@
    return $capture.Replace('/*__OPTIONAL_PRM_OBJECTS__*/',$prmObjects)+"`r`n"+$Stage.Replace('DECLARE @VerifiedIsolatedUat bit = 0;','DECLARE @VerifiedIsolatedUat bit = 1;')+"`r`n"+$PrmStage+"`r`n"+$verify
}
function Assert-WaterAnalysisSqlProof([int]$ExitCode,[string]$LogText,[bool]$Finished=$true) {
    $marker='PASS: Water analysis outer transaction committed; all existing rows preserved.'
    $lines=@($LogText -split '\r?\n'|ForEach-Object { $_.Trim() })
    if (-not $Finished -or $ExitCode -ne 0 -or @($lines|Where-Object { $_ -ceq $marker }).Count -ne 1 -or
        $LogText -match '(?im)timeout|^\s*(Msg\s+\d+|Sqlcmd:\s*Error)|exception|transport-level error') {
        throw 'No committed analysis SQL completion proof. Keep service stopped; inspect protected SQL log.'
    }
}
function Invoke-WaterAnalysisSql([string]$Sql,[string]$Prefix,[string]$Marker) {
    $sqlPath=$Prefix+'.sql'; $logPath=$Prefix+'.txt'
    Write-DurableBytes $sqlPath ([Text.UTF8Encoding]::new($false).GetBytes($Sql))
    $info=[Diagnostics.ProcessStartInfo]::new()
    $info.FileName=(Get-Command sqlcmd.exe -ErrorAction Stop).Source
    if ($sqlPath.Contains('"')) { throw 'Unsafe SQL evidence path.' }
    $info.Arguments='-S "lpc:.\SQLEXPRESS" -E -d "EQ001_UAT_AMEEN_20261024" -I -b -l 10 -t 120 -w 240 -f 65001 -i "'+$sqlPath+'"'
    $info.UseShellExecute=$false; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    try {
        [void]$process.Start()
        $output=$process.StandardOutput.ReadToEndAsync(); $errors=$process.StandardError.ReadToEndAsync()
        $finished=$process.WaitForExit(150000)
        if (-not $finished) { $process.Kill(); $process.WaitForExit() }
        $log=$output.GetAwaiter().GetResult()+"`r`n"+$errors.GetAwaiter().GetResult()
        Write-DurableBytes $logPath ([Text.Encoding]::UTF8.GetBytes($log))
        if ($Marker -eq 'Stage') { Assert-WaterAnalysisSqlProof $process.ExitCode $log $finished }
        elseif (-not $finished -or $process.ExitCode -ne 0 -or
            @($log -split '\r?\n'|Where-Object { $_.Trim() -ceq $Marker }).Count -ne 1 -or
            $log -match '(?im)timeout|^\s*(Msg\s+\d+|Sqlcmd:\s*Error)|exception') { throw 'Independent analysis metadata validation failed.' }
    } finally { $process.Dispose() }
}
function Assert-BuildInputs($Manifest) {
    $inputs=@($Manifest.BuildInputs)
    if ($inputs.Count -lt 20) { throw 'Reviewed build-input closure is missing.' }
    # MSBuild walks ancestors for automatic imports. Unknown machine/repository
    # hooks cannot participate in this qualified site build.
    $ancestor=[IO.Directory]::GetParent($project)
    while ($ancestor) {
        foreach ($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props')) {
            if (Test-Path -LiteralPath (Join-Path $ancestor.FullName $name)) { throw 'Unreviewed ancestor MSBuild import would affect the qualified build.' }
        }
        $ancestor=$ancestor.Parent
    }
    $known=@{}
    foreach ($entry in $inputs) {
        if ($entry.Path -notmatch '^[A-Za-z0-9_./-]+$' -or $entry.Path.Contains('..') -or $known.ContainsKey($entry.Path)) { throw 'Unsafe/duplicate build input.' }
        $known[$entry.Path]=$true
        $path=Join-Path $project $entry.Path
        Assert-NoReparsePoint $path
        if ((Get-NormalizedHash ([IO.File]::ReadAllBytes($path))) -ne (Get-NormalizedHash (Read-GitBytes $entry.SourcePath $(if ($entry.SourceCommit) { $entry.SourceCommit } else { $SourceCommit })))) { throw "Unreviewed build source: $($entry.Path)" }
    }
    # App default Compile/Page globs and the two service project directories.
    foreach ($file in @(Get-ChildItem -LiteralPath $project -File -Recurse -Force)) {
        $relative=$file.FullName.Substring($project.Length).TrimStart('\').Replace('\','/')
        if ($relative -match '(^|/)(bin|obj|tests|\.git)/' -or
            ($relative.StartsWith('tools/') -and $relative -notmatch '^tools/PharmaLIMS\.TrustedSigning(?:\.Host)?/')) { continue }
        if ($file.Extension -in @('.cs','.xaml','.csproj','.props','.targets') -and -not $known.ContainsKey($relative)) { throw "Unlisted active build input: $relative" }
    }
}
function Enable-JsonBooleanFlag([string]$Json,[string]$Name) {
    if ($Name -notin @('EnableWaterAnalysisUat','EnablePrmRegistrationUat')) { throw 'Unreviewed feature flag.' }
    $matches=[regex]::Matches($Json,'"'+$Name+'"\s*:\s*(?:false|true)')
    if ($matches.Count -gt 1) { throw 'Duplicate feature flags are invalid.' }
    if ($matches.Count -eq 1) {
        $match=$matches[0]
        return $Json.Substring(0,$match.Index)+[regex]::Replace($match.Value,'(?:false|true)$','true')+$Json.Substring($match.Index+$match.Length)
    }
    $end=$Json.LastIndexOf('}')
    if ($end -lt 0) { throw 'Private host JSON must be an object.' }
    return $Json.Insert($end,',"'+$Name+'":true')
}
function Assert-ProtectedBytes {
    if ((Get-Hash ([IO.File]::ReadAllBytes($desktopConfigFile))) -ne (Get-Hash $desktopBytes) -or
        (Get-Hash ([IO.File]::ReadAllBytes($hostConfigFile))) -ne (Get-Hash $hostBytes) -or
        (Get-Hash ([IO.File]::ReadAllBytes($pfxFile))) -ne $pfxHash) { throw 'Protected configuration/PFX bytes changed.' }
}
foreach ($path in @($project,$privateRoot,$serviceRuntime,$desktop,$hostConfigFile,$desktopConfigFile,$pfxFile)) { Assert-NoReparsePoint $path }
Require-PrivateDirectory $pfxFile
Require-PrivateDirectory $hostConfigFile
Assert-BuildInputs $manifest
# Kernel account identity must also match the database's isolated Windows signer.
$certificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxFile,$hostConfig.CertificatePassword)
try {
    if ((Get-Hash $certificate.RawData) -ne $settings.TrustedSigning.ServerCertificateSha256 -or -not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'Private TLS certificate does not match the configured desktop pin or is expired.' }
} finally { $certificate.Dispose() }
$signerSid=([Security.Principal.NTAccount]::new($env:COMPUTERNAME,'PharmaLIMSSigner')).Translate([Security.Principal.SecurityIdentifier]).Value
if ($signerSid -ne 'S-1-5-21-3887410152-3473494482-1737934645-1009') { throw 'Unexpected signing service SID.' }
if ($hostConfig.EnableWaterRegistrationUat -ne $true) { throw 'Existing Water registration runtime must remain enabled.' }
$stage=Get-WaterWorkflowStage ([Text.Encoding]::UTF8.GetString((Read-GitBytes 'PharmaLIMS/scripts/EQ001_UAT_Water_Analysis.sql'))) ([Text.Encoding]::UTF8.GetString((Read-GitBytes 'PharmaLIMS/tools/PharmaLIMS.TrustedSigning/WaterWorkflowSqlContract.cs')))
$prmStage=if ($IncludePrmRegistration) { [Text.Encoding]::UTF8.GetString((Read-GitBytes 'PharmaLIMS/scripts/EQ001_UAT_Prm_Registration.sql')) } else { '' }
$batch=New-WaterAnalysisBatch $stage $prmStage
Write-Host 'PASS: Exact reviewed source/build closure, isolated UAT, TLS, private bytes and native service SID verified.'
if (-not $Apply) {
    Write-Host ('Read-only preview complete: one qualified host/desktop build, analysis SQL, nine project outputs. PRM registration opt-in: '+$IncludePrmRegistration.IsPresent)
    Write-Host 'No build, SQL connection, service, source, certificate or configuration mutation occurred.'
    return
}
if (Get-Process PharmaLIMS -ErrorAction SilentlyContinue) { throw 'Close PharmaLIMS before applying the guarded rollout.' }
if ((Get-Service $serviceName).Status -ne 'Stopped') { throw 'Signing service must be stopped before the guarded rollout.' }
# Installation saves original source bytes durably before replacing any source.
$sourceRollback=@(Get-ChildItem -LiteralPath $privateRoot -Filter 'source-water-analysis-rollback-*.json' -File | Where-Object {
    $record=Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json
    $record.Schema -eq 1 -and $record.SourceCommit -ceq $SourceCommit
})
if ($sourceRollback.Count -eq 0) { throw 'Matching protected original-source rollback record is required; use the guarded WaterAnalysisOnly installer.' }
foreach ($recordFile in $sourceRollback) {
    Require-PrivateDirectory $recordFile.FullName; Assert-NoReparsePoint $recordFile.FullName
    $record=Get-Content -LiteralPath $recordFile.FullName -Raw|ConvertFrom-Json
    foreach ($entry in $record.Files) {
        if ((Get-Hash ([Convert]::FromBase64String($entry.OriginalBase64))) -ne $entry.BeforeSha256) { throw 'Original-source rollback bytes do not match their durable hash.' }
    }
}
$rollout=Join-Path $privateRoot ('water-analysis-rollback-'+[guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($rollout); Require-PrivateDirectory $rollout
Backup-Tree $desktop (Join-Path $rollout 'desktop')
Backup-Tree $serviceRuntime (Join-Path $rollout 'service')
foreach ($file in $sourceRollback) { Write-DurableBytes (Join-Path $rollout $file.Name) ([IO.File]::ReadAllBytes($file.FullName)) }
Write-DurableBytes (Join-Path $rollout 'desktop-appsettings.json') $desktopBytes
Write-DurableBytes (Join-Path $rollout 'hostsettings.json') $hostBytes
Write-DurableBytes (Join-Path $rollout 'original-certificate.pfx') ([IO.File]::ReadAllBytes($pfxFile))
Write-DurableBytes (Join-Path $rollout 'source-commit.txt') ([Text.Encoding]::ASCII.GetBytes($SourceCommit))
$buildOutput=Join-Path $rollout 'build'
$mutated=New-Object 'System.Collections.Generic.List[object]'
try {
    # One incremental dependency build, with output isolated from both installed
    # runtime trees. Do not stage registration/PRM SQL or repeat the old bulk build.
    dotnet build (Join-Path $project 'tools/PharmaLIMS.TrustedSigning.Host/PharmaLIMS.TrustedSigning.Host.csproj') `
        --configuration Release --no-restore --output $buildOutput -maxcpucount:2 `
        -p:DeploymentEnvironment=UAT 2>&1 | Tee-Object -FilePath (Join-Path $rollout 'build.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Qualified Water analysis host/desktop build failed.' }
    Assert-ProtectedBytes
    Assert-BuildInputs $manifest
    $copies=New-Object 'System.Collections.Generic.List[object]'
    foreach ($file in @(Get-ChildItem -LiteralPath $buildOutput -File -Recurse -Force)) {
        $relative=$file.FullName.Substring($buildOutput.Length).TrimStart('\')
        if ($file.Name -in @('appsettings.json','hostsettings.json') -or $file.Extension -in @('.pfx','.key','.cer','.crt','.pdb')) { continue }
        foreach ($targetRoot in @($desktop,$serviceRuntime)) {
            # The desktop does not run the service host executable.
            if ($targetRoot -eq $desktop -and $file.Name.StartsWith('PharmaLIMS.TrustedSigning.Host.')) { continue }
            $target=Join-Path $targetRoot $relative
            $exists=Test-Path -LiteralPath $target -PathType Leaf
            $hash=Get-Hash ([IO.File]::ReadAllBytes($file.FullName))
            if ($exists -and (Get-Hash ([IO.File]::ReadAllBytes($target))) -eq $hash) { continue }
            if ($relative.Contains('\') -or $file.Name -notin $runtimeNames) { throw "Unqualified dependency/runtime difference: $relative" }
            if (-not $exists) { throw "Required installed project runtime is missing: $relative" }
            $copies.Add([pscustomobject]@{ Source=$file.FullName; Target=$target; Before=[IO.File]::ReadAllBytes($target); Hash=$hash })
        }
    }
    foreach ($name in $runtimeNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $buildOutput $name) -PathType Leaf)) { throw "Qualified build output missing: $name" }
    }
    # Stage and verify the committed boundary before any runtime/flag activation.
    Invoke-WaterAnalysisSql $batch (Join-Path $rollout 'stage') 'Stage'
    $metadata=@'
SET NOCOUNT ON;
IF DB_NAME()<>N'EQ001_UAT_AMEEN_20261024' OR @@SERVERNAME<>N'AMEEN\SQLEXPRESS'
 THROW 57042,'Wrong independent metadata target.',1;
IF OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow',N'P') IS NULL OR OBJECT_ID(N'dbo.EQ001_WaterWorkflowReceipts',N'U') IS NULL
 OR NOT EXISTS(SELECT 1 FROM sys.sql_modules WHERE object_id=OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow') AND execute_as_principal_id=-2)
 OR NOT EXISTS(SELECT 1 FROM sys.triggers WHERE name=N'TRG_EQ001_WaterWorkflowReceipts_AppendOnly' AND parent_id=OBJECT_ID(N'dbo.EQ001_WaterWorkflowReceipts') AND is_disabled=0)
 OR NOT EXISTS(SELECT 1 FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner')
 AND major_id=OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow') AND class=1 AND permission_name=N'EXECUTE' AND state=N'G')
 OR NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'PharmaLIMS_TrustedEquipmentSigner' AND type=N'U' AND sid=SUSER_SID(N'AMEEN\PharmaLIMSSigner'))
 OR ISNULL(IS_SRVROLEMEMBER(N'sysadmin',N'AMEEN\PharmaLIMSSigner'),1)<>0
 THROW 57043,'Independent boundary owner, SID, permissions or append-only metadata invalid.',1;
PRINT N'PASS: Independent committed Water analysis metadata verified.';
'@
    $procedureLiteral=[regex]::Matches($stage,"(?s)DECLARE @Procedure nvarchar\(max\)=N'((?:''|[^'])*)';")
    if ($procedureLiteral.Count -ne 1) { throw 'Cannot independently verify exact procedure definition.' }
    $expectedDefinition="DECLARE @Expected nvarchar(max)=N'"+$procedureLiteral[0].Groups[1].Value+"';`r`n"
    $expectedDefinition+=@'
IF HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow')),CHARINDEX(N'PROCEDURE',OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow'))),2147483647),CHAR(13),N''))<>
 HASHBYTES('SHA2_256',REPLACE(SUBSTRING(@Expected,CHARINDEX(N'PROCEDURE',@Expected),2147483647),CHAR(13),N''))
 THROW 57044,'Independent exact committed procedure definition differs.',1;
'@
    if ($IncludePrmRegistration) {
        $prmLiteral=[regex]::Matches($prmStage,"(?s)DECLARE @Procedure nvarchar\(max\)=N'((?:''|[^'])*)';")
        $triggerLiteral=[regex]::Matches($prmStage,"(?s)DECLARE @Trigger nvarchar\(max\)=N'((?:''|[^'])*)';")
        if ($prmLiteral.Count -ne 1 -or $triggerLiteral.Count -ne 1) { throw 'PRM independent module proof could not be composed.' }
        $expectedDefinition+="`r`nDECLARE @ExpectedPrm nvarchar(max)=N'"+$prmLiteral[0].Groups[1].Value+"';`r`nDECLARE @ExpectedPrmTrigger nvarchar(max)=N'"+$triggerLiteral[0].Groups[1].Value+"';`r`n"
        $expectedDefinition+=@'
IF OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration',N'P') IS NULL OR OBJECT_ID(N'dbo.EQ001_PrmRegistrationReceipts',N'U') IS NULL
 OR NOT EXISTS(SELECT 1 FROM sys.sql_modules WHERE object_id=OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration') AND execute_as_principal_id=-2)
 OR NOT EXISTS(SELECT 1 FROM sys.triggers WHERE name=N'TRG_EQ001_PrmRegistrationReceipts_AppendOnly' AND parent_id=OBJECT_ID(N'dbo.EQ001_PrmRegistrationReceipts') AND is_disabled=0)
 OR NOT EXISTS(SELECT 1 FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') AND class=1
 AND major_id=OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration') AND permission_name=N'EXECUTE' AND state=N'G')
 OR (SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') AND class=1
 AND major_id=OBJECT_ID(N'dbo.EQ001_PrmRegistrationReceipts') AND permission_name IN(N'INSERT',N'UPDATE',N'DELETE') AND state=N'D')<>3
 OR ISNULL(HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration')),CHARINDEX(N'PROCEDURE',OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration'))),2147483647),CHAR(13),N'')),0x)<>
 HASHBYTES('SHA2_256',REPLACE(SUBSTRING(@ExpectedPrm,CHARINDEX(N'PROCEDURE',@ExpectedPrm),2147483647),CHAR(13),N''))
 OR ISNULL(HASHBYTES('SHA2_256',REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'dbo.TRG_EQ001_PrmRegistrationReceipts_AppendOnly')),CHAR(13),N'')),0x)<>HASHBYTES('SHA2_256',REPLACE(@ExpectedPrmTrigger,CHAR(13),N''))
 THROW 57046,'Independent PRM owner, scoped grants, receipt protection or exact committed module definition invalid.',1;
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests') AND
 ((name IN(N'SourceSpecificationTestID',N'SpecificationVersionNo') AND system_type_id=TYPE_ID(N'int') AND max_length=4)
 OR (name IN(N'SpecificationItemCode',N'SpecificationProductionStage') AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=160)))<>4
 OR NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'SpecificationVersionNo' AND system_type_id=TYPE_ID(N'int') AND max_length=4)
 THROW 57047,'Independent exact PRM provenance column types invalid.',1;
'@
    }
    $metadata=$metadata.Replace("PRINT N'PASS: Independent committed Water analysis metadata verified.';",$expectedDefinition+"`r`nPRINT N'PASS: Independent committed Water analysis metadata verified.';")
    Invoke-WaterAnalysisSql $metadata (Join-Path $rollout 'metadata') 'PASS: Independent committed Water analysis metadata verified.'
    Assert-ProtectedBytes
    if (Get-Process PharmaLIMS -ErrorAction SilentlyContinue) { throw 'PharmaLIMS reopened during rollout; keep it closed.' }
    if ((Get-Service $serviceName).Status -ne 'Stopped') { throw 'Signing service unexpectedly restarted during rollout.' }
    foreach ($copy in $copies) {
        if ((Get-Hash ([IO.File]::ReadAllBytes($copy.Target))) -ne (Get-Hash $copy.Before)) { throw 'Installed runtime changed during preparation.' }
        $mutated.Add($copy)
        [IO.File]::WriteAllBytes($copy.Target,[IO.File]::ReadAllBytes($copy.Source))
        if ((Get-Hash ([IO.File]::ReadAllBytes($copy.Target))) -ne $copy.Hash) { throw 'Installed runtime hash mismatch.' }
    }
    Assert-ProtectedBytes
    # Change only the independently selected properties in-place so every unrelated config byte,
    # including credentials/formatting, is retained. No JSON round-trip rewrite.
    $hostText=[Text.Encoding]::UTF8.GetString($hostBytes)
    $enabledFlags=@('EnableWaterAnalysisUat')
    if ($IncludePrmRegistration) { $enabledFlags+='EnablePrmRegistrationUat' }
    $updated=$hostText
    foreach ($flag in $enabledFlags) { $updated=Enable-JsonBooleanFlag $updated $flag }
    $check=$updated.TrimStart([char]0xFEFF)|ConvertFrom-Json
    $beforeOthers=[ordered]@{}; $afterOthers=[ordered]@{}
    foreach ($property in $hostConfig.PSObject.Properties) { if ($property.Name -notin $enabledFlags) { $beforeOthers[$property.Name]=$property.Value } }
    foreach ($property in $check.PSObject.Properties) { if ($property.Name -notin $enabledFlags) { $afterOthers[$property.Name]=$property.Value } }
    if ($check.EnableWaterAnalysisUat -ne $true -or ($IncludePrmRegistration -and $check.EnablePrmRegistrationUat -ne $true) -or ($beforeOthers|ConvertTo-Json -Depth 30 -Compress) -cne ($afterOthers|ConvertTo-Json -Depth 30 -Compress)) { throw 'An unrelated host setting changed.' }
    $temporary=Join-Path $serviceRuntime ('analysis-config-'+[guid]::NewGuid()+'.tmp')
    try {
        Write-DurableBytes $temporary ([Text.Encoding]::UTF8.GetBytes($updated))
        [IO.File]::Replace($temporary,$hostConfigFile,(Join-Path $rollout 'hostsettings-pre-enable.json'))
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    Start-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    $pipe='\\.\pipe\PharmaLIMS_EQ001_AMEEN_UAT'; $ready=$false
    for ($i=0;$i -lt 25;$i++) {
        if ($pipe -in [IO.Directory]::GetFiles('\\.\pipe\')) { $ready=$true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Expected trusted UAT pipe did not appear.' }
    foreach ($copy in $copies) { if ((Get-Hash ([IO.File]::ReadAllBytes($copy.Target))) -ne $copy.Hash) { throw 'Final qualified runtime validation failed.' } }
    if ((Get-Hash ([IO.File]::ReadAllBytes($desktopConfigFile))) -ne (Get-Hash $desktopBytes) -or (Get-Hash ([IO.File]::ReadAllBytes($pfxFile))) -ne $pfxHash) { throw 'Final desktop/PFX byte preservation failed.' }
    Write-Host "PASS: Analysis boundary committed and independently verified; $($copies.Count) qualified runtime differences installed; UAT service running."
    Write-Host "Protected source/runtime rollback: $rollout"
    if ($IncludePrmRegistration) { Write-Host 'PRM registration was independently selected, committed and enabled; native standard-user registration/readback remains required.' }
    Write-Host 'Native standard-user signed Start/Save/Submit/Review/Approve/CorrectTimes and persisted scientific/signature/audit readback remain required.'
    Write-Host 'Certificate issue/print/cancel and manual investigation editing remain unqualified. This rollout is not laboratory readiness.'
} catch {
    $failure=$_
    try {
        Stop-Service $serviceName -ErrorAction Stop
        (Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
        for ($i=$mutated.Count-1;$i -ge 0;$i--) { [IO.File]::WriteAllBytes($mutated[$i].Target,$mutated[$i].Before) }
        [IO.File]::WriteAllBytes($hostConfigFile,$hostBytes)
        [IO.File]::WriteAllBytes($desktopConfigFile,$desktopBytes)
        Assert-ProtectedBytes
    } catch { Write-Warning 'Automatic recovery was incomplete. Keep desktop closed/service stopped and use the protected rollback inventory.' }
    Write-Host "Deployment stopped. Analysis SQL, if committed, is retained disabled; protected original runtime/source bytes: $rollout"
    throw $failure
}
