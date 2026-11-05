-- EQ-001 isolated UAT: controlled equipment actual-use confirmation.
-- Inert by default; no UAT migration without controlled opt-in.
THROW 57198, 'SECURITY HOLD: Trusted service reauthentication and signer-only SQL boundary are not implemented. Do not deploy.', 1;
SET NOCOUNT ON; SET XACT_ABORT ON;
DECLARE @ApprovedIsolatedUat bit = 0;
IF @ApprovedIsolatedUat<>1 OR DB_NAME()<>N'EQ001_UAT_AMEEN_20261024'
 OR UPPER(@@SERVERNAME)<>N'AMEEN\SQLEXPRESS'
 THROW 57130,'Isolated UAT deployment authorization required.',1;
IF OBJECT_ID(N'dbo.EQ001_UAT_ConfirmMicroEquipmentUse') IS NOT NULL
 THROW 57131,'Existing procedure requires independent comparison.',1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivities',N'U') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivityAudit',N'U') IS NULL
 THROW 57132,'Required protected equipment objects are absent.',1;
BEGIN TRY BEGIN TRANSACTION;
EXEC sys.sp_executesql N'
CREATE PROCEDURE dbo.EQ001_UAT_ConfirmMicroEquipmentUse
 @ActivityID bigint, @Version binary(8), @Actor nvarchar(100),
 @Started datetimeoffset(7), @Ended datetimeoffset(7)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF DB_NAME()<>N''EQ001_UAT_AMEEN_20261024''
 OR UPPER(@@SERVERNAME)<>N''AMEEN\SQLEXPRESS''
 THROW 57133,''Wrong UAT target.'',1;
 DECLARE @Expected nvarchar(100)=CASE ORIGINAL_LOGIN()
 WHEN N''AMEEN\LimsUatAdmin'' THEN N''admin''
 WHEN N''AMEEN\LimsUatQA'' THEN N''qa''
 WHEN N''AMEEN\LimsReview'' THEN N''AMEEN\LimsReview''
 ELSE NULL END;
 IF @Expected IS NULL OR @Actor IS NULL OR @Actor<>@Expected
 THROW 57134,''Windows account and PharmaLIMS actor disagree.'',1;
 IF @ActivityID IS NULL OR @ActivityID<=0 OR @Version IS NULL
 OR @Started IS NULL OR @Ended IS NULL OR @Ended<@Started
 THROW 57135,''Invalid equipment-use evidence.'',1;
 IF (SELECT COUNT(*) FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
 WHERE Username=@Expected AND IsActive=1 AND CanEnterResults=1 AND MustChangePassword=0
 AND (IsLocked=0 OR (LockedUntil IS NOT NULL AND LockedUntil<=SYSDATETIME())))<>1
 THROW 57136,''Actor is not authorized to enter equipment use.'',1;
 UPDATE dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
 SET ActivityStatus=N''Submitted'',ActualStartAt=@Started,ActualEndAt=@Ended
 WHERE ActivityID=@ActivityID AND ActivityStatus=N''Draft''
 AND ActivityType=N''Use'' AND PerformedBy=@Expected AND VersionToken=@Version
 AND EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivityLinks WITH(UPDLOCK,HOLDLOCK)
  WHERE ActivityID=@ActivityID);
 IF @@ROWCOUNT<>1
 THROW 57137,''Stale, unlinked, or unauthorized draft. Reload before confirming.'',1;
 INSERT dbo.MicroEquipmentActivityAudit(ActivityID,ActionType,NewStateJson,ActionReason,ChangedBy)
 SELECT a.ActivityID,N''USE CONFIRMED'',
 (SELECT a.ActivityID,a.EquipmentID,a.ActivityType,a.ActivityStatus,a.PerformedBy,
 a.ActualStartAt,a.ActualEndAt,a.EquipmentCodeSnapshot,a.CalibrationStatusSnapshot,
 a.QualificationStatusSnapshot,a.ActivityReason,a.MethodReference
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),
 N''Actual equipment use confirmed by technician.'',@Expected
 FROM dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK) WHERE a.ActivityID=@ActivityID;
 IF @@ROWCOUNT<>1 THROW 57138,''Use audit record was not created.'',1;
END';
GRANT EXECUTE ON dbo.EQ001_UAT_ConfirmMicroEquipmentUse TO PharmaLIMS_EquipmentWindowsUat;
COMMIT;
PRINT N'PASS: Controlled actual-use boundary staged.';
END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;