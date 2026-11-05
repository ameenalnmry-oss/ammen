-- v309: least-privilege authentication bridge for trusted equipment-activity signing.
-- The dedicated Windows service can reauthenticate ONLY the caller-bound PharmaLIMS account
-- through owner-executed procedures. No direct credential-table SELECT/UPDATE is required.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.Users',N'U') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage',N'P') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentSignerEligibility',N'V') IS NULL
 THROW 56840,'Trusted activity authentication prerequisites are missing.',1;

EXEC(N'CREATE OR ALTER PROCEDURE dbo.MicroEquipment_ReadActivitySignerCredential
 @Actor nvarchar(100)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON;
 IF NOT (EXISTS (
      SELECT 1 FROM sys.database_principals p
      WHERE p.name=N''PharmaLIMS_TrustedEquipmentSigner'' AND p.type=N''U''
        AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
    AND ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)=0)
  THROW 56841,''Activity reauthentication requires the dedicated trusted Windows service origin.'',1;

 IF @Actor IS NULL OR LEN(LTRIM(RTRIM(@Actor)))=0 OR LEN(@Actor)>100
  THROW 56842,''Invalid activity signer identity.'',1;

 SELECT UserID,Username,PasswordHashNew,PasswordSalt,
        CONVERT(bit,ISNULL(IsActive,1)) AS IsActive,
        CONVERT(bit,CASE WHEN ISNULL(IsLocked,0)=1
             AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()) THEN 1 ELSE 0 END) AS IsLocked,
        CONVERT(bit,ISNULL(MustChangePassword,0)) AS MustChangePassword,
        CONVERT(bit,ISNULL(CanReviewResults,0)) AS CanReviewResults,
        CONVERT(bit,ISNULL(CanApproveResults,0)) AS CanApproveResults,
        AuthenticationRowVersion
 FROM dbo.Users WITH(HOLDLOCK)
 WHERE Username=@Actor
   AND ISNULL(IsActive,1)=1
   AND ISNULL(MustChangePassword,0)=0
   AND PasswordChangedAt IS NOT NULL
   AND NULLIF(PasswordHashNew,N'''') IS NOT NULL
   AND NULLIF(PasswordSalt,N'''') IS NOT NULL
   AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));
END;');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.MicroEquipment_RecordActivityAuthFailure
 @UserID int,
 @Actor nvarchar(100),
 @VerifiedHash nvarchar(512),
 @VerifiedSalt nvarchar(256)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON;
 IF NOT (EXISTS (
      SELECT 1 FROM sys.database_principals p
      WHERE p.name=N''PharmaLIMS_TrustedEquipmentSigner'' AND p.type=N''U''
        AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
    AND ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)=0)
  THROW 56841,''Activity reauthentication requires the dedicated trusted Windows service origin.'',1;

 UPDATE dbo.Users SET
   FailedLoginAttempts=CASE
      WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN 1
      ELSE ISNULL(FailedLoginAttempts,0)+1 END,
   IsLocked=CASE
      WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN 0
      WHEN ISNULL(FailedLoginAttempts,0)+1>=5 THEN 1
      ELSE 0 END,
   LockedUntil=CASE
      WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN NULL
      WHEN ISNULL(FailedLoginAttempts,0)+1>=5 THEN DATEADD(MINUTE,15,SYSDATETIME())
      ELSE LockedUntil END
 WHERE UserID=@UserID AND Username=@Actor
   AND ISNULL(IsActive,1)=1
   AND ISNULL(MustChangePassword,0)=0
   AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))
   AND CONVERT(varbinary(max),ISNULL(PasswordHashNew,N''''))=CONVERT(varbinary(max),@VerifiedHash)
   AND CONVERT(varbinary(max),ISNULL(PasswordSalt,N''''))=CONVERT(varbinary(max),@VerifiedSalt);
END;');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.MicroEquipment_CommitActivityAuthSuccess
 @UserID int,
 @Actor nvarchar(100),
 @ExpectedVersion binary(8)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON;
 IF NOT (EXISTS (
      SELECT 1 FROM sys.database_principals p
      WHERE p.name=N''PharmaLIMS_TrustedEquipmentSigner'' AND p.type=N''U''
        AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
    AND ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)=0)
  THROW 56841,''Activity reauthentication requires the dedicated trusted Windows service origin.'',1;

 DECLARE @Committed TABLE(AuthenticationRowVersion binary(8) NOT NULL);
 UPDATE dbo.Users
 SET FailedLoginAttempts=0,IsLocked=0,LockedUntil=NULL
 OUTPUT inserted.AuthenticationRowVersion INTO @Committed
 WHERE UserID=@UserID AND Username=@Actor
   AND AuthenticationRowVersion=@ExpectedVersion
   AND ISNULL(IsActive,1)=1
   AND ISNULL(MustChangePassword,0)=0
   AND PasswordChangedAt IS NOT NULL
   AND NULLIF(PasswordHashNew,N'''') IS NOT NULL
   AND NULLIF(PasswordSalt,N'''') IS NOT NULL
   AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));
 SELECT AuthenticationRowVersion FROM @Committed;
END;');

IF USER_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NOT NULL
BEGIN
 GRANT EXECUTE ON dbo.MicroEquipment_ReadActivitySignerCredential TO PharmaLIMS_TrustedEquipmentSigner;
 GRANT EXECUTE ON dbo.MicroEquipment_RecordActivityAuthFailure TO PharmaLIMS_TrustedEquipmentSigner;
 GRANT EXECUTE ON dbo.MicroEquipment_CommitActivityAuthSuccess TO PharmaLIMS_TrustedEquipmentSigner;

 -- v308 temporarily granted broad dbo.Users access to make activity authentication work.
 -- v309 removes that broad access; the three owner-executed procedures above are the only
 -- credential/authentication bridge for the dedicated activity-signing service.
 DENY SELECT ON OBJECT::dbo.Users (PasswordHash,PasswordHashNew,PasswordSalt) TO PharmaLIMS_TrustedEquipmentSigner;
 DENY UPDATE ON OBJECT::dbo.Users TO PharmaLIMS_TrustedEquipmentSigner;
END;
