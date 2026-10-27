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
try {
    & (Join-Path $project 'tests/Test-Eq001SourceInstaller.ps1') 2>&1 |
        Tee-Object -FilePath (Join-Path $results 'source-installer-checks.txt')
} catch {
    $details = ($_.Exception.Message + '; ' + $_.ScriptStackTrace).Replace("`r",' ').Replace("`n",' ')
    Add-Content -LiteralPath (Join-Path $results 'source-installer-checks.txt') -Value $details
    Write-Host "::error title=EQ001 source installer qualification::$details"
    throw
}
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
    if (-not (Get-Service -Name 'MSSQL$SQLEXPRESS' -ErrorAction SilentlyContinue)) {
        Write-Host 'Installing SQL Server Express only on the fresh disposable CI runner.'
        choco install sql-server-express -y --no-progress --execution-timeout=1800
        if ($LASTEXITCODE -notin @(0,1641,3010)) { throw 'SQL Server Express installation failed.' }
    }
    $sqlService = Get-Service -Name 'MSSQL$SQLEXPRESS'
    $instanceId = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL').SQLEXPRESS
    if ([string]::IsNullOrWhiteSpace($instanceId)) { throw 'Isolated SQL Express instance was not registered.' }
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
