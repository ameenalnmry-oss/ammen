-- Explicit isolated EQ001 Water analysis stage. Review and opt in; registration is not restaged.
SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF; SET XACT_ABORT ON;
DECLARE @VerifiedIsolatedUat bit = 0;
IF @VerifiedIsolatedUat<>1 OR DB_NAME()<>N'EQ001_UAT_AMEEN_20261024' OR @@SERVERNAME<>N'AMEEN\SQLEXPRESS'
 THROW 57000,'Exact isolated site UAT confirmation is required to stage Water analysis.',1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
 OR COL_LENGTH(N'dbo.Users',N'AuthenticationRowVersion')<>8 OR COL_LENGTH(N'dbo.SampleTests',N'ResultRowVersion')<>8
 OR COL_LENGTH(N'dbo.WaterResultResourceEvidence',N'ExecutionDate') IS NULL
 THROW 57000,'Actual Water analysis schema and independent service principal are required.',1;
IF NOT EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.ElectronicSignatures') AND name=N'TRG_ElectronicSignatures_AppendOnly' AND is_disabled=0)
 OR NOT EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.AuditTrail') AND name=N'TRG_AuditTrail_AppendOnly' AND is_disabled=0)
 THROW 57000,'Enabled historical signature and audit protection is required.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 IF OBJECT_ID(N'dbo.EQ001_WaterWorkflowReceipts',N'U') IS NULL
  CREATE TABLE dbo.EQ001_WaterWorkflowReceipts(RequestId uniqueidentifier NOT NULL PRIMARY KEY,Operation nvarchar(30) NOT NULL,SampleId int NOT NULL,
   PayloadHash binary(32) NOT NULL,Actor nvarchar(100) NOT NULL,CallerSid nvarchar(184) NOT NULL,ActorVersion binary(8) NOT NULL,
   State nvarchar(12) NOT NULL CHECK(State IN(N'Committed',N'Cancelled')),Outcome nvarchar(max) NOT NULL CHECK(ISJSON(Outcome)=1),
   Reason nvarchar(500) NOT NULL,Meaning nvarchar(200) NOT NULL,RecordedAtUtc datetime2(7) NOT NULL);
 DECLARE @Procedure nvarchar(max)=N'CREATE OR ALTER PROCEDURE dbo.EQ001_UAT_WaterWorkflow
 @Mode nvarchar(16),@Operation nvarchar(30),@RequestId uniqueidentifier,@SampleId int,
 @Payload nvarchar(max),@Plan nvarchar(max),@Actor nvarchar(100),@ActorVersion binary(8),
 @CallerSid nvarchar(184),@Reason nvarchar(500),@Meaning nvarchar(200)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF @@TRANCOUNT=0 OR DB_NAME()<>N''EQ001_UAT_AMEEN_20261024'' OR @@SERVERNAME<>N''AMEEN\SQLEXPRESS''
  THROW 57000,''Water analysis requires an explicit transaction and the exact isolated site UAT.'',1;
 IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N''PharmaLIMS_TrustedEquipmentSigner'' AND type=N''U'' AND sid=SUSER_SID(ORIGINAL_LOGIN()))
  OR ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)<>0
  THROW 57001,''Only the independent Windows signing principal can execute Water analysis.'',1;
 IF @Mode IS NULL OR @Mode NOT IN(N''Read'',N''Resolve'',N''Cancel'',N''Commit'') OR @Operation IS NULL
  OR @Operation NOT IN(N''StartAnalysis'',N''SaveResults'',N''SubmitReview'',N''Review'',N''Approve'',N''CorrectTimes'')
  OR @RequestId IS NULL OR @RequestId=CONVERT(uniqueidentifier,0x0) OR @SampleId IS NULL OR @SampleId<=0
  OR @Payload IS NULL OR ISJSON(@Payload)<>1 OR DATALENGTH(@Payload)>65536
  OR NULLIF(LTRIM(RTRIM(@Actor)),N'''') IS NULL OR NULLIF(LTRIM(RTRIM(@CallerSid)),N'''') IS NULL
  OR NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL OR NULLIF(LTRIM(RTRIM(@Meaning)),N'''') IS NULL
  OR ISNULL(JSON_VALUE(@Payload,''$.Operation''),N'''')<>@Operation OR ISNULL(TRY_CONVERT(int,JSON_VALUE(@Payload,''$.SampleId'')),0)<>@SampleId
  THROW 57002,''Invalid Water analysis request identity.'',1;
 DECLARE @Role nvarchar(50),@Version binary(8),@Enter bit,@Review bit,@Approve bit,@Register bit,@Now datetime2=SYSDATETIME(),@Utc datetime2=SYSUTCDATETIME();
 SELECT @Role=Role,@Version=AuthenticationRowVersion,@Enter=CanEnterResults,@Review=CanReviewResults,@Approve=CanApproveResults,@Register=CanRegisterSamples
 FROM dbo.Users WITH(UPDLOCK,HOLDLOCK) WHERE Username=@Actor AND IsActive=1 AND MustChangePassword=0
 AND (ISNULL(IsLocked,0)=0 OR (LockedUntil IS NOT NULL AND LockedUntil<=@Now));
 IF @Version IS NULL OR @ActorVersion IS NULL OR @Version<>@ActorVersion THROW 57003,''Actor is inactive, locked, or authentication version changed.'',1;
 DECLARE @Identity nvarchar(max)=(SELECT @Payload AS Payload,@Reason AS Reason,@Meaning AS Meaning FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
 DECLARE @Hash binary(32)=HASHBYTES(''SHA2_256'',@Identity),@Lock int,@LockName nvarchar(255)=N''EQ001.Water.Workflow.''+CONVERT(nvarchar(36),@RequestId);
 EXEC @Lock=sys.sp_getapplock @Resource=@LockName,@LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=10000;
 IF @Lock<0 THROW 57004,''Water request is busy; retry the same identity.'',1;
 DECLARE @Receipt nvarchar(max),@ReceiptState nvarchar(12);
 SELECT @Receipt=Outcome,@ReceiptState=State FROM dbo.EQ001_WaterWorkflowReceipts WITH(UPDLOCK,HOLDLOCK) WHERE RequestId=@RequestId;
 IF @ReceiptState IS NOT NULL
 BEGIN
  IF EXISTS(SELECT 1 FROM dbo.EQ001_WaterWorkflowReceipts WHERE RequestId=@RequestId AND
    (Operation<>@Operation OR SampleId<>@SampleId OR PayloadHash<>@Hash OR Actor<>@Actor OR CallerSid<>@CallerSid))
   THROW 57005,''A request identity cannot be rebound to another operation, payload, actor, or SID.'',1;
  IF @Mode<>N''Read'' BEGIN SELECT @Receipt; RETURN; END;
 END;
 IF @Mode=N''Cancel''
 BEGIN
  SET @Receipt=(SELECT @RequestId RequestId,N''Cancelled'' State,@SampleId SampleId,JSON_VALUE(@Payload,''$.SampleNumber'') SampleNumber,N'''' Status,
   0 SignatureId,0 CompletedTests,0 PendingTests,N'''' QualityEventSummary FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  INSERT dbo.EQ001_WaterWorkflowReceipts(RequestId,Operation,SampleId,PayloadHash,Actor,CallerSid,ActorVersion,State,Outcome,Reason,Meaning,RecordedAtUtc)
  VALUES(@RequestId,@Operation,@SampleId,@Hash,@Actor,@CallerSid,@ActorVersion,N''Cancelled'',@Receipt,@Reason,@Meaning,@Utc);
  SELECT @Receipt; RETURN;
 END;
 IF @Mode=N''Resolve''
 BEGIN
  SELECT @RequestId RequestId,N''Pending'' State,@SampleId SampleId,JSON_VALUE(@Payload,''$.SampleNumber'') SampleNumber,N'''' Status,
   0 SignatureId,0 CompletedTests,0 PendingTests,N'''' QualityEventSummary FOR JSON PATH,WITHOUT_ARRAY_WRAPPER; RETURN;
 END;
 -- Recovery checks precede current operation permissions and clinical-state validation.
 IF (@Operation IN(N''StartAnalysis'',N''SaveResults'',N''SubmitReview'') AND ISNULL(@Enter,0)<>1)
  OR (@Operation=N''Review'' AND ISNULL(@Review,0)<>1)
  OR (@Operation=N''Approve'' AND (ISNULL(@Approve,0)<>1 OR UPPER(LTRIM(RTRIM(ISNULL(@Role,N'''')))) NOT IN(N''QA'',N''QUALITY ASSURANCE'')))
  OR (@Operation=N''CorrectTimes'' AND ISNULL(@Register,0)<>1)
  THROW 57006,''The current actor does not have the required Water operation permission or QA role.'',1;
 IF @Mode=N''Read''
 BEGIN
  /*__WATER_WORKFLOW_SNAPSHOT_SQL__*/
  RETURN;
 END;
 DECLARE @Status nvarchar(100),@Number nvarchar(20),@Start datetime,@Sampling datetime,@Received datetime,@Incubation datetime,@End datetime,@Completed datetime,@Locked bit;
 SELECT @Status=Status,@Number=SampleNumber,@Start=AnalysisStartedDateTime,@Sampling=SamplingDateTime,@Received=ReceivedDateTime,
 @Incubation=IncubationStartedDateTime,@End=IncubationEndDate,@Completed=AnalysisCompletedDateTime,@Locked=IsLocked
 FROM dbo.Samples WITH(UPDLOCK,HOLDLOCK) WHERE SampleID=@SampleId AND SampleType IN(N''Purified Water'',N''Potable Water'',N''Purified'',N''Potable'');
 IF @Status IS NULL OR ISNULL(JSON_VALUE(@Payload,''$.ExpectedStatus''),N'''')<>@Status OR ISNULL(JSON_VALUE(@Payload,''$.SampleNumber''),N'''')<>@Number
  THROW 57010,''The sample identity or expected status changed. Reload the Water sample.'',1;
 IF ISNULL(@Locked,0)=1 OR @Status IN(N''Approved'',N''COA Issued'',N''COA Cancelled'',N''Rejected'',N''Cancelled'')
  OR EXISTS(SELECT 1 FROM dbo.Certificates WITH(UPDLOCK,HOLDLOCK) WHERE SampleID=@SampleId AND ISNULL(IsCancelled,0)=0)
  THROW 57011,''The sample is locked, released, rejected, or has an active certificate.'',1;
 IF @Plan IS NULL OR ISJSON(@Plan)<>1 OR ISNULL(TRY_CONVERT(int,JSON_VALUE(@Plan,''$.Version'')),0)<>1
  OR ISNULL(JSON_VALUE(@Plan,''$.Operation''),N'''')<>@Operation OR ISNULL(TRY_CONVERT(int,JSON_VALUE(@Plan,''$.SampleId'')),0)<>@SampleId
  OR ISNULL(JSON_VALUE(@Plan,''$.ExpectedStatus''),N'''')<>@Status
  OR ISNULL(JSON_VALUE(@Plan,''$.ExpectedSnapshot''),N'''')<>ISNULL(JSON_VALUE(@Payload,''$.ExpectedSnapshot''),N'''')
  OR NULLIF(JSON_VALUE(@Plan,''$.ExpectedSnapshot''),N'''') IS NULL
  THROW 57012,''Server mutation plan is not bound to the original Water request.'',1;
 DECLARE @NewStatus nvarchar(100)=JSON_VALUE(@Plan,''$.Status''),@Action nvarchar(50),@OldHeader nvarchar(max),@NewHeader nvarchar(max);
 SET @OldHeader=(SELECT * FROM dbo.Samples WHERE SampleID=@SampleId FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
 IF @NewStatus IS NULL OR (@Operation=N''StartAnalysis'' AND (@Status NOT IN(N''Registered'',N''Incubation'') OR ISNULL(@NewStatus,N'''')<>N''In Analysis''))
  OR (@Operation=N''SaveResults'' AND (@Status NOT IN(N''In Analysis'',N''Partially Entered'',N''Results Entered'',N''Alert'',N''Under Investigation'',N''Released After Investigation'') OR @NewStatus NOT IN(N''Partially Entered'',N''Results Entered'',N''Alert'',N''Under Investigation'',N''Released After Investigation'')))
  OR (@Operation=N''SubmitReview'' AND (@Status NOT IN(N''Results Entered'',N''Alert'',N''Released After Investigation'') OR ISNULL(@NewStatus,N'''')<>N''Under Review''))
  OR (@Operation=N''Review'' AND (@Status<>N''Under Review'' OR ISNULL(@NewStatus,N'''')<>N''Reviewed''))
  OR (@Operation=N''Approve'' AND (@Status<>N''Reviewed'' OR ISNULL(@NewStatus,N'''')<>N''Approved''))
  OR (@Operation=N''CorrectTimes'' AND ISNULL(@NewStatus,N'''')<>@Status)
  THROW 57013,''The Water operation is not permitted in the current workflow state.'',1;
 IF @Operation IN(N''Review'',N''Approve'') AND EXISTS(SELECT 1 FROM dbo.ElectronicSignatures WITH(UPDLOCK,HOLDLOCK)
  WHERE SampleID=@SampleId AND UPPER(LTRIM(RTRIM(SignedBy)))=UPPER(LTRIM(RTRIM(@Actor))) AND
  (ActionType IN(N''Result Entry'',N''Results Entry'',N''Enter Results'',N''Entered Results'')
  OR (@Operation=N''Review'' AND ActionType IN(N''Submit Review'',N''Submit for Review'',N''Submitted for Review''))
  OR (@Operation=N''Approve'' AND ActionType IN(N''Review'',N''Review Sample'',N''Reviewed''))))
  THROW 57014,''Independent Water reviewer or QA approver is required.'',1;
 IF @Operation IN(N''SubmitReview'',N''Review'',N''Approve'')
 BEGIN
  IF EXISTS(SELECT 1 FROM dbo.SampleTests WITH(UPDLOCK,HOLDLOCK) WHERE SampleID=@SampleId AND ResultValue IS NULL)
   THROW 57015,''Pending Water results block this operation.'',1;
  IF EXISTS(SELECT 1 FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK) WHERE (SampleID=@SampleId OR (SourceModule=N''Water'' AND SourceRecordID=@SampleId))
   AND ISNULL(CurrentStatus,N'''') NOT IN(N''Closed'',N''QA Closed'',N''Cancelled'',N''Rejected Closed''))
   OR EXISTS(SELECT 1 FROM dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK) WHERE st.SampleID=@SampleId AND UPPER(ISNULL(st.ResultStatus,N'''')) IN(N''OOS'',N''ACTION'',N''FAIL'') AND NOT EXISTS(
    SELECT 1 FROM dbo.QualityEventAffectedResults a WITH(UPDLOCK,HOLDLOCK) JOIN dbo.QualityEvents q WITH(UPDLOCK,HOLDLOCK) ON q.QualityEventID=a.QualityEventID
    WHERE q.SampleID=@SampleId AND a.SampleTestID=st.SampleTestID AND q.CurrentStatus=N''Closed'' AND q.FinalDisposition IN(N''Accept with Justification'',N''Release After Investigation'',N''Released After Investigation'',N''Retest Accepted'',N''Resample Accepted'')))
   THROW 57016,''Water Quality Event or closed-disposition gate blocks this operation.'',1;
 END;
 IF @Operation=N''StartAnalysis''
 BEGIN
  SET @Start=TRY_CONVERT(datetime2,JSON_VALUE(@Plan,''$.AnalysisStartedAt''),126);
  IF @Start IS NULL OR @Sampling IS NULL OR @Received IS NULL OR @Start<@Sampling OR @Start<@Received OR @Start>DATEADD(minute,1,@Now)
   THROW 57017,''Analysis start must follow sampling and receipt and the database clock.'',1;
  UPDATE dbo.Samples SET AnalysisStartedDateTime=@Start WHERE SampleID=@SampleId;
 END;
 IF @Operation=N''CorrectTimes''
 BEGIN
  DECLARE @NewSampling datetime=TRY_CONVERT(datetime2,JSON_VALUE(@Plan,''$.Times.SamplingDateTime''),126),@NewReceived datetime=TRY_CONVERT(datetime2,JSON_VALUE(@Plan,''$.Times.ReceivedDateTime''),126),
   @NewIncubation datetime=TRY_CONVERT(datetime2,JSON_VALUE(@Plan,''$.Times.IncubationStartedDateTime''),126),@NewStart datetime=TRY_CONVERT(datetime2,JSON_VALUE(@Plan,''$.Times.AnalysisStartedDateTime''),126),@NewEnd datetime;
  SET @NewEnd=CASE WHEN @NewIncubation IS NOT NULL AND @Incubation IS NOT NULL AND @End IS NOT NULL THEN DATEADD(second,DATEDIFF(second,@Incubation,@End),@NewIncubation) ELSE NULL END;
  IF @NewSampling IS NULL OR @NewReceived IS NULL OR @NewReceived<@NewSampling OR (@Incubation IS NULL AND @NewIncubation IS NOT NULL)
   OR (@Incubation IS NOT NULL AND (@NewIncubation IS NULL OR @NewIncubation<@NewReceived OR @NewEnd IS NULL))
   OR (@Start IS NOT NULL AND @NewStart IS NULL) OR (@NewStart IS NOT NULL AND (@NewStart<@NewReceived OR @NewStart<@NewSampling))
   OR (@Completed IS NOT NULL AND (@NewStart IS NULL OR @NewStart>@Completed))
   OR @NewSampling>DATEADD(minute,1,@Now) OR @NewReceived>DATEADD(minute,1,@Now) OR @NewIncubation>DATEADD(minute,1,@Now) OR @NewStart>DATEADD(minute,1,@Now)
   THROW 57018,''Corrected Water timestamps violate chronology or original incubation duration.'',1;
  UPDATE dbo.Samples SET SamplingDateTime=@NewSampling,ReceivedDateTime=@NewReceived,IncubationStartedDateTime=@NewIncubation,
   IncubationEndDate=@NewEnd,AnalysisStartedDateTime=@NewStart WHERE SampleID=@SampleId;
 END;
 DECLARE @Rows TABLE(SampleTestId int PRIMARY KEY,WriteResult bit NOT NULL,ResultValue nvarchar(200),Remarks nvarchar(max),ResultStatus nvarchar(50),DeviationType nvarchar(100),
  WriteEvidence bit NOT NULL,EquipmentId int,SupportingEquipmentIds nvarchar(max),TestKitCode nvarchar(100),TestKitLot nvarchar(100),TestKitExpiry date,RawResultSnapshot nvarchar(200),Execution nvarchar(max));
 IF @Operation<>N''SaveResults'' AND EXISTS(SELECT 1 FROM OPENJSON(@Plan,''$.Rows'')) THROW 57019,''Only SaveResults can contain result mutation rows.'',1;
 IF @Operation=N''SaveResults''
 BEGIN
  IF @Start IS NULL THROW 57017,''Start analysis before saving Water results.'',1;
  IF EXISTS(SELECT 1 FROM dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK) LEFT JOIN dbo.Tests t WITH(UPDLOCK,HOLDLOCK) ON t.TestID=st.TestID
    WHERE st.SampleID=@SampleId AND COALESCE(st.TestCategorySnapshot,t.TestCategory) LIKE N''%micro%'' AND (@End IS NULL OR @End>@Now))
   THROW 57020,''Required Water microbiology incubation is incomplete.'',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Plan,''$.Rows'') WHERE type<>5) THROW 57019,''Malformed Water result plan.'',1;
  IF EXISTS(SELECT TRY_CONVERT(int,JSON_VALUE(value,''$.SampleTestId'')) FROM OPENJSON(@Plan,''$.Rows'') GROUP BY TRY_CONVERT(int,JSON_VALUE(value,''$.SampleTestId'')) HAVING COUNT(*)>1)
   THROW 57021,''Duplicate Water result identities are not permitted.'',1;
  INSERT @Rows SELECT SampleTestId,WriteResult,ResultValue,Remarks,ResultStatus,DeviationType,WriteEvidence,EquipmentId,SupportingEquipmentIds,TestKitCode,TestKitLot,TestKitExpiry,RawResultSnapshot,Execution
  FROM OPENJSON(@Plan,''$.Rows'') WITH(SampleTestId int,WriteResult bit,ResultValue nvarchar(200),Remarks nvarchar(max),ResultStatus nvarchar(50),DeviationType nvarchar(100),WriteEvidence bit,
   EquipmentId int,SupportingEquipmentIds nvarchar(max) AS JSON,TestKitCode nvarchar(100),TestKitLot nvarchar(100),TestKitExpiry date,RawResultSnapshot nvarchar(200),Execution nvarchar(max) AS JSON);
  IF EXISTS(SELECT 1 FROM @Rows r LEFT JOIN dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK) ON st.SampleTestID=r.SampleTestId AND st.SampleID=@SampleId WHERE st.SampleTestID IS NULL)
   THROW 57021,''A Water result is missing or belongs to another sample.'',1;
  IF EXISTS(SELECT 1 FROM dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK) LEFT JOIN dbo.Tests t WITH(UPDLOCK,HOLDLOCK) ON t.TestID=st.TestID
   WHERE st.SampleID=@SampleId AND UPPER(LTRIM(RTRIM(COALESCE(NULLIF(st.TestNameSnapshot,N''''),t.TestName)))) NOT IN(N''TASTE'',N''ODOR'',N''ODOUR'',N''TASTE AND ODOR'',N''TASTE & ODOR'',N''TASTE AND ODOUR'',N''TASTE & ODOUR'') AND NOT EXISTS(SELECT 1 FROM @Rows r WHERE r.SampleTestId=st.SampleTestID))
   THROW 57021,''SaveResults must cover every visible Water result exactly once.'',1;
  IF EXISTS(SELECT 1 FROM @Rows r JOIN dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK) ON st.SampleTestID=r.SampleTestId WHERE
   (r.WriteResult=1 AND (r.ResultValue IS NULL OR TRY_CONVERT(decimal(18,4),r.ResultValue) IS NULL OR TRY_CONVERT(decimal(38,18),r.ResultValue) IS NULL
     OR TRY_CONVERT(decimal(38,18),r.ResultValue)<>CONVERT(decimal(38,18),TRY_CONVERT(decimal(18,4),r.ResultValue)) OR CHARINDEX(N''e'',LOWER(r.ResultValue))>0 OR (CHARINDEX(N''.'',r.ResultValue)>0 AND REPLACE(SUBSTRING(r.ResultValue,CHARINDEX(N''.'',r.ResultValue)+5,200),N''0'',N'''')<>N'''')))
    OR (r.WriteResult=1 AND r.ResultStatus NOT IN(N''PASS'',N''ALERT'',N''OOS''))
    OR (r.WriteResult=1 AND r.ResultStatus IN(N''OOS'',N''ALERT'') AND NULLIF(LTRIM(RTRIM(r.Remarks)),N'''') IS NULL))
   THROW 57022,''Water numeric values must fit decimal(18,4) exactly, with assessed status and excursion remarks.'',1;
  IF @Status IN(N''Under Investigation'',N''Released After Investigation'') AND EXISTS(SELECT 1 FROM @Rows r JOIN dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK) ON st.SampleTestID=r.SampleTestId
   WHERE st.ResultValue IS NOT NULL AND (r.WriteResult=1 OR r.WriteEvidence=1 OR ISNULL(r.Remarks,N'''')<>ISNULL(st.Remarks,N'''')))
   THROW 57023,''Investigation completion can change pending Water results only.'',1;
  DECLARE @NewOos bit=CASE WHEN EXISTS(SELECT 1 FROM @Rows r JOIN dbo.SampleTests st ON st.SampleTestID=r.SampleTestId AND st.SampleID=@SampleId WHERE r.WriteResult=1 AND st.ResultValue IS NULL AND r.ResultStatus=N''OOS'') THEN 1 ELSE 0 END;
  DECLARE @Delta TABLE(RecordId int,OldValue nvarchar(max),NewValue nvarchar(max),WriteResult bit);
  UPDATE st SET Remarks=NULLIF(r.Remarks,N'''')
   OUTPUT inserted.SampleTestID,CONCAT(N''ResultValue='',deleted.ResultValue,N''; Remarks='',deleted.Remarks,N''; ResultStatus='',deleted.ResultStatus,N''; DeviationType='',deleted.DeviationType,N''; LimitDescription='',deleted.LimitDescription),
    CONCAT(N''ResultValue='',inserted.ResultValue,N''; Remarks='',inserted.Remarks,N''; ResultStatus='',inserted.ResultStatus,N''; DeviationType='',inserted.DeviationType,N''; LimitDescription='',inserted.LimitDescription),0 INTO @Delta
   FROM dbo.SampleTests st JOIN @Rows r ON r.SampleTestId=st.SampleTestID WHERE st.SampleID=@SampleId AND r.WriteResult=0 AND ISNULL(st.Remarks,N'''')<>ISNULL(r.Remarks,N'''');
  UPDATE st SET ResultValue=CONVERT(decimal(18,4),r.ResultValue),Remarks=NULLIF(r.Remarks,N''''),ResultStatus=r.ResultStatus,DeviationType=NULLIF(r.DeviationType,N''''),EnteredBy=@Actor,ResultEnteredDate=@Now
   OUTPUT inserted.SampleTestID,CONCAT(N''ResultValue='',deleted.ResultValue,N''; Remarks='',deleted.Remarks,N''; ResultStatus='',deleted.ResultStatus,N''; DeviationType='',deleted.DeviationType,N''; LimitDescription='',deleted.LimitDescription),
    CONCAT(N''ResultValue='',inserted.ResultValue,N''; Remarks='',inserted.Remarks,N''; ResultStatus='',inserted.ResultStatus,N''; DeviationType='',inserted.DeviationType,N''; LimitDescription='',inserted.LimitDescription),1 INTO @Delta
   FROM dbo.SampleTests st JOIN @Rows r ON r.SampleTestId=st.SampleTestID WHERE st.SampleID=@SampleId AND r.WriteResult=1;
  INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,SourceWorkstation,SourceApplication,UtcRecordedAt,FieldName,TestName,SampleNumber,ModuleName)
  SELECT N''SampleTests'',d.RecordId,CASE WHEN d.WriteResult=1 THEN N''Water Result Entry'' ELSE N''Water Result Remarks'' END,d.OldValue,d.NewValue,@Actor,@Now,@Actor,@Reason,@Utc,HOST_NAME(),N''PharmaLIMS.TrustedSigning'',@Utc,
   CASE WHEN d.WriteResult=1 THEN N''ResultValue'' ELSE N''Remarks'' END,COALESCE(NULLIF(st.TestNameSnapshot,N''''),t.TestName),@Number,N''Water''
  FROM @Delta d JOIN dbo.SampleTests st ON st.SampleTestID=d.RecordId LEFT JOIN dbo.Tests t ON t.TestID=st.TestID;
  -- Read back after every result trigger, before evidence/signature/receipt can commit.
  IF EXISTS(SELECT 1 FROM @Rows r LEFT JOIN dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK) ON st.SampleTestID=r.SampleTestId AND st.SampleID=@SampleId
   WHERE r.WriteResult=1 AND (st.SampleTestID IS NULL OR st.ResultValue IS NULL OR st.ResultValue<>CONVERT(decimal(18,4),r.ResultValue)))
   THROW 57024,''Water result precision changed during persistence; the entire transaction must roll back.'',1;
  DECLARE @RowId int,@Equipment int,@Previous int,@Test int,@TestName nvarchar(200),@Raw nvarchar(200),@Kit nvarchar(100),@Lot nvarchar(100),@Expiry date,@Supports nvarchar(max),@Execution nvarchar(max),@Batch uniqueidentifier;
  DECLARE evidence CURSOR LOCAL FAST_FORWARD FOR SELECT r.SampleTestId,r.EquipmentId,st.TestID,COALESCE(NULLIF(st.TestNameSnapshot,N''''),t.TestName),r.RawResultSnapshot,r.TestKitCode,r.TestKitLot,r.TestKitExpiry,r.SupportingEquipmentIds,r.Execution
   FROM @Rows r JOIN dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK) ON st.SampleTestID=r.SampleTestId AND st.SampleID=@SampleId LEFT JOIN dbo.Tests t WITH(UPDLOCK,HOLDLOCK) ON t.TestID=st.TestID WHERE r.WriteEvidence=1 ORDER BY r.SampleTestId;
  OPEN evidence;
  FETCH NEXT FROM evidence INTO @RowId,@Equipment,@Test,@TestName,@Raw,@Kit,@Lot,@Expiry,@Supports,@Execution;
  WHILE @@FETCH_STATUS=0
  BEGIN
   IF NULLIF(LTRIM(RTRIM(@Raw)),N'''') IS NULL OR NOT EXISTS(SELECT 1 FROM dbo.SampleTests WHERE SampleTestID=@RowId AND SampleID=@SampleId AND ResultValue IS NOT NULL AND ResultValue=TRY_CONVERT(decimal(18,4),@Raw))
    THROW 57025,''Water signed resource evidence does not match the persisted result.'',1;
   IF @Expiry IS NOT NULL AND @Expiry<CAST(@Now AS date) OR (NULLIF(@Kit,N'''') IS NOT NULL AND (NULLIF(@Lot,N'''') IS NULL OR @Expiry IS NULL))
    THROW 57026,''Water test kit lot or expiry is missing or expired.'',1;
   IF EXISTS(SELECT 1 FROM OPENJSON(@Supports) WHERE type<>2 OR TRY_CONVERT(int,value) IS NULL)
    OR EXISTS(SELECT value FROM OPENJSON(@Supports) GROUP BY value HAVING COUNT(*)>1)
    OR EXISTS(SELECT 1 FROM OPENJSON(@Supports) WHERE TRY_CONVERT(int,value)=@Equipment)
    THROW 57026,''Invalid or duplicate Water supporting equipment.'',1;
   DECLARE @Selected TABLE(EquipmentId int PRIMARY KEY);
   DELETE @Selected;
   INSERT @Selected SELECT TRY_CONVERT(int,value) FROM OPENJSON(@Supports);
   IF @Equipment IS NOT NULL INSERT @Selected VALUES(@Equipment);
   IF EXISTS(SELECT 1 FROM @Selected s LEFT JOIN dbo.LabEquipment e WITH(UPDLOCK,HOLDLOCK) ON e.EquipmentID=s.EquipmentId WHERE e.EquipmentID IS NULL OR e.IsActive<>1 OR e.EquipmentStatus<>N''Active''
    OR e.QualificationStatus NOT IN(N''Qualified'',N''Due Soon'',N''Not Required'') OR e.NextQualificationDate<CAST(@Now AS date)
    OR (e.CalibrationRequired=1 AND (e.CalibrationStatus NOT IN(N''Calibrated'',N''Due Soon'',N''Not Required'') OR e.NextCalibrationDate<CAST(@Now AS date))))
    THROW 57027,''Selected Water laboratory equipment is unavailable at the database clock.'',1;
   IF @Equipment IS NOT NULL AND EXISTS(SELECT 1 FROM dbo.LabEquipmentCompatibilityRules WITH(UPDLOCK,HOLDLOCK) WHERE Module=N''WATER'' AND MatchKey=N''TESTID:''+CONVERT(nvarchar(20),@Test) AND IsActive=1)
    AND NOT EXISTS(SELECT 1 FROM dbo.LabEquipmentCompatibilityRules r WITH(UPDLOCK,HOLDLOCK) JOIN dbo.LabEquipment e WITH(UPDLOCK,HOLDLOCK) ON e.EquipmentType=r.EquipmentType WHERE e.EquipmentID=@Equipment AND r.Module=N''WATER'' AND r.MatchKey=N''TESTID:''+CONVERT(nvarchar(20),@Test) AND r.IsActive=1)
    THROW 57028,''Primary Water equipment is incompatible with the locked test.'',1;
   SET @Previous=NULL;
   SELECT @Previous=EquipmentID FROM dbo.LabEquipmentUsage WITH(UPDLOCK,HOLDLOCK) WHERE Module=N''WATER'' AND ParentRecordID=@SampleId AND ResultRecordID=@RowId;
   IF ISNULL(@Previous,0)<>ISNULL(@Equipment,0)
   BEGIN
    IF @Equipment IS NULL DELETE dbo.LabEquipmentUsage WHERE Module=N''WATER'' AND ParentRecordID=@SampleId AND ResultRecordID=@RowId;
    ELSE
    BEGIN
     UPDATE dbo.LabEquipmentUsage SET EquipmentID=@Equipment,AssignedBy=@Actor,AssignedAt=@Utc WHERE Module=N''WATER'' AND ParentRecordID=@SampleId AND ResultRecordID=@RowId;
     IF @@ROWCOUNT=0 INSERT dbo.LabEquipmentUsage(Module,ParentRecordID,ResultRecordID,EquipmentID,AssignedBy,AssignedAt) VALUES(N''WATER'',@SampleId,@RowId,@Equipment,@Actor,@Utc);
    END;
    INSERT dbo.LabEquipmentUsageHistory(Module,ParentRecordID,ResultRecordID,PreviousEquipmentID,EquipmentID,EquipmentCodeSnapshot,EquipmentNameSnapshot,EquipmentTypeSnapshot,EquipmentStatusSnapshot,QualificationStatusSnapshot,CalibrationStatusSnapshot,NextQualificationDateSnapshot,NextCalibrationDateSnapshot,ChangeType,MeaningOfSignature,ActionReason,SignedBy,UserRole,SignedAt,SourceWorkstation)
    SELECT N''WATER'',@SampleId,@RowId,@Previous,@Equipment,e.EquipmentCode,e.EquipmentName,e.EquipmentType,e.EquipmentStatus,e.QualificationStatus,e.CalibrationStatus,e.NextQualificationDate,e.NextCalibrationDate,
     CASE WHEN @Equipment IS NULL THEN N''CLEAR'' WHEN @Previous IS NULL THEN N''ASSIGN'' ELSE N''REASSIGN'' END,@Meaning,@Reason,@Actor,@Role,@Utc,HOST_NAME()
    FROM (SELECT 1 AS n) x LEFT JOIN dbo.LabEquipment e WITH(UPDLOCK,HOLDLOCK) ON e.EquipmentID=@Equipment;
    INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,SourceWorkstation,SourceApplication,UtcRecordedAt,FieldName,TestName,SampleNumber,ModuleName)
    VALUES(N''LabEquipmentUsage'',@RowId,CASE WHEN @Equipment IS NULL THEN N''Laboratory Equipment Assignment Cleared'' ELSE N''Laboratory Equipment Assignment'' END,CONVERT(nvarchar(20),@Previous),CONVERT(nvarchar(20),@Equipment),@Actor,@Now,@Actor,@Reason,@Utc,HOST_NAME(),N''PharmaLIMS.TrustedSigning'',@Utc,N''EquipmentID'',@TestName,@Number,N''Water'');
   END;
   SET @Batch=NEWID();
   IF @Equipment IS NULL AND NULLIF(@Kit,N'''') IS NULL
    INSERT dbo.WaterResultResourceEvidence(BatchID,SampleID,SampleTestID,TestID,ResourceKind,ResultSnapshot,SignedBy,ExecutionDate)
    VALUES(@Batch,@SampleId,@RowId,@Test,N''NO_INSTRUMENT'',@Raw,@Actor,CAST(@Now AS date));
   ELSE IF NULLIF(@Kit,N'''') IS NOT NULL
    INSERT dbo.WaterResultResourceEvidence(BatchID,SampleID,SampleTestID,TestID,ResourceKind,KitCode,KitLot,KitExpiry,ResultSnapshot,SignedBy,ExecutionDate)
    VALUES(@Batch,@SampleId,@RowId,@Test,N''TEST_KIT'',@Kit,@Lot,@Expiry,@Raw,@Actor,CAST(@Now AS date));
   INSERT dbo.WaterResultResourceEvidence(BatchID,SampleID,SampleTestID,TestID,ResourceKind,EquipmentID,EquipmentCodeSnapshot,EquipmentNameSnapshot,ResultSnapshot,SignedBy,ExecutionDate)
    SELECT @Batch,@SampleId,@RowId,@Test,N''INSTRUMENT'',e.EquipmentID,e.EquipmentCode,e.EquipmentName,@Raw,@Actor,CAST(@Now AS date)
    FROM @Selected s JOIN dbo.LabEquipment e WITH(UPDLOCK,HOLDLOCK) ON e.EquipmentID=s.EquipmentId ORDER BY CASE WHEN e.EquipmentID=@Equipment THEN 0 ELSE 1 END,e.EquipmentID;
   IF @Test IN(2,3)
   BEGIN
    IF @Equipment IS NULL OR ISJSON(@Execution)<>1 OR ISNULL(TRY_CONVERT(bit,JSON_VALUE(@Execution,''$.VerificationConfirmed'')),0)<>1
     OR NULLIF(LTRIM(RTRIM(JSON_VALUE(@Execution,''$.VerificationReference''))),N'''') IS NULL
     OR (@Test=3 AND (TRY_CONVERT(decimal(5,2),JSON_VALUE(@Execution,''$.SampleTemperatureC'')) IS NULL
       OR TRY_CONVERT(decimal(38,18),JSON_VALUE(@Execution,''$.SampleTemperatureC''))<>TRY_CONVERT(decimal(5,2),JSON_VALUE(@Execution,''$.SampleTemperatureC''))
       OR TRY_CONVERT(decimal(5,2),JSON_VALUE(@Execution,''$.SampleTemperatureC'')) NOT BETWEEN 24 AND 26))
     THROW 57029,''Structured Water LES verification or conductivity temperature is incomplete.'',1;
    INSERT dbo.WaterResultExecutionEvidence(SampleID,SampleTestID,TestID,ProcedureReference,MethodGuidanceSnapshot,SampleTemperatureC,VerificationReference,VerificationConfirmed,ExecutionRemarks,RawResultSnapshot,EquipmentID,EquipmentCodeSnapshot,EquipmentNameSnapshot,EquipmentTypeSnapshot,SignedBy,UserRole,MeaningOfSignature,ActionReason,SignedAt,SourceWorkstation)
    SELECT @SampleId,@RowId,@Test,N''MQC-G-0018'',CASE @Test WHEN 2 THEN N''Routine direct pH measurement using a calibrated pH meter according to the current approved MQC-G-0018 method. Follow any stage-specific KCl instruction only when that approved method explicitly requires it.''
     ELSE N''Conductivity: collect 100 mL in clean conductivity-grade glassware, avoid air bubbles, maintain 25 C +/- 1 C, verify the calibrated conductivity meter with the approved KCl standard, and record the reading in uS/cm.'' END,
     CASE WHEN @Test=3 THEN TRY_CONVERT(decimal(5,2),JSON_VALUE(@Execution,''$.SampleTemperatureC'')) ELSE NULL END,JSON_VALUE(@Execution,''$.VerificationReference''),1,JSON_VALUE(@Execution,''$.ExecutionRemarks''),@Raw,e.EquipmentID,e.EquipmentCode,e.EquipmentName,e.EquipmentType,@Actor,@Role,@Meaning,@Reason,@Utc,HOST_NAME()
    FROM dbo.LabEquipment e WITH(UPDLOCK,HOLDLOCK) WHERE e.EquipmentID=@Equipment;
   END;
   FETCH NEXT FROM evidence INTO @RowId,@Equipment,@Test,@TestName,@Raw,@Kit,@Lot,@Expiry,@Supports,@Execution;
  END;
  CLOSE evidence; DEALLOCATE evidence;
  DECLARE @Qe int,@EventNumber nvarchar(60),@QeCreated bit=0,@Affected int=0;
  IF EXISTS(SELECT 1 FROM dbo.SampleTests st WHERE st.SampleID=@SampleId AND st.ResultStatus=N''OOS'') AND (@Status<>N''Released After Investigation'' OR @NewOos=1)
  BEGIN
   SELECT TOP(1) @Qe=QualityEventID,@EventNumber=EventNumber FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK)
    WHERE SampleID=@SampleId AND EventType=N''OOS'' AND CurrentStatus NOT IN(N''Closed'',N''QA Closed'',N''Cancelled'',N''Rejected Closed'') ORDER BY QualityEventID DESC;
   IF @Qe IS NULL
   BEGIN
    DECLARE @DateKey nvarchar(8)=CONVERT(nvarchar(8),@Now,112),@SeqKey nvarchar(100)=N''QE-''+CONVERT(nvarchar(8),@Now,112),@Next int,@Period int=CONVERT(int,CONVERT(nvarchar(8),@Now,112));
    SET @LockName=N''EQ001.Water.QualityEvent.''+@DateKey;
    EXEC @Lock=sys.sp_getapplock @Resource=@LockName,@LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=10000;
    IF @Lock<0 THROW 57004,''Quality Event numbering is busy.'',1;
    SELECT @Next=LastNumber FROM dbo.LIMS_NumberSequences WITH(UPDLOCK,HOLDLOCK) WHERE SequenceKey=@SeqKey OR (NumberKey=N''QE'' AND YearNo=@Period);
    IF @Next IS NULL INSERT dbo.LIMS_NumberSequences(NumberKey,YearNo,LastNumber,Prefix,CreatedAt,UpdatedAt,SequenceKey) VALUES(N''QE'',@Period,0,N''QE'',@Now,@Now,@SeqKey);
    UPDATE dbo.LIMS_NumberSequences SET LastNumber=LastNumber+1,UpdatedAt=@Now WHERE SequenceKey=@SeqKey OR (NumberKey=N''QE'' AND YearNo=@Period);
    IF @@ROWCOUNT<>1 THROW 57030,''Ambiguous Quality Event numbering state.'',1;
    SELECT @Next=LastNumber FROM dbo.LIMS_NumberSequences WHERE SequenceKey=@SeqKey OR (NumberKey=N''QE'' AND YearNo=@Period);
    SET @EventNumber=N''QE-''+@DateKey+N''-''+CASE WHEN @Next<10000 THEN RIGHT(N''0000''+CONVERT(nvarchar(10),@Next),4) ELSE CONVERT(nvarchar(10),@Next) END;
    INSERT dbo.QualityEvents(EventNumber,EventType,Severity,SampleID,SampleNumber,CurrentStatus,DetectedBy,DetectedDate,DetectionSource,InitialDescription,ImmediateAction,CAPARequired,CreatedDate,ModifiedBy,ModifiedDate)
     VALUES(@EventNumber,N''OOS'',N''Major'',@SampleId,@Number,N''Open'',@Actor,@Now,N''Result Entry'',N''OOS detected during result entry.'',N''Sample placed on hold. Approval and COA issuance are blocked pending QA disposition.'',0,@Now,@Actor,@Now);
    SET @Qe=CONVERT(int,SCOPE_IDENTITY()); SET @QeCreated=1;
    INSERT dbo.QualityEventActions(QualityEventID,ActionType,ActionDescription,PerformedBy,PerformedDate)
     VALUES(@Qe,N''System Opened'',N''Quality Event opened automatically because one or more OOS results were saved.'',@Actor,@Now);
   END
   ELSE UPDATE dbo.QualityEvents SET ModifiedBy=@Actor,ModifiedDate=@Now WHERE QualityEventID=@Qe;
   INSERT dbo.QualityEventAffectedResults(QualityEventID,SampleTestID,TestID,TestName,ResultValue,SpecificationLimit,Unit,FailureType,CreatedDate)
    SELECT @Qe,st.SampleTestID,st.TestID,COALESCE(NULLIF(st.TestNameSnapshot,N''''),t.TestName,N''Unmapped Test''),st.ResultValue,
     CASE WHEN NULLIF(st.TestNameSnapshot,N'''') IS NOT NULL THEN COALESCE(NULLIF(st.LimitDescription,N''''),N''Action Limit: ''+CONVERT(nvarchar(50),st.ActionLimitSnapshot),N''Alert Limit: ''+CONVERT(nvarchar(50),st.AlertLimitSnapshot))
      ELSE COALESCE(N''Action Limit: ''+CONVERT(nvarchar(50),t.ActionLimit),N''Alert Limit: ''+CONVERT(nvarchar(50),t.AlertLimit)) END,
     CASE WHEN NULLIF(st.TestNameSnapshot,N'''') IS NOT NULL THEN st.UnitSnapshot ELSE t.Unit END,ISNULL(st.ResultStatus,N''OOS''),@Now
    FROM dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK) LEFT JOIN dbo.Tests t WITH(UPDLOCK,HOLDLOCK) ON t.TestID=st.TestID
    WHERE st.SampleID=@SampleId AND UPPER(ISNULL(st.ResultStatus,N'''')) IN(N''OOS'',N''ACTION'',N''FAIL'')
    AND NOT EXISTS(SELECT 1 FROM dbo.QualityEventAffectedResults a WITH(UPDLOCK,HOLDLOCK) WHERE a.QualityEventID=@Qe AND a.SampleTestID=st.SampleTestID);
   SET @Affected=@@ROWCOUNT;
   IF NOT EXISTS(SELECT 1 FROM dbo.QualityEventAffectedResults WITH(UPDLOCK,HOLDLOCK) WHERE QualityEventID=@Qe) THROW 57031,''OOS Quality Event has no affected Water result.'',1;
   IF @QeCreated=0 AND @Affected>0 INSERT dbo.QualityEventActions(QualityEventID,ActionType,ActionDescription,PerformedBy,PerformedDate)
    VALUES(@Qe,N''OOS Result Linked'',N''Additional OOS/ACTION result evidence was linked during controlled result entry.'',@Actor,@Now);
   INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,SourceWorkstation,SourceApplication,UtcRecordedAt,FieldName,SampleNumber,ModuleName)
    VALUES(N''QualityEvents'',@Qe,CASE WHEN @QeCreated=1 THEN N''Open Quality Event'' ELSE N''Update Quality Event'' END,CASE WHEN @QeCreated=1 THEN N'''' ELSE N''Existing open OOS Quality Event'' END,CONCAT(N''EventNumber='',@EventNumber,N''; AffectedResultsAdded='',@Affected),@Actor,@Now,@Actor,N''OOS/ACTION result evidence linked during result entry.'',@Utc,HOST_NAME(),N''PharmaLIMS.TrustedSigning'',@Utc,N''CurrentStatus'',@Number,N''Water'');
   SET @NewStatus=N''Under Investigation'';
  END
  ELSE IF @Status=N''Released After Investigation'' AND @NewOos=0 SET @NewStatus=@Status;
  ELSE IF EXISTS(SELECT 1 FROM dbo.SampleTests WHERE SampleID=@SampleId AND ResultValue IS NULL) SET @NewStatus=N''Partially Entered'';
  ELSE IF EXISTS(SELECT 1 FROM dbo.SampleTests st LEFT JOIN dbo.Tests t ON t.TestID=st.TestID WHERE st.SampleID=@SampleId AND st.ResultStatus=N''ALERT'' AND UPPER(LTRIM(RTRIM(COALESCE(NULLIF(st.TestNameSnapshot,N''''),t.TestName)))) NOT IN(N''TASTE'',N''ODOR'',N''ODOUR'',N''TASTE AND ODOR'',N''TASTE & ODOR'',N''TASTE AND ODOUR'',N''TASTE & ODOUR'')) SET @NewStatus=N''Alert'';
  ELSE SET @NewStatus=N''Results Entered'';
 END;
 IF @Operation=N''SubmitReview'' UPDATE dbo.Samples SET SubmittedBy=@Actor,SubmittedDate=@Now WHERE SampleID=@SampleId;
 IF @Operation=N''Review'' UPDATE dbo.Samples SET ReviewedBy=@Actor,ReviewDate=@Now,ReviewedDate=@Now,ReviewedDateTime=@Now WHERE SampleID=@SampleId;
 IF @Operation=N''Approve'' UPDATE dbo.Samples SET ApprovedBy=@Actor,ApprovalDate=@Now,ApprovedDate=@Now,ApprovedDateTime=@Now WHERE SampleID=@SampleId;
 IF @Operation=N''SaveResults'' AND EXISTS(SELECT 1 FROM @Rows WHERE WriteResult=1) AND NOT EXISTS(SELECT 1 FROM dbo.SampleTests WITH(UPDLOCK,HOLDLOCK) WHERE SampleID=@SampleId AND ResultValue IS NULL)
  UPDATE dbo.Samples SET AnalysisCompletedDateTime=COALESCE(AnalysisCompletedDateTime,@Now) WHERE SampleID=@SampleId;
 UPDATE dbo.Samples SET Status=@NewStatus,ModifiedBy=@Actor,ModifiedDate=@Now WHERE SampleID=@SampleId AND Status=@Status;
 IF @@ROWCOUNT<>1 THROW 57010,''Water sample changed before mutation.'',1;
 IF @Operation=N''Approve''
 BEGIN
  DECLARE @PlanChanges TABLE(PlanId int,PlanNo nvarchar(50),OldStatus nvarchar(40),NewStatus nvarchar(40));
  UPDATE ps SET Status=N''Approved'' FROM dbo.Water_PlanSamples ps WITH(UPDLOCK,HOLDLOCK) JOIN dbo.Samples s WITH(UPDLOCK,HOLDLOCK) ON s.SampleID=ps.SampleID
   WHERE ps.WaterPlanID IN(SELECT WaterPlanID FROM dbo.Water_PlanSamples WITH(UPDLOCK,HOLDLOCK) WHERE SampleID=@SampleId) AND s.Status IN(N''Approved'',N''COA Issued'');
  UPDATE p SET Status=CASE WHEN p.Status=N''Cancelled'' THEN p.Status
   WHEN EXISTS(SELECT 1 FROM dbo.Water_PlanSamples x WITH(UPDLOCK,HOLDLOCK) WHERE x.WaterPlanID=p.WaterPlanID AND x.Status=N''Rejected'') THEN N''Recollection Required''
   WHEN NOT EXISTS(SELECT 1 FROM dbo.Water_PlanSamples x WITH(UPDLOCK,HOLDLOCK) LEFT JOIN dbo.Samples s WITH(UPDLOCK,HOLDLOCK) ON s.SampleID=x.SampleID WHERE x.WaterPlanID=p.WaterPlanID AND (x.SampleID IS NULL OR ISNULL(s.Status,N'''') NOT IN(N''Approved'',N''COA Issued''))) THEN N''Completed''
   ELSE N''Analysis In Progress'' END
  OUTPUT inserted.WaterPlanID,inserted.PlanNo,deleted.Status,inserted.Status INTO @PlanChanges
  FROM dbo.Water_Plans p WITH(UPDLOCK,HOLDLOCK) WHERE p.WaterPlanID IN(SELECT WaterPlanID FROM dbo.Water_PlanSamples WITH(UPDLOCK,HOLDLOCK) WHERE SampleID=@SampleId);
  INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,SourceWorkstation,SourceApplication,UtcRecordedAt,FieldName,SampleNumber,ModuleName)
   SELECT N''Water_Plans'',PlanId,N''Water Plan Status Synchronized'',OldStatus,NewStatus,@Actor,@Now,@Actor,N''Automatically synchronized from approved water samples'',@Utc,HOST_NAME(),N''PharmaLIMS.TrustedSigning'',@Utc,N''Status'',PlanNo,N''Water'' FROM @PlanChanges WHERE OldStatus<>NewStatus;
 END;
 SET @Action=CASE @Operation WHEN N''StartAnalysis'' THEN N''Analysis Start'' WHEN N''SaveResults'' THEN N''Result Entry'' WHEN N''SubmitReview'' THEN N''Submit Review'' WHEN N''Review'' THEN N''Review'' WHEN N''Approve'' THEN N''Approval'' ELSE N''Water Time Correction'' END;
 INSERT dbo.ElectronicSignatures(SampleID,ActionType,ActionReason,SignedBy,MeaningOfSignature,UserRole,SignedAt,CreatedUtc,SourceWorkstation,SourceApplication,UtcSignedAt)
  VALUES(@SampleId,@Action,@Reason,@Actor,@Meaning,@Role,@Now,@Utc,HOST_NAME(),N''PharmaLIMS.TrustedSigning'',@Utc);
 DECLARE @SignatureId int=CONVERT(int,SCOPE_IDENTITY());
 SET @NewHeader=(SELECT * FROM dbo.Samples WHERE SampleID=@SampleId FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
 INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,SourceWorkstation,SourceApplication,UtcRecordedAt,FieldName,SampleNumber,ModuleName)
  VALUES(N''Samples'',@SampleId,@Action,@OldHeader,@NewHeader,@Actor,@Now,@Actor,@Reason,@Utc,HOST_NAME(),N''PharmaLIMS.TrustedSigning'',@Utc,CASE WHEN @Operation=N''CorrectTimes'' THEN N''WaterDateTimes'' ELSE N''Status'' END,@Number,N''Water'');
 DECLARE @CompletedCount int,@PendingCount int,@QeSummary nvarchar(500)=N'''';
 SELECT @CompletedCount=SUM(CASE WHEN ResultValue IS NOT NULL THEN 1 ELSE 0 END),@PendingCount=SUM(CASE WHEN ResultValue IS NULL THEN 1 ELSE 0 END) FROM dbo.SampleTests WITH(UPDLOCK,HOLDLOCK) WHERE SampleID=@SampleId;
 SELECT TOP(1) @QeSummary=CONCAT(EventNumber,N'' - '',EventType,N'' - '',CurrentStatus,N'' - '',Severity) FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK) WHERE SampleID=@SampleId AND CurrentStatus NOT IN(N''Closed'',N''QA Closed'',N''Cancelled'',N''Rejected Closed'') ORDER BY QualityEventID DESC;
 SET @Receipt=(SELECT @RequestId RequestId,N''Committed'' State,@SampleId SampleId,@Number SampleNumber,@NewStatus Status,@SignatureId SignatureId,ISNULL(@CompletedCount,0) CompletedTests,ISNULL(@PendingCount,0) PendingTests,@QeSummary QualityEventSummary FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
 INSERT dbo.EQ001_WaterWorkflowReceipts(RequestId,Operation,SampleId,PayloadHash,Actor,CallerSid,ActorVersion,State,Outcome,Reason,Meaning,RecordedAtUtc)
  VALUES(@RequestId,@Operation,@SampleId,@Hash,@Actor,@CallerSid,@ActorVersion,N''Committed'',@Receipt,@Reason,@Meaning,@Utc);
 -- Caller retains the Serializable transaction; this procedure never commits it.
 SELECT @Receipt;
END;
';
 IF OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow') IS NOT NULL AND
  (OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow',N'P') IS NULL OR OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow')) IS NULL OR
   HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow')),CHARINDEX(N'PROCEDURE',OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow'))),2147483647),CHAR(13),N''))<>
   HASHBYTES('SHA2_256',REPLACE(SUBSTRING(@Procedure,CHARINDEX(N'PROCEDURE',@Procedure),2147483647),CHAR(13),N'')))
  THROW 57000,'An unknown existing Water analysis procedure must not be overwritten.',1;
 IF OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow') IS NULL EXEC(@Procedure);
 IF OBJECT_ID(N'dbo.TRG_EQ001_WaterWorkflowReceipts_AppendOnly',N'TR') IS NULL
  EXEC(N'CREATE TRIGGER dbo.TRG_EQ001_WaterWorkflowReceipts_AppendOnly ON dbo.EQ001_WaterWorkflowReceipts AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 57032,''Water workflow receipts are append-only.'',1; END;');
 GRANT EXECUTE ON dbo.EQ001_UAT_WaterWorkflow TO PharmaLIMS_TrustedEquipmentSigner;
 DENY INSERT,UPDATE,DELETE ON dbo.EQ001_WaterWorkflowReceipts TO PharmaLIMS_TrustedEquipmentSigner;
 COMMIT;
 PRINT N'PASS: Exact isolated Water analysis boundary staged; historical clinical rows and existing client rights are unchanged.';
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
