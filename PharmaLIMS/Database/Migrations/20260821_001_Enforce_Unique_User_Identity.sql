SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
    THROW 52000, 'Required table dbo.Users is missing.', 1;

IF COL_LENGTH(N'dbo.Users', N'Username') IS NULL
    THROW 52001, 'Required column dbo.Users.Username is missing.', 1;

-- Do not silently rewrite regulated user identities. Any invalid identity must be
-- reconciled by an authorized administrator before this migration is applied.
IF EXISTS
(
    SELECT 1
    FROM dbo.Users
    WHERE Username IS NULL
       OR LEN(LTRIM(RTRIM(Username))) = 0
       OR Username <> LTRIM(RTRIM(Username))
)
    THROW 52002, 'Users contains blank or untrimmed usernames. Reconcile the affected accounts before migration.', 1;

IF EXISTS
(
    SELECT LTRIM(RTRIM(Username))
    FROM dbo.Users
    GROUP BY LTRIM(RTRIM(Username))
    HAVING COUNT(*) > 1
)
    THROW 52003, 'Users contains duplicate usernames. Reconcile duplicate regulated identities before migration.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Users')
      AND name = N'UX_Users_Username_20260821'
)
BEGIN
    CREATE UNIQUE INDEX UX_Users_Username_20260821
        ON dbo.Users(Username);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.Users')
      AND name = N'CK_Users_Username_Controlled_20260821'
)
BEGIN
    ALTER TABLE dbo.Users WITH CHECK
    ADD CONSTRAINT CK_Users_Username_Controlled_20260821
        CHECK
        (
            Username IS NOT NULL
            AND LEN(Username) BETWEEN 1 AND 100
            AND Username = LTRIM(RTRIM(Username))
        );

    ALTER TABLE dbo.Users
        CHECK CONSTRAINT CK_Users_Username_Controlled_20260821;
END;
