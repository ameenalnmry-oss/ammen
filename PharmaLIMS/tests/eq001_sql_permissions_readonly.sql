-- EQ-001 database boundary gate. No DML or DDL: safe against current UAT.
-- Pass requires all shared application principals to be unable to execute
-- the elevated signing procedure after a trusted signing route is deployed.
SET NOCOUNT ON;
DECLARE @Result TABLE(PrincipalName sysname, CanInsertSignature int, CanExecuteSigning int);
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentApp') IS NULL
 THROW 56580,'Equipment application role missing.',1;
IF USER_ID(N'pharmalims_app') IS NULL
 THROW 56581,'pharmalims_app user missing.',1;
EXECUTE AS USER = N'pharmalims_app';
INSERT @Result SELECT USER_NAME(),
 HAS_PERMS_BY_NAME(N'dbo.MicroEquipmentActivitySignatures',N'OBJECT',N'INSERT'),
 HAS_PERMS_BY_NAME(N'dbo.MicroEquipment_SignActivityStage',N'OBJECT',N'EXECUTE');
REVERT;
IF USER_ID(N'PharmaLIMSApp') IS NOT NULL
BEGIN
 EXECUTE AS USER = N'PharmaLIMSApp';
 INSERT @Result SELECT USER_NAME(),
  HAS_PERMS_BY_NAME(N'dbo.MicroEquipmentActivitySignatures',N'OBJECT',N'INSERT'),
  HAS_PERMS_BY_NAME(N'dbo.MicroEquipment_SignActivityStage',N'OBJECT',N'EXECUTE');
 REVERT;
END;
SELECT DB_NAME() AS DatabaseName,* FROM @Result ORDER BY PrincipalName;
IF EXISTS(SELECT 1 FROM @Result WHERE CanInsertSignature<>0 OR CanExecuteSigning<>0
 OR CanInsertSignature IS NULL OR CanExecuteSigning IS NULL)
 THROW 56582,'EQ-001 BLOCKED: restricted principal retains signature INSERT or elevated EXECUTE.',1;
PRINT 'EQ-001 SQL permissions PASS';