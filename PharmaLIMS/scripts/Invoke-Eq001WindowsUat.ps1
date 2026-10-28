[CmdletBinding()]
param([switch]$IsolatedGithubUat)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsolatedGithubUat -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This provisioning script is restricted to an ephemeral GitHub-hosted Windows UAT runner.'
}
if (-not [OperatingSystem]::IsWindows()) { throw 'Windows is required.' }
$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$results = Join-Path $project 'artifacts/eq001-windows-uat'
New-Item -ItemType Directory -Force -Path $results | Out-Null
& dotnet run --project (Join-Path $project 'tests/PharmaLIMS.SessionRegression/PharmaLIMS.SessionRegression.csproj') --configuration Release 2>&1 |
    Tee-Object -FilePath (Join-Path $results 'session-regression.txt')
if ($LASTEXITCODE -ne 0) { throw 'Session handoff regression checks failed.' }
& (Join-Path $project 'scripts/Test-Eq001SessionSiteBuild.ps1') 2>&1 |
    Tee-Object -FilePath (Join-Path $results 'session-site-build.txt')
if (-not $?) { throw 'Original-site session desktop build failed.' }
try {
    & (Join-Path $project 'tests/Test-Eq001SourceInstaller.ps1') 2>&1 |
        Tee-Object -FilePath (Join-Path $results 'source-installer-checks.txt')
} catch {
    $details = ($_.Exception.Message + '; ' + $_.ScriptStackTrace).Replace("`r",' ').Replace("`n",' ')
    Add-Content -LiteralPath (Join-Path $results 'source-installer-checks.txt') -Value $details
    Write-Host "::error title=EQ001 source installer qualification::$details"
    throw
}
& dotnet run --project (Join-Path $project 'tests/PharmaLIMS.WaterRegistrationRegression/PharmaLIMS.WaterRegistrationRegression.csproj') --configuration Release 2>&1 |
    Tee-Object -FilePath (Join-Path $results 'water-policy-regression.txt')
if ($LASTEXITCODE -ne 0) { throw 'Water registration policy checks failed.' }
& dotnet run --project (Join-Path $project 'tests/PharmaLIMS.WaterRecoveryWindows/PharmaLIMS.WaterRecoveryWindows.csproj') --configuration Release -- $project 2>&1 |
    Tee-Object -FilePath (Join-Path $results 'water-recovery-windows.txt')
if ($LASTEXITCODE -ne 0) { throw 'Native Windows Water recovery checks failed.' }
& (Join-Path $project 'tests/Test-Eq001SqlProvisioning.ps1') -RepositoryRoot $project 2>&1 |
    Tee-Object -FilePath (Join-Path $results 'sql-provisioning-contract.txt')
if (-not $?) { throw 'SQL provisioning contract regression failed.' }
$stamp = [Guid]::NewGuid().ToString('N').Substring(0,10)
$serviceName = "eq001_$stamp"
$privateRoot = Join-Path $env:ProgramData "PharmaLIMS-EQ001-UAT-$stamp"
$windowsPassword = 'Aa1!' + [Guid]::NewGuid().ToString('N')
$certificatePassword = 'Aa1!' + [Guid]::NewGuid().ToString('N')
Write-Output "::add-mask::$windowsPassword"
Write-Output "::add-mask::$certificatePassword"
$cert = $null
$serviceCreated = $false
$scmCreated = $false
$rootCertificate = $null
try {
    # SQL Express provisioning contract: this is only reachable on a disposable
    # GitHub-hosted runner. Chocolatey success alone does not prove SQLENGINE exists.
    # Upstream flcdrg/au-packages/sql-server-express supports /ConfigurationFile;
    # its default setup arguments omit FEATURES and allow setup exit code 1116.
    $sqlConfiguration = Join-Path $env:TEMP "eq001-sql-$stamp.ini"
    $sqlInstallExitCode = $null
    function Write-SqlProvisioningDiagnostics {
        $diagnostic = [System.Collections.Generic.List[string]]::new()
        $diagnostic.Add('SQL Express provisioning: package=sql-server-express version=2022.16.0.20260305; expected=SQLEXPRESS/SQLENGINE')
        $diagnostic.Add("Chocolatey exit code: $sqlInstallExitCode")
        $services = @(Get-Service -Name 'MSSQL*' -ErrorAction SilentlyContinue)
        if ($services.Count -eq 0) { $diagnostic.Add('No MSSQL engine service was registered.') }
        foreach ($service in $services) { $diagnostic.Add("SQL service: $($service.Name); status=$($service.Status)") }
        # Emit only setup result fields. Full setup/Chocolatey logs can include
        # command lines and machine configuration and must not be uploaded raw.
        $logRoot = Join-Path $env:ProgramFiles 'Microsoft SQL Server'
        $summaries = @(Get-ChildItem -Path "$logRoot\*\Setup Bootstrap\Log\Summary.txt" -ErrorAction SilentlyContinue)
        foreach ($summary in $summaries) {
            $diagnostic.Add("SQL setup summary: $($summary.FullName)")
            $safeFields = Get-Content -LiteralPath $summary.FullName -ErrorAction SilentlyContinue |
                Where-Object { $_ -match '^\s*(Final result|Exit code(?: \([^)]*\))?|Exit message|Requested action|Feature|Status|Component name|Component error code|Error description)\s*:' } |
                Select-Object -First 40
            foreach ($field in $safeFields) {
                # Defense in depth if a future setup error embeds a credential.
                $safeField = [regex]::Replace($field, '(?i)((?:password|pwd|token|secret)\s*[=:]\s*)(?:"[^"]*"|''[^'']*''|[^;\s]+)', '$1[redacted]')
                $diagnostic.Add($safeField)
            }
        }
        if ($summaries.Count -eq 0) { $diagnostic.Add('No SQL setup Summary.txt found; installer may not have run.') }
        $diagnostic | Set-Content -LiteralPath (Join-Path $results 'sql-provisioning.txt') -Encoding utf8
        $diagnostic | ForEach-Object { Write-Host $_ }
    }
    try {
        if (-not (Get-Service -Name 'MSSQL$SQLEXPRESS' -ErrorAction SilentlyContinue)) {
            Write-Host 'Installing the explicit SQLEXPRESS SQLENGINE instance on the fresh disposable CI runner.'
            $sqlAdministrator = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            @"
[OPTIONS]
ACTION="Install"
FEATURES="SQLENGINE"
INSTANCENAME="SQLEXPRESS"
INSTANCEID="SQLEXPRESS"
SQLSYSADMINACCOUNTS="$sqlAdministrator"
SQLSVCSTARTUPTYPE="Automatic"
TCPENABLED="1"
NPENABLED="0"
UPDATEENABLED="False"
"@ | Set-Content -LiteralPath $sqlConfiguration -Encoding utf8
            # Reapply on images that contain package metadata without the required
            # engine service; --force is confined to this ephemeral-runner guard.
            choco install sql-server-express --version=2022.16.0.20260305 -y --force --no-progress --execution-timeout=1800 --params "/ConfigurationFile:`"$sqlConfiguration`""
            $sqlInstallExitCode = $LASTEXITCODE
            if ($sqlInstallExitCode -notin @(0,3010)) { throw "SQL Server Express installation failed (Chocolatey exit $sqlInstallExitCode)." }
        }
        $registrationDeadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            $sqlService = Get-Service -Name 'MSSQL$SQLEXPRESS' -ErrorAction SilentlyContinue
            $instanceNames = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL' -ErrorAction SilentlyContinue
            $instanceId = if ($instanceNames -and $instanceNames.PSObject.Properties['SQLEXPRESS']) { [string]$instanceNames.SQLEXPRESS } else { $null }
            if ($sqlService -and -not [string]::IsNullOrWhiteSpace($instanceId)) { break }
            if ([DateTime]::UtcNow -ge $registrationDeadline) {
                throw 'SQL setup returned without registering both MSSQL$SQLEXPRESS and its named-instance registry entry within 60 seconds.'
            }
            Start-Sleep -Seconds 2
        } while ($true)
        if ($sqlService.Status -ne 'Running') {
            Start-Service -Name $sqlService.Name
            $sqlService.WaitForStatus('Running',[TimeSpan]::FromSeconds(60))
        }
        Write-Host "SQL Express provisioned: service=$($sqlService.Name); instance=$instanceId."
    }
    catch {
        Write-SqlProvisioningDiagnostics
        throw
    }
    finally {
        Remove-Item -LiteralPath $sqlConfiguration -Force -ErrorAction SilentlyContinue
    }
    $instanceRoot = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId\MSSQLServer"
    $cert = New-SelfSignedCertificate -DnsName @($env:COMPUTERNAME,'localhost','PharmaLIMS.TrustedSigning') -Type SSLServerAuthentication -CertStoreLocation Cert:\LocalMachine\My -Provider 'Microsoft RSA SChannel Cryptographic Provider' -KeySpec KeyExchange -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddDays(2)
    $cer = Join-Path $env:TEMP "eq001-$stamp.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    $rootCertificate = Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root
    $sqlIdentity = (Get-CimInstance Win32_Service -Filter "Name='MSSQL`$SQLEXPRESS'").StartName
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
        $uniqueKey = $rsa.CspKeyContainerInfo.UniqueKeyContainerName
    } elseif ($rsa -is [System.Security.Cryptography.RSACng]) {
        $uniqueKey = $rsa.Key.UniqueName
    } else { throw 'Unsupported certificate private-key provider.' }
    $keyPath = @((Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$uniqueKey"),(Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$uniqueKey")) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $keyPath) { throw 'Certificate private-key file was not found.' }
    $keyAcl = Get-Acl $keyPath
    $keyAcl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($sqlIdentity,'Read','Allow'))
    Set-Acl $keyPath $keyAcl
    Set-ItemProperty $instanceRoot -Name LoginMode -Value 2
    $network = Join-Path $instanceRoot 'SuperSocketNetLib'
    Set-ItemProperty $network -Name Certificate -Value $cert.Thumbprint.ToLowerInvariant()
    Set-ItemProperty $network -Name ForceEncryption -Value 1
    Set-ItemProperty (Join-Path $network 'Tcp') -Name Enabled -Value 1
    Set-ItemProperty (Join-Path $network 'Tcp\IPAll') -Name TcpDynamicPorts -Value ''
    Set-ItemProperty (Join-Path $network 'Tcp\IPAll') -Name TcpPort -Value '14335'
    Restart-Service $sqlService.Name -Force
    $sqlService.WaitForStatus('Running',[TimeSpan]::FromMinutes(2))
    New-LocalUser -Name $serviceName -Password (ConvertTo-SecureString $windowsPassword -AsPlainText -Force) -AccountNeverExpires -PasswordNeverExpires | Out-Null
    $serviceCreated = $true
    $serviceSid = (Get-LocalUser -Name $serviceName).SID.Value
    $callerSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    New-Item -ItemType Directory -Path $privateRoot -Force | Out-Null
    $acl = [System.Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($sid in @('S-1-5-32-544','S-1-5-18',$serviceSid)) {
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new([System.Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl $privateRoot $acl
    $pfx = Join-Path $privateRoot 'service.pfx'
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $certificatePassword -AsPlainText -Force) | Out-Null
    $pfxAcl = Get-Acl $pfx
    $pfxAcl.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    Set-Acl $pfx $pfxAcl
    $hostOutput = Join-Path $privateRoot 'host'
    dotnet publish (Join-Path $project 'tools/PharmaLIMS.TrustedSigning.Host/PharmaLIMS.TrustedSigning.Host.csproj') --configuration Release --output $hostOutput
    if ($LASTEXITCODE -ne 0) { throw 'Signing host publish failed.' }
    # Grant logon-as-service only to the ephemeral local test account.
    $rights = Join-Path $privateRoot 'rights.inf'
    secedit.exe /export /cfg $rights /areas USER_RIGHTS /quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not export isolated runner service-logon policy.' }
    $policy = Get-Content $rights -Raw
    if ($policy -match '(?m)^SeServiceLogonRight[^\S\r\n]*=[^\r\n]*') {
        $policy = [regex]::Replace($policy,'(?m)^SeServiceLogonRight[^\S\r\n]*=[^\r\n]*',{ param($m) $m.Value + ",*$serviceSid" })
    } else { $policy = $policy.Replace('[Privilege Rights]',"[Privilege Rights]`r`nSeServiceLogonRight = *$serviceSid") }
    Set-Content -LiteralPath $rights -Value $policy -Encoding unicode
    secedit.exe /configure /db (Join-Path $privateRoot 'rights.sdb') /cfg $rights /areas USER_RIGHTS /quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant ephemeral service-logon right.' }
    if (Get-Service -Name 'PharmaLIMS.TrustedSigning' -ErrorAction SilentlyContinue) { throw 'Refusing to replace an existing signing service.' }
    $binary = '"' + (Join-Path $hostOutput 'PharmaLIMS.TrustedSigning.Host.exe') + '" --service --config "' + (Join-Path $privateRoot 'hostsettings.json') + '"'
    $credential = [PSCredential]::new("$env:COMPUTERNAME\$serviceName",(ConvertTo-SecureString $windowsPassword -AsPlainText -Force))
    New-Service -Name 'PharmaLIMS.TrustedSigning' -BinaryPathName $binary -Credential $credential -StartupType Manual | Out-Null
    $scmCreated = $true
    $fixture = Join-Path $privateRoot 'fixture.json'
    $settings = @{
        MasterConnectionString='Server=localhost,14335;Database=master;Integrated Security=true;Encrypt=Mandatory;TrustServerCertificate=false;Connection Timeout=30;Pooling=false'
        ServiceAccount=$serviceName;ServicePassword=$windowsPassword;ServiceSid=$serviceSid;CallerSid=$callerSid
        PrivateDirectory=$privateRoot;HostExePath=(Join-Path $hostOutput 'PharmaLIMS.TrustedSigning.Host.exe')
        CertificatePath=$pfx;CertificatePassword=$certificatePassword;CertificateSha256=[Convert]::ToHexString($cert.GetCertHash([System.Security.Cryptography.HashAlgorithmName]::SHA256))
        PipeName="PharmaLIMS_EQ001_UAT_$stamp";RepositoryRoot=$project;ResultsDirectory=$results;SettingsPath=$fixture
    }
    $settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fixture -Encoding utf8
    dotnet run --project (Join-Path $project 'tests/PharmaLIMS.TrustedSigning.WindowsUat/PharmaLIMS.TrustedSigning.WindowsUat.csproj') --configuration Release -- --settings $fixture 2>&1 | Tee-Object -FilePath (Join-Path $results 'windows-wpf-sql-uat.txt')
    if ($LASTEXITCODE -ne 0) { throw 'EQ-001 actual Windows/WPF/SQL UAT failed.' }
    $env:EQ001_SQL_TEST_SETTINGS = $fixture
    try {
        dotnet run --project (Join-Path $project 'tests/PharmaLIMS.WaterSqlRegression/PharmaLIMS.WaterSqlRegression.csproj') --configuration Release -- $project 2>&1 |
            Tee-Object -FilePath (Join-Path $results 'water-actual-schema-sql.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Actual-site Water SQL regression failed.' }
        & (Join-Path $project 'scripts/Invoke-Pr62ReviewRegressions.ps1') -SqlSettings $fixture 2>&1 |
            Tee-Object -FilePath (Join-Path $results 'pr62-review-sql-regressions.txt')
        if (-not $?) { throw 'PR62 disposable SQL regressions failed.' }
    }
    finally { Remove-Item Env:\EQ001_SQL_TEST_SETTINGS -ErrorAction SilentlyContinue }
    $outcome = Get-Content (Join-Path $results 'results.json') -Raw | ConvertFrom-Json
    if ($outcome.Passed -ne 122 -or $outcome.Commit -ne $env:GITHUB_SHA -or $outcome.BuildConfiguration -ne 'Release' -or $outcome.DesktopRuntime -ne 'Production' -or $outcome.ProductionActivated) { throw 'Incomplete, stale, or invalid Windows UAT outcome; all 122 checks must belong to this Release commit.' }
    Write-Host "EQ-001 Windows UAT completed with $($outcome.Passed) passing checks. Production remains inactive."
    Write-Host "::notice title=EQ001 Windows UAT verified::$($outcome.Passed) actual Windows/WPF/SQL checks passed; production inactive; sanitized evidence in eq001-windows-wpf-sql-uat artifact."
    $report = @(
        '# EQ-001 actual Windows / WPF / SQL UAT'
        ''
        "Validated commit: $($outcome.Commit)"
        "CI: https://github.com/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID"
        "Platform: $($outcome.Platform)"
        "Desktop: $($outcome.BuildConfiguration) build / $($outcome.DesktopRuntime) configuration; isolated UAT database only."
        "Result: $($outcome.Passed) checks passed. Production activation: false."
        ''
        'The Windows service and desktop caller use distinct real Windows accounts. SQL and UI fixtures are isolated and disposable; historical controls are synthetic UAT records.'
        ''
        ($outcome.Checks | ForEach-Object { "- PASS: $_" })
    ) -join "`n"
    $report | Set-Content -LiteralPath (Join-Path $results 'report.md') -Encoding utf8
    $report | Out-File -LiteralPath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
catch {
    Write-Host ("::error title=EQ001 UAT provisioning::" + $_.Exception.Message.Replace("`r", " ").Replace("`n", " "))
    throw
}
finally {
    if ($scmCreated) { Stop-Service -Name 'PharmaLIMS.TrustedSigning' -Force -ErrorAction SilentlyContinue; Remove-Service -Name 'PharmaLIMS.TrustedSigning' -ErrorAction SilentlyContinue }
    if ($serviceCreated) { Remove-LocalUser -Name $serviceName -ErrorAction SilentlyContinue }
    if ($cert) {
        Stop-Service -Name 'MSSQL$SQLEXPRESS' -Force -ErrorAction SilentlyContinue
        Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
        Remove-Item "Cert:\LocalMachine\Root\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
    }
    if (Test-Path $privateRoot) { Remove-Item $privateRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
