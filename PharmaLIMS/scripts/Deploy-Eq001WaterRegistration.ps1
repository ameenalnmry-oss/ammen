[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit,
    [switch]$Apply
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
function Read-GitBytes([string]$Path) {
    if ($Path -notmatch '^PharmaLIMS/[A-Za-z0-9_./-]+$' -or $Path -match '(^|/)\.\.(/|$)') { throw 'Unsafe repository path.' }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command git).Source
    $info.WorkingDirectory = $project
    $info.Arguments = 'show "{0}:{1}"' -f $SourceCommit,$Path
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
function Backup-Tree([string]$Source,[string]$Target) {
    [void][IO.Directory]::CreateDirectory($Target)
    foreach ($file in @(Get-ChildItem -LiteralPath $Source -File -Recurse -Force)) {
        $relative = $file.FullName.Substring($Source.Length).TrimStart('\')
        $copy = Join-Path $Target $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy))
        [IO.File]::Copy($file.FullName,$copy,$false)
        if ((Get-Hash ([IO.File]::ReadAllBytes($copy))) -ne (Get-Hash ([IO.File]::ReadAllBytes($file.FullName)))) {
            throw 'Runtime rollback copy verification failed.'
        }
    }
}
Require-PrivateDirectory $privateRoot
Require-PrivateDirectory $serviceRuntime
$resolved = git -C $project rev-parse --verify "$SourceCommit^{commit}"
if ($LASTEXITCODE -ne 0 -or $resolved -ne $SourceCommit) { throw 'Fetch the exact reviewed source commit first.' }
$manifest = [Text.Encoding]::UTF8.GetString((Read-GitBytes 'PharmaLIMS/scripts/Eq001-WaterSourcePrerequisites.json')) | ConvertFrom-Json
if ($manifest.OriginalProject -ne $project) { throw 'Unexpected source manifest project.' }
foreach ($entry in $manifest.Files) {
    if ($entry.Path -notmatch '^[A-Za-z0-9_./-]+$' -or $entry.Path -match '(^|/)\.\.(/|$)') { throw 'Unsafe source target.' }
    $target = Join-Path $project $entry.Path
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
        (Get-NormalizedHash ([IO.File]::ReadAllBytes($target))) -ne (Get-NormalizedHash (Read-GitBytes $entry.SourcePath))) {
        throw "Installed source does not match the reviewed Water commit: $($entry.Path)"
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
$dbOptions.ConnectionString = $hostConfig.ConnectionString
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
Write-Host 'PASS: Exact Water source, original project, isolated desktop/service targets, TLS pin and service identity verified.'
if (-not $Apply) { Write-Host 'Read-only deployment check completed. No build, SQL, service or settings change.'; return }
if (Get-Process PharmaLIMS -ErrorAction SilentlyContinue) { throw 'Close PharmaLIMS before rollout.' }
if ((Get-Service $serviceName).Status -ne 'Stopped') { throw 'Stop the signing service before this coordinated Water rollout.' }
$rollout = Join-Path $privateRoot ('water-registration-rollback-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($rollout)
Require-PrivateDirectory $rollout
Backup-Tree $desktop (Join-Path $rollout 'desktop')
Backup-Tree $serviceRuntime (Join-Path $rollout 'service')
[IO.File]::WriteAllBytes((Join-Path $rollout 'desktop-appsettings.json'),$desktopBytes)
[IO.File]::WriteAllBytes((Join-Path $rollout 'hostsettings.json'),$hostBytes)
Write-Host "Verified desktop/service rollback: $rollout"
$buildCode = -1
try {
    try {
        dotnet build (Join-Path $project 'tools\PharmaLIMS.TrustedSigning.Host\PharmaLIMS.TrustedSigning.Host.csproj') `
            --configuration Release --no-restore -maxcpucount:2 2>&1 |
            Tee-Object -FilePath (Join-Path $rollout 'build.txt')
        $buildCode = $LASTEXITCODE
    }
    finally { [IO.File]::WriteAllBytes($desktopConfigFile,$desktopBytes) }
    if ($buildCode -ne 0) { throw 'Water desktop/host build failed. Keep both closed.' }
    if ((Get-Hash ([IO.File]::ReadAllBytes($desktopConfigFile))) -ne (Get-Hash $desktopBytes) -or
        (Get-Hash ([IO.File]::ReadAllBytes($hostConfigFile))) -ne (Get-Hash $hostBytes) -or
        (Get-Hash ([IO.File]::ReadAllBytes($pfxFile))) -ne $pfxHash) { throw 'Protected settings/PFX changed during build.' }
    $stage = [Text.Encoding]::UTF8.GetString((Read-GitBytes 'PharmaLIMS/scripts/EQ001_UAT_Water_Registration.sql'))
    $stage = $stage.Replace('DECLARE @VerifiedIsolatedUat bit = 0;','DECLARE @VerifiedIsolatedUat bit = 1;')
    $tables = @('LIMS_NumberSequences','Samples','SampleTests','ElectronicSignatures','AuditTrail','Water_Plans','Water_PlanSamples','Water_PlanSampleTests','Water_PlanSampleAttempts')
    $orders = @('Id','SampleID','SampleTestID','SignatureID','AuditID','WaterPlanID','WaterPlanSampleID','WaterPlanSampleID,TestID','AttemptID')
    $capture = "SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRANSACTION;`r`n"
    $verify = "`r`n"
    for ($i=0; $i -lt $tables.Count; $i++) {
        $table = $tables[$i]
        $order = $orders[$i]
        $capture += "DECLARE @Before$i binary(32) = HASHBYTES('SHA2_256',(SELECT * FROM dbo.[$table] WITH(HOLDLOCK) ORDER BY $order FOR JSON PATH,INCLUDE_NULL_VALUES));`r`n"
        $verify += "IF @Before$i<>HASHBYTES('SHA2_256',(SELECT * FROM dbo.[$table] WITH(HOLDLOCK) ORDER BY $order FOR JSON PATH,INCLUDE_NULL_VALUES)) THROW 56917,'Historical Water rows changed during staging.',1;`r`n"
    }
    $verify += "COMMIT; PRINT N'PASS: Water SQL boundary staged; existing business rows preserved.';`r`n"
    $sqlFile = Join-Path $rollout 'stage.sql'
    [IO.File]::WriteAllText($sqlFile,$capture+$stage+$verify,[Text.UTF8Encoding]::new($false))
    $sqlLog = Join-Path $rollout 'stage.txt'
    sqlcmd.exe -S 'lpc:.\SQLEXPRESS' -E -d 'EQ001_UAT_AMEEN_20261024' -I -b -l 10 -t 90 -w 240 -f 65001 -i $sqlFile -o $sqlLog
    $sqlCode = $LASTEXITCODE
    Get-Content -LiteralPath $sqlLog
    if ($sqlCode -ne 0) { throw "Water SQL staging failed. Keep service stopped. Log: $sqlLog" }
    $buildOutput = Join-Path $project 'tools\PharmaLIMS.TrustedSigning.Host\bin\Release\net8.0-windows'
    $inventory = @(Get-ChildItem -LiteralPath $buildOutput -File -Recurse -Force)
    if ($inventory.Count -eq 0) { throw 'Host build inventory is empty.' }
    $copied = 0
    foreach ($file in $inventory) {
        $relative = $file.FullName.Substring($buildOutput.Length).TrimStart('\')
        if ($file.Name -in @('appsettings.json','hostsettings.json') -or $file.Extension -in @('.pfx','.key','.cer','.crt')) { continue }
        $target = Join-Path $serviceRuntime $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy($file.FullName,$target,$true)
        if ((Get-Hash ([IO.File]::ReadAllBytes($target))) -ne (Get-Hash ([IO.File]::ReadAllBytes($file.FullName)))) { throw 'Host runtime hash verification failed.' }
        $copied++
    }
    if ((Get-Hash ([IO.File]::ReadAllBytes($pfxFile))) -ne $pfxHash -or
        (Get-Hash ([IO.File]::ReadAllBytes($hostConfigFile))) -ne (Get-Hash $hostBytes)) { throw 'Private configuration or PFX changed during runtime refresh.' }
    $beforeOthers = [ordered]@{}
    foreach ($property in $hostConfig.PSObject.Properties) {
        if ($property.Name -ne 'EnableWaterRegistrationUat') { $beforeOthers[$property.Name] = $property.Value }
    }
    $hostConfig | Add-Member -NotePropertyName EnableWaterRegistrationUat -NotePropertyValue $true -Force
    $temporaryConfig = Join-Path $serviceRuntime ('hostsettings-' + [guid]::NewGuid() + '.tmp')
    $newConfigBytes = [Text.Encoding]::UTF8.GetBytes(($hostConfig | ConvertTo-Json -Depth 20))
    try {
        $stream = [IO.FileStream]::new($temporaryConfig,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($newConfigBytes,0,$newConfigBytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        [IO.File]::Replace($temporaryConfig,$hostConfigFile,(Join-Path $rollout 'hostsettings-pre-enable.json'))
    }
    finally { if (Test-Path -LiteralPath $temporaryConfig) { Remove-Item -LiteralPath $temporaryConfig } }
    $check = Get-Content -LiteralPath $hostConfigFile -Raw | ConvertFrom-Json
    $afterOthers = [ordered]@{}
    foreach ($property in $check.PSObject.Properties) {
        if ($property.Name -ne 'EnableWaterRegistrationUat') { $afterOthers[$property.Name] = $property.Value }
    }
    if ($check.EnableWaterRegistrationUat -ne $true -or
        ($beforeOthers | ConvertTo-Json -Depth 20 -Compress) -ne ($afterOthers | ConvertTo-Json -Depth 20 -Compress)) {
        throw 'Water activation changed an unrelated service setting.'
    }
    Start-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    $pipe = '\\.\pipe\PharmaLIMS_EQ001_AMEEN_UAT'
    $ready = $false
    for ($i=0; $i -lt 25; $i++) {
        if (@([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -eq $pipe }).Count -eq 1) { $ready=$true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Expected UAT pipe did not appear.' }
    if ((Get-Hash ([IO.File]::ReadAllBytes($pfxFile))) -ne $pfxHash -or
        (Get-Hash ([IO.File]::ReadAllBytes($desktopConfigFile))) -ne (Get-Hash $desktopBytes)) { throw 'Final protected bytes verification failed.' }
    Write-Host "PASS: Water boundary staged and $copied runtime files verified; UAT service running."
    Write-Host 'PASS: Desktop settings and PFX bytes preserved; only the independent Water host flag enabled.'
    Write-Host "Rollback: $rollout"
    Write-Host 'Actual standard-user Water registration and evidence verification are still required.'
}
catch {
    $failure = $_
    try {
        Stop-Service $serviceName -ErrorAction Stop
        (Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
        [IO.File]::WriteAllBytes($hostConfigFile,$hostBytes)
        [IO.File]::WriteAllBytes($desktopConfigFile,$desktopBytes)
    }
    catch { Write-Warning 'Could not fully restore settings or stop the service. Keep the desktop closed.' }
    Write-Host "Rollout stopped. Preserve rollback directory: $rollout"
    throw $failure
}
