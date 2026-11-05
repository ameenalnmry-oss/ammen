# EQ-001 isolated UAT prerequisite. Run intentionally from an elevated PowerShell.
# This does not start a service, modify approved records, or cut over SQL roles.
# Requires the Windows/SQL administrator to approve the service-account creation.
$ErrorActionPreference = 'Stop'
$principalName = 'PharmaLIMSSigner'
$description = 'Medica LIMS UAT signer'
if ($principalName.Length -gt 20) { throw 'Windows service-account name exceeds 20 characters.' }
if ($description.Length -gt 48) { throw 'Windows account description exceeds 48 characters.' }
$computer = $env:COMPUTERNAME
$login = "$computer\$principalName"
$uat = 'PharmaLIMS_Integration_2c941d953f70'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Elevated Windows Administrator session required. No changes made.'
}
if (Get-LocalUser -Name $principalName -ErrorAction SilentlyContinue) {
    throw 'Signing account already exists: reconcile its identity and rights manually.'
}
$answer = Read-Host "Create dedicated Windows signing account $login for isolated UAT only? Type CREATE"
if ($answer -cne 'CREATE') { throw 'No changes made: confirmation declined.' }
$secret = Read-Host -AsSecureString 'Enter a new dedicated strong service-account password'
if (-not $secret -or $secret.Length -lt 14) { throw 'Password must contain at least 14 characters.' }
New-LocalUser -Name $principalName -Password $secret -Description $description -PasswordNeverExpires:$false | Out-Null
Write-Host "Windows account created: $login"
Write-Host 'SQL login creation, service logon rights, group SID policy and service deployment require separate controlled checks.'
Write-Host 'Do not run permission cutover until authenticated end-to-end UAT passes.'
