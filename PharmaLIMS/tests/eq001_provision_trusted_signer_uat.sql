-- EQ-001 isolated UAT ONLY: map an EXISTING dedicated Windows service login.
-- The Windows account and SQL server login must be provisioned separately
-- by an authorized administrator; never use the interactive WPF account.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() NOT LIKE N'PharmaLIMS_Integration_%'
   AND DB_NAME() NOT LIKE N'PharmaLIMS_UAT_CLEAN_%'
   AND DB_NAME() <> N'PharmaLIMS_UAT_20261001'
 THROW 56730, 'Trusted signing provisioning allowed only on isolated UAT.', 1;
IF OBJECT_ID(N'dbo.MicroEquipmentSignerEligibility',N'V') IS NULL
   OR OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage',N'P') IS NULL
   OR OBJECT_ID(N'dbo.MicroEquipment_ReadActivitySignerCredential',N'P') IS NULL
   OR OBJECT_ID(N'dbo.MicroEquipment_RecordActivityAuthFailure',N'P') IS NULL
   OR OBJECT_ID(N'dbo.MicroEquipment_CommitActivityAuthSuccess',N'P') IS NULL
 THROW 56731, 'Apply the approved v309 equipment signing migrations first.', 1;
DECLARE @serviceLogin sysname=N'AMEEN\PharmaLIMSSigner';
IF SUSER_ID(@serviceLogin) IS NULL
 THROW 56732, 'Dedicated Windows service login is not provisioned at SQL Server level.', 1;
IF NOT EXISTS(SELECT 1 FROM sys.server_principals WHERE name=@serviceLogin AND type IN(N'U',N'G'))
 THROW 56733, 'Trusted service identity must be a Windows principal.', 1;
IF EXISTS(SELECT 1 FROM sys.server_role_members m JOIN sys.server_principals r ON r.principal_id=m.role_principal_id
 JOIN sys.server_principals s ON s.principal_id=m.member_principal_id
 WHERE s.name=@serviceLogin AND r.name IN(N'sysadmin',N'securityadmin',N'serveradmin',N'dbcreator'))
 THROW 56734, 'Trusted service Windows identity has elevated SQL server roles.', 1;
BEGIN TRANSACTION;
BEGIN TRY
 IF USER_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
 BEGIN
  DECLARE @create nvarchar(max)=N'CREATE USER [PharmaLIMS_TrustedEquipmentSigner] FOR LOGIN '+QUOTENAME(@serviceLogin)+N';';
  EXEC sys.sp_executesql @create;
 END;
 IF NOT EXISTS(SELECT 1 FROM sys.database_principals p WHERE name=N'PharmaLIMS_TrustedEquipmentSigner' AND type=N'U' AND sid=SUSER_SID(@serviceLogin))
  THROW 56735, 'The mapped trusted signer database identity is not a Windows user.', 1;
 -- Read readiness and permission flags only; password hashes/salts are never exposed.
 GRANT SELECT ON OBJECT::dbo.MicroEquipmentSignerEligibility TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_AssertTrustedBoundary TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_ReadActivitySignerCredential TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_RecordActivityAuthFailure TO [PharmaLIMS_TrustedEquipmentSigner];
 GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_CommitActivityAuthSuccess TO [PharmaLIMS_TrustedEquipmentSigner];
 DENY SELECT ON OBJECT::dbo.Users (PasswordHash,PasswordHashNew,PasswordSalt) TO [PharmaLIMS_TrustedEquipmentSigner];
 DENY UPDATE ON OBJECT::dbo.Users TO [PharmaLIMS_TrustedEquipmentSigner];
 -- SQL Server 2022: necessary to verify the shared application DENY from the
 -- dedicated least-privilege service process. Metadata access is read-only.
 GRANT VIEW SECURITY DEFINITION TO [PharmaLIMS_TrustedEquipmentSigner];
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
PRINT 'UAT trusted signer provisioned. Execute cutover only after functional UAT.';
