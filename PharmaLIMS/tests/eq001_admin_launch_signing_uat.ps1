# Launch ONLY a local UAT signing host with a dedicated Windows identity.
# Requires authorized Administrator; asks securely for existing service account
# password. Does not change SQL roles, touch production, or create another project.
param([switch]$Rehearsal)
$ErrorActionPreference = 'Stop'
$admin = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Ask Windows for a genuine UAC elevation; do not bypass access controls.
    if (-not $PSCommandPath) { throw 'Cannot identify the script to request elevation.' }
    Write-Host 'Administrator elevation is required. Requesting Windows UAC consent.'
    $selfArgs='-NoExit -NoProfile -ExecutionPolicy RemoteSigned -File "' + $PSCommandPath + '"'
    if ($Rehearsal) { $selfArgs+=' -Rehearsal' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $selfArgs -ErrorAction Stop | Out-Null
    return
}
$account='AMEEN\PharmaLIMSSigner'
$service=Get-LocalUser -Name 'PharmaLIMSSigner' -ErrorAction Stop
if (-not $service.Enabled) { throw 'UAT signing Windows identity is disabled.' }
$reviewGroup=Get-LocalGroup -Name 'PharmaLIMSUATReview' -ErrorAction Stop
$approveGroup=Get-LocalGroup -Name 'PharmaLIMSUATApprove' -ErrorAction Stop
if ($reviewGroup.SID.Value -ne 'S-1-5-21-3887410152-3473494482-1737934645-1010' -or
    $approveGroup.SID.Value -ne 'S-1-5-21-3887410152-3473494482-1737934645-1011') {
    throw 'UAT authorization group SIDs have changed. No changes made.'
}
# Eligible approvers may be enrolled for the actual review/approval UAT.
# Service exclusion uses its effective token in the credential-launched child.
$root='E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS'
$hostScript=Join-Path $root 'tests\eq001_run_signing_host_uat.ps1'
$dll=Join-Path $root 'tools\PharmaLIMS.TrustedSigning\bin\Debug\net8.0-windows\PharmaLIMS.TrustedSigning.dll'
if (-not (Test-Path -LiteralPath $hostScript) -or -not (Test-Path -LiteralPath $dll)) {
    throw 'Required UAT signer service code has not been built.'
}
$listener=Get-NetTCPConnection -LocalPort 51847 -State Listen -ErrorAction SilentlyContinue
if ($listener) { throw 'Signing port 51847 is occupied. No URL reservation changed.' }
$url='http://localhost:51847/'
$aclOutput = & netsh.exe http show urlacl "url=$url" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw 'HTTP.sys URL ACL inspection failed. Nothing was changed.'
}
$aclText = $aclOutput -join [Environment]::NewLine
# netsh may return exit code 0 even when it prints only the empty
# "URL Reservations" heading. Match the actual reservation URL.
$escapedUrl = [regex]::Escape($url)
$hasReservation = [regex]::IsMatch($aclText, '(?im)^\s*Reserved\s+URL\s*:\s*' + $escapedUrl + '\s*$')
if ($hasReservation) {
    if ($aclText.IndexOf($account,[StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'HTTP URL namespace is reserved for a different identity. No changes made.'
    }
    Write-Host 'Existing HTTP.sys namespace reservation belongs to the UAT signer.'
} elseif ($aclText -match '(?im)^\s*Reserved\s+URL\s*:') {
    throw 'HTTP.sys returned an unexpected URL reservation. Manual inspection required.'
} else {
    Write-Host 'No existing localhost signer URL reservation found.'
    Write-Host 'Reserving localhost HTTP.sys namespace for the separate UAT signer account.'
    & netsh.exe http add urlacl "url=$url" "user=$account" 'listen=yes'
    if ($LASTEXITCODE -ne 0) { throw 'HTTP.sys URL ACL creation failed. Service was not started.' }
    $verifyText = (& netsh.exe http show urlacl "url=$url" 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or
        -not [regex]::IsMatch($verifyText, '(?im)^\s*Reserved\s+URL\s*:\s*' + $escapedUrl + '\s*$') -or
        $verifyText.IndexOf($account,[StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'HTTP.sys reservation verification failed. Inspect the reservation before retrying.'
    }
    Write-Host 'PASS: HTTP.sys signer reservation verified.'
}
$credentials=Get-Credential -UserName $account -Message 'Enter the password of the EXISTING UAT signer Windows account'
if (-not $credentials) { throw 'Credential prompt cancelled. No signer host was started.' }
$exe=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
# Apply a process-only policy permitting locally authored scripts. This never
# changes LocalMachine/CurrentUser policy and cannot override enforced GPO.
$hostArgs='-NoProfile -ExecutionPolicy RemoteSigned -File "' + $hostScript + '"'
if ($Rehearsal) { $hostArgs+=' -Rehearsal' }
# Capture bootstrap errors in the administrator's own protected profile.
# NEVER create a folder in the signer's profile from the admin account:
# the resulting directory owner could prevent signer-side logging.
$diagnosticRoot=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PharmaLIMS-EQ001-UAT\AdminLaunch'
if (-not (Test-Path -LiteralPath $diagnosticRoot)) {
    New-Item -ItemType Directory -Path $diagnosticRoot -Force -ErrorAction Stop | Out-Null
}
$runTag=Get-Date -Format 'yyyyMMdd-HHmmss'
$stdout=Join-Path $diagnosticRoot ("launch_stdout_$runTag.log")
$stderr=Join-Path $diagnosticRoot ("launch_stderr_$runTag.log")
Write-Host ('EQ-001 isolated UAT startup diagnostics: ' + $diagnosticRoot)
$process=Start-Process -FilePath $exe -ArgumentList $hostArgs -Credential $credentials -LoadUserProfile -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
Start-Sleep -Seconds 4
if ($process.HasExited) {
    $exitCode=$process.ExitCode
    Write-Host ('EQ-001 UAT signer exited. PID=' + $process.Id + ', ExitCode=' + $exitCode)
    foreach ($diagnostic in @($stderr,$stdout)) {
        if (Test-Path -LiteralPath $diagnostic) {
            Write-Host ('---- UAT DIAGNOSTIC: ' + [IO.Path]::GetFileName($diagnostic) + ' ----')
            Get-Content -LiteralPath $diagnostic -Tail 40
        }
    }
    if (-not (Test-Path -LiteralPath $stderr) -and -not (Test-Path -LiteralPath $stdout)) {
        Write-Warning ('Startup diagnostics unavailable: ' + $diagnosticRoot)
    }
    throw 'UAT signer startup failed. Review diagnostic output above. No permission cutover was made.'
}
$activeListener=Get-NetTCPConnection -LocalPort 51847 -State Listen -ErrorAction SilentlyContinue
if (-not $activeListener) {
    Write-Warning 'UAT signer process is still running, but port 51847 is not yet listening.'
    Write-Host ('Inspect diagnostic files: ' + $diagnosticRoot)
    Write-Host 'Do not treat the service as ready or perform permission cutover.'
    return
}
Write-Host ('PASS: UAT signer host process alive and localhost signing port listening. PID: ' + $process.Id)
Write-Host 'Use separate eligible reviewer and approver Windows logon sessions for functional UAT.'
Write-Host 'Listener startup is not UAT acceptance. No SQL permission cutover was executed.'
