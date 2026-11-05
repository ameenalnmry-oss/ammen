# Isolated EQ-001 UAT only: provision Windows authorization groups.
# This does not authorize production or alter PharmaLIMS data.
$ErrorActionPreference = 'Stop'
$admin = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Elevated Windows administrator required. No changes made.'
}
$service = Get-LocalUser -Name 'PharmaLIMSSigner' -ErrorAction Stop
if (-not $service.Enabled) { throw 'Dedicated Windows service identity is disabled.' }
$review = 'PharmaLIMSUATReview'
$approve = 'PharmaLIMSUATApprove'
$reviewer = "$env:COMPUTERNAME\$env:USERNAME"
$answer = Read-Host "Create isolated reviewer/approver groups and add $reviewer as REVIEWER ONLY? Type UAT"
if ($answer -cne 'UAT') { throw 'Declined. No changes made.' }
if (-not (Get-LocalGroup -Name $review -ErrorAction SilentlyContinue)) {
    New-LocalGroup -Name $review -Description 'LIMS UAT reviewers' | Out-Null
}
if (-not (Get-LocalGroup -Name $approve -ErrorAction SilentlyContinue)) {
    New-LocalGroup -Name $approve -Description 'LIMS UAT approvers' | Out-Null
}
$reviewerSid = ([Security.Principal.NTAccount]$reviewer).Translate([Security.Principal.SecurityIdentifier]).Value
if (-not (Get-LocalGroupMember -Group $review -ErrorAction Stop | Where-Object {$_.SID.Value -eq $reviewerSid})) {
    Add-LocalGroupMember -Group $review -Member $reviewer
}
Write-Host ('ReviewGroupSID: ' + (Get-LocalGroup -Name $review).SID.Value)
Write-Host ('ApproveGroupSID: ' + (Get-LocalGroup -Name $approve).SID.Value)
Write-Host 'PASS: isolated groups configured; approver group remains empty.'
Write-Host 'IMPORTANT: Windows group membership changes may require the reviewer to sign out and in before Windows authentication picks them up.'
Write-Host 'No Windows service started; SQL permission cutover remains blocked until UAT is validated.'
