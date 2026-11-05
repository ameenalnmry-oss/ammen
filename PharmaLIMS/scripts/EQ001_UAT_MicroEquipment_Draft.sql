-- MEDICA / EQ-001 / Isolated UAT Microequipment draft boundary.
-- This file is inert by default. Install only through a controlled, reviewed UAT deployment.
THROW 57198, 'SECURITY HOLD: Trusted service reauthentication and signer-only SQL boundary are not implemented. Do not deploy.', 1;
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @ExplicitlyApprovedUat bit = 0;
IF @ExplicitlyApprovedUat <> 1
 OR DB_NAME() <> N'EQ001_UAT_AMEEN_20261024'
 OR @@SERVERNAME <> N'AMEEN\SQLEXPRESS'
 THROW 57100, 'Controlled isolated UAT approval and target are required.', 1;
IF SCHEMA_ID(N'dbo') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivities', N'U') IS NULL
 OR OBJECT_ID(N'dbo.MicroEquipmentActivityAudit', N'U') IS NULL
 OR OBJECT_ID(N'dbo.LabEquipment', N'U') IS NULL
 OR OBJECT_ID(N'dbo.Users', N'U') IS NULL
 OR DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat') IS NULL
 THROW 57101, 'Microequipment and Windows UAT security prerequisites are missing.', 1;
IF OBJECT_ID(N'dbo.EQ001_UAT_CreateMicroEquipmentDraft') IS NOT NULL
 THROW 57102, 'Unknown existing draft boundary must not be replaced.', 1;
IF NOT EXISTS (
 SELECT 1 FROM sys.triggers
 WHERE parent_id = OBJECT_ID(N'dbo.MicroEquipmentActivityAudit')
 AND name = N'TRG_MicroEquipmentActivityAudit_AppendOnly' AND is_disabled = 0)
 THROW 57103, 'Append-only equipment audit protection is required.', 1;
BEGIN TRY
 BEGIN TRANSACTION;
 DECLARE @Definition nvarchar(max) = N'
CREATE PROCEDURE dbo.EQ001_UAT_CreateMicroEquipmentDraft
 @EquipmentID int,
 @ActivityType nvarchar(60),
 @Actor nvarchar(100),
 @MethodReference nvarchar(200)
WITH EXECUTE AS OWNER
AS
BEGIN
 SET NOCOUNT ON;
 SET XACT_ABORT ON;
 IF DB_NAME()<>N''EQ001_UAT_AMEEN_20261024''
 OR @@SERVERNAME<>N''AMEEN\SQLEXPRESS''
 THROW 57104,''Wrong UAT destination.'',1;

 DECLARE @ExpectedActor nvarchar(100) =
  CASE ORIGINAL_LOGIN()
   WHEN N''AMEEN\LimsUatAdmin'' THEN N''admin''
   WHEN N''AMEEN\LimsUatQA'' THEN N''qa''
   WHEN N''AMEEN\LimsReview'' THEN N''AMEEN\LimsReview''
   ELSE NULL END;
 IF @ExpectedActor IS NULL OR @Actor IS NULL OR @Actor<>@ExpectedActor
 THROW 57105,''Windows caller and PharmaLIMS actor identity mismatch.'',1;
 IF @EquipmentID IS NULL OR @EquipmentID<=0 OR
  @ActivityType NOT IN (N''Use'',N''Incubation'',N''Calibration'',N''Periodic Verification'',N''Cleaning'',
                      N''Sanitization'',N''Maintenance'',N''Breakdown'',N''Repair'')
 OR @MethodReference IS NULL OR LEN(LTRIM(RTRIM(@MethodReference)))=0 OR LEN(@MethodReference)>200
 THROW 57106,''Invalid equipment activity draft input.'',1;

 DECLARE @Matches int;
 SELECT @Matches=COUNT(*)
 FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
 WHERE Username=@ExpectedActor AND IsActive=1 AND MustChangePassword=0
 AND CanEnterResults=1
 AND (IsLocked=0 OR (LockedUntil IS NOT NULL AND LockedUntil<=SYSDATETIME()));
 IF @Matches<>1
 THROW 57107,''Actor does not have active equipment-entry authorization.'',1;

 DECLARE @Created TABLE(ActivityID bigint NOT NULL);
 INSERT dbo.MicroEquipmentActivities
  (EquipmentID,ActivityType,PerformedBy,MethodReference,
   EquipmentCodeSnapshot,EquipmentNameSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot)
 OUTPUT INSERTED.ActivityID INTO @Created(ActivityID)
 SELECT e.EquipmentID,@ActivityType,@ExpectedActor,LTRIM(RTRIM(@MethodReference)),
        e.EquipmentCode,e.EquipmentName,e.CalibrationStatus,e.QualificationStatus
 FROM dbo.LabEquipment e WITH(UPDLOCK,HOLDLOCK)
 WHERE e.EquipmentID=@EquipmentID AND e.IsActive=1
 AND e.EquipmentCode LIKE N''MIC-EQ-[0-9][0-9][0-9]''
 AND LEN(e.EquipmentCode)=10
 AND TRY_CONVERT(int,SUBSTRING(e.EquipmentCode,8,3))>=1
 AND UPPER(LTRIM(RTRIM(e.Department)))=N''MICROBIOLOGY'';
 IF (SELECT COUNT(*) FROM @Created)<>1
 THROW 57108,''Selected equipment is not a registered active Microbiology device.'',1;

 DECLARE @NewID bigint=(SELECT ActivityID FROM @Created);
 INSERT dbo.MicroEquipmentActivityAudit(ActivityID,ActionType,NewStateJson,ActionReason,ChangedBy)
 SELECT a.ActivityID,N''DRAFT CREATED'',
 (SELECT a.ActivityID,a.EquipmentID,a.ActivityType,a.ActivityStatus,a.PerformedBy,
         a.ActualStartAt,a.ActualEndAt,a.EquipmentCodeSnapshot,a.CalibrationStatusSnapshot,
         a.QualificationStatusSnapshot,a.ActivityReason,a.MethodReference
  FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),
 N''Created pending execution confirmation.'',@ExpectedActor
 FROM dbo.MicroEquipmentActivities a WITH(UPDLOCK,HOLDLOCK)
 WHERE a.ActivityID=@NewID;
 IF @@ROWCOUNT<>1 THROW 57109,''Draft audit insert failed.'',1;
 SELECT @NewID AS ActivityID;
END';
 EXEC sys.sp_executesql @Definition;
 GRANT EXECUTE ON dbo.EQ001_UAT_CreateMicroEquipmentDraft TO PharmaLIMS_EquipmentWindowsUat;
 IF NOT EXISTS(
 SELECT 1 FROM sys.database_permissions p
 WHERE p.major_id=OBJECT_ID(N'dbo.EQ001_UAT_CreateMicroEquipmentDraft')
 AND p.grantee_principal_id=DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat')
 AND p.permission_name=N'EXECUTE' AND p.state=N'G')
 THROW 57110,'Scoped execution permission was not verified.',1;
 COMMIT;
 PRINT N'PASS: UAT Microequipment draft procedure installed with scoped EXECUTE.';
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
