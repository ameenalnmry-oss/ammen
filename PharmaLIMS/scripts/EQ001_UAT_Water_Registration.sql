-- Explicit EQ001 Water staging only; disabled until the reviewed host flag is enabled.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
DECLARE @VerifiedIsolatedUat bit = 0;
IF @VerifiedIsolatedUat<>1 OR DB_NAME()<>N'EQ001_UAT_AMEEN_20261024' OR @@SERVERNAME<>N'AMEEN\SQLEXPRESS'
 THROW 56900,'Explicit exact-site isolated UAT confirmation is required.',1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
 THROW 56900,'Existing independent service principal is required.',1;
IF COL_LENGTH(N'dbo.Users',N'AuthenticationRowVersion')<>8 OR COL_LENGTH(N'dbo.Samples',N'SampleNumber')<>40
 OR COL_LENGTH(N'dbo.SampleTests',N'ResultRowVersion')<>8
 THROW 56900,'Actual Water registration schema prerequisites are missing.',1;
IF NOT EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.ElectronicSignatures') AND name=N'TRG_ElectronicSignatures_AppendOnly' AND is_disabled=0)
 OR NOT EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.AuditTrail') AND name=N'TRG_AuditTrail_AppendOnly' AND is_disabled=0)
 OR NOT EXISTS(SELECT 1 FROM sys.triggers WHERE name=N'TRG_Water_PlanSampleTests_FreezeDistributed_20260828' AND is_disabled=0)
 OR NOT EXISTS(SELECT 1 FROM sys.triggers WHERE name=N'TRG_Water_PlanSamples_ProtectDistributed_20260828' AND is_disabled=0)
 THROW 56900,'Enabled historical signature/audit and Water Plan protection is required.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 IF OBJECT_ID(N'dbo.EQ001_WaterRegistrationReceipts',N'U') IS NULL
  CREATE TABLE dbo.EQ001_WaterRegistrationReceipts(RequestId uniqueidentifier NOT NULL PRIMARY KEY,PayloadHash binary(32) NOT NULL,
   Actor nvarchar(100) NOT NULL,CallerSid nvarchar(184) NOT NULL,State nvarchar(12) NOT NULL CHECK(State IN(N'Committed',N'Cancelled')),
   SampleId int NULL REFERENCES dbo.Samples(SampleID),SampleNumber nvarchar(20) NULL,SampleStatus nvarchar(30) NULL,TestCount int NOT NULL,RecordedAtUtc datetime2(7) NOT NULL,Reason nvarchar(500) NOT NULL,Meaning nvarchar(200) NOT NULL,ActorVersion binary(8) NOT NULL,
   CHECK((State=N'Committed' AND SampleId IS NOT NULL AND TestCount>0) OR (State=N'Cancelled' AND SampleId IS NULL AND TestCount=0)));
 IF OBJECT_ID(N'dbo.EQ001_WaterLabelReceipts',N'U') IS NULL
  CREATE TABLE dbo.EQ001_WaterLabelReceipts(RequestId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES dbo.EQ001_WaterRegistrationReceipts(RequestId),
   Actor nvarchar(100) NOT NULL,CallerSid nvarchar(184) NOT NULL,RecordedAtUtc datetime2(7) NOT NULL);
 DECLARE @Procedure nvarchar(max)=N'CREATE OR ALTER PROCEDURE dbo.EQ001_UAT_WaterRegistration
 @Action nvarchar(16), @RequestId uniqueidentifier, @Payload nvarchar(max),
 @Actor nvarchar(100), @ActorVersion binary(8), @CallerSid nvarchar(184),
 @Reason nvarchar(500), @Meaning nvarchar(200)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF DB_NAME()<>N''EQ001_UAT_AMEEN_20261024'' OR @@SERVERNAME<>N''AMEEN\SQLEXPRESS'' OR @@TRANCOUNT=0
  THROW 56900,''Water registration requires the exact isolated UAT and an explicit transaction.'',1;
 IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N''PharmaLIMS_TrustedEquipmentSigner''
   AND type=N''U'' AND sid=SUSER_SID(ORIGINAL_LOGIN()))
   OR ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)<>0
  THROW 56901,''Only the independent Windows signing service may execute this operation.'',1;
 IF @Action NOT IN(N''Register'',N''Resolve'',N''Cancel'',N''Label'',N''ReadSpecs'') OR @Action IS NULL
  OR @RequestId IS NULL OR @RequestId=CONVERT(uniqueidentifier,0x0) OR ISJSON(@Payload)<>1
  OR @Payload IS NULL OR DATALENGTH(@Payload)>32768 OR NULLIF(LTRIM(RTRIM(@CallerSid)),N'''') IS NULL
  OR NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL OR NULLIF(LTRIM(RTRIM(@Meaning)),N'''') IS NULL
  THROW 56902,''Invalid Water request.'',1;
 DECLARE @Role nvarchar(50), @ActualVersion binary(8);
 SELECT @Role=Role,@ActualVersion=AuthenticationRowVersion
 FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
 WHERE Username=@Actor AND IsActive=1 AND IsLocked=0 AND MustChangePassword=0 AND CanRegisterSamples=1
 AND (LockedUntil IS NULL OR LockedUntil<=SYSDATETIME());
 IF @ActualVersion IS NULL OR @ActorVersion IS NULL OR @ActualVersion<>@ActorVersion
  THROW 56903,''Registration actor or authentication state changed.'',1;
 DECLARE @Hash binary(32)=HASHBYTES(''SHA2_256'',@Payload),@Lock int;
 DECLARE @LockName nvarchar(255)=N''EQ001.Water.''+CONVERT(nvarchar(36),@RequestId);
 EXEC @Lock=sys.sp_getapplock @Resource=@LockName,@LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=10000;
 IF @Lock<0 THROW 56904,''Water request is busy; retry the same request identity.'',1;
 DECLARE @State nvarchar(12),@SampleId int,@Number nvarchar(20),@Status nvarchar(30),@TestCount int=0;
 SELECT @State=State,@SampleId=SampleId,@Number=SampleNumber,@Status=SampleStatus,@TestCount=TestCount
 FROM dbo.EQ001_WaterRegistrationReceipts WITH(UPDLOCK,HOLDLOCK)
 WHERE RequestId=@RequestId;
 IF @State IS NOT NULL AND EXISTS(SELECT 1 FROM dbo.EQ001_WaterRegistrationReceipts WHERE RequestId=@RequestId
    AND (PayloadHash<>@Hash OR Actor<>@Actor OR CallerSid<>@CallerSid))
  THROW 56905,''Request identity cannot be reused with different payload or caller.'',1;
 IF @State=N''Cancelled'' OR (@State=N''Committed'' AND @Action<>N''Label'')
 BEGIN
  SELECT @RequestId RequestId,@State State,ISNULL(@SampleId,0) SampleId,ISNULL(@Number,N'''') SampleNumber,
   ISNULL(@Status,N'''') Status,ISNULL(@TestCount,0) TestCount,
   CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.EQ001_WaterLabelReceipts WHERE RequestId=@RequestId) THEN 1 ELSE 0 END) LabelPrinted
  FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
  RETURN;
 END;
 IF @Action=N''Cancel''
 BEGIN
  INSERT dbo.EQ001_WaterRegistrationReceipts(RequestId,PayloadHash,Actor,CallerSid,State,SampleId,SampleNumber,SampleStatus,TestCount,RecordedAtUtc,Reason,Meaning,ActorVersion)
  VALUES(@RequestId,@Hash,@Actor,@CallerSid,N''Cancelled'',NULL,NULL,NULL,0,SYSUTCDATETIME(),@Reason,@Meaning,@ActorVersion);
  SELECT @RequestId RequestId,N''Cancelled'' State,0 SampleId,N'''' SampleNumber,N'''' Status,0 TestCount,CONVERT(bit,0) LabelPrinted FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
  RETURN;
 END;
 IF @Action=N''Resolve''
 BEGIN
  SELECT @RequestId RequestId,N''Pending'' State,0 SampleId,N'''' SampleNumber,N'''' Status,0 TestCount,CONVERT(bit,0) LabelPrinted FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
  RETURN;
 END;
 IF @Action=N''Label''
 BEGIN
  IF @State<>N''Committed'' OR @State IS NULL THROW 56906,''A committed registration receipt is required.'',1;
  IF NOT EXISTS(SELECT 1 FROM dbo.EQ001_WaterLabelReceipts WHERE RequestId=@RequestId)
  BEGIN
   UPDATE dbo.Samples SET LabelPrinted=1 WHERE SampleID=@SampleId AND SampleNumber=@Number;
   IF @@ROWCOUNT<>1 THROW 56906,''Registered sample changed or is unavailable.'',1;
   INSERT dbo.EQ001_WaterLabelReceipts(RequestId,Actor,CallerSid,RecordedAtUtc) VALUES(@RequestId,@Actor,@CallerSid,SYSUTCDATETIME());
   INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,
    SourceWorkstation,SourceApplication,UtcRecordedAt,FieldName,SampleNumber,ModuleName)
   VALUES(N''Samples'',@SampleId,N''Water Sample Label Printed'',N''Not Printed'',N''Printed'',@Actor,SYSDATETIME(),@Actor,@Reason,SYSUTCDATETIME(),
    HOST_NAME(),N''PharmaLIMS.TrustedSigning'',SYSUTCDATETIME(),N''LabelPrinted'',@Number,N''Water'');
  END;
  SELECT @RequestId RequestId,N''Committed'' State,@SampleId SampleId,@Number SampleNumber,@Status Status,@TestCount TestCount,CONVERT(bit,1) LabelPrinted FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
  RETURN;
 END;
 DECLARE @PointId int=TRY_CONVERT(int,JSON_VALUE(@Payload,''$.PointId'')),@Type nvarchar(30)=JSON_VALUE(@Payload,''$.WaterType'');
 DECLARE @Sampling datetime2=TRY_CONVERT(datetime2,JSON_VALUE(@Payload,''$.SamplingAt''),126),@Received datetime2=TRY_CONVERT(datetime2,JSON_VALUE(@Payload,''$.ReceivedAt''),126),
  @Incubation datetime2=TRY_CONVERT(datetime2,JSON_VALUE(@Payload,''$.IncubationAt''),126),@PlanSample int=TRY_CONVERT(int,JSON_VALUE(@Payload,''$.WaterPlanSampleId''));
 DECLARE @Decision nvarchar(30)=JSON_VALUE(@Payload,''$.ReceiptDecision''),@Program nvarchar(200)=JSON_VALUE(@Payload,''$.IncubationProgram''),
  @Instrument nvarchar(100)=JSON_VALUE(@Payload,''$.IncubatorId''),@Days int,@Now datetime2=SYSDATETIME();
 DECLARE @PointCode nvarchar(50),@PointName nvarchar(200),@Location nvarchar(300),@WaterType nvarchar(100),@Prefix nvarchar(3);
 SELECT @PointCode=PointCode,@PointName=PointName,@Location=Location,@WaterType=WaterType
 FROM dbo.WaterSamplingPoints WITH(UPDLOCK,HOLDLOCK) WHERE Id=@PointId AND Status=N''Active'';
 IF @PointCode IS NULL OR @Type NOT IN(N''Purified Water'',N''Potable Water'') OR @Type IS NULL
  OR (@Type=N''Purified Water'' AND @WaterType<>N''Purified'') OR (@Type=N''Potable Water'' AND @WaterType<>N''Potable'')
  THROW 56907,''Active sampling point and Water type do not match.'',1;
 SET @Prefix=CASE WHEN @Type=N''Purified Water'' THEN N''PW'' ELSE N''PTW'' END;
 IF @Sampling IS NULL OR @Sampling<CONVERT(datetime2,''17530101'') OR @Received IS NULL OR @Received<@Sampling OR @Incubation<@Received
  OR @Sampling>DATEADD(minute,1,@Now) OR @Received>DATEADD(minute,1,@Now) OR @Incubation>DATEADD(minute,1,@Now)
  THROW 56908,''Enter actual Water timestamps in sequence, no later than the database clock.'',1;
 IF @Decision NOT IN(N''Accepted'',N''Rejected'') OR @Decision IS NULL
  OR (@Decision=N''Rejected'' AND NULLIF(LTRIM(RTRIM(JSON_VALUE(@Payload,''$.ReceiptReason''))),N'''') IS NULL)
  OR ISNULL(TRY_CONVERT(int,JSON_VALUE(@Payload,''$.ContainerCount'')),0)<=0
  THROW 56909,''Invalid sample receipt information.'',1;
 DECLARE @Ids TABLE(TestID int NOT NULL PRIMARY KEY);
 IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,''$.TestIds'') WHERE type<>2 OR TRY_CONVERT(int,value) IS NULL OR TRY_CONVERT(int,value)<=0)
  OR (SELECT COUNT(*) FROM OPENJSON(@Payload,''$.TestIds'')) NOT BETWEEN 1 AND 64
  OR EXISTS(SELECT value FROM OPENJSON(@Payload,''$.TestIds'') GROUP BY value HAVING COUNT(*)>1)
  THROW 56910,''Invalid selected Water tests.'',1;
 INSERT @Ids SELECT CONVERT(int,value) FROM OPENJSON(@Payload,''$.TestIds'');
 DECLARE @Tests TABLE(TestID int PRIMARY KEY,TestName nvarchar(200),Category nvarchar(100),Unit nvarchar(50),
  Alert decimal(18,6),ActionLimit decimal(18,6),Specification nvarchar(500));
 INSERT @Tests
 SELECT t.TestID,t.TestName,ISNULL(t.TestCategory,N''''),ISNULL(t.Unit,N''''),COALESCE(s.AlertLimit,s.LowerLimit),COALESCE(s.ActionLimit,s.UpperLimit),s.SpecificationText
 FROM @Ids ids JOIN dbo.Tests t WITH(UPDLOCK,HOLDLOCK) ON t.TestID=ids.TestID AND t.IsActive=1 AND t.IsWaterTest=1
 CROSS APPLY(SELECT TOP(1) spec.AlertLimit,spec.LowerLimit,spec.ActionLimit,spec.UpperLimit,spec.SpecificationText
  FROM dbo.WaterTestProfiles p WITH(UPDLOCK,HOLDLOCK)
  JOIN dbo.WaterSpecifications spec WITH(UPDLOCK,HOLDLOCK) ON spec.ProfileID=p.ProfileID
  WHERE UPPER(LTRIM(RTRIM(p.ProfileCode)))=@Prefix AND p.IsActive=1 AND p.ApprovalStatus=N''Approved''
   AND p.ApprovedBy IS NOT NULL AND p.ApprovedAt IS NOT NULL AND NULLIF(LTRIM(RTRIM(p.ControlledReference)),N'''') IS NOT NULL
   AND (p.EffectiveFrom IS NULL OR p.EffectiveFrom<=CAST(@Now AS date)) AND (p.EffectiveTo IS NULL OR p.EffectiveTo>=CAST(@Now AS date))
   AND spec.TestID=t.TestID AND spec.IsActive=1 AND spec.ApprovalStatus=N''Approved''
   AND NULLIF(LTRIM(RTRIM(spec.SpecificationText)),N'''') IS NOT NULL
   AND (spec.EffectiveFrom IS NULL OR spec.EffectiveFrom<=CAST(@Now AS date)) AND (spec.EffectiveTo IS NULL OR spec.EffectiveTo>=CAST(@Now AS date))
   AND (spec.PointCode IS NULL OR spec.PointCode=N'''' OR spec.PointCode=@PointCode)
  ORDER BY CASE WHEN spec.PointCode=@PointCode THEN 0 ELSE 1 END,spec.EffectiveFrom DESC,spec.SpecificationID DESC) s;
 IF (SELECT COUNT(*) FROM @Tests)<>(SELECT COUNT(*) FROM @Ids) THROW 56911,''Selected active tests need effective approved Water specifications.'',1;
 IF EXISTS(SELECT 1 FROM @Tests WHERE Alert<0 OR ActionLimit<0 OR Alert<>ROUND(Alert,4) OR ActionLimit<>ROUND(ActionLimit,4)
    OR TRY_CONVERT(decimal(18,4),Alert) IS NULL AND Alert IS NOT NULL OR TRY_CONVERT(decimal(18,4),ActionLimit) IS NULL AND ActionLimit IS NOT NULL)
  THROW 56912,''Controlled limits must fit decimal(18,4) exactly; rounding is not permitted.'',1;
 DECLARE @Micro bit=CASE WHEN @Decision=N''Accepted'' AND EXISTS(SELECT 1 FROM @Tests WHERE Category LIKE N''%micro%'') THEN 1 ELSE 0 END;
 IF @Micro=1
 BEGIN
  SET @Days=CASE @Program WHEN N''Water TAMC — 30–35 °C / 5 days'' THEN 5 WHEN N''TAMC — 30–35 °C / 3–5 days'' THEN 5 WHEN N''TYMC — 20–25 °C / 5–7 days'' THEN 7 ELSE NULL END;
  IF @Incubation IS NULL OR NULLIF(LTRIM(RTRIM(@Instrument)),N'''') IS NULL OR @Days IS NULL THROW 56913,''Selected microbiological tests require controlled incubation details.'',1;
 END
 ELSE IF @Incubation IS NOT NULL OR ISNULL(@Program,N'''')<>N'''' OR ISNULL(@Instrument,N'''')<>N''''
  THROW 56913,''Incubation must match the selected accepted microbiological tests.'',1;
 SET @Status=CASE WHEN @Decision=N''Rejected'' THEN N''Rejected'' WHEN @Micro=1 THEN N''Incubation'' ELSE N''Registered'' END;
 DECLARE @PlanId int,@OldPlanStatus nvarchar(40);
 IF @PlanSample IS NOT NULL
 BEGIN
  SELECT @PlanId=ps.WaterPlanID,@OldPlanStatus=ps.Status FROM dbo.Water_PlanSamples ps WITH(UPDLOCK,HOLDLOCK)
  JOIN dbo.Water_Plans p WITH(UPDLOCK,HOLDLOCK) ON p.WaterPlanID=ps.WaterPlanID
  WHERE ps.WaterPlanSampleID=@PlanSample AND ps.PointID=@PointId AND (ps.SampleID IS NULL OR ps.Status=N''Rejected'')
   AND p.Status IN(N''Distributed'',N''In Collection'',N''Recollection Required'')
   AND p.WaterType=@WaterType AND ps.Status IN(N''Distributed'',N''Rejected'',N''Recollection Required'');
  IF @PlanId IS NULL THROW 56914,''Water Plan is stale, linked, cancelled, or mismatched.'',1;
  IF NOT EXISTS(SELECT 1 FROM dbo.Water_PlanSampleTests WITH(HOLDLOCK) WHERE WaterPlanSampleID=@PlanSample)
   OR EXISTS(SELECT TestID FROM @Ids EXCEPT SELECT TestID FROM dbo.Water_PlanSampleTests WITH(HOLDLOCK) WHERE WaterPlanSampleID=@PlanSample)
   OR EXISTS(SELECT TestID FROM dbo.Water_PlanSampleTests WITH(HOLDLOCK) WHERE WaterPlanSampleID=@PlanSample EXCEPT SELECT TestID FROM @Ids)
   THROW 56914,''Water Plan test assignment changed.'',1;
 END;
 IF @Action=N''ReadSpecs''
 BEGIN
  SELECT TestID,TestName,Category,Unit,Alert,ActionLimit,Specification FROM @Tests ORDER BY TestID;
  RETURN;
 END;
 -- The trusted host validates numeric interpretation after ReadSpecs in this same locked transaction.
 DECLARE @Year int=YEAR(@Now),@Key nvarchar(100)=@Prefix+N''-''+CONVERT(nvarchar(4),YEAR(@Now)),@Max int=0,@Last int;
 SET @LockName=N''EQ001.Water.Number.''+@Key;
 EXEC @Lock=sys.sp_getapplock @Resource=@LockName,@LockMode=N''Exclusive'',@LockOwner=N''Transaction'',@LockTimeout=10000;
 IF @Lock<0 THROW 56904,''Water numbering is busy; retry the same request.'',1;
 SELECT @Max=ISNULL(MAX(TRY_CONVERT(int,SUBSTRING(SampleNumber,LEN(@Key)+2,20))),0)
 FROM dbo.Samples WITH(UPDLOCK,HOLDLOCK) WHERE SampleNumber LIKE @Key+N''-%'';
 SELECT @Last=LastNumber FROM dbo.LIMS_NumberSequences WITH(UPDLOCK,HOLDLOCK)
 WHERE SequenceKey=@Key OR (NumberKey=@Prefix AND YearNo=@Year);
 IF @Last IS NULL
  INSERT dbo.LIMS_NumberSequences(NumberKey,YearNo,LastNumber,Prefix,CreatedAt,UpdatedAt,SequenceKey) VALUES(@Prefix,@Year,@Max,@Prefix,@Now,@Now,@Key);
 ELSE IF @Last<@Max UPDATE dbo.LIMS_NumberSequences SET LastNumber=@Max,Prefix=@Prefix,SequenceKey=@Key,UpdatedAt=@Now WHERE SequenceKey=@Key OR (NumberKey=@Prefix AND YearNo=@Year);
 UPDATE dbo.LIMS_NumberSequences SET LastNumber=LastNumber+1,UpdatedAt=@Now WHERE SequenceKey=@Key OR (NumberKey=@Prefix AND YearNo=@Year);
 IF @@ROWCOUNT<>1 THROW 56915,''Ambiguous Water numbering state.'',1;
 SELECT @Last=LastNumber FROM dbo.LIMS_NumberSequences WHERE SequenceKey=@Key;
 SET @Number=@Key+N''-''+CASE WHEN @Last<10000 THEN RIGHT(N''0000''+CONVERT(nvarchar(10),@Last),4) ELSE CONVERT(nvarchar(10),@Last) END;
 IF LEN(@Number)>20 THROW 56915,''Water number exceeds the deployed sample number capacity.'',1;
 INSERT dbo.Samples(SampleNumber,PointID,PointCodeSnapshot,PointNameSnapshot,PointLocationSnapshot,SampleType,SamplingMethod,
  SamplingDateTime,ReceivedDateTime,IncubationStartedDateTime,SampledBy,ReceivedBy,ContainerCount,SampleVolume,ContainerCondition,
  ReceiptTemperature,ReceiptDecision,ReceiptDeviationReason,WaterIncubationProgram,WaterIncubatorID,IncubationEndDate,Status,LabelPrinted,CreatedDate)
 VALUES(@Number,@PointId,@PointCode,@PointName,@Location,@Type,N''N/A'',@Sampling,@Received,@Incubation,
  JSON_VALUE(@Payload,''$.SampledBy''),JSON_VALUE(@Payload,''$.ReceivedBy''),CONVERT(int,JSON_VALUE(@Payload,''$.ContainerCount'')),
  JSON_VALUE(@Payload,''$.SampleVolume''),JSON_VALUE(@Payload,''$.ContainerCondition''),JSON_VALUE(@Payload,''$.ReceiptTemperature''),@Decision,
  NULLIF(JSON_VALUE(@Payload,''$.ReceiptReason''),N''''),NULLIF(@Program,N''''),NULLIF(@Instrument,N''''),DATEADD(day,@Days,@Incubation),@Status,0,@Now);
 SET @SampleId=CONVERT(int,SCOPE_IDENTITY());
 INSERT dbo.SampleTests(SampleID,TestID,TestNameSnapshot,TestCategorySnapshot,UnitSnapshot,AlertLimitSnapshot,ActionLimitSnapshot,LimitDescription)
 SELECT @SampleId,TestID,TestName,Category,Unit,Alert,ActionLimit,Specification FROM @Tests;
 SET @TestCount=@@ROWCOUNT;
 IF @PlanSample IS NOT NULL
 BEGIN
  DECLARE @Outcome nvarchar(40)=CASE WHEN @Status=N''Rejected'' THEN N''Rejected'' ELSE N''Registered'' END;
  INSERT dbo.Water_PlanSampleAttempts(WaterPlanSampleID,SampleID,SampleNumber,Outcome,LinkedBy) VALUES(@PlanSample,@SampleId,@Number,@Outcome,@Actor);
  UPDATE dbo.Water_PlanSamples SET SampleID=@SampleId,SampleNumber=@Number,RegisteredAt=@Now,Status=@Outcome
  WHERE WaterPlanSampleID=@PlanSample AND (SampleID IS NULL OR Status=N''Rejected'');
  IF @@ROWCOUNT<>1 THROW 56914,''Water Plan changed before linkage.'',1;
  UPDATE dbo.Water_Plans SET Status=CASE
   WHEN EXISTS(SELECT 1 FROM dbo.Water_PlanSamples WHERE WaterPlanID=@PlanId AND Status=N''Rejected'') THEN N''Recollection Required''
   WHEN NOT EXISTS(SELECT 1 FROM dbo.Water_PlanSamples WHERE WaterPlanID=@PlanId AND Status<>N''Registered'') THEN N''Registered''
   ELSE N''In Collection'' END WHERE WaterPlanID=@PlanId;
  INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,
   SourceWorkstation,SourceApplication,UtcRecordedAt,FieldName,SampleNumber,ModuleName)
  VALUES(N''Water_PlanSamples'',@PlanSample,N''Planned Water Sample Linked'',@OldPlanStatus,@Outcome,@Actor,@Now,@Actor,
   N''Sample registration and Water Plan linkage committed atomically'',SYSUTCDATETIME(),HOST_NAME(),N''PharmaLIMS.TrustedSigning'',SYSUTCDATETIME(),N''SampleID'',@Number,N''Water'');
 END;
 INSERT dbo.ElectronicSignatures(SampleID,ActionType,ActionReason,SignedBy,MeaningOfSignature,UserRole,SignedAt,CreatedUtc,SourceWorkstation,SourceApplication,UtcSignedAt)
 VALUES(@SampleId,N''Water Sample Registration'',@Reason,@Actor,@Meaning,@Role,@Now,SYSUTCDATETIME(),HOST_NAME(),N''PharmaLIMS.TrustedSigning'',SYSUTCDATETIME());
 INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,
  SourceWorkstation,SourceApplication,UtcRecordedAt,FieldName,SampleNumber,ModuleName)
 VALUES(N''Samples'',@SampleId,N''Water Sample Registered'',N'''',@Status,@Actor,@Now,@Actor,@Reason,SYSUTCDATETIME(),HOST_NAME(),N''PharmaLIMS.TrustedSigning'',SYSUTCDATETIME(),N''Status'',@Number,N''Water'');
 INSERT dbo.EQ001_WaterRegistrationReceipts(RequestId,PayloadHash,Actor,CallerSid,State,SampleId,SampleNumber,SampleStatus,TestCount,RecordedAtUtc,Reason,Meaning,ActorVersion)
 VALUES(@RequestId,@Hash,@Actor,@CallerSid,N''Committed'',@SampleId,@Number,@Status,@TestCount,SYSUTCDATETIME(),@Reason,@Meaning,@ActorVersion);
 SELECT @RequestId RequestId,N''Committed'' State,@SampleId SampleId,@Number SampleNumber,@Status Status,@TestCount TestCount,CONVERT(bit,0) LabelPrinted FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
END;
';
 IF OBJECT_ID(N'dbo.EQ001_UAT_WaterRegistration') IS NOT NULL AND
  (OBJECT_ID(N'dbo.EQ001_UAT_WaterRegistration',N'P') IS NULL OR OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WaterRegistration')) IS NULL OR
   HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WaterRegistration')),CHARINDEX(N'PROCEDURE',OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WaterRegistration'))),2147483647),CHAR(13),N''))<>
   HASHBYTES('SHA2_256',REPLACE(SUBSTRING(@Procedure,CHARINDEX(N'PROCEDURE',@Procedure),2147483647),CHAR(13),N'')))
  THROW 56900,'An unknown existing Water procedure must not be overwritten.',1;
 IF OBJECT_ID(N'dbo.EQ001_UAT_WaterRegistration') IS NULL EXEC(@Procedure);
 IF OBJECT_ID(N'dbo.TRG_EQ001_WaterRegistrationReceipts_AppendOnly',N'TR') IS NULL EXEC(N'CREATE TRIGGER dbo.TRG_EQ001_WaterRegistrationReceipts_AppendOnly ON dbo.EQ001_WaterRegistrationReceipts AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 56916,''Water registration receipts are append-only.'',1; END;');
 IF OBJECT_ID(N'dbo.TRG_EQ001_WaterLabelReceipts_AppendOnly',N'TR') IS NULL EXEC(N'CREATE TRIGGER dbo.TRG_EQ001_WaterLabelReceipts_AppendOnly ON dbo.EQ001_WaterLabelReceipts AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 56916,''Water registration receipts are append-only.'',1; END;');
 GRANT EXECUTE ON dbo.EQ001_UAT_WaterRegistration TO PharmaLIMS_TrustedEquipmentSigner;
 DENY INSERT,UPDATE,DELETE ON dbo.EQ001_WaterRegistrationReceipts TO PharmaLIMS_TrustedEquipmentSigner;
 DENY INSERT,UPDATE,DELETE ON dbo.EQ001_WaterLabelReceipts TO PharmaLIMS_TrustedEquipmentSigner;
 COMMIT;
 PRINT N'PASS: Exact isolated UAT Water boundary staged. Historical rows and client permissions unchanged.';
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
