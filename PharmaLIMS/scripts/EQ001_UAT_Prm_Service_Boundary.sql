-- Explicit controlled staging only. No business rows, existing grants or Production settings are modified.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
DECLARE @VerifiedIsolatedUat bit = 0;
IF @VerifiedIsolatedUat<>1 OR DB_NAME() NOT LIKE N'EQ001[_]UAT[_]%'
 THROW 56800,'Explicit isolated UAT confirmation is required.',1;
IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
 OR OBJECT_ID(N'dbo.PRM_SpecificationContentHistory',N'U') IS NULL
 OR OBJECT_ID(N'dbo.PRM_SpecificationSignatures',N'U') IS NULL
 OR COL_LENGTH(N'dbo.Users',N'AuthenticationRowVersion')<>8
 OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'SpecificationLimit') IS NULL
 OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'IsDefaultForCategory') IS NULL
 OR COL_LENGTH(N'dbo.AuditTrail',N'UtcRecordedAt') IS NULL
 THROW 56800,'Actual site specification/history/audit prerequisites are missing.',1;
IF NOT EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.PRM_SpecificationContentHistory') AND name=N'TRG_PRM_SpecificationContentHistory_AppendOnly' AND is_disabled=0)
 OR NOT EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.PRM_SpecificationSignatures') AND name=N'TRG_PRM_SpecificationSignatures_AppendOnly' AND is_disabled=0)
 THROW 56800,'Existing enabled append-only history/signature protection is required.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 IF OBJECT_ID(N'dbo.EQ001_PrmSigningRequests',N'U') IS NULL
  CREATE TABLE dbo.EQ001_PrmSigningRequests(RequestId uniqueidentifier NOT NULL PRIMARY KEY,Actor nvarchar(100) NOT NULL,WindowsCallerSid nvarchar(184) NOT NULL,
   ActionType nvarchar(30) NOT NULL,SpecificationNo nvarchar(120) NOT NULL,SampleCategory nvarchar(40) NOT NULL,
   VersionNo int NOT NULL,HistoryID bigint NOT NULL,AcceptedAtUtc datetime2(7) NOT NULL);
 DECLARE @EQ001_UAT_ReadPrmSpecification nvarchar(max)=N'CREATE OR ALTER PROCEDURE dbo.EQ001_UAT_ReadPrmSpecification
 @No nvarchar(120), @Category nvarchar(40), @Version int
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON;
 SET XACT_ABORT ON;
 IF DB_NAME() NOT LIKE N''EQ001[_]UAT[_]%'' OR @@TRANCOUNT=0
  THROW 56800,''PRM service calls require isolated UAT and an explicit transaction.'',1;
 IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N''PharmaLIMS_TrustedEquipmentSigner''
   AND type=N''U'' AND sid=SUSER_SID(ORIGINAL_LOGIN()))
   OR ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)<>0
  THROW 56801,''Only the independent Windows service origin may use the PRM boundary.'',1;
 SELECT * FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK)
 WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
 ORDER BY SpecificationTestID;
END;';
 IF OBJECT_ID(N'dbo.EQ001_UAT_ReadPrmSpecification') IS NOT NULL AND
  (OBJECT_ID(N'dbo.EQ001_UAT_ReadPrmSpecification',N'P') IS NULL OR OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_ReadPrmSpecification')) IS NULL OR
   HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_ReadPrmSpecification')),CHARINDEX(N'PROCEDURE',OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_ReadPrmSpecification'))),LEN(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_ReadPrmSpecification')))),CHAR(13),N'')) <>
   HASHBYTES('SHA2_256',REPLACE(SUBSTRING(@EQ001_UAT_ReadPrmSpecification,CHARINDEX(N'PROCEDURE',@EQ001_UAT_ReadPrmSpecification),LEN(@EQ001_UAT_ReadPrmSpecification)),CHAR(13),N'')))
  THROW 56800,'An unreviewed existing PRM procedure must not be overwritten.',1;
 IF OBJECT_ID(N'dbo.EQ001_UAT_ReadPrmSpecification') IS NULL EXEC(@EQ001_UAT_ReadPrmSpecification);
 DECLARE @EQ001_UAT_WritePrmSpecification nvarchar(max)=N'CREATE OR ALTER PROCEDURE dbo.EQ001_UAT_WritePrmSpecification
 @Action nvarchar(30), @Payload nvarchar(max), @Actor nvarchar(100),
 @CallerSid nvarchar(184), @RequestId uniqueidentifier, @ActorVersion binary(8), @Reason nvarchar(500), @Meaning nvarchar(250)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON;
 SET XACT_ABORT ON;
 SET QUOTED_IDENTIFIER ON;
 IF DB_NAME() NOT LIKE N''EQ001[_]UAT[_]%'' OR @@TRANCOUNT=0
  THROW 56800,''PRM service calls require isolated UAT and an explicit transaction.'',1;
 IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N''PharmaLIMS_TrustedEquipmentSigner''
   AND type=N''U'' AND sid=SUSER_SID(ORIGINAL_LOGIN()))
   OR ISNULL(IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN()),1)<>0
  THROW 56801,''Only the independent Windows service origin may use the PRM boundary.'',1;
 IF @Action IS NULL OR @Action NOT IN(N''PrmSaveDraft'',N''PrmReview'',N''PrmApprove'')
  OR ISJSON(@Payload)<>1 OR @Payload IS NULL OR DATALENGTH(@Payload)>65536
  OR NULLIF(LTRIM(RTRIM(@Reason)),N'''') IS NULL OR NULLIF(LTRIM(RTRIM(@Meaning)),N'''') IS NULL
  OR @CallerSid IS NULL OR @CallerSid NOT LIKE N''S-1-%'' OR @ActorVersion IS NULL OR @RequestId IS NULL OR @RequestId=''00000000-0000-0000-0000-000000000000''
  THROW 56802,''Invalid PRM mutation request.'',1;
 IF EXISTS(SELECT 1 FROM dbo.EQ001_PrmSigningRequests WITH(UPDLOCK,HOLDLOCK) WHERE RequestId=@RequestId)
  THROW 56807,''PRM request was already committed.'',1;
 DECLARE @Role nvarchar(100), @Users int;
 SELECT @Users=COUNT(*), @Role=MAX(Role) FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
 WHERE Username=@Actor AND AuthenticationRowVersion=@ActorVersion AND IsActive=1
 AND MustChangePassword=0
 AND (IsLocked=0 OR (LockedUntil IS NOT NULL AND LockedUntil<=SYSDATETIME()))
 AND ((@Action=N''PrmSaveDraft'' AND CanEnterResults=1)
   OR (@Action=N''PrmReview'' AND CanReviewResults=1)
   OR (@Action=N''PrmApprove'' AND CanApproveResults=1 AND UPPER(LTRIM(RTRIM(Role))) IN(N''QA'',N''QUALITY ASSURANCE'')));
 IF ISNULL(@Users,0)<>1 THROW 56803,''Live PRM account permission or authentication version was rejected.'',1;

 DECLARE @No nvarchar(120)=JSON_VALUE(@Payload,''$.SpecificationNo''),
 @Category nvarchar(40)=JSON_VALUE(@Payload,''$.Category''),
 @Version int=TRY_CONVERT(int,JSON_VALUE(@Payload,''$.Version'')),
 @Reference nvarchar(160)=JSON_VALUE(@Payload,''$.Reference''),
 @Item nvarchar(80)=JSON_VALUE(@Payload,''$.ItemCode''),
 @Stage nvarchar(80)=JSON_VALUE(@Payload,''$.ProductionStage''),
 @Effective date=TRY_CONVERT(date,JSON_VALUE(@Payload,''$.EffectiveDate''));
 IF @No IS NULL OR LEN(LTRIM(RTRIM(@No)))=0 OR LEN(JSON_VALUE(@Payload,''$.SpecificationNo''))>120
 OR @Category IS NULL OR LEN(JSON_VALUE(@Payload,''$.Category''))>40
 OR @Category NOT IN(N''Raw Material'',N''Primary Packaging'',N''Production / In-Process'',N''Finished Product'',N''Stability'')
 OR @Reference IS NULL OR LEN(LTRIM(RTRIM(@Reference)))=0 OR LEN(JSON_VALUE(@Payload,''$.Reference''))>160
 OR @Item IS NULL OR LEN(LTRIM(RTRIM(@Item)))=0 OR LEN(JSON_VALUE(@Payload,''$.ItemCode''))>80
 OR @Stage IS NULL OR LEN(JSON_VALUE(@Payload,''$.ProductionStage''))>80
 OR (@Category=N''Production / In-Process'' AND LEN(LTRIM(RTRIM(@Stage)))=0)
 OR @Version IS NULL OR @Version<0 OR (@Action<>N''PrmSaveDraft'' AND @Version=0)
 OR (JSON_VALUE(@Payload,''$.EffectiveDate'') IS NOT NULL AND @Effective IS NULL)
  THROW 56802,''Invalid PRM scope or version.'',1;

 DECLARE @Before nvarchar(max)=N''[]'', @After nvarchar(max), @OldStatus nvarchar(30)=N'''',
 @NewStatus nvarchar(30), @Creator nvarchar(120)=@Actor, @Created datetime2(0)=SYSDATETIME(),
 @History bigint, @Signature bigint=NULL, @HistoryAction nvarchar(60), @Controlled bit=0, @Rows int=0;
 IF @Version>0
 BEGIN
  SELECT @Rows=COUNT(*),@OldStatus=MIN(ApprovalStatus),@Creator=MIN(CreatedBy),@Created=MIN(CreatedDate)
  FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK)
  WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version;
  IF @Rows=0 OR EXISTS(SELECT 1 FROM dbo.PRM_SpecificationTests
    WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
      AND (ApprovalStatus<>@OldStatus OR (@Action<>N''PrmSaveDraft'' AND (ISNULL(ItemCode,N'''')<>@Item
       OR (@Category=N''Production / In-Process'' AND ISNULL(ProductionStage,N'''')<>@Stage)))))
   THROW 56804,''The complete specification state or scope is inconsistent.'',1;
  SELECT @Before=(SELECT * FROM dbo.PRM_SpecificationTests
    WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
    ORDER BY SpecificationTestID FOR JSON PATH,INCLUDE_NULL_VALUES);
  IF EXISTS(SELECT 1 FROM dbo.PRM_SpecificationContentHistory h WITH(UPDLOCK,HOLDLOCK)
    JOIN dbo.EQ001_PrmSigningRequests r ON r.HistoryID=h.HistoryID AND r.ActionType=N''PrmSaveDraft''
      AND r.Actor=h.ChangedBy AND r.SpecificationNo=h.SpecificationNo AND r.SampleCategory=h.SampleCategory AND r.VersionNo=h.VersionNo
    WHERE h.SpecificationNo=@No AND h.SampleCategory=@Category AND h.VersionNo=@Version AND h.ActionType=N''Draft Created'')
   SET @Controlled=1;
 END;

 IF @Action=N''PrmSaveDraft''
 BEGIN
  IF @Version>0 AND @OldStatus<>N''Draft'' THROW 56804,''Reviewed and approved versions cannot be edited.'',1;
  IF @Controlled=0
  BEGIN
   SELECT @Version=ISNULL(MAX(VersionNo),0)+1 FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK)
   WHERE SpecificationNo=@No AND SampleCategory=@Category;
   SET @Creator=@Actor; SET @Created=SYSDATETIME(); SET @Before=N''[]'';
  END;
  DECLARE @Tests TABLE(Code nvarchar(40),Name nvarchar(160),Criteria nvarchar(500),
   LimitValue decimal(18,3),DefaultFlag bit,Unit nvarchar(50),ResultType nvarchar(60),
   RequiredFlag bit,Hours decimal(9,2),SortOrder int);
  IF JSON_QUERY(@Payload,''$.Tests'') IS NULL OR LEFT(LTRIM(JSON_QUERY(@Payload,''$.Tests'')),1)<>N''[''
    THROW 56802,''A complete test array is required.'',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,''$.Tests'') j WHERE j.type<>5
   OR NULLIF(LTRIM(RTRIM(JSON_VALUE(j.value,''$.TestCode''))),N'''') IS NULL
   OR LEN(JSON_VALUE(j.value,''$.TestCode''))>40
   OR NULLIF(LTRIM(RTRIM(JSON_VALUE(j.value,''$.TestName''))),N'''') IS NULL OR LEN(JSON_VALUE(j.value,''$.TestName''))>160
   OR NULLIF(LTRIM(RTRIM(JSON_VALUE(j.value,''$.SpecificationText''))),N'''') IS NULL OR LEN(JSON_VALUE(j.value,''$.SpecificationText''))>500
   OR JSON_VALUE(j.value,''$.Unit'') IS NULL OR LEN(JSON_VALUE(j.value,''$.Unit''))>50
   OR JSON_VALUE(j.value,''$.RequiredTest'') IS NULL OR JSON_VALUE(j.value,''$.RequiredTest'') NOT IN(N''true'',N''false'')
   OR JSON_VALUE(j.value,''$.IsDefaultForCategory'') IS NULL OR JSON_VALUE(j.value,''$.IsDefaultForCategory'') NOT IN(N''true'',N''false'')
   OR JSON_VALUE(j.value,''$.ResultType'') IS NULL OR JSON_VALUE(j.value,''$.ResultType'') NOT IN(N''Numeric'',N''Qualitative'',N''Text'')
   OR TRY_CONVERT(decimal(9,2),JSON_VALUE(j.value,''$.MinimumElapsedHours'')) IS NULL
   OR TRY_CONVERT(decimal(9,2),JSON_VALUE(j.value,''$.MinimumElapsedHours''))<0
   OR TRY_CONVERT(decimal(38,10),JSON_VALUE(j.value,''$.MinimumElapsedHours''))<>TRY_CONVERT(decimal(9,2),JSON_VALUE(j.value,''$.MinimumElapsedHours''))
   OR (JSON_VALUE(j.value,''$.RequiredTest'')=N''true'' AND TRY_CONVERT(decimal(9,2),JSON_VALUE(j.value,''$.MinimumElapsedHours''))<=0)
   OR (JSON_VALUE(j.value,''$.SpecificationLimit'') IS NOT NULL AND
      (TRY_CONVERT(decimal(18,3),JSON_VALUE(j.value,''$.SpecificationLimit'')) IS NULL
       OR TRY_CONVERT(decimal(18,3),JSON_VALUE(j.value,''$.SpecificationLimit''))<0
       OR TRY_CONVERT(decimal(38,10),JSON_VALUE(j.value,''$.SpecificationLimit''))<>TRY_CONVERT(decimal(18,3),JSON_VALUE(j.value,''$.SpecificationLimit''))))
   OR TRY_CONVERT(int,JSON_VALUE(j.value,''$.SortOrder'')) IS NULL OR TRY_CONVERT(int,JSON_VALUE(j.value,''$.SortOrder''))<0)
    THROW 56802,''Invalid or inexact PRM test values.'',1;
  INSERT @Tests SELECT JSON_VALUE(value,''$.TestCode''),JSON_VALUE(value,''$.TestName''),JSON_VALUE(value,''$.SpecificationText''),
   TRY_CONVERT(decimal(18,3),JSON_VALUE(value,''$.SpecificationLimit'')),
   CASE JSON_VALUE(value,''$.IsDefaultForCategory'') WHEN N''true'' THEN 1 ELSE 0 END,
   JSON_VALUE(value,''$.Unit''),JSON_VALUE(value,''$.ResultType''),
   CASE JSON_VALUE(value,''$.RequiredTest'') WHEN N''true'' THEN 1 ELSE 0 END,
   CONVERT(decimal(9,2),JSON_VALUE(value,''$.MinimumElapsedHours'')),CONVERT(int,JSON_VALUE(value,''$.SortOrder''))
   FROM OPENJSON(@Payload,''$.Tests'');
  IF (SELECT COUNT(*) FROM @Tests) NOT BETWEEN 1 AND 16 OR EXISTS(SELECT UPPER(LTRIM(RTRIM(Code))) FROM @Tests GROUP BY UPPER(LTRIM(RTRIM(Code))) HAVING COUNT(*)>1)
   THROW 56802,''Invalid complete PRM test set.'',1;
  IF @Controlled=1 DELETE dbo.PRM_SpecificationTests
   WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version AND ApprovalStatus=N''Draft'';
  INSERT dbo.PRM_SpecificationTests
   (SpecificationNo,SampleCategory,VersionNo,TestCode,TestName,SpecificationText,SpecificationLimit,IsDefaultForCategory,
    Unit,ResultType,RequiredTest,MinimumElapsedHours,SortOrder,ApprovalStatus,EffectiveDate,IsActive,CreatedBy,CreatedDate,ItemCode,CompendialReference,ProductionStage)
  SELECT @No,@Category,@Version,Code,Name,Criteria,LimitValue,DefaultFlag,NULLIF(Unit,N''''),ResultType,RequiredFlag,Hours,
    CASE WHEN SortOrder=0 THEN ROW_NUMBER() OVER(ORDER BY Code) ELSE SortOrder END,N''Draft'',@Effective,0,@Creator,@Created,@Item,@Reference,NULLIF(@Stage,N'''') FROM @Tests;
  SET @HistoryAction=CASE WHEN @Controlled=1 THEN N''Draft Updated'' ELSE N''Draft Created'' END;
  SET @NewStatus=N''Draft'';
 END
 ELSE
 BEGIN
  IF @Controlled=0 THROW 56804,''Historical authorship is incomplete; create a new controlled draft.'',1;
  IF EXISTS(SELECT 1 FROM dbo.PRM_SpecificationTests WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
    AND (RequiredTest=1 AND ISNULL(MinimumElapsedHours,0)<=0))
   THROW 56804,''Required incubation timing is missing.'',1;
  IF @Action=N''PrmReview''
  BEGIN
   IF @OldStatus<>N''Draft'' THROW 56804,''Only a saved draft can be reviewed.'',1;
   IF EXISTS(SELECT 1 FROM dbo.PRM_SpecificationTests WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
      AND UPPER(LTRIM(RTRIM(CreatedBy)))=UPPER(LTRIM(RTRIM(@Actor))))
    OR EXISTS(SELECT 1 FROM dbo.PRM_SpecificationContentHistory WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
      AND ActionType IN(N''Draft Created'',N''Draft Updated'') AND UPPER(LTRIM(RTRIM(ChangedBy)))=UPPER(LTRIM(RTRIM(@Actor))))
    THROW 56805,''The reviewer must be independent from every draft author/editor.'',1;
   UPDATE dbo.PRM_SpecificationTests SET ApprovalStatus=N''Reviewed'',ReviewedBy=@Actor,ReviewedDate=SYSDATETIME()
    WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version AND ApprovalStatus=N''Draft'';
   SET @NewStatus=N''Reviewed''; SET @HistoryAction=N''Review Specification'';
  END
  ELSE
  BEGIN
   IF @OldStatus<>N''Reviewed'' THROW 56804,''Independent review is required before approval.'',1;
   IF EXISTS(SELECT 1 FROM dbo.PRM_SpecificationTests WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
      AND (ReviewedBy IS NULL OR ReviewedDate IS NULL OR UPPER(LTRIM(RTRIM(ReviewedBy)))=UPPER(LTRIM(RTRIM(@Actor)))))
    THROW 56805,''The QA approver must be independent from the reviewer.'',1;
   IF EXISTS(SELECT 1 FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK)
      WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo<>@Version AND IsActive=1 AND ApprovalStatus=N''Approved'')
    THROW 56806,''Existing approved versions are preserved. Controlled supersession is outside this UAT patch.'',1;
   IF NOT EXISTS(SELECT 1 FROM dbo.PRM_SpecificationSignatures sig
    JOIN dbo.PRM_SpecificationContentHistory h ON h.SignatureID=sig.SignatureID AND h.ActionType=N''Review Specification''
      AND h.SpecificationNo=sig.SpecificationNo AND h.SampleCategory=sig.SampleCategory AND h.VersionNo=sig.VersionNo
    JOIN dbo.EQ001_PrmSigningRequests r ON r.HistoryID=h.HistoryID AND r.ActionType=N''PrmReview'' AND r.Actor=sig.SignedBy
    WHERE sig.SpecificationNo=@No AND sig.SampleCategory=@Category AND sig.VersionNo=@Version AND sig.ActionType=N''Review Specification''
      AND NULLIF(LTRIM(RTRIM(sig.ActionReason)),N'''') IS NOT NULL AND NULLIF(LTRIM(RTRIM(sig.MeaningOfSignature)),N'''') IS NOT NULL
      AND NOT EXISTS(SELECT 1 FROM dbo.PRM_SpecificationTests test WHERE test.SpecificationNo=@No AND test.SampleCategory=@Category AND test.VersionNo=@Version
        AND (UPPER(LTRIM(RTRIM(test.ReviewedBy)))<>UPPER(LTRIM(RTRIM(sig.SignedBy))) OR test.ReviewedDate>sig.SignedAt)))
    THROW 56804,''Controlled review signature evidence is missing.'',1;
   UPDATE dbo.PRM_SpecificationTests SET ApprovalStatus=N''Approved'',ApprovedBy=@Actor,ApprovedDate=SYSDATETIME(),IsActive=1
    WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version AND ApprovalStatus=N''Reviewed'';
   SET @NewStatus=N''Approved''; SET @HistoryAction=N''Approve Specification'';
  END;
  INSERT dbo.PRM_SpecificationSignatures(SpecificationNo,SampleCategory,VersionNo,ActionType,ActionReason,SignedBy,MeaningOfSignature,UserRole,SignedAt)
    VALUES(@No,@Category,@Version,@HistoryAction,@Reason,@Actor,@Meaning,@Role,SYSDATETIME());
  SET @Signature=CONVERT(bigint,SCOPE_IDENTITY());
 END;
 SELECT @After=(SELECT * FROM dbo.PRM_SpecificationTests WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
    ORDER BY SpecificationTestID FOR JSON PATH,INCLUDE_NULL_VALUES);
 INSERT dbo.PRM_SpecificationContentHistory
  (SpecificationNo,SampleCategory,VersionNo,ActionType,OldRowsJson,NewRowsJson,ChangedBy,ChangedAt,ChangeReason,SignatureID,ContentHash)
 VALUES(@No,@Category,@Version,@HistoryAction,@Before,@After,@Actor,SYSDATETIME(),@Reason,@Signature,
   HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),CONVERT(varchar(max),@After COLLATE Latin1_General_100_BIN2_UTF8))));
 SET @History=CONVERT(bigint,SCOPE_IDENTITY());
 INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,ChangedBy,ChangeDate,PerformedBy,Reason,CreatedUtc,UtcRecordedAt,FieldName,SampleNumber,ModuleName,SourceApplication)
 VALUES(N''PRM_SpecificationTests'',0,@HistoryAction,@Before,@After,@Actor,GETDATE(),@Actor,@Reason,SYSUTCDATETIME(),SYSUTCDATETIME(),N''ApprovalStatus'',LEFT(@No,100),N''PRM Specification'',N''PharmaLIMS Trusted Signing UAT'');
 INSERT dbo.EQ001_PrmSigningRequests(RequestId,Actor,WindowsCallerSid,ActionType,SpecificationNo,SampleCategory,VersionNo,HistoryID,AcceptedAtUtc)
 VALUES(@RequestId,@Actor,@CallerSid,@Action,@No,@Category,@Version,@History,SYSUTCDATETIME());
 SELECT @Version;
END;';
 IF OBJECT_ID(N'dbo.EQ001_UAT_WritePrmSpecification') IS NOT NULL AND
  (OBJECT_ID(N'dbo.EQ001_UAT_WritePrmSpecification',N'P') IS NULL OR OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WritePrmSpecification')) IS NULL OR
   HASHBYTES('SHA2_256',REPLACE(SUBSTRING(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WritePrmSpecification')),CHARINDEX(N'PROCEDURE',OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WritePrmSpecification'))),LEN(OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_UAT_WritePrmSpecification')))),CHAR(13),N'')) <>
   HASHBYTES('SHA2_256',REPLACE(SUBSTRING(@EQ001_UAT_WritePrmSpecification,CHARINDEX(N'PROCEDURE',@EQ001_UAT_WritePrmSpecification),LEN(@EQ001_UAT_WritePrmSpecification)),CHAR(13),N'')))
  THROW 56800,'An unreviewed existing PRM procedure must not be overwritten.',1;
 IF OBJECT_ID(N'dbo.EQ001_UAT_WritePrmSpecification') IS NULL EXEC(@EQ001_UAT_WritePrmSpecification);
 IF OBJECT_ID(N'dbo.TRG_EQ001_PrmSigningRequests_AppendOnly',N'TR') IS NULL
 EXEC(N'CREATE TRIGGER dbo.TRG_EQ001_PrmSigningRequests_AppendOnly ON dbo.EQ001_PrmSigningRequests AFTER UPDATE,DELETE AS
 BEGIN SET NOCOUNT ON; THROW 56808,''PRM request evidence is append-only.'',1; END;');
 COMMIT TRANSACTION;
 PRINT N'PASS: UAT PRM entry points staged; no service EXECUTE permission was granted.';
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
