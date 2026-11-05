-- EQ-001 isolated acceptance gate. NEVER run on operational databases.
-- Requires a disposable database with full migrations applied.
-- PASS = db_datareader/db_datawriter actor cannot forge Reviewed/Approved.
-- FAIL = forged signatures lead to Approved; the transaction still rolls back.
IF DB_NAME() NOT LIKE N'PharmaLIMS_UAT_CLEAN_%' AND DB_NAME() NOT LIKE N'PharmaLIMS_Integration_%'
 THROW 56589,'EQ-001 test requires an isolated disposable database.',1;
SET NOCOUNT ON;
SET XACT_ABORT OFF;
BEGIN TRANSACTION;
-- Rehearse the trusted-origin signature trigger; the transaction restores
-- all original schema permissions, triggers and test records after the check.
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentActivitySignatures_TrustedOrigin
  ON dbo.MicroEquipmentActivitySignatures AFTER INSERT AS
  BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (
      SELECT 1 FROM sys.database_principals p
      WHERE p.name=N''PharmaLIMS_TrustedEquipmentSigner''
        AND p.type=N''U''
        AND p.sid=SUSER_SID(ORIGINAL_LOGIN()))
      THROW 56609, ''Direct or untrusted equipment signature insertion is forbidden.'', 1;
  END;');
DECLARE @bypass bit = 0;
BEGIN TRY
 CREATE USER eq001_test_writer WITHOUT LOGIN;
 ALTER ROLE db_datawriter ADD MEMBER eq001_test_writer;
 ALTER ROLE db_datareader ADD MEMBER eq001_test_writer;
 INSERT dbo.LabEquipment (EquipmentCode,EquipmentName,EquipmentType,Department,GmpCritical,
    EquipmentStatus,QualificationStatus,CalibrationRequired,CalibrationStatus,IsActive,CreatedBy,CreatedAt)
 VALUES (N'MIC-EQ-998',N'EQ001 ISOLATED',N'Incubator',N'Microbiology',1,
    N'Active',N'Qualified',0,N'Not Required',1,N'test',SYSUTCDATETIME());
 DECLARE @equipment int = CONVERT(int,SCOPE_IDENTITY());
 INSERT dbo.MicroEquipmentActivities(EquipmentID,ActivityType,ActivityStatus,PerformedBy,
    EquipmentCodeSnapshot,EquipmentNameSnapshot)
 VALUES(@equipment,N'Cleaning',N'Submitted',N'operator',N'MIC-EQ-998',N'EQ001 ISOLATED');
 DECLARE @activity bigint = CONVERT(bigint,SCOPE_IDENTITY());
 EXECUTE AS USER=N'eq001_test_writer';
 BEGIN TRY
  INSERT dbo.MicroEquipmentActivitySignatures(ActivityID,SignatureMeaning,SignatureReason,ContentHash,SignedBy)
  VALUES(@activity,N'Reviewed',N'forged',0x0101010101010101010101010101010101010101010101010101010101010101,N'reviewer');
  UPDATE dbo.MicroEquipmentActivities SET ActivityStatus=N'Reviewed' WHERE ActivityID=@activity;
  INSERT dbo.MicroEquipmentActivitySignatures(ActivityID,SignatureMeaning,SignatureReason,ContentHash,SignedBy)
  VALUES(@activity,N'Approved',N'forged',0x0202020202020202020202020202020202020202020202020202020202,N'approver');
  UPDATE dbo.MicroEquipmentActivities SET ActivityStatus=N'Approved' WHERE ActivityID=@activity;
  SET @bypass = 1;
 END TRY
 BEGIN CATCH
  DECLARE @blockedError int=ERROR_NUMBER();
  -- SQL triggers treat THROW as a doomed transaction. Roll back before
  -- reverting the simulated caller; nothing is left in the database.
  IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
  REVERT;
  IF @blockedError<>56609 THROW 56591,'Unexpected signature rejection; review SQL evidence.',1;
  PRINT 'EQ-001 PASS: db_datawriter direct signature forgery rejected by trusted-origin guard; all changes rolled back.';
  RETURN;
 END CATCH;
 REVERT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 IF USER_NAME()=N'eq001_test_writer' REVERT;
 THROW;
END CATCH;
IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
IF @bypass=1 THROW 56590,'EQ-001 CRITICAL FAIL: forged equipment approval accepted by application-equivalent SQL roles.',1;
THROW 56592,'The expected rejection was not observed.',1;
