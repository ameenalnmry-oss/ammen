-- EQ-001 TEST ONLY. Verify least-privilege service SQL boundary without cutover.
-- All signing permission changes ROLLBACK; never touches equipment samples.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'PharmaLIMS_Integration_2c941d953f70'
 THROW 56750, 'Only the specified isolated UAT database is allowed.', 1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') IS NULL
 THROW 56751, 'Windows signer DB principal is missing.', 1;
IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentApp') IS NULL
 THROW 56752, 'Shared application DB role is missing.', 1;
BEGIN TRANSACTION;
BEGIN TRY
 REVOKE EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage FROM PharmaLIMS_EquipmentApp;
 DENY EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage TO PharmaLIMS_EquipmentApp;
 DECLARE @boundary int=0, @app int=-1, @read int=0;
 EXECUTE AS LOGIN=N'AMEEN\PharmaLIMSSigner';
 SELECT USER_NAME() AS EffectivePrincipal,
  IS_ROLEMEMBER(N'PharmaLIMS_EquipmentApp') AS InSharedRole,
  IS_ROLEMEMBER(N'db_owner') AS IsDbOwner,
  IS_ROLEMEMBER(N'db_datawriter') AS IsDbWriter,
  HAS_PERMS_BY_NAME(N'dbo.MicroEquipment_SignActivityStage',N'OBJECT',N'EXECUTE') AS CanSign,
  HAS_PERMS_BY_NAME(N'dbo.MicroEquipmentActivitySignatures',N'OBJECT',N'INSERT') AS CanInsertSignatures,
  (SELECT COUNT(*) FROM sys.database_permissions p JOIN sys.database_principals u ON p.grantee_principal_id=u.principal_id
   WHERE u.name=N'PharmaLIMS_EquipmentApp' AND p.major_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
     AND p.permission_name=N'EXECUTE' AND p.state=N'D') AS VisibleSharedRoleDeny;
 SELECT @boundary=CASE WHEN
    USER_NAME()=N'PharmaLIMS_TrustedEquipmentSigner'
    AND IS_ROLEMEMBER(N'PharmaLIMS_EquipmentApp')=0
    AND IS_ROLEMEMBER(N'db_owner')=0
    AND IS_ROLEMEMBER(N'db_datawriter')=0
    AND HAS_PERMS_BY_NAME(N'dbo.MicroEquipment_SignActivityStage',N'OBJECT',N'EXECUTE')=1
    AND HAS_PERMS_BY_NAME(N'dbo.MicroEquipmentActivitySignatures',N'OBJECT',N'INSERT')=0
    AND EXISTS(SELECT 1 FROM sys.database_permissions p
      JOIN sys.database_principals u ON p.grantee_principal_id=u.principal_id
      WHERE u.name=N'PharmaLIMS_EquipmentApp'
      AND p.major_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
      AND p.permission_name=N'EXECUTE' AND p.state=N'D')
    THEN 1 ELSE 0 END;
 SELECT @read=COUNT_BIG(1) FROM dbo.Users
   WHERE Username=N'AMEEN\Elite' AND IsActive=1 AND CanReviewResults=1;
 REVERT;
 EXECUTE AS USER=N'pharmalims_app';
 SELECT @app=HAS_PERMS_BY_NAME(N'dbo.MicroEquipment_SignActivityStage',N'OBJECT',N'EXECUTE');
 REVERT;
 SELECT @boundary AS TrustedBoundaryReadyDuringProbe,
        @app AS SharedAppExecuteDuringProbe, @read AS WindowsSignerUserMappingCount;
 IF @boundary<>1 THROW 56753, 'Trusted service SQL metadata or privilege check failed.', 1;
 IF @app<>0 THROW 56754, 'Shared app SQL procedure execution still permitted.', 1;
 ROLLBACK TRANSACTION;
 PRINT 'PASS: Trusted Windows signer SQL boundary and shared app DENY proven transactionally.';
END TRY
BEGIN CATCH
 IF USER_NAME() NOT IN (N'dbo',N'AMEEN\Elite') REVERT;
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
