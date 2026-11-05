-- EQ-001 MEDICA / unsigned equipment use -> submitted / REVIEW ONLY.
-- Includes caller binding, existing link, rowversion, replay receipt and immutable audit.
SET NOCOUNT ON;SET XACT_ABORT ON;
THROW 57198,'SECURITY HOLD: signer-only equipment confirmation is not approved for deployment.',1;
IF DB_NAME()<>N'EQ001_UAT_AMEEN_20261024'
 OR ORIGINAL_LOGIN()<>N'AMEEN\PharmaLIMSSigner'
 THROW 57409,'Controlled signer and database target are mandatory.',1;
IF OBJECT_ID(N'dbo.EQ001_TrustedEquipmentConfirm',N'P') IS NOT NULL
 OR OBJECT_ID(N'dbo.EQ001_EquipmentConfirmReceipts',N'U') IS NOT NULL
 OR DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
 THROW 57410,'Unreconciled schema or missing principal.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 CREATE TABLE dbo.EQ001_EquipmentConfirmReceipts
 (
   RequestId uniqueidentifier NOT NULL PRIMARY KEY,
   ActivityId bigint NOT NULL REFERENCES dbo.MicroEquipmentActivities(ActivityID),
   Actor nvarchar(100) NOT NULL, CallerSid nvarchar(184) NOT NULL,
   AuthenticationVersion binary(8) NOT NULL,
   ExpectedVersion binary(8) NOT NULL,
   PayloadHash binary(32) NOT NULL,Reason nvarchar(500) NOT NULL,
   Meaning nvarchar(200) NOT NULL,
   CreatedAt datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
 );
 EXEC(N'CREATE TRIGGER dbo.TRG_EQ001_EquipmentConfirmReceipts_AppendOnly
 ON dbo.EQ001_EquipmentConfirmReceipts AFTER UPDATE,DELETE AS
 BEGIN SET NOCOUNT ON;
   THROW 57411, ''Equipment confirmation receipts are immutable.'',1;
 END');
 EXEC sys.sp_executesql N'CREATE PROCEDURE dbo.EQ001_TrustedEquipmentConfirm
 @RequestId uniqueidentifier,@ActivityId bigint,@ExpectedVersion binary(8),
 @Started datetimeoffset(7),@Ended datetimeoffset(7),@Payload nvarchar(max),
 @Actor nvarchar(100),@AuthenticationVersion binary(8),@CallerSid nvarchar(184),
 @Reason nvarchar(500),@Meaning nvarchar(200)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF DB_NAME()<>N''EQ001_UAT_AMEEN_20261024''
   OR ORIGINAL_LOGIN()<>N''AMEEN\PharmaLIMSSigner'' OR @@TRANCOUNT<1
   THROW 57401,''Trusted signer and controlled transaction are required.'',1;
 IF @RequestId IS NULL OR @RequestId=''00000000-0000-0000-0000-000000000000''
   OR @ActivityId IS NULL OR @ActivityId<=0 OR @ExpectedVersion IS NULL
   OR @Started IS NULL OR @Ended IS NULL OR @Ended<@Started
   OR @Ended>DATEADD(year,1,@Started) OR @Started>DATEADD(day,1,SYSDATETIMEOFFSET())
   OR @Payload IS NULL OR LEN(@Payload)>4096 OR ISJSON(@Payload)<>1
   OR NULLIF(LTRIM(RTRIM(@Actor)),N'''') IS NULL
   OR NULLIF(LTRIM(RTRIM(@CallerSid)),N'''') IS NULL OR LEN(@CallerSid)>184
   OR NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL
   OR NULLIF(LTRIM(RTRIM(@Meaning)),N'''') IS NULL
   OR @AuthenticationVersion IS NULL OR DATALENGTH(@AuthenticationVersion)<>8
   THROW 57402,''Invalid confirmed-use evidence or actor.'',1;
 IF (SELECT COUNT_BIG(*) FROM OPENJSON(@Payload))<>4
   OR TRY_CONVERT(bigint,JSON_VALUE(@Payload,''$.ActivityId''))<>@ActivityId
   OR TRY_CONVERT(datetimeoffset(7),JSON_VALUE(@Payload,''$.Started''))<>@Started
   OR TRY_CONVERT(datetimeoffset(7),JSON_VALUE(@Payload,''$.Ended''))<>@Ended
   THROW 57403,''Confirmed-use canonical evidence is inconsistent.'',1;
 DECLARE @Hash binary(32)=HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),@Payload));
 IF NOT EXISTS(SELECT 1 FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
      WHERE Username=@Actor AND IsActive=1 AND IsLocked=0
      AND MustChangePassword=0 AND CanEnterResults=1
      AND AuthenticationRowVersion=@AuthenticationVersion)
   THROW 57404,''Actor or password version changed.'',1;
 IF EXISTS(SELECT 1 FROM dbo.EQ001_EquipmentConfirmReceipts WITH(UPDLOCK,HOLDLOCK)
           WHERE RequestId=@RequestId)
 BEGIN
   IF NOT EXISTS(SELECT 1 FROM dbo.EQ001_EquipmentConfirmReceipts
       WHERE RequestId=@RequestId AND ActivityId=@ActivityId AND Actor=@Actor
         AND CallerSid=@CallerSid AND AuthenticationVersion=@AuthenticationVersion
         AND PayloadHash=@Hash AND Reason=@Reason AND Meaning=@Meaning
         AND ExpectedVersion=@ExpectedVersion)
     THROW 57405,''Confirmation receipt identity does not match original.'',1;
   SELECT @ActivityId AS ActivityId;
   RETURN;
 END;
 UPDATE dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
 SET ActivityStatus=N''Submitted'', ActualStartAt=@Started,ActualEndAt=@Ended
 WHERE ActivityID=@ActivityId AND ActivityType=N''Use'' AND ActivityStatus=N''Draft''
 AND PerformedBy=@Actor AND VersionToken=@ExpectedVersion
 AND EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivityLinks WITH(UPDLOCK,HOLDLOCK)
            WHERE ActivityID=@ActivityId);
 IF @@ROWCOUNT<>1
   THROW 57406,''Unlinked, stale or unauthorized equipment use.'',1;
 INSERT dbo.MicroEquipmentActivityAudit
   (ActivityID,ActionType,NewStateJson,ActionReason,ChangedBy)
 SELECT a.ActivityID,N''USE CONFIRMED'',
   (SELECT a.ActivityID,a.EquipmentID,a.ActivityType,a.ActivityStatus,a.PerformedBy,
           a.ActualStartAt,a.ActualEndAt,a.EquipmentCodeSnapshot,
           a.CalibrationStatusSnapshot,a.QualificationStatusSnapshot,
           a.ActivityReason,a.MethodReference
    FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),
   @Reason,@Actor
 FROM dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK)
 WHERE a.ActivityID=@ActivityId;
 IF @@ROWCOUNT<>1 THROW 57407,''Confirmed-use audit was not recorded.'',1;
 INSERT dbo.EQ001_EquipmentConfirmReceipts
   (RequestId,ActivityId,Actor,CallerSid,AuthenticationVersion,
    ExpectedVersion,PayloadHash,Reason,Meaning)
 VALUES(@RequestId,@ActivityId,@Actor,@CallerSid,@AuthenticationVersion,
        @ExpectedVersion,@Hash,@Reason,@Meaning);
 IF @@ROWCOUNT<>1 THROW 57408,''Confirmed-use receipt was not recorded.'',1;
 SELECT @ActivityId AS ActivityId;
END';

 GRANT EXECUTE ON dbo.EQ001_TrustedEquipmentConfirm TO PharmaLIMS_TrustedEquipmentSigner;
 IF EXISTS(SELECT 1 FROM sys.database_permissions
    WHERE major_id=OBJECT_ID(N'dbo.EQ001_TrustedEquipmentConfirm')
    AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat')
    AND state='G' AND permission_name='EXECUTE')
 THROW 57412,'WPF Windows client must not access the trusted confirm procedure.',1;
 COMMIT TRANSACTION;
 PRINT N'Trusted confirmation installed in controlled UAT.';
END TRY BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
