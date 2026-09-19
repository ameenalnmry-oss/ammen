SET NOCOUNT ON;
SET XACT_ABORT ON;

-- PharmaLIMS 2026.9.11.265
-- Authentication-only compatibility backfill.
-- This migration does not create users, reset passwords, unlock accounts,
-- activate accounts, grant permissions, or modify laboratory/sample/result data.
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 55010, 'Authentication compatibility requires dbo.Users. No user table was found in the configured PharmaLIMS database.', 1;

    IF COL_LENGTH(N'dbo.Users', N'UserID') IS NULL
       OR COL_LENGTH(N'dbo.Users', N'Username') IS NULL
       OR COL_LENGTH(N'dbo.Users', N'FullName') IS NULL
       OR COL_LENGTH(N'dbo.Users', N'Role') IS NULL
       OR COL_LENGTH(N'dbo.Users', N'IsActive') IS NULL
        THROW 55011, 'dbo.Users is missing a core identity column (UserID, Username, FullName, Role, or IsActive). Controlled schema reconciliation is required.', 1;

    -- Legacy password storage is retained only as a migration source. Never populate it here.
    IF COL_LENGTH(N'dbo.Users', N'PasswordHash') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD PasswordHash NVARCHAR(512) NULL;';

    IF COL_LENGTH(N'dbo.Users', N'PasswordHashNew') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD PasswordHashNew NVARCHAR(512) NULL;';

    IF COL_LENGTH(N'dbo.Users', N'PasswordSalt') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD PasswordSalt NVARCHAR(256) NULL;';

    IF COL_LENGTH(N'dbo.Users', N'Department') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD Department NVARCHAR(150) NULL;';

    IF COL_LENGTH(N'dbo.Users', N'Section') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD Section NVARCHAR(150) NULL;';

    IF COL_LENGTH(N'dbo.Users', N'LastLogin') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD LastLogin DATETIME2(0) NULL;';

    IF COL_LENGTH(N'dbo.Users', N'FailedLoginAttempts') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD FailedLoginAttempts INT NOT NULL CONSTRAINT DF_Users_FailedLoginAttempts_20260911 DEFAULT (0) WITH VALUES;';

    IF COL_LENGTH(N'dbo.Users', N'IsLocked') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD IsLocked BIT NOT NULL CONSTRAINT DF_Users_IsLocked_20260911 DEFAULT (0) WITH VALUES;';

    IF COL_LENGTH(N'dbo.Users', N'LockedUntil') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD LockedUntil DATETIME2(0) NULL;';

    -- Missing permission columns are fail-safe: new compatibility columns default to no permission.
    -- No role is automatically granted any permission by this migration.
    IF COL_LENGTH(N'dbo.Users', N'CanAccessWater') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanAccessWater BIT NOT NULL CONSTRAINT DF_Users_CanAccessWater_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanAccessEM') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanAccessEM BIT NOT NULL CONSTRAINT DF_Users_CanAccessEM_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanRegisterSamples') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanRegisterSamples BIT NOT NULL CONSTRAINT DF_Users_CanRegisterSamples_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanEnterResults') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanEnterResults BIT NOT NULL CONSTRAINT DF_Users_CanEnterResults_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanReviewResults') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanReviewResults BIT NOT NULL CONSTRAINT DF_Users_CanReviewResults_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanApproveResults') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanApproveResults BIT NOT NULL CONSTRAINT DF_Users_CanApproveResults_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanIssueCOA') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanIssueCOA BIT NOT NULL CONSTRAINT DF_Users_CanIssueCOA_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanCancelCOA') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanCancelCOA BIT NOT NULL CONSTRAINT DF_Users_CanCancelCOA_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanAccessReports') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanAccessReports BIT NOT NULL CONSTRAINT DF_Users_CanAccessReports_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanManageUsers') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanManageUsers BIT NOT NULL CONSTRAINT DF_Users_CanManageUsers_20260911 DEFAULT (0) WITH VALUES;';
    IF COL_LENGTH(N'dbo.Users', N'CanManageSettings') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD CanManageSettings BIT NOT NULL CONSTRAINT DF_Users_CanManageSettings_20260911 DEFAULT (0) WITH VALUES;';

    -- SQL Server permits only one rowversion/timestamp column per table.
    IF COL_LENGTH(N'dbo.Users', N'AuthenticationRowVersion') IS NULL
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.Users')
              AND system_type_id = 189
        )
            THROW 55012, 'dbo.Users already contains another rowversion/timestamp column. AuthenticationRowVersion cannot be added automatically; controlled schema reconciliation is required.', 1;

        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD AuthenticationRowVersion rowversion NOT NULL;';
    END;

    -- Fail closed on incompatible pre-existing columns. No ALTER COLUMN is performed here.
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users') AND name=N'PasswordHashNew'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR (max_length<>-1 AND max_length<1024))
    )
        THROW 55013, 'dbo.Users.PasswordHashNew is incompatible; expected NVARCHAR(512) or wider.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users') AND name=N'PasswordSalt'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR (max_length<>-1 AND max_length<512))
    )
        THROW 55014, 'dbo.Users.PasswordSalt is incompatible; expected NVARCHAR(256) or wider.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users') AND name=N'FailedLoginAttempts'
          AND (system_type_id<>TYPE_ID(N'int') OR is_nullable<>0)
    )
        THROW 55015, 'dbo.Users.FailedLoginAttempts is incompatible; expected INT NOT NULL.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users') AND name=N'IsLocked'
          AND (system_type_id<>TYPE_ID(N'bit') OR is_nullable<>0)
    )
        THROW 55016, 'dbo.Users.IsLocked is incompatible; expected BIT NOT NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users') AND name=N'AuthenticationRowVersion'
          AND system_type_id=189 AND is_nullable=0
    )
        THROW 55017, 'dbo.Users.AuthenticationRowVersion is missing or incompatible after authentication compatibility preparation.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name IN
          (
              N'CanAccessWater', N'CanAccessEM', N'CanRegisterSamples', N'CanEnterResults',
              N'CanReviewResults', N'CanApproveResults', N'CanIssueCOA', N'CanCancelCOA',
              N'CanAccessReports', N'CanManageUsers', N'CanManageSettings'
          )
          AND (system_type_id<>TYPE_ID(N'bit') OR is_nullable<>0)
    )
        THROW 55018, 'One or more dbo.Users permission columns are incompatible; expected BIT NOT NULL.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
