# PharmaLIMS EQ-001 - explicitly authorized LOCAL UAT reviewer enrollment.
# Execute interactively as Windows Administrator on this PC ONLY.
# No new Windows account, no changes to Elite, production, signature history or SQL signing permissions.
# Password is entered locally as a SecureString and NEVER passed in command arguments or logs.
$ErrorActionPreference = 'Stop'
$database = 'PharmaLIMS_Integration_2c941d953f70'
$username = 'AMEEN\ameen'
$windowsAccount = Get-LocalUser -Name 'ameen' -ErrorAction Stop
if (-not $windowsAccount.Enabled) { throw 'Existing Windows reviewer is disabled.' }
$operator = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $operator.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script locally in an elevated PowerShell window. No changes made.'
}
if ($operator.Identity.Name -ne 'AMEEN\Elite') {
    throw 'Expected designated UAT enrollment administrator AMEEN\Elite. No changes made.'
}
$admins = @(Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop)
if (@($admins | Where-Object { $_.SID.Value -eq $windowsAccount.SID.Value }).Count) {
    throw 'The selected reviewer belongs to local Administrators. No changes made.'
}
$reviewGroup = Get-LocalGroup -Name 'PharmaLIMSUATReview' -ErrorAction Stop
$approveGroup = Get-LocalGroup -Name 'PharmaLIMSUATApprove' -ErrorAction Stop
if ($reviewGroup.SID.Value -ne 'S-1-5-21-3887410152-3473494482-1737934645-1010' -or
    $approveGroup.SID.Value -ne 'S-1-5-21-3887410152-3473494482-1737934645-1011') {
    throw 'UAT security group identities changed. No changes made.'
}
if (@(Get-LocalGroupMember -Group 'PharmaLIMSUATApprove').Count -ne 0) {
    throw 'UAT approval group must remain empty. No changes made.'
}
$connectionString = 'Server=localhost\SQLEXPRESS;Database=' + $database +
    ';Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=20;'
$connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
try {
    $connection.Open()
    if ($connection.Database -ne $database) { throw 'Unexpected database; no changes made.' }
    $check = $connection.CreateCommand()
    $check.CommandText = @'
SELECT
 (SELECT COUNT_BIG(*) FROM dbo.Users WHERE Username=N'AMEEN\ameen') AS ExistingUser,
 (SELECT COUNT_BIG(*) FROM sys.database_principals WHERE name=N'PharmaLIMS_TrustedEquipmentSigner') AS TrustedSigner;
'@
    $r = $check.ExecuteReader()
    try {
        if (-not $r.Read()) { throw 'UAT preflight yielded no record.' }
        if ($r.GetInt64(0) -ne 0) { throw 'PharmaLIMS user already exists. Review it in User Administration; do not overwrite.' }
        if ($r.GetInt64(1) -ne 1) { throw 'Dedicated trusted signer is not provisioned.' }
    } finally { $r.Close() }
    Write-Host 'Preflight passed: existing non-admin Windows reviewer; no PharmaLIMS UAT mapping exists.'
    Write-Host 'This action creates only a UAT Supervisor account with CanReviewResults=1 and CanApproveResults=0.'
    Write-Host 'The account must change its initial PharmaLIMS password on first login.'
    $confirmation = Read-Host 'Type ENROLL to authorize this isolated UAT-only account creation'
    if ($confirmation -cne 'ENROLL') { throw 'Cancelled. No changes made.' }
    $secure = Read-Host 'Choose a NEW temporary PharmaLIMS UAT password (not the Windows password)' -AsSecureString
    $confirmSecure = Read-Host 'Re-enter that temporary password' -AsSecureString
    $password = $null
    $confirmationPassword = $null
    $bstr = [IntPtr]::Zero
    $confirmBstr = [IntPtr]::Zero
    try {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        $confirmBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirmSecure)
        $confirmationPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($confirmBstr)
        if ($password -cne $confirmationPassword) { throw 'Temporary passwords do not match. No changes made.' }
        if ($password.Length -lt 12 -or $password -cnotmatch '[A-Z]' -or
            $password -cnotmatch '[a-z]' -or $password -notmatch '[0-9]' -or
            $password -notmatch '[^\p{L}\p{N}]' -or
            $password.IndexOf($username,[StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw 'Password must be 12+ characters with upper, lower, number and special character. No changes made.'
        }
        $salt = [Security.Cryptography.RandomNumberGenerator]::Create()
        $saltBytes = New-Object byte[] 32
        try { $salt.GetBytes($saltBytes) } finally { $salt.Dispose() }
        $pbkdf2 = [Security.Cryptography.Rfc2898DeriveBytes]::new(
            $password,$saltBytes,600000,[Security.Cryptography.HashAlgorithmName]::SHA256)
        try { $hashBase64 = [Convert]::ToBase64String($pbkdf2.GetBytes(32)) }
        finally { $pbkdf2.Dispose() }
        $saltBase64 = [Convert]::ToBase64String($saltBytes)
        [Array]::Clear($saltBytes,0,$saltBytes.Length)
    }
    finally {
        $password = $null
        $confirmationPassword = $null
        if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        if ($confirmBstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($confirmBstr) }
        $secure.Dispose()
        $confirmSecure.Dispose()
    }
    $transaction = $connection.BeginTransaction([System.Data.IsolationLevel]::Serializable)
    try {
        $create = $connection.CreateCommand()
        $create.Transaction = $transaction
        $create.CommandText = @'
IF DB_NAME() <> N'PharmaLIMS_Integration_2c941d953f70'
    THROW 56901, 'Not approved isolated EQ-001 UAT.', 1;
IF EXISTS(SELECT 1 FROM dbo.Users WITH (UPDLOCK,HOLDLOCK) WHERE Username=@Username)
    THROW 56902, 'UAT reviewer already exists: refusing to overwrite credentials.', 1;
INSERT dbo.Users(
  Username,PasswordHash,PasswordHashNew,PasswordSalt,FullName,
  Role,Department,Section,IsActive,MustChangePassword,PasswordChangedAt,
  CanReviewResults,CanApproveResults,CanManageUsers,CanManageSettings
) VALUES(
  @Username,N'[MIGRATED]',@PasswordHash,@PasswordSalt,N'EQ-001 Existing Windows UAT Reviewer',
  N'Supervisor',N'Microbiology',N'UAT',1,1,NULL,1,0,0,0
);
DECLARE @NewId int=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.AuditTrail(
  TableName,RecordID,Action,ModuleName,OldValue,NewValue,Reason,
  ChangedBy,PerformedBy,ChangeDate,CreatedUtc,UtcRecordedAt,SourceWorkstation,SourceApplication
) VALUES(
  N'Users',@NewId,N'User Account Created',N'User Management',
  N'',N'Existing Windows UAT reviewer; temporary credential must be changed',
  N'Controlled EQ-001 isolated UAT account enrollment; no signing performed',
  ORIGINAL_LOGIN(),ORIGINAL_LOGIN(),SYSUTCDATETIME(),SYSUTCDATETIME(),
  SYSUTCDATETIME(),HOST_NAME(),N'PharmaLIMS EQ001 UAT Enrollment'
);
SELECT @NewId;
'@
        [void]$create.Parameters.Add('@Username',[Data.SqlDbType]::NVarChar,100)
        $create.Parameters['@Username'].Value=$username
        [void]$create.Parameters.Add('@PasswordHash',[Data.SqlDbType]::NVarChar,512)
        $create.Parameters['@PasswordHash'].Value=$hashBase64
        [void]$create.Parameters.Add('@PasswordSalt',[Data.SqlDbType]::NVarChar,256)
        $create.Parameters['@PasswordSalt'].Value=$saltBase64
        $id=$create.ExecuteScalar()
        if ([int]$id -le 0) { throw 'Expected UAT reviewer record was not created.' }
        $transaction.Commit()
        Write-Host ('PASS: UAT PharmaLIMS reviewer provisioned with audited initial credential. UserID=' + $id)
    }
    catch {
        try { $transaction.Rollback() } catch { }
        throw
    }
    finally {
        $hashBase64 = $null
        $saltBase64 = $null
        $transaction.Dispose()
    }
    $members = @(Get-LocalGroupMember -Group 'PharmaLIMSUATReview')
    if (-not @($members | Where-Object { $_.SID.Value -eq $windowsAccount.SID.Value }).Count) {
        Add-LocalGroupMember -Group 'PharmaLIMSUATReview' -Member $windowsAccount.SID.Value -ErrorAction Stop
    }
    Write-Host 'PASS: Existing Windows reviewer belongs to isolated UAT REVIEW group only.'
    Write-Host 'NEXT: sign into Windows as AMEEN\ameen; change the temporary PharmaLIMS password through normal login.'
    Write-Host 'WARNING: No SQL permission cutover or electronic signature has been performed.'
}
finally { $connection.Dispose() }
