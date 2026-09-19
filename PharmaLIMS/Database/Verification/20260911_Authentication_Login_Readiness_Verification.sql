SET NOCOUNT ON;

-- Read-only authentication readiness diagnostic. It never selects PasswordHash,
-- PasswordHashNew, PasswordSalt, or any secret value.
SELECT
    DB_NAME() AS DatabaseName,
    CASE WHEN OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL THEN N'PASS' ELSE N'BLOCKER' END AS UsersTable,
    CASE WHEN COL_LENGTH(N'dbo.Users', N'PasswordHashNew') IS NOT NULL THEN N'PASS' ELSE N'MISSING' END AS PasswordHashNewColumn,
    CASE WHEN COL_LENGTH(N'dbo.Users', N'PasswordSalt') IS NOT NULL THEN N'PASS' ELSE N'MISSING' END AS PasswordSaltColumn,
    CASE WHEN COL_LENGTH(N'dbo.Users', N'AuthenticationRowVersion') IS NOT NULL THEN N'PASS' ELSE N'MISSING' END AS AuthenticationRowVersionColumn,
    CASE WHEN COL_LENGTH(N'dbo.Users', N'FailedLoginAttempts') IS NOT NULL THEN N'PASS' ELSE N'MISSING' END AS FailedLoginAttemptsColumn,
    CASE WHEN COL_LENGTH(N'dbo.Users', N'IsLocked') IS NOT NULL THEN N'PASS' ELSE N'MISSING' END AS IsLockedColumn,
    CASE WHEN COL_LENGTH(N'dbo.Users', N'LockedUntil') IS NOT NULL THEN N'PASS' ELSE N'MISSING' END AS LockedUntilColumn;

IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Users', N'UserID') IS NOT NULL
   AND COL_LENGTH(N'dbo.Users', N'Username') IS NOT NULL
   AND COL_LENGTH(N'dbo.Users', N'FullName') IS NOT NULL
   AND COL_LENGTH(N'dbo.Users', N'Role') IS NOT NULL
   AND COL_LENGTH(N'dbo.Users', N'IsActive') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Users',N'IsLocked') IS NOT NULL
       AND COL_LENGTH(N'dbo.Users',N'LockedUntil') IS NOT NULL
    BEGIN
        EXEC sys.sp_executesql N'
            SELECT UserID, Username, FullName, Role, IsActive,
                   ISNULL(IsLocked,0) AS IsLocked,
                   TRY_CONVERT(datetime2(0),LockedUntil) AS LockedUntil
            FROM dbo.Users
            ORDER BY Username;';
    END
    ELSE
    BEGIN
        EXEC sys.sp_executesql N'
            SELECT UserID, Username, FullName, Role, IsActive,
                   CAST(0 AS bit) AS IsLocked,
                   CAST(NULL AS datetime2(0)) AS LockedUntil
            FROM dbo.Users
            ORDER BY Username;';
    END;
END;
