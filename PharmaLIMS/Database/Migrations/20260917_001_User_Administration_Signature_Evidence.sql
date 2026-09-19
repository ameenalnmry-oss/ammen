SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS 2026.9.17.289
    User administration electronic-signature evidence.

    Purpose:
    - Persist the electronic signature that authorizes each user-administration write.
    - Keep the signature evidence in the same SQL transaction as the account mutation and AuditTrail record.
    - Preserve target identity, action, reason, meaning, signer, signer role and server-side UTC timestamp.
    - Protect the evidence as append-only.

    This migration does not change any existing user role, permission, activation state,
    password, lock state, Water/EM/PRM data, or historical signature record.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 55210, 'User administration signature evidence requires dbo.Users.', 1;

    IF OBJECT_ID(N'dbo.UserAdministrationSignatures', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.UserAdministrationSignatures
        (
            SignatureID BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_UserAdministrationSignatures PRIMARY KEY,
            TargetUserID INT NOT NULL,
            TargetUsername NVARCHAR(100) NOT NULL,
            ActionType NVARCHAR(100) NOT NULL,
            ActionReason NVARCHAR(1000) NOT NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            SignedBy NVARCHAR(100) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            SignedAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_UserAdministrationSignatures_SignedAt DEFAULT SYSUTCDATETIME(),
            SourceWorkstation NVARCHAR(200) NULL,
            SourceApplication NVARCHAR(100) NOT NULL
                CONSTRAINT DF_UserAdministrationSignatures_SourceApplication DEFAULT N'PharmaLIMS',
            CONSTRAINT CK_UserAdministrationSignatures_ActionReason_Specific
                CHECK (LEN(LTRIM(RTRIM(ActionReason))) >= 10
                       AND UPPER(LTRIM(RTRIM(ActionReason))) <> N'USER ACCOUNT ADMINISTRATION'),
            CONSTRAINT CK_UserAdministrationSignatures_Meaning_NotBlank
                CHECK (LEN(LTRIM(RTRIM(MeaningOfSignature))) >= 3),
            CONSTRAINT CK_UserAdministrationSignatures_SignedBy_NotBlank
                CHECK (LEN(LTRIM(RTRIM(SignedBy))) >= 1),
            CONSTRAINT FK_UserAdministrationSignatures_TargetUser
                FOREIGN KEY (TargetUserID) REFERENCES dbo.Users(UserID)
        );
    END;

    IF COL_LENGTH(N'dbo.UserAdministrationSignatures', N'SignatureID') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'TargetUserID') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'TargetUsername') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'ActionType') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'ActionReason') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'MeaningOfSignature') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'SignedBy') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'UserRole') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'SignedAt') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'SourceWorkstation') IS NULL
       OR COL_LENGTH(N'dbo.UserAdministrationSignatures', N'SourceApplication') IS NULL
        THROW 55211, 'Existing dbo.UserAdministrationSignatures has an incompatible schema and requires controlled reconciliation.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.UserAdministrationSignatures')
          AND name = N'IX_UserAdministrationSignatures_Target_SignedAt'
    )
    BEGIN
        CREATE INDEX IX_UserAdministrationSignatures_Target_SignedAt
            ON dbo.UserAdministrationSignatures(TargetUserID, SignedAt DESC, SignatureID DESC)
            INCLUDE (ActionType, SignedBy, TargetUsername);
    END;

    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_UserAdministrationSignatures_AppendOnly
ON dbo.UserAdministrationSignatures
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- user administration signature evidence is append-only and cannot be updated or deleted
    THROW 55212, ''User administration signature evidence is append-only and cannot be updated or deleted.'', 1;
END;';

    ENABLE TRIGGER dbo.TRG_UserAdministrationSignatures_AppendOnly ON dbo.UserAdministrationSignatures;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
