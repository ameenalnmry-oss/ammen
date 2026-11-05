-- EQ-001 UAT ONLY: transactional SQL-path positive and negative signing tests.
-- This is NOT a substitute for a Windows-authenticated HTTP end-to-end UAT.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME()<>N'PharmaLIMS_Integration_2c941d953f70'
 THROW 56760, 'This script is restricted to the named isolated UAT database.', 1;
IF NOT EXISTS(SELECT 1 FROM dbo.Users WHERE Username=N'AMEEN\Elite'
              AND Role=N'UAT-Test' AND IsActive=1 AND IsLocked=0
              AND CanReviewResults=1 AND CanApproveResults=0)
 THROW 56761, 'Dedicated UAT reviewer mapping is not present: no accounts will be changed.', 1;
BEGIN TRANSACTION;
BEGIN TRY
 INSERT dbo.LabEquipment(EquipmentCode,EquipmentName,EquipmentType,Department,GmpCritical,
  EquipmentStatus,QualificationStatus,CalibrationRequired,CalibrationStatus,IsActive,CreatedBy,CreatedAt)
 VALUES(N'MIC-EQ-997',N'EQ001 TEMP REVIEW',N'Incubator',N'Microbiology',1,
  N'Active',N'Qualified',0,N'Not Required',1,N'EQ001 UAT',SYSUTCDATETIME());
 DECLARE @equipment int=CONVERT(int,SCOPE_IDENTITY());
 INSERT dbo.MicroEquipmentActivities(EquipmentID,ActivityType,ActivityStatus,PerformedBy,
  ActualStartAt,ActualEndAt,MethodReference,EquipmentCodeSnapshot,EquipmentNameSnapshot)
 VALUES(@equipment,N'Cleaning',N'Submitted',N'EQ001 UAT operator',SYSDATETIMEOFFSET(),
  SYSDATETIMEOFFSET(),N'EQ001 UAT METHOD',N'MIC-EQ-997',N'EQ001 TEMP REVIEW');
 DECLARE @activity bigint=CONVERT(bigint,SCOPE_IDENTITY());
 DECLARE @version binary(8);
 SELECT @version=VersionToken FROM dbo.MicroEquipmentActivities WHERE ActivityID=@activity;
 REVOKE EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage FROM PharmaLIMS_EquipmentApp;
 DENY EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage TO PharmaLIMS_EquipmentApp;
 DECLARE @authorization bigint=0;
 EXECUTE AS LOGIN=N'AMEEN\PharmaLIMSSigner';
 SELECT @authorization=COUNT_BIG(*) FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
 WHERE Username=N'AMEEN\Elite' AND IsActive=1 AND IsLocked=0 AND CanReviewResults=1;
 IF @authorization<>1 THROW 56762,'Trusted signer could not verify UAT reviewer row.',1;
 EXEC dbo.MicroEquipment_SignActivityStage
  @Activity=@activity,@Version=@version,@Actor=N'AMEEN\Elite',
  @Stage=N'Reviewed',@Reason=N'Isolated transactional positive UAT test';
 REVERT;
 IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivities WHERE ActivityID=@activity AND ActivityStatus=N'Reviewed')
 THROW 56763,'Trusted signing procedure did not advance status to Reviewed.',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivitySignatures
  WHERE ActivityID=@activity AND SignatureMeaning=N'Reviewed' AND SignedBy=N'AMEEN\Elite'
  AND ContentHash IS NOT NULL)
 THROW 56764,'Trusted signing procedure did not produce immutable review evidence.',1;
 DECLARE @appCanExecute int=1;
 EXECUTE AS USER=N'pharmalims_app';
 SELECT @appCanExecute=HAS_PERMS_BY_NAME(N'dbo.MicroEquipment_SignActivityStage',N'OBJECT',N'EXECUTE');
 REVERT;
 IF @appCanExecute<>0 THROW 56765,'Shared SQL application role still executes elevated signing.',1;
 SELECT @authorization AS AuthorizedReviewer, @appCanExecute AS SharedAppCanSign,
  (SELECT COUNT_BIG(*) FROM dbo.MicroEquipmentActivitySignatures WHERE ActivityID=@activity) AS ReviewSignatures;
 ROLLBACK TRANSACTION;
 PRINT 'PASS: Isolated reviewed-stage SQL execution produced signed evidence; shared app denied; ALL test rows rolled back.';
END TRY
BEGIN CATCH
 IF USER_NAME()=N'PharmaLIMS_TrustedEquipmentSigner' OR USER_NAME()=N'pharmalims_app' REVERT;
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
