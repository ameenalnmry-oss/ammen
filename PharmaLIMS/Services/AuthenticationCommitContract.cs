namespace PharmaLIMS.Services
{
    /// <summary>One compare-and-swap covers credential upgrade and success accounting.</summary>
    internal static class AuthenticationCommitContract
    {
        internal const string Sql = @"
DECLARE @Committed TABLE (AuthenticationRowVersion binary(8) NOT NULL);
UPDATE dbo.Users
SET LastLogin = CASE WHEN @IsLogin=1 THEN SYSDATETIME() ELSE LastLogin END,
    FailedLoginAttempts = 0,
    IsLocked = 0,
    LockedUntil = NULL,
    PasswordHashNew = CASE WHEN @Upgrade=1 THEN @PasswordHashNew ELSE PasswordHashNew END,
    PasswordSalt = CASE WHEN @Upgrade=1 THEN @PasswordSalt ELSE PasswordSalt END,
    PasswordHash = CASE WHEN @Upgrade=1 THEN N'[MIGRATED]' ELSE PasswordHash END
OUTPUT inserted.AuthenticationRowVersion INTO @Committed
WHERE UserID=@UserID AND Username=@Username
  AND AuthenticationRowVersion=@ExpectedVersion
  AND ISNULL(IsActive,1)=1
  AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));
SELECT AuthenticationRowVersion FROM @Committed;";

        // Shared with the SQL regression: the credential compare-and-swap is
        // unchanged. AuthService must verify the current password before calling it.
        internal const string PasswordChangeSql = @"
UPDATE dbo.Users
SET PasswordHash = N'[MIGRATED]',
    PasswordHashNew = @PasswordHashNew,
    PasswordSalt = @PasswordSalt,
    MustChangePassword = 0,
    PasswordChangedAt = SYSUTCDATETIME(),
    FailedLoginAttempts = 0,
    IsLocked = 0,
    LockedUntil = NULL,
    UpdatedAt = SYSUTCDATETIME()
OUTPUT inserted.AuthenticationRowVersion, inserted.PasswordChangedAt
WHERE UserID = @UserID
  AND Username = @Username
  AND AuthenticationRowVersion = @ExpectedVersion
  AND ISNULL(IsActive,1)=1
  AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));";

        // Only the account updated in this transaction can own this audit event.
        // No caller-selected audit actor and no dependency on a workspace session.
        internal const string PasswordChangeAuditSql = @"
INSERT dbo.AuditTrail
(
    TableName, RecordID, Action, ModuleName, OldValue, NewValue, Reason,
    ChangedBy, PerformedBy, ChangeDate, CreatedUtc, UtcRecordedAt,
    SourceWorkstation, ComputerName, SourceApplication
)
SELECT N'Users', account.UserID, N'Password Changed', N'Authentication',
       N'Credential retained securely; value not exposed',
       N'Personal credential replaced securely; value not exposed',
       N'Authenticated self-service password change',
       account.Username, account.Username, GETDATE(), SYSUTCDATETIME(), SYSUTCDATETIME(),
       @Workstation, @Workstation, N'PharmaLIMS'
FROM dbo.Users account WITH (HOLDLOCK)
WHERE account.UserID = @UserID
  AND account.Username = @Username
  AND account.AuthenticationRowVersion = @CommittedVersion
  AND account.MustChangePassword = 0
  AND account.PasswordChangedAt IS NOT NULL
  AND ISNULL(account.IsActive,1)=1
  AND NOT (ISNULL(account.IsLocked,0)=1 AND (account.LockedUntil IS NULL OR account.LockedUntil>SYSDATETIME()));
IF @@ROWCOUNT <> 1
    THROW 55221, 'Password-change audit could not be bound to the updated account.', 1;";

        // Bind a failed verification to the credentials actually checked. Do not
        // compare AuthenticationRowVersion here: another genuine failure changes
        // it, and must not cause this attempt to be silently discarded. Active
        // administrative/timed locks are never weakened or extended by this path.
        internal const string FailureSql = @"
UPDATE dbo.Users SET
    FailedLoginAttempts = CASE
        WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN 1
        ELSE ISNULL(FailedLoginAttempts,0)+1 END,
    IsLocked = CASE
        WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN 0
        WHEN ISNULL(FailedLoginAttempts,0)+1>=5 THEN 1
        ELSE 0 END,
    LockedUntil = CASE
        WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN NULL
        WHEN ISNULL(FailedLoginAttempts,0)+1>=5 THEN DATEADD(MINUTE,15,SYSDATETIME())
        ELSE LockedUntil END
WHERE UserID=@VerifiedUserID AND Username=@Username AND ISNULL(IsActive,1)=1
  AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))
  AND CONVERT(varbinary(max),ISNULL(PasswordHash,N''))=CONVERT(varbinary(max),@VerifiedLegacyHash)
  AND CONVERT(varbinary(max),ISNULL(PasswordHashNew,N''))=CONVERT(varbinary(max),@VerifiedHash)
  AND CONVERT(varbinary(max),ISNULL(PasswordSalt,N''))=CONVERT(varbinary(max),@VerifiedSalt);";
    }
}
