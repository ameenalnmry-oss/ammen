-- EQ-001 isolated UAT / Microbiology Equipment: controlled draft-to-sample association.
-- Inert by default. Controlled activation requires isolated UAT change approval.
THROW 57198, 'SECURITY HOLD: Trusted service reauthentication and signer-only SQL boundary are not implemented. Do not deploy.', 1;
SET NOCOUNT ON; SET XACT_ABORT ON;
DECLARE @ApprovedIsolatedUat bit = 0;
IF @ApprovedIsolatedUat<>1 OR DB_NAME()<>N'EQ001_UAT_AMEEN_20261024'
 OR UPPER(@@SERVERNAME)<>N'AMEEN\SQLEXPRESS'
 THROW 57120,'Isolated UAT deployment authorization required.',1;
IF OBJECT_ID(N'dbo.EQ001_UAT_LinkMicroEquipmentDraft') IS NOT NULL
 THROW 57121,'Existing procedure must be independently reviewed.',1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivityLinks',N'U') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivities',N'U') IS NULL
 THROW 57122,'Required roles or equipment tables missing.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 EXEC sys.sp_executesql N'
CREATE PROCEDURE dbo.EQ001_UAT_LinkMicroEquipmentDraft
 @ActivityID bigint, @Module nvarchar(20), @ParentID int,
 @ResultID int = NULL, @Actor nvarchar(100)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF DB_NAME()<>N''EQ001_UAT_AMEEN_20261024''
 OR UPPER(@@SERVERNAME)<>N''AMEEN\SQLEXPRESS''
 THROW 57123,''Wrong UAT target.'',1;
 DECLARE @Expected nvarchar(100)=CASE ORIGINAL_LOGIN()
 WHEN N''AMEEN\LimsUatAdmin'' THEN N''admin''
 WHEN N''AMEEN\LimsUatQA'' THEN N''qa''
 WHEN N''AMEEN\LimsReview'' THEN N''AMEEN\LimsReview''
 ELSE NULL END;
 IF @Expected IS NULL OR @Actor IS NULL OR @Actor<>@Expected
 THROW 57124,''Windows actor identity mismatch.'',1;
 IF @ActivityID IS NULL OR @ActivityID<=0 OR @ParentID IS NULL OR @ParentID<=0
 OR (@ResultID IS NOT NULL AND @ResultID<=0)
 OR @Module NOT IN (N''WATER'',N''EM'',N''PRM'')
 THROW 57125,''Invalid link identity or module.'',1;
 IF (SELECT COUNT(*) FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
 WHERE Username=@Expected AND IsActive=1 AND CanEnterResults=1 AND MustChangePassword=0
 AND (IsLocked=0 OR (LockedUntil IS NOT NULL AND LockedUntil<=SYSDATETIME())))<>1
 THROW 57126,''Active user authorization required.'',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
 WHERE ActivityID=@ActivityID AND ActivityStatus=N''Draft'' AND PerformedBy=@Expected
 AND ActivityType IN(N''Use'',N''Incubation''))
 THROW 57127,''Activity is not a permitted current-user use draft.'',1;
 DECLARE @SampleNo nvarchar(100)=NULL,@TestCode nvarchar(100)=NULL;
 IF @Module=N''WATER''
 SELECT TOP(1) @SampleNo=p.SampleNumber,@TestCode=CONVERT(nvarchar(100),t.SampleTestID)
 FROM dbo.Samples p WITH(UPDLOCK,HOLDLOCK)
 LEFT JOIN dbo.SampleTests t WITH(UPDLOCK,HOLDLOCK) ON t.SampleID=p.SampleID AND t.SampleTestID=@ResultID
 WHERE p.SampleID=@ParentID AND (@ResultID IS NULL OR t.SampleTestID IS NOT NULL);
 IF @Module=N''EM''
 SELECT TOP(1) @SampleNo=p.EventNo,@TestCode=t.PlateCode
 FROM dbo.EM_Events p WITH(UPDLOCK,HOLDLOCK)
 LEFT JOIN dbo.EM_EventPlates t WITH(UPDLOCK,HOLDLOCK) ON t.EventId=p.Id AND t.Id=@ResultID
 WHERE p.Id=@ParentID AND (@ResultID IS NULL OR t.Id IS NOT NULL);
 IF @Module=N''PRM''
 SELECT TOP(1) @SampleNo=p.SampleNumber,@TestCode=t.TestCode
 FROM dbo.PRM_Samples p WITH(UPDLOCK,HOLDLOCK)
 LEFT JOIN dbo.PRM_SampleTests t WITH(UPDLOCK,HOLDLOCK) ON t.SampleID=p.SampleID AND t.SampleTestID=@ResultID
 WHERE p.SampleID=@ParentID AND (@ResultID IS NULL OR t.SampleTestID IS NOT NULL);
 IF NULLIF(LTRIM(RTRIM(@SampleNo)),N'''') IS NULL
 THROW 57128,''Sample/event was not found or test does not belong to it.'',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivityLinks WITH(UPDLOCK,HOLDLOCK)
 WHERE ActivityID=@ActivityID AND Module=@Module AND ParentRecordID=@ParentID
 AND ((@ResultID IS NULL AND ResultRecordID IS NULL) OR ResultRecordID=@ResultID))
 BEGIN
 INSERT dbo.MicroEquipmentActivityLinks
 (ActivityID,Module,ParentRecordID,ResultRecordID,SampleNumberSnapshot,TestCodeSnapshot,LinkedBy)
 VALUES(@ActivityID,@Module,@ParentID,@ResultID,@SampleNo,@TestCode,@Expected);
 END;
END';
 GRANT EXECUTE ON dbo.EQ001_UAT_LinkMicroEquipmentDraft TO PharmaLIMS_EquipmentWindowsUat;
 COMMIT;
 PRINT N'PASS: Scoped equipment link boundary installed.';
END TRY
BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;