# EQ-001: read-only readiness assessment for a PREEXISTING Windows reviewer.
# Never creates users, changes passwords, edits permissions, or signs records.
$ErrorActionPreference = 'Stop'
$db = 'PharmaLIMS_Integration_2c941d953f70'
$candidate = 'AMEEN\ameen'
$localName = 'ameen'
Write-Output ('EQ-001 ISOLATED UAT READINESS: ' + $db)
Write-Output ('Candidate Windows reviewer: ' + $candidate)
$account = Get-LocalUser -Name $localName -ErrorAction Stop
if ($account.Enabled) { Write-Output 'PASS: Windows reviewer exists and is enabled.' }
else { Write-Output 'BLOCKED: Existing Windows reviewer is disabled.' }
$admins = @(Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop | ForEach-Object { $_.Name })
if ($admins -contains $candidate) { Write-Output 'BLOCKED: Windows candidate is an Administrator.' }
else { Write-Output 'PASS: Windows candidate is not a direct member of local Administrators.' }
$reviewers = @(Get-LocalGroupMember -Group 'PharmaLIMSUATReview' -ErrorAction Stop | ForEach-Object { $_.Name })
if ($reviewers -contains $candidate) { Write-Output 'PASS: Candidate belongs to isolated UAT review group.' }
else { Write-Output 'PENDING: Candidate is not in the isolated UAT review group.' }
$query = @'
SET NOCOUNT ON;
SELECT
 DB_NAME() AS DatabaseName,
 (SELECT COUNT_BIG(*) FROM dbo.Users WHERE Username=N'AMEEN\ameen' AND IsActive=1 AND
   ISNULL(MustChangePassword,0)=0 AND CanReviewResults=1 AND
   PasswordHashNew IS NOT NULL AND PasswordSalt IS NOT NULL AND PasswordChangedAt IS NOT NULL) AS ReadyUser,
 (SELECT COUNT_BIG(*) FROM sys.server_principals WHERE name=N'AMEEN\ameen' AND is_disabled=0) AS ReviewerSqlLogin,
 (SELECT COUNT_BIG(*) FROM sys.database_permissions dp
   JOIN sys.database_principals p ON dp.grantee_principal_id=p.principal_id
   WHERE p.name=N'PharmaLIMS_EquipmentApp' AND dp.major_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
    AND dp.permission_name=N'EXECUTE' AND dp.state=N'D') AS SharedSqlDenied,
 (SELECT COUNT_BIG(*) FROM sys.database_permissions dp
   JOIN sys.database_principals p ON dp.grantee_principal_id=p.principal_id
   WHERE p.name=N'PharmaLIMS_TrustedEquipmentSigner' AND dp.major_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
    AND dp.permission_name=N'EXECUTE' AND dp.state=N'G') AS ServiceGranted;
'@
$results = & sqlcmd -S 'localhost\SQLEXPRESS' -E -d $db -l 20 -W -s '|' -Q $query
if ($LASTEXITCODE -ne 0) { throw 'SQL readiness query failed: do not alter signing permissions.' }
$record = @($results | Where-Object { $_ -match '^PharmaLIMS_Integration_' } | Select-Object -Last 1)
if ($record.Count -ne 1) { throw 'Unexpected SQL readiness result; no changes were made.' }
$values = @($record[0].Split('|') | ForEach-Object { $_.Trim() })
if ($values.Count -ne 5 -or $values[0] -ne $db) { throw 'Unexpected readiness data.' }
$labels = @('Existing PharmaLIMS reviewer credential and rights','Existing Windows SQL reviewer login','Shared WPF signing permission denied','Trusted signing service permission granted')
for($i=1; $i -le 4; $i++) {
    $state = if ($values[$i] -eq '1') { 'PASS' } else { 'PENDING' }
    Write-Output ($state + ': ' + $labels[$i-1])
}
Write-Output 'READ-ONLY: No account, authentication, production database, role or signature record was modified.'
