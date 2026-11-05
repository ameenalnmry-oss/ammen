-- Controlled ISOLATED WINDOWS UAT support for already-initialized signers only.
-- No password replacement/upgrade, user administration, business-data write,
-- SQL authentication-mode change, signature acceptance, or signing permission cutover.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME()<>N'PharmaLIMS_UAT_20261001'
 AND DB_NAME() NOT LIKE N'PharmaLIMS_Integration[_]%'
 AND DB_NAME() NOT LIKE N'PharmaLIMS_UAT_CLEAN[_]%'
 THROW 56870,'Windows signing support is restricted to isolated UAT.',1;
IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin'),0)<>1
 THROW 56871,'An existing maintenance administrator must provision UAT support.',1;
IF OBJECT_ID(N'dbo.MicroEquipmentSignerEligibility',N'V') IS NULL
 OR DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentApp') IS NULL
 THROW 56872,'Apply the canonical equipment signing prerequisites first.',1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat') IS NULL
 CREATE ROLE PharmaLIMS_EquipmentWindowsUat;
IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'PharmaLIMS_EquipmentWindowsUat' AND type=N'R')
 THROW 56878,'Restricted runtime principal is not a database role.',1;


EXEC(N'CREATE OR ALTER PROCEDURE dbo.EQ001_WindowsUat_AssertActor
 @Username nvarchar(256)=NULL, @UserID int=NULL,
 @Actor nvarchar(256) OUTPUT, @ActorID int OUTPUT
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 SET @Actor=ORIGINAL_LOGIN(); SET @ActorID=NULL;
 IF DB_NAME()<>N''PharmaLIMS_UAT_20261001''
  AND DB_NAME() NOT LIKE N''PharmaLIMS_Integration[_]%''
  AND DB_NAME() NOT LIKE N''PharmaLIMS_UAT_CLEAN[_]%''
  THROW 56873,''Windows signing authentication is restricted to isolated UAT.'',1;
 DECLARE @Principal sysname;
 SELECT @Principal=p.name FROM sys.database_principals p
 WHERE p.type=N''U'' AND p.sid=SUSER_SID(ORIGINAL_LOGIN());
 IF @Principal IS NULL OR ISNULL(IS_ROLEMEMBER(N''PharmaLIMS_EquipmentWindowsUat'',@Principal),0)<>1
  OR ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)<>0
  OR LEN(@Actor)>100 OR CHARINDEX(N''\'',@Actor)=0
  THROW 56874,''An independently authenticated restricted Windows principal is required.'',1;
 IF @Username IS NOT NULL AND @Username<>@Actor
  THROW 56875,''Cross-account authentication is forbidden.'',1;
 IF (SELECT COUNT_BIG(*) FROM dbo.Users WHERE Username=@Actor)<>1
  THROW 56876,''An exact existing PharmaLIMS Windows identity is required.'',1;
 SELECT @ActorID=UserID FROM dbo.Users WHERE Username=@Actor;
 IF @UserID IS NOT NULL AND @UserID<>@ActorID
  THROW 56875,''Cross-account authentication is forbidden.'',1;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.EQ001_WindowsUat_ReadOwnUser
 @Username nvarchar(256)=NULL, @UserID int=NULL
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @Actor nvarchar(256),@ActorID int;
 EXEC dbo.EQ001_WindowsUat_AssertActor @Username,@UserID,@Actor OUTPUT,@ActorID OUTPUT;
SELECT UserID, Username, FullName, Role, Department, Section,
                    PasswordHash, PasswordHashNew, PasswordSalt, AuthenticationRowVersion,
                    IsActive, LastLogin, FailedLoginAttempts,
                    CONVERT(bit, CASE
                        WHEN ISNULL(IsLocked, 0) = 1
                         AND (LockedUntil IS NULL OR LockedUntil > SYSDATETIME()) THEN 1
                        ELSE 0
                    END) AS IsLocked,
                    LockedUntil,
                    MustChangePassword, PasswordChangedAt,
                    CanAccessWater, CanAccessEM, CanRegisterSamples, CanEnterResults,
                    CanReviewResults, CanApproveResults, CanIssueCOA, CanCancelCOA,
                    CanAccessReports, CanManageUsers, CanManageSettings FROM Users WHERE UserID = @ActorID AND Username = @Actor
 AND IsActive=1 AND MustChangePassword=0 AND PasswordChangedAt IS NOT NULL
 AND NULLIF(PasswordHashNew,N'''') IS NOT NULL AND NULLIF(PasswordSalt,N'''') IS NOT NULL
 AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));
END;');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.EQ001_WindowsUat_CommitSuccess
 @UserID int,@Username nvarchar(256),@ExpectedVersion binary(8),@IsLogin bit
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT ON;
 DECLARE @Actor nvarchar(256),@ActorID int;
 EXEC dbo.EQ001_WindowsUat_AssertActor @Username,@UserID,@Actor OUTPUT,@ActorID OUTPUT;
DECLARE @Committed TABLE (AuthenticationRowVersion binary(8) NOT NULL);
UPDATE dbo.Users
SET LastLogin=CASE WHEN @IsLogin=1 THEN SYSDATETIME() ELSE LastLogin END,
    FailedLoginAttempts=0, IsLocked=0, LockedUntil=NULL
OUTPUT inserted.AuthenticationRowVersion INTO @Committed
WHERE UserID=@ActorID AND Username=@Actor
 AND AuthenticationRowVersion=@ExpectedVersion AND ISNULL(IsActive,1)=1 AND MustChangePassword=0 AND PasswordChangedAt IS NOT NULL
 AND NULLIF(PasswordHashNew,N'''') IS NOT NULL AND NULLIF(PasswordSalt,N'''') IS NOT NULL
 AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()));
SELECT AuthenticationRowVersion FROM @Committed;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.EQ001_WindowsUat_RecordFailure
 @VerifiedUserID int,@Username nvarchar(256),@VerifiedLegacyHash nvarchar(max),
 @VerifiedHash nvarchar(max),@VerifiedSalt nvarchar(max)
WITH EXECUTE AS OWNER AS
BEGIN
 SET NOCOUNT OFF;
 DECLARE @Actor nvarchar(256),@ActorID int;
 EXEC dbo.EQ001_WindowsUat_AssertActor @Username,@VerifiedUserID,@Actor OUTPUT,@ActorID OUTPUT;

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
WHERE UserID=@ActorID AND Username=@Actor AND ISNULL(IsActive,1)=1 AND MustChangePassword=0 AND PasswordChangedAt IS NOT NULL
  AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))
  AND CONVERT(varbinary(max),ISNULL(PasswordHash,N''''))=CONVERT(varbinary(max),@VerifiedLegacyHash)
  AND CONVERT(varbinary(max),ISNULL(PasswordHashNew,N''''))=CONVERT(varbinary(max),@VerifiedHash)
  AND CONVERT(varbinary(max),ISNULL(PasswordSalt,N''''))=CONVERT(varbinary(max),@VerifiedSalt);
END;');

GRANT SELECT ON SCHEMA::dbo TO PharmaLIMS_EquipmentWindowsUat;
GRANT VIEW DEFINITION TO PharmaLIMS_EquipmentWindowsUat;
DENY SELECT ON OBJECT::dbo.Users (PasswordHash,PasswordHashNew,PasswordSalt) TO PharmaLIMS_EquipmentWindowsUat;
DENY INSERT, UPDATE, DELETE ON SCHEMA::dbo TO PharmaLIMS_EquipmentWindowsUat;
GRANT EXECUTE ON dbo.EQ001_WindowsUat_ReadOwnUser TO PharmaLIMS_EquipmentWindowsUat;
GRANT EXECUTE ON dbo.EQ001_WindowsUat_CommitSuccess TO PharmaLIMS_EquipmentWindowsUat;
GRANT EXECUTE ON dbo.EQ001_WindowsUat_RecordFailure TO PharmaLIMS_EquipmentWindowsUat;
