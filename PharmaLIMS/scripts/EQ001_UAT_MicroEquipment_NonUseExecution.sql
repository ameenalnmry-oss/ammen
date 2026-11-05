-- EQ-001 isolated UAT: controlled non-use execution and immutable evidence.
-- This SQL is INERT. A reviewed deployment must explicitly turn on the guard.
THROW 57198, 'SECURITY HOLD: Trusted service reauthentication and signer-only SQL boundary are not implemented. Do not deploy.', 1;
SET NOCOUNT ON; SET XACT_ABORT ON;
DECLARE @ApprovedIsolatedUat bit=0;
IF @ApprovedIsolatedUat<>1 OR DB_NAME()<>N'EQ001_UAT_AMEEN_20261024'
 OR UPPER(@@SERVERNAME)<>N'AMEEN\SQLEXPRESS'
 THROW 57140,'Explicit isolated EQ001 UAT approval required.',1;
IF OBJECT_ID(N'dbo.EQ001_UAT_SubmitMicroEquipmentExecution') IS NOT NULL
 THROW 57141,'An existing procedure cannot be overwritten.',1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentStructuredEvidence',N'U') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentCalibrationPoints',N'U') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivityAudit',N'U') IS NULL
 THROW 57142,'Required equipment evidence boundaries unavailable.',1;
BEGIN TRY BEGIN TRANSACTION;
EXEC sys.sp_executesql N'
CREATE PROCEDURE dbo.EQ001_UAT_SubmitMicroEquipmentExecution
 @ActivityID bigint,@Version binary(8),@Actor nvarchar(100),
 @Started datetimeoffset(7),@Ended datetimeoffset(7),
 @Details nvarchar(500),@StructuredJson nvarchar(max)=N''{}''
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF DB_NAME()<>N''EQ001_UAT_AMEEN_20261024''
 OR UPPER(@@SERVERNAME)<>N''AMEEN\SQLEXPRESS''
 THROW 57143,''Wrong equipment UAT target.'',1;
 DECLARE @Expected nvarchar(100)=CASE ORIGINAL_LOGIN()
 WHEN N''AMEEN\LimsUatAdmin'' THEN N''admin''
 WHEN N''AMEEN\LimsUatQA'' THEN N''qa''
 WHEN N''AMEEN\LimsReview'' THEN N''AMEEN\LimsReview''
 ELSE NULL END;
 IF @Expected IS NULL OR @Actor IS NULL OR @Actor<>@Expected
 THROW 57144,''Windows account / PharmaLIMS actor mismatch.'',1;
 IF @ActivityID IS NULL OR @ActivityID<=0 OR @Version IS NULL
 OR @Started IS NULL OR @Ended IS NULL OR @Ended<@Started
 OR NULLIF(LTRIM(RTRIM(@Details)),N'''') IS NULL OR LEN(@Details)>500
 OR @StructuredJson IS NULL OR ISJSON(@StructuredJson)<>1 OR LEN(@StructuredJson)>16000
 THROW 57145,''Invalid equipment execution identity or evidence.'',1;
 IF (SELECT COUNT(*) FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
 WHERE Username=@Expected AND IsActive=1 AND CanEnterResults=1 AND MustChangePassword=0
 AND (IsLocked=0 OR (LockedUntil IS NOT NULL AND LockedUntil<=SYSDATETIME())))<>1
 THROW 57146,''User not authorized for equipment execution.'',1;
 DECLARE @Type nvarchar(60);
 SELECT @Type=ActivityType FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
 WHERE ActivityID=@ActivityID AND ActivityStatus=N''Draft''
 AND PerformedBy=@Expected AND VersionToken=@Version
 AND ActivityType<>N''Use'';
 IF @Type IS NULL THROW 57147,''Stale or unauthorized equipment draft.'',1;
 DECLARE @StructuredType nvarchar(60)=JSON_VALUE(@StructuredJson,''$.Type'');
 IF @Type IN (N''Calibration'',N''Cleaning'',N''Sanitization'')
 BEGIN
 IF @StructuredType IS NULL OR @StructuredType<>@Type
 THROW 57148,''Structured execution type does not match activity.'',1;
 END
 ELSE IF @StructuredJson<>N''{}''
 THROW 57149,''Unexpected structured evidence for this activity type.'',1;

 DECLARE @Reference decimal(18,6)=TRY_CONVERT(decimal(18,6),JSON_VALUE(@StructuredJson,''$.Reference''));
 DECLARE @Observed decimal(18,6)=TRY_CONVERT(decimal(18,6),JSON_VALUE(@StructuredJson,''$.Observed''));
 DECLARE @Tolerance decimal(18,6)=TRY_CONVERT(decimal(18,6),JSON_VALUE(@StructuredJson,''$.Tolerance''));
 DECLARE @Minimum decimal(18,6)=TRY_CONVERT(decimal(18,6),JSON_VALUE(@StructuredJson,''$.Minimum''));
 DECLARE @Maximum decimal(18,6)=TRY_CONVERT(decimal(18,6),JSON_VALUE(@StructuredJson,''$.Maximum''));
 DECLARE @AgentFull nvarchar(4000)=JSON_VALUE(@StructuredJson,''$.Agent'');
 DECLARE @Agent nvarchar(90)=@AgentFull;
 DECLARE @Outcome nvarchar(10)=JSON_VALUE(@StructuredJson,''$.Outcome'');
 DECLARE @Mode nvarchar(20)=JSON_VALUE(@StructuredJson,''$.Mode'');
 DECLARE @Points TABLE(PointNo int NOT NULL PRIMARY KEY,FirstVal decimal(18,6),ObservedVal decimal(18,6),ThirdVal decimal(18,6));
 IF @Type=N''Calibration''
 BEGIN
 IF @Mode IS NULL OR @Mode NOT IN(N''Range'',N''Tolerance'') OR @Observed IS NULL
 OR ABS(@Observed)>=1000000000000
 THROW 57150,''Invalid calibration values.'',1;
 IF @Mode=N''Range'' AND (@Minimum IS NULL OR @Maximum IS NULL OR @Minimum>@Maximum
   OR @Reference IS NOT NULL OR @Tolerance IS NOT NULL)
 THROW 57151,''Invalid calibration normal range.'',1;
 IF @Mode=N''Tolerance'' AND (@Reference IS NULL OR @Tolerance IS NULL OR @Tolerance<0
   OR @Minimum IS NOT NULL OR @Maximum IS NOT NULL)
 THROW 57152,''Invalid calibration reference and tolerance.'',1;
 IF @Mode=N''Range'' AND (ABS(@Minimum)>=1000000000000 OR ABS(@Maximum)>=1000000000000)
 OR @Mode=N''Tolerance'' AND (ABS(@Reference)>=1000000000000 OR @Tolerance>=1000000000000)
 THROW 57153,''Calibration precision/range exceeded.'',1;
 DECLARE @Calculated nvarchar(10)=CASE WHEN @Mode=N''Range'' THEN
  CASE WHEN @Observed>=@Minimum AND @Observed<=@Maximum THEN N''PASS'' ELSE N''FAIL'' END
 ELSE CASE WHEN ABS(@Observed-@Reference)<=@Tolerance THEN N''PASS'' ELSE N''FAIL'' END END;
 IF @Outcome IS NULL OR @Outcome<>@Calculated
 THROW 57154,''Calibration outcome must match frozen numeric evidence.'',1;
 IF JSON_QUERY(@StructuredJson,''$.Points'') IS NULL
 THROW 57155,''Calibration measurement points required.'',1;
 INSERT @Points(PointNo,FirstVal,ObservedVal,ThirdVal)
 SELECT TRY_CONVERT(int,j.[key])+1,
 TRY_CONVERT(decimal(18,6),JSON_VALUE(j.value,''$.First'')),
 TRY_CONVERT(decimal(18,6),JSON_VALUE(j.value,''$.Observed'')),
 TRY_CONVERT(decimal(18,6),JSON_VALUE(j.value,''$.Third''))
 FROM OPENJSON(@StructuredJson,''$.Points'') AS j;
 IF (SELECT COUNT(*) FROM @Points) NOT BETWEEN 1 AND 100
 OR EXISTS(SELECT 1 FROM @Points WHERE PointNo NOT BETWEEN 1 AND 100
 OR FirstVal IS NULL OR ObservedVal IS NULL OR ThirdVal IS NULL
 OR ABS(FirstVal)>=1000000000000 OR ABS(ObservedVal)>=1000000000000 OR ABS(ThirdVal)>=1000000000000
 OR (@Mode=N''Range'' AND FirstVal>ThirdVal) OR (@Mode=N''Tolerance'' AND ThirdVal<0))
 THROW 57156,''Calibration points are incomplete or out of range.'',1;
 IF NOT EXISTS(SELECT 1 FROM @Points WHERE PointNo=1 AND ObservedVal=@Observed
 AND FirstVal=CASE @Mode WHEN N''Range'' THEN @Minimum ELSE @Reference END
 AND ThirdVal=CASE @Mode WHEN N''Range'' THEN @Maximum ELSE @Tolerance END)
 THROW 57157,''Primary calibration reading must match first calibration point.'',1;
 END
 ELSE IF @Type IN(N''Cleaning'',N''Sanitization'')
 BEGIN
 IF @AgentFull IS NULL OR LEN(@AgentFull)>90 OR NULLIF(LTRIM(RTRIM(@Agent)),N'''') IS NULL OR @Outcome NOT IN(N''PASS'',N''FAIL'')
 OR CHARINDEX(N'';'',@Agent)>0 OR @Reference IS NOT NULL OR @Observed IS NOT NULL
 OR @Tolerance IS NOT NULL OR @Minimum IS NOT NULL OR @Maximum IS NOT NULL
 OR JSON_QUERY(@StructuredJson,''$.Points'') IS NOT NULL
 THROW 57158,''Invalid cleaning/sanitization execution evidence.'',1;
 END;

 UPDATE dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
 SET ActivityStatus=N''Submitted'',ActualStartAt=@Started,ActualEndAt=@Ended,
     ActivityReason=@Details
 WHERE ActivityID=@ActivityID AND ActivityStatus=N''Draft''
 AND ActivityType=@Type AND PerformedBy=@Expected AND VersionToken=@Version;
 IF @@ROWCOUNT<>1 THROW 57159,''Equipment draft changed during submission.'',1;

 IF @Type IN(N''Calibration'',N''Cleaning'',N''Sanitization'')
 BEGIN
 INSERT dbo.MicroEquipmentStructuredEvidence
 (ActivityID,EvidenceType,ReferenceValue,ObservedValue,MaximumAbsoluteError,
 NormalMinimum,NormalMaximum,CleaningAgentLot,Outcome,RecordedBy)
 VALUES(@ActivityID,@Type,@Reference,@Observed,@Tolerance,@Minimum,@Maximum,@Agent,@Outcome,@Expected);
 IF @@ROWCOUNT<>1 THROW 57160,''Structured execution evidence not committed.'',1;
 END;
 IF @Type=N''Calibration''
 BEGIN
 INSERT dbo.MicroEquipmentCalibrationPoints
 (ActivityID,PointNo,ReferenceValue,ObservedValue,MaxAbsoluteError,NormalMinimum,NormalMaximum)
 SELECT @ActivityID,PointNo,
 CASE WHEN @Mode=N''Tolerance'' THEN FirstVal END,
 ObservedVal,CASE WHEN @Mode=N''Tolerance'' THEN ThirdVal END,
 CASE WHEN @Mode=N''Range'' THEN FirstVal END,
 CASE WHEN @Mode=N''Range'' THEN ThirdVal END FROM @Points;
 IF @@ROWCOUNT<>(SELECT COUNT(*) FROM @Points)
 THROW 57161,''Calibration point count does not match input.'',1;
 END;
 INSERT dbo.MicroEquipmentActivityAudit(ActivityID,ActionType,NewStateJson,ActionReason,ChangedBy)
 SELECT a.ActivityID,N''EXECUTION SUBMITTED'',
 (SELECT a.ActivityID,a.EquipmentID,a.ActivityType,a.ActivityStatus,a.PerformedBy,
 a.ActualStartAt,a.ActualEndAt,a.EquipmentCodeSnapshot,a.CalibrationStatusSnapshot,
 a.QualificationStatusSnapshot,a.ActivityReason,a.MethodReference
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),
 N''Actual non-use execution submitted; review/approval pending.'',@Expected
 FROM dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK) WHERE ActivityID=@ActivityID;
 IF @@ROWCOUNT<>1 THROW 57162,''Execution audit not committed.'',1;
END';
GRANT EXECUTE ON dbo.EQ001_UAT_SubmitMicroEquipmentExecution TO PharmaLIMS_EquipmentWindowsUat;
COMMIT;
PRINT N'PASS: Controlled equipment execution + evidence boundary staged.';
END TRY
BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;