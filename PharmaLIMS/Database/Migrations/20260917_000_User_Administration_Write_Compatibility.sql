SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS 2026.9.17.288
    User Administration write-path compatibility for legacy dbo.Users schemas.

    Rationale:
    - Legacy databases can satisfy login/authentication migrations while still lacking
      dbo.Users.CreatedAt and/or dbo.Users.UpdatedAt.
    - UserAdministrationService and Change Password write paths reference those columns.
    - Existing legacy CreatedAt values are never fabricated. If CreatedAt is absent,
      the new column is nullable so historical accounts remain NULL (unknown original
      creation time). A default is added only for future inserts that omit the column.
    - No role, permission, activation state, password, lock state, or laboratory data
      is modified by this migration.
*/

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
    THROW 55200, 'User administration write compatibility requires dbo.Users.', 1;

IF COL_LENGTH(N'dbo.Users', N'CreatedAt') IS NULL
BEGIN
    ALTER TABLE dbo.Users
        ADD CreatedAt DATETIME2(0) NULL
            CONSTRAINT DF_Users_CreatedAt_20260917 DEFAULT SYSUTCDATETIME();
END;
ELSE IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Users')
      AND name = N'CreatedAt'
      AND system_type_id = TYPE_ID(N'datetime2')
      AND scale = 0
)
BEGIN
    THROW 55201, 'dbo.Users.CreatedAt has an incompatible schema; expected DATETIME2(0). Automatic conversion is blocked to preserve historical evidence.', 1;
END;

IF COL_LENGTH(N'dbo.Users', N'UpdatedAt') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD UpdatedAt DATETIME2(0) NULL;
END;
ELSE IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Users')
      AND name = N'UpdatedAt'
      AND system_type_id = TYPE_ID(N'datetime2')
      AND scale = 0
)
BEGIN
    THROW 55202, 'dbo.Users.UpdatedAt has an incompatible schema; expected DATETIME2(0). Automatic conversion is blocked.', 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Users')
      AND name = N'CreatedAt'
      AND system_type_id = TYPE_ID(N'datetime2')
      AND scale = 0
)
    THROW 55203, 'dbo.Users.CreatedAt is missing or incompatible after write compatibility preparation.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Users')
      AND name = N'UpdatedAt'
      AND system_type_id = TYPE_ID(N'datetime2')
      AND scale = 0
)
    THROW 55204, 'dbo.Users.UpdatedAt is missing or incompatible after write compatibility preparation.', 1;
