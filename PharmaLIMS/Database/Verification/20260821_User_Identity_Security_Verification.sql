SET NOCOUNT ON;

SELECT
    CASE WHEN OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL THEN 'PASS' ELSE 'FAIL' END AS UsersTable,
    CASE WHEN NOT EXISTS
    (
        SELECT 1
        FROM dbo.Users
        WHERE Username IS NULL
           OR LEN(LTRIM(RTRIM(Username))) = 0
           OR Username <> LTRIM(RTRIM(Username))
    ) THEN 'PASS' ELSE 'FAIL' END AS ControlledUsernameFormat,
    CASE WHEN NOT EXISTS
    (
        SELECT LTRIM(RTRIM(Username))
        FROM dbo.Users
        GROUP BY LTRIM(RTRIM(Username))
        HAVING COUNT(*) > 1
    ) THEN 'PASS' ELSE 'FAIL' END AS UniqueUserIdentity,
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Users')
          AND name = N'UX_Users_Username_20260821'
          AND is_unique = 1
    ) THEN 'PASS' ELSE 'FAIL' END AS UniqueUsernameIndex;
