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
