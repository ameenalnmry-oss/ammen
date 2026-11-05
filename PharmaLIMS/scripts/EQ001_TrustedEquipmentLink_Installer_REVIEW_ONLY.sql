-- MEDICA EQ-001 / trusted equipment-link installer / UNRELEASED SOURCE.
-- Mandatory review: SQL origin, actor/SID/ticket binding, sample grain, read/write permissions,
-- idempotency, race handling and native service account identity.
SET NOCOUNT ON;
SET XACT_ABORT ON;
THROW 57198, 'SECURITY HOLD: Equipment link installer is not approved for activation.', 1;
IF DB_NAME()<>N'EQ001_UAT_AMEEN_20261024'
 OR ORIGINAL_LOGIN()<>N'AMEEN\PharmaLIMSSigner'
 THROW 57314,'Controlled signer and UAT destination are required.',1;
IF OBJECT_ID(N'dbo.EQ001_TrustedEquipmentLink',N'P') IS NOT NULL
 OR OBJECT_ID(N'dbo.EQ001_EquipmentLinkReceipts',N'U') IS NOT NULL
 OR DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivityAudit',N'U') IS NULL
 THROW 57315,'Existing schema or missing prerequisites require reconciliation.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 CREATE TABLE dbo.EQ001_EquipmentLinkReceipts
 (
   RequestId uniqueidentifier NOT NULL PRIMARY KEY,
   ActivityId bigint NOT NULL REFERENCES dbo.MicroEquipmentActivities(ActivityID),
   Actor nvarchar(100) NOT NULL,
   CallerSid nvarchar(184) NOT NULL,
   AuthenticationVersion binary(8) NOT NULL,
   PayloadHash binary(32) NOT NULL,
   Reason nvarchar(500) NOT NULL,
   Meaning nvarchar(200) NOT NULL,
   LinkCount int NOT NULL CHECK(LinkCount BETWEEN 1 AND 100),
   CreatedAt datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
 );
 EXEC(N'CREATE TRIGGER dbo.TRG_EQ001_EquipmentLinkReceipts_AppendOnly
     ON dbo.EQ001_EquipmentLinkReceipts AFTER UPDATE,DELETE AS
     BEGIN SET NOCOUNT ON;
       THROW 57316, ''Equipment link receipts cannot be modified or deleted.'',1;
     END');
 EXEC sys.sp_executesql N'CREATE PROCEDURE dbo.EQ001_TrustedEquipmentLink
  @RequestId uniqueidentifier, @ActivityId bigint, @Module nvarchar(20),
  @ParentIds nvarchar(max), @ResultId int=NULL, @Payload nvarchar(max),
  @Actor nvarchar(100), @AuthenticationVersion binary(8),
  @CallerSid nvarchar(184), @Reason nvarchar(500), @Meaning nvarchar(200)
WITH EXECUTE AS OWNER
AS
BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  IF DB_NAME()<>N''EQ001_UAT_AMEEN_20261024''
    OR ORIGINAL_LOGIN()<>N''AMEEN\PharmaLIMSSigner''
    OR @@TRANCOUNT<1
    THROW 57301,''Trusted signer and a controlled transaction required.'',1;
  IF @RequestId IS NULL OR @RequestId=''00000000-0000-0000-0000-000000000000''
    OR @ActivityId IS NULL OR @ActivityId<=0 OR @Module NOT IN(N''WATER'',N''EM'',N''PRM'')
    OR @Actor IS NULL OR LEN(LTRIM(RTRIM(@Actor)))=0
    OR @AuthenticationVersion IS NULL OR DATALENGTH(@AuthenticationVersion)<>8
    OR NULLIF(LTRIM(RTRIM(@CallerSid)),N'''') IS NULL OR LEN(@CallerSid)>184
    OR NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL
    OR NULLIF(LTRIM(RTRIM(@Meaning)),N'''') IS NULL
    OR @Payload IS NULL OR LEN(@Payload)>8192 OR ISJSON(@Payload)<>1
    OR @ParentIds IS NULL OR LEN(@ParentIds)>4096 OR ISJSON(@ParentIds)<>1
    OR (@ResultId IS NOT NULL AND @ResultId<=0)
    THROW 57302,''Invalid equipment-link envelope.'',1;
  IF TRY_CONVERT(bigint,JSON_VALUE(@Payload,''$.ActivityId''))<>@ActivityId
    OR JSON_VALUE(@Payload,''$.Module'')<>@Module
    OR JSON_QUERY(@Payload,''$.ParentIds'')<>@ParentIds
    OR (@ResultId IS NULL AND JSON_VALUE(@Payload,''$.ResultId'') IS NOT NULL)
    OR (@ResultId IS NOT NULL AND TRY_CONVERT(int,JSON_VALUE(@Payload,''$.ResultId''))<>@ResultId)
    OR (SELECT COUNT_BIG(*) FROM OPENJSON(@Payload))<>4
    THROW 57303,''Canonical equipment-link envelope mismatch.'',1;

  DECLARE @Selected TABLE(ParentId int NOT NULL PRIMARY KEY);
  IF EXISTS(SELECT 1 FROM OPENJSON(@ParentIds)
       WHERE type<>2 OR TRY_CONVERT(int,value) IS NULL OR TRY_CONVERT(int,value)<=0)
    THROW 57304,''Equipment-link parent identity invalid.'',1;
  IF EXISTS(SELECT value FROM OPENJSON(@ParentIds) GROUP BY value HAVING COUNT(*)>1)
    THROW 57305,''Duplicate equipment-link parent identity.'',1;
  INSERT @Selected(ParentId) SELECT CONVERT(int,value) FROM OPENJSON(@ParentIds);
  DECLARE @Count int=(SELECT COUNT(*) FROM @Selected);
  IF @Count<1 OR @Count>100 OR (@ResultId IS NOT NULL AND @Count<>1)
    THROW 57306,''Invalid equipment-link batch size.'',1;

  IF NOT EXISTS(SELECT 1 FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
       WHERE Username=@Actor AND IsActive=1 AND IsLocked=0
       AND MustChangePassword=0 AND CanEnterResults=1
       AND AuthenticationRowVersion=@AuthenticationVersion)
    THROW 57307,''Equipment-link actor is not currently authorized.'',1;
  DECLARE @Hash binary(32)=HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),@Payload));
  IF EXISTS(SELECT 1 FROM dbo.EQ001_EquipmentLinkReceipts WITH(UPDLOCK,HOLDLOCK)
      WHERE RequestId=@RequestId)
  BEGIN
    IF NOT EXISTS(SELECT 1 FROM dbo.EQ001_EquipmentLinkReceipts
      WHERE RequestId=@RequestId AND ActivityId=@ActivityId
        AND Actor=@Actor AND CallerSid=@CallerSid
        AND AuthenticationVersion=@AuthenticationVersion
        AND PayloadHash=@Hash AND Reason=@Reason AND Meaning=@Meaning AND LinkCount=@Count)
      THROW 57308,''Equipment-link request identity changed.'',1;
    SELECT @ActivityId AS ActivityId,@Count AS LinkCount;
    RETURN;
  END;

  IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
       WHERE ActivityID=@ActivityId AND PerformedBy=@Actor
         AND ActivityType IN(N''Use'',N''Incubation'') AND ActivityStatus=N''Draft'')
    THROW 57309,''Equipment-link activity is stale or belongs to another signer.'',1;

  DECLARE @Sources TABLE(ParentId int NOT NULL PRIMARY KEY,
     SampleNo nvarchar(100) NOT NULL, TestCode nvarchar(100) NULL);
  IF @Module=N''WATER''
    INSERT @Sources(ParentId,SampleNo,TestCode)
    SELECT p.SampleID,p.SampleNumber,
      CASE WHEN t.SampleTestID IS NULL THEN NULL ELSE CONVERT(nvarchar(100),t.SampleTestID) END
    FROM dbo.Samples p WITH(UPDLOCK,HOLDLOCK)
    JOIN @Selected s ON s.ParentId=p.SampleID
    LEFT JOIN dbo.SampleTests t WITH(UPDLOCK,HOLDLOCK)
      ON t.SampleID=p.SampleID AND t.SampleTestID=@ResultId
    WHERE @ResultId IS NULL OR t.SampleTestID IS NOT NULL;
  ELSE IF @Module=N''EM''
    INSERT @Sources(ParentId,SampleNo,TestCode)
    SELECT p.Id,p.EventNo,t.PlateCode
    FROM dbo.EM_Events p WITH(UPDLOCK,HOLDLOCK)
    JOIN @Selected s ON s.ParentId=p.Id
    LEFT JOIN dbo.EM_EventPlates t WITH(UPDLOCK,HOLDLOCK)
      ON t.EventId=p.Id AND t.Id=@ResultId
    WHERE @ResultId IS NULL OR t.Id IS NOT NULL;
  ELSE IF @Module=N''PRM''
    INSERT @Sources(ParentId,SampleNo,TestCode)
    SELECT p.SampleID,p.SampleNumber,t.TestCode
    FROM dbo.PRM_Samples p WITH(UPDLOCK,HOLDLOCK)
    JOIN @Selected s ON s.ParentId=p.SampleID
    LEFT JOIN dbo.PRM_SampleTests t WITH(UPDLOCK,HOLDLOCK)
      ON t.SampleID=p.SampleID AND t.SampleTestID=@ResultId
    WHERE @ResultId IS NULL OR t.SampleTestID IS NOT NULL;
  IF (SELECT COUNT(*) FROM @Sources)<>@Count
     OR EXISTS(SELECT 1 FROM @Sources WHERE NULLIF(LTRIM(RTRIM(SampleNo)),N'''') IS NULL)
    THROW 57310,''Registered sample, event or corresponding test is missing.'',1;

  INSERT dbo.MicroEquipmentActivityLinks
    (ActivityID,Module,ParentRecordID,ResultRecordID,SampleNumberSnapshot,TestCodeSnapshot,LinkedBy)
  SELECT @ActivityId,@Module,s.ParentId,@ResultId,s.SampleNo,s.TestCode,@Actor
  FROM @Sources s
  WHERE NOT EXISTS
  (
    SELECT 1 FROM dbo.MicroEquipmentActivityLinks l WITH(UPDLOCK,HOLDLOCK)
    WHERE l.ActivityID=@ActivityId AND l.Module=@Module AND l.ParentRecordID=s.ParentId
      AND ((@ResultId IS NULL AND l.ResultRecordID IS NULL) OR l.ResultRecordID=@ResultId)
  );
  IF (SELECT COUNT(*) FROM dbo.MicroEquipmentActivityLinks l
      JOIN @Sources s ON s.ParentId=l.ParentRecordID
      WHERE l.ActivityID=@ActivityId AND l.Module=@Module AND
        ((@ResultId IS NULL AND l.ResultRecordID IS NULL) OR l.ResultRecordID=@ResultId))<>@Count
    THROW 57311,''Equipment-link persistence mismatch.'',1;
  INSERT dbo.MicroEquipmentActivityAudit
    (ActivityID,ActionType,NewStateJson,ActionReason,ChangedBy)
  SELECT a.ActivityID,N''LINK REGISTERED'',
    (SELECT @Module AS Module, @ParentIds AS ParentIds, @ResultId AS ResultId,
            @RequestId AS RequestId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),
    @Reason,@Actor
  FROM dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK)
  WHERE a.ActivityID=@ActivityId;
  IF @@ROWCOUNT<>1 THROW 57312,''Equipment-link audit failed.'',1;
  INSERT dbo.EQ001_EquipmentLinkReceipts
    (RequestId,ActivityId,Actor,CallerSid,AuthenticationVersion,PayloadHash,
     Reason,Meaning,LinkCount)
  VALUES(@RequestId,@ActivityId,@Actor,@CallerSid,@AuthenticationVersion,@Hash,
         @Reason,@Meaning,@Count);
  IF @@ROWCOUNT<>1 THROW 57313,''Equipment-link receipt failed.'',1;
  SELECT @ActivityId AS ActivityId,@Count AS LinkCount;
END';

 GRANT EXECUTE ON dbo.EQ001_TrustedEquipmentLink TO PharmaLIMS_TrustedEquipmentSigner;
 IF EXISTS(SELECT 1 FROM sys.database_permissions
   WHERE major_id=OBJECT_ID(N'dbo.EQ001_TrustedEquipmentLink')
   AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat')
   AND permission_name='EXECUTE' AND state='G')
 THROW 57317,'WPF client must not execute the signer-only equipment-link procedure.',1;
 COMMIT TRANSACTION;
 PRINT N'Trusted equipment link boundary staged successfully.';
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
