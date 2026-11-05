-- EQ-001 isolated UAT: prove shared role denial without persisting changes.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() NOT LIKE N'PharmaLIMS_Integration_%' AND DB_NAME() NOT LIKE N'PharmaLIMS_UAT_CLEAN_%'
 THROW 56720, 'Only isolated UAT is permitted.', 1;
IF OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage',N'P') IS NULL
 THROW 56721, 'Signing procedure is missing.', 1;
IF USER_ID(N'pharmalims_app') IS NULL
 THROW 56722, 'Application UAT user is missing.', 1;
BEGIN TRANSACTION;
BEGIN TRY
 REVOKE EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage FROM PharmaLIMS_EquipmentApp;
 DENY EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage TO PharmaLIMS_EquipmentApp;
 DECLARE @allowed int;
 EXECUTE AS USER=N'pharmalims_app';
 SELECT @allowed=HAS_PERMS_BY_NAME(N'dbo.MicroEquipment_SignActivityStage',N'OBJECT',N'EXECUTE');
 REVERT;
 IF @allowed IS NULL OR @allowed<>0
  THROW 56723, 'Shared application user can still execute signing.', 1;
 PRINT 'PASS: shared application user denied elevated signing in transactional UAT probe.';
 ROLLBACK TRANSACTION;
 PRINT 'ROLLBACK: no persistent SQL permission changes were made.';
END TRY
BEGIN CATCH
 IF USER_NAME()=N'pharmalims_app' REVERT;
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
