-- EQ-001 CONTROLLED CUTOVER - UAT acceptance and change approval required.
-- Execute ONLY against approved isolated UAT after trusted signer functionality PASS.
-- Do not run this script against production; use controlled release SQL for production.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() NOT LIKE N'PharmaLIMS_UAT_CLEAN_%'
   AND DB_NAME() NOT LIKE N'PharmaLIMS_Integration_%'
   AND DB_NAME() <> N'PharmaLIMS_UAT_20261001'
    THROW 56600, 'EQ-001 permission cutover allowed only in isolated UAT.', 1;
IF OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage', N'P') IS NULL
    THROW 56601, 'EQ-001 signing procedure not deployed.', 1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentApp') IS NULL
    THROW 56602, 'EQ-001 application SQL role not deployed.', 1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
    THROW 56603, 'EQ-001 dedicated signing SQL principal not provisioned.', 1;
-- Verify the actual Windows UAT runtime when the bounded Windows-only role is installed.
-- The legacy shared SQL login check remains for the original SQL-auth deployment.
DECLARE @Runtime TABLE(Name sysname NOT NULL PRIMARY KEY, WindowsRuntime bit NOT NULL);
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentWindowsUat') IS NOT NULL
 INSERT @Runtime VALUES(N'AMEEN\LimsReview',1),(N'AMEEN\LimsApprove',1);
ELSE
 INSERT @Runtime VALUES(N'pharmalims_app',0);
IF EXISTS(SELECT 1 FROM @Runtime runtime
 LEFT JOIN sys.database_principals principal ON principal.name=runtime.Name
 WHERE principal.principal_id IS NULL
 OR SUSER_SID(runtime.Name) IS NULL OR principal.sid<>SUSER_SID(runtime.Name)
 OR (runtime.WindowsRuntime=1 AND principal.type<>N'U')
 OR (runtime.WindowsRuntime=0 AND principal.type<>N'S')
 OR ISNULL(IS_ROLEMEMBER(N'PharmaLIMS_EquipmentApp',runtime.Name),0)<>1
 OR (runtime.WindowsRuntime=1 AND ISNULL(IS_ROLEMEMBER(N'PharmaLIMS_EquipmentWindowsUat',runtime.Name),0)<>1)
 OR ISNULL(IS_SRVROLEMEMBER(N'sysadmin',runtime.Name),1)<>0)
 THROW 56610,'Actual UAT runtime mapping/role is missing or elevated; no cutover.',1;
IF EXISTS(SELECT 1 FROM @Runtime runtime
 WHERE ISNULL(IS_ROLEMEMBER(N'db_owner',runtime.Name),1)<>0
 OR ISNULL(IS_ROLEMEMBER(N'db_securityadmin',runtime.Name),1)<>0
 OR ISNULL(IS_ROLEMEMBER(N'db_ddladmin',runtime.Name),1)<>0)
 THROW 56611,'Actual UAT runtime has elevated database membership; no cutover.',1;
-- TrustedSigning requires Windows Integrated Security; SQL-password-only
-- users cannot be used as its dedicated service identity.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals
               WHERE name=N'PharmaLIMS_TrustedEquipmentSigner' AND type=N'U')
    THROW 56607, 'EQ-001 trusted signer must map to a dedicated Windows login.', 1;
IF EXISTS (SELECT 1 FROM sys.database_role_members m
   JOIN sys.database_principals r ON r.principal_id=m.role_principal_id
   JOIN sys.database_principals u ON u.principal_id=m.member_principal_id
   WHERE u.name=N'PharmaLIMS_TrustedEquipmentSigner'
     AND r.name IN (N'PharmaLIMS_EquipmentApp',N'db_owner',N'db_securityadmin',N'db_ddladmin',N'db_datawriter'))
    THROW 56604, 'EQ-001 dedicated signing principal has forbidden role membership.', 1;
-- Do not mistake source contracts, synthetic SQL probes, or a service launch for functional UAT.
EXEC dbo.MicroEquipment_AssertTrustedBoundary;
IF OBJECT_ID(N'dbo.EQ001_UAT_Acceptance',N'U') IS NULL
 THROW 56766,'Controlled EQ-001 UAT acceptance is missing; no permissions changed.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.EQ001_UAT_Acceptance
 WHERE DatabaseName=DB_NAME() AND TrustedBoundary=N'20261017_001'
 AND ReviewAndApprovalPassed=1 AND NegativeSecurityPassed=1
 AND ContentIntegrityPassed=1 AND RegressionPassed=1)
 THROW 56767,'Functional EQ-001 UAT has not been accepted; no permissions changed.',1;
BEGIN TRANSACTION;
BEGIN TRY
    REVOKE EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage FROM PharmaLIMS_EquipmentApp;
    DENY EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage TO PharmaLIMS_EquipmentApp;
    -- Integrity/origin/audit guards are owned by the additive hardening migration.
    -- AssertTrustedBoundary above verifies them; cutover must never replace their bodies.
    GRANT EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage TO PharmaLIMS_TrustedEquipmentSigner;
    IF NOT EXISTS (SELECT 1 FROM sys.database_permissions p
        JOIN sys.database_principals r ON r.principal_id=p.grantee_principal_id
        WHERE r.name=N'PharmaLIMS_EquipmentApp'
          AND p.major_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
          AND p.permission_name=N'EXECUTE' AND p.state=N'D')
        THROW 56605, 'EQ-001 shared role EXECUTE denial was not applied.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.database_permissions p
        JOIN sys.database_principals r ON r.principal_id=p.grantee_principal_id
        WHERE r.name=N'PharmaLIMS_TrustedEquipmentSigner'
          AND p.major_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
          AND p.permission_name=N'EXECUTE' AND p.state=N'G')
        THROW 56606, 'EQ-001 trusted signer EXECUTE grant was not applied.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.triggers
      WHERE object_id=OBJECT_ID(N'dbo.TRG_MicroEquipmentActivitySignatures_TrustedOrigin',N'TR')
        AND is_disabled=0)
        THROW 56608, 'EQ-001 trusted SQL origin guard for direct signature inserts is missing.', 1;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
PRINT 'EQ-001 isolated UAT permission cutover complete. Run positive/negative signature tests.';
