-- Read-only credential metadata for the restricted UAT desktop. No verifier is exposed.
-- Existing users, credentials, signatures and historical records are never updated.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() NOT LIKE N'EQ001[_]UAT[_]%'
   AND DB_NAME() NOT LIKE N'PharmaLIMS[_]EQ001[_]UAT[_]%'
    THROW 56770, 'Credential readiness deployment requires isolated EQ001 UAT.', 1;
IF OBJECT_ID(N'dbo.Users',N'U') IS NULL
   OR COL_LENGTH(N'dbo.Users',N'PasswordHashNew') IS NULL
   OR COL_LENGTH(N'dbo.Users',N'PasswordSalt') IS NULL
    THROW 56770, 'Reviewed authentication schema is missing.', 1;

DECLARE @Definition nvarchar(max) = N'CREATE VIEW dbo.EQ001_CredentialReadiness AS SELECT Username,IsActive,CONVERT(bit,CASE WHEN NULLIF(LTRIM(RTRIM(PasswordHashNew)),N'''') IS NOT NULL AND NULLIF(LTRIM(RTRIM(PasswordSalt)),N'''') IS NOT NULL THEN 1 ELSE 0 END) AS CredentialInitialized FROM dbo.Users;';

BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.EQ001_CredentialReadiness') IS NULL
        EXEC(@Definition);
    ELSE IF OBJECT_ID(N'dbo.EQ001_CredentialReadiness',N'V') IS NULL
         OR OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_CredentialReadiness')) IS NULL
         OR CONVERT(varbinary(max),OBJECT_DEFINITION(OBJECT_ID(N'dbo.EQ001_CredentialReadiness')))
            <> CONVERT(varbinary(max),@Definition)
        THROW 56771, 'Existing credential readiness object differs; no definition was replaced.', 1;

    IF DATABASE_PRINCIPAL_ID(N'PharmaLIMS_EquipmentApp') IS NOT NULL
        GRANT SELECT ON dbo.EQ001_CredentialReadiness TO PharmaLIMS_EquipmentApp;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
PRINT N'PASS: UAT credential readiness metadata staged; credential-column DENYs remain unchanged.';
