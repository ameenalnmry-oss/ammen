/*
EQ-001 read-only SQL permission preflight.
Run only on an isolated UAT database as a database owner/security reviewer.
Set @AppPrincipal to the EXACT database user used by the shared WPF connection.
No grants, revokes, schema changes, or historical-record edits are performed.
A PASS here is necessary but not sufficient for trusted-signing approval.
*/
SET NOCOUNT ON;
DECLARE @AppPrincipal sysname = NULL; -- REQUIRED: N'actual_database_user'
IF @AppPrincipal IS NULL OR LTRIM(RTRIM(@AppPrincipal)) = N''
    THROW 56020, 'EQ-001: Specify the verified WPF database principal before running this audit.', 1;

DECLARE @PrincipalId int = DATABASE_PRINCIPAL_ID(@AppPrincipal);
IF @PrincipalId IS NULL
    THROW 56021, 'EQ-001: WPF database principal does not exist in the current database.', 1;
IF OBJECT_ID(N'dbo.LabEquipmentSignatures', N'U') IS NULL
    THROW 56022, 'EQ-001: LabEquipmentSignatures table is missing; audit cannot continue.', 1;

;WITH RoleMembership AS
(
    SELECT @PrincipalId AS principal_id, 0 AS depth
    UNION ALL
    SELECT drm.role_principal_id, rm.depth + 1
    FROM sys.database_role_members drm
    JOIN RoleMembership rm ON drm.member_principal_id = rm.principal_id
    WHERE rm.depth < 20
),
PrincipalClosure AS
(
    SELECT DISTINCT principal_id FROM RoleMembership
    UNION SELECT DATABASE_PRINCIPAL_ID(N'public')
),
Acl AS
(
    SELECT USER_NAME(p.grantee_principal_id) AS grantee,
           p.class_desc, p.major_id, p.permission_name, p.state_desc
    FROM sys.database_permissions p
    JOIN PrincipalClosure pc ON pc.principal_id = p.grantee_principal_id
),
Dangerous AS
(
    SELECT grantee,class_desc,major_id,permission_name,state_desc,
        CASE WHEN state_desc LIKE N'GRANT%'
            AND (
                (class_desc = N'DATABASE' AND permission_name IN
                    (N'CONTROL',N'ALTER ANY SCHEMA',N'IMPERSONATE'))
                OR (class_desc = N'SCHEMA' AND major_id = SCHEMA_ID(N'dbo')
                    AND permission_name IN (N'CONTROL',N'ALTER',N'INSERT'))
                OR (class_desc = N'OBJECT_OR_COLUMN'
                    AND major_id = OBJECT_ID(N'dbo.LabEquipmentSignatures')
                    AND permission_name IN (N'INSERT',N'CONTROL',N'ALTER'))
            )
        THEN 1 ELSE 0 END AS PotentialDirectSigningAccess
    FROM Acl
)
SELECT @AppPrincipal AS AuditedPrincipal, DB_NAME() AS DatabaseName,
       grantee,class_desc,major_id,permission_name,state_desc,
       PotentialDirectSigningAccess
FROM Dangerous
WHERE PotentialDirectSigningAccess = 1
ORDER BY grantee,class_desc,permission_name
OPTION (MAXRECURSION 25);

IF EXISTS (
    SELECT 1 FROM sys.database_role_members drm
    WHERE drm.role_principal_id = DATABASE_PRINCIPAL_ID(N'db_owner')
      AND drm.member_principal_id = @PrincipalId
)
    THROW 56023, 'EQ-001 BLOCKER: the WPF principal is a direct db_owner member.', 1;

-- This is a metadata-only preflight. DENY/GRANT interactions, inherited
-- Windows group rights, sysadmin membership, stored procedure rights,
-- schema ownership, and effective rights require separate controlled UAT checks.
PRINT N'EQ-001: ACL metadata listed. Independently verify effective rights as the WPF login.';
