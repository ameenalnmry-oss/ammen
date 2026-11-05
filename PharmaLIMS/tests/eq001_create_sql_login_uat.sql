-- EQ-001: dedicated Windows service SQL login. UAT preparation only.
-- No production database is modified and no server role is assigned.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'PharmaLIMS_Integration_2c941d953f70'
    THROW 56740, 'Refusing SQL login creation outside the approved isolated UAT database.', 1;
IF IS_SRVROLEMEMBER(N'sysadmin') <> 1
    THROW 56741, 'Administrator authorization required for Windows login provisioning.', 1;
IF OBJECT_ID(N'dbo.MicroEquipment_SignActivityStage', N'P') IS NULL
    THROW 56742, 'UAT signing migration is missing.', 1;
IF EXISTS (SELECT 1 FROM sys.server_principals
           WHERE name=N'AMEEN\PharmaLIMSSigner' AND type<>N'U')
    THROW 56743, 'Existing login name has unexpected SQL principal type.', 1;
IF SUSER_ID(N'AMEEN\PharmaLIMSSigner') IS NULL
    EXEC (N'CREATE LOGIN [AMEEN\PharmaLIMSSigner] FROM WINDOWS;');
IF NOT EXISTS (SELECT 1 FROM sys.server_principals
               WHERE name=N'AMEEN\PharmaLIMSSigner' AND type=N'U')
    THROW 56744, 'Dedicated Windows login was not created.', 1;
IF EXISTS (SELECT 1 FROM sys.server_role_members m
           JOIN sys.server_principals u ON m.member_principal_id=u.principal_id
           WHERE u.name=N'AMEEN\PharmaLIMSSigner')
    THROW 56745, 'Dedicated signer must have no SQL server role membership.', 1;
PRINT 'PASS: Dedicated Windows signer SQL login exists with no server role memberships.';
