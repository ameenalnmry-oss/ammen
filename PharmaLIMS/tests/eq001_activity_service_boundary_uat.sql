-- Controlled deployment to a disposable EQ001 UAT database only.
-- Does not create signers, copy records, change existing signatures, or enable Production.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
BEGIN TRY
 IF DB_NAME() NOT LIKE N'EQ001[_]UAT[_]%' AND DB_NAME() NOT LIKE N'PharmaLIMS[_]EQ001[_]UAT[_]%'
  THROW 56764,'Activity service qualification requires an explicitly isolated EQ001 UAT database.',1;
 IF OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage',N'P') IS NULL
    OR OBJECT_ID(N'dbo.MicroEquipment_AssertTrustedBoundary',N'P') IS NULL
    OR OBJECT_ID(N'dbo.Users',N'U') IS NULL
  THROW 56764,'Reviewed activity SQL and authentication prerequisites are missing.',1;
 IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'PharmaLIMS_TrustedEquipmentSigner' AND type=N'U')
  THROW 56764,'The independent Windows service database principal must be provisioned first.',1;
 EXEC dbo.MicroEquipment_AssertTrustedBoundary;

 IF OBJECT_ID(N'dbo.EQ001_ActivitySigningRequests',N'U') IS NULL
  CREATE TABLE dbo.EQ001_ActivitySigningRequests(
   RequestId uniqueidentifier NOT NULL PRIMARY KEY,
   ActivityId bigint NOT NULL,
   Stage nvarchar(30) NOT NULL,
   Actor nvarchar(100) NOT NULL,
   AcceptedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME());

 EXEC(N'CREATE OR ALTER PROCEDURE dbo.EQ001_CommitActivitySignature
 @RequestId uniqueidentifier,@Activity bigint,@Version binary(8),@Actor nvarchar(100),
 @Stage nvarchar(30),@Reason nvarchar(500),@AuthenticationVersion binary(8),@IssuedAtUtc datetimeoffset(7)
 WITH EXECUTE AS OWNER AS
 BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;
  IF DB_NAME() NOT LIKE N''EQ001[_]UAT[_]%'' AND DB_NAME() NOT LIKE N''PharmaLIMS[_]EQ001[_]UAT[_]%''
   THROW 56764,''Activity signing is not enabled outside isolated EQ001 UAT.'',1;
  IF NOT EXISTS(SELECT 1 FROM sys.database_principals
    WHERE name=N''PharmaLIMS_TrustedEquipmentSigner'' AND type=N''U'' AND sid=SUSER_SID(ORIGINAL_LOGIN()))
    OR ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)<>0
   THROW 56764,''Only the dedicated Windows service origin may commit activity signatures.'',1;
  IF @RequestId IS NULL OR @RequestId=''00000000-0000-0000-0000-000000000000''
    OR @Activity IS NULL OR @Activity<=0 OR @Version IS NULL OR @Version=0x0000000000000000
    OR @Stage IS NULL OR @Stage NOT IN(N''Reviewed'',N''Approved'')
    OR @Reason IS NULL OR LEN(LTRIM(RTRIM(@Reason)))<10
    OR @Reason LIKE N''%''+NCHAR(10)+N''%'' OR @Reason LIKE N''%''+NCHAR(13)+N''%''
    OR @IssuedAtUtc IS NULL OR DATEPART(TZOFFSET,@IssuedAtUtc)<>0
    OR @IssuedAtUtc>SYSUTCDATETIME() OR @IssuedAtUtc<DATEADD(SECOND,-120,SYSUTCDATETIME())
   THROW 56765,''Invalid or expired activity signing request.'',1;
  BEGIN TRANSACTION;
  BEGIN TRY
   EXEC dbo.MicroEquipment_AssertTrustedBoundary;
   IF NOT EXISTS(SELECT 1 FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
     WHERE Username=@Actor AND AuthenticationRowVersion=@AuthenticationVersion
       AND ISNULL(IsActive,1)=1 AND ISNULL(MustChangePassword,0)=0
       AND NOT(ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))
       AND ((@Stage=N''Reviewed'' AND ISNULL(CanReviewResults,0)=1)
         OR (@Stage=N''Approved'' AND ISNULL(CanApproveResults,0)=1)))
    THROW 56764,''Signer credentials, state or stage permission changed.'',1;
   INSERT dbo.EQ001_ActivitySigningRequests(RequestId,ActivityId,Stage,Actor)
    VALUES(@RequestId,@Activity,@Stage,@Actor);
   EXEC dbo.MicroEquipment_SignActivityStage @Activity,@Version,@Actor,@Stage,@Reason;
   COMMIT TRANSACTION;
  END TRY
  BEGIN CATCH
   IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
   THROW;
  END CATCH;
 END;');

 GRANT EXECUTE ON dbo.EQ001_CommitActivitySignature TO PharmaLIMS_TrustedEquipmentSigner;
 IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentApp') IS NOT NULL
  DENY EXECUTE ON dbo.EQ001_CommitActivitySignature TO PharmaLIMS_EquipmentApp;
 -- Only the fresh-credential wrapper may enter the underlying stage procedure.
 -- Its dbo ownership chain remains valid; old direct service/desktop calls do not.
 DENY EXECUTE ON dbo.MicroEquipment_SignActivityStage TO public;
 -- Retire the old desktop-owned credential/counter compatibility API in this
 -- new UAT database. Direct table DENY alone cannot block EXECUTE AS OWNER RPCs.
 IF OBJECT_ID(N'dbo.EQ001_WindowsUat_ReadOwnUser',N'P') IS NOT NULL
  DENY EXECUTE ON dbo.EQ001_WindowsUat_ReadOwnUser TO public;
 IF OBJECT_ID(N'dbo.EQ001_WindowsUat_CommitSuccess',N'P') IS NOT NULL
  DENY EXECUTE ON dbo.EQ001_WindowsUat_CommitSuccess TO public;
 IF OBJECT_ID(N'dbo.EQ001_WindowsUat_RecordFailure',N'P') IS NOT NULL
  DENY EXECUTE ON dbo.EQ001_WindowsUat_RecordFailure TO public;
 IF OBJECT_ID(N'dbo.EQ001_WindowsUat_AssertActor',N'P') IS NOT NULL
  DENY EXECUTE ON dbo.EQ001_WindowsUat_AssertActor TO public;
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
