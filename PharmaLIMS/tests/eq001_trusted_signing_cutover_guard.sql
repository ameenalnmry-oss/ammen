-- EQ-001 controlled cutover PRECHECK ONLY: never changes permissions or records.
-- A trusted signing principal is a separately provisioned SQL principal, not a
-- SESSION_CONTEXT value or a username passed by the desktop client.
SET NOCOUNT ON;
DECLARE @Trusted sysname = N'PharmaLIMS_TrustedEquipmentSigner';
IF OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage', N'P') IS NULL
 THROW 56591, 'Controlled equipment signing procedure is missing.', 1;
IF DATABASE_PRINCIPAL_ID(@Trusted) IS NULL
 THROW 56592, 'Trusted equipment signer principal is not provisioned; do not revoke app access yet.', 1;
IF EXISTS (SELECT 1 FROM sys.database_role_members m
 JOIN sys.database_principals r ON r.principal_id=m.role_principal_id
 JOIN sys.database_principals u ON u.principal_id=m.member_principal_id
 WHERE r.name=N'PharmaLIMS_EquipmentApp' AND u.name=@Trusted)
 THROW 56593, 'Trusted signer must not be a member of the shared application role.', 1;
IF EXISTS (SELECT 1 FROM sys.database_role_members m
 JOIN sys.database_principals r ON r.principal_id=m.role_principal_id
 JOIN sys.database_principals u ON u.principal_id=m.member_principal_id
 WHERE u.name=@Trusted AND r.name IN(N'db_owner',N'db_securityadmin',N'db_ddladmin',N'db_datawriter'))
 THROW 56595, 'Trusted signer must not have broad database write or administrative roles.', 1;
IF EXISTS (SELECT 1 FROM sys.database_principals u
 WHERE u.name=@Trusted AND u.type NOT IN(N'S',N'U',N'G'))
 THROW 56596, 'Trusted signer must be a dedicated SQL or Windows principal.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_permissions p
 JOIN sys.database_principals u ON u.principal_id=p.grantee_principal_id
 WHERE u.name=@Trusted AND p.major_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
 AND p.permission_name=N'EXECUTE' AND p.state=N'G')
 THROW 56594, 'Trusted signer has not been granted controlled procedure access.', 1;
IF EXISTS (SELECT 1 FROM sys.database_permissions p
 JOIN sys.database_principals u ON u.principal_id=p.grantee_principal_id
 WHERE u.name=N'PharmaLIMS_EquipmentApp'
 AND p.major_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
 AND p.permission_name=N'EXECUTE' AND p.state IN(N'G',N'W'))
 THROW 56597, 'EQ-001 cutover blocked: shared application role retains elevated EXECUTE permission.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_permissions p
 JOIN sys.database_principals u ON u.principal_id=p.grantee_principal_id
 WHERE u.name=N'PharmaLIMS_EquipmentApp'
 AND p.major_id=OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage')
 AND p.permission_name=N'EXECUTE' AND p.state=N'D')
 THROW 56598, 'EQ-001 cutover blocked: explicit shared application role DENY EXECUTE is not in force.', 1;
PRINT 'CUTOVER PRECHECK: principal isolation and direct SQL grants verified; independent authentication and functional UAT still required.';
