SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
    THROW 55100, 'User administration security hardening requires dbo.Users.', 1;

IF COL_LENGTH(N'dbo.Users', N'MustChangePassword') IS NULL
BEGIN
    ALTER TABLE dbo.Users
        ADD MustChangePassword BIT NOT NULL
            CONSTRAINT DF_Users_MustChangePassword_20260915 DEFAULT (0) WITH VALUES;
END;

IF COL_LENGTH(N'dbo.Users', N'PasswordChangedAt') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD PasswordChangedAt DATETIME2(0) NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Users')
      AND name = N'MustChangePassword'
      AND system_type_id = TYPE_ID(N'bit')
      AND is_nullable = 0
)
    THROW 55101, 'dbo.Users.MustChangePassword is missing or has an incompatible schema.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Users')
      AND name = N'PasswordChangedAt'
      AND system_type_id = TYPE_ID(N'datetime2')
      AND scale = 0
      AND is_nullable = 1
)
    THROW 55102, 'dbo.Users.PasswordChangedAt is missing or has an incompatible schema.', 1;
