SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       v232 Water controlled-profile governance.
       This migration is additive. It does not seed or guess PW/PTW tests, limits,
       units, or compendial requirements. Site-approved master data remains a
       human-controlled quality decision.
    */

    IF OBJECT_ID(N'dbo.WaterTestProfiles', N'U') IS NULL
        THROW 54600, 'Required table dbo.WaterTestProfiles is missing before 20260907_000.', 1;

    IF OBJECT_ID(N'dbo.WaterTestProfileTests', N'U') IS NULL
        THROW 54601, 'Required table dbo.WaterTestProfileTests is missing before 20260907_000.', 1;

    IF OBJECT_ID(N'dbo.WaterSpecifications', N'U') IS NULL
        THROW 54602, 'Required table dbo.WaterSpecifications is missing before 20260907_000.', 1;

    IF COL_LENGTH(N'dbo.WaterTestProfiles', N'ControlledReference') IS NULL
    BEGIN
        ALTER TABLE dbo.WaterTestProfiles
            ADD ControlledReference NVARCHAR(300) NULL;
    END;

    IF COL_LENGTH(N'dbo.WaterSpecifications', N'ProfileID') IS NULL
    BEGIN
        ALTER TABLE dbo.WaterSpecifications
            ADD ProfileID INT NULL;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.columns c
        INNER JOIN sys.types t ON t.user_type_id=c.user_type_id
        WHERE c.object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
          AND c.name=N'ControlledReference'
          AND t.name=N'nvarchar'
          AND (c.max_length=-1 OR c.max_length>=600)
          AND c.is_nullable=1
    )
        THROW 54605, 'dbo.WaterTestProfiles.ControlledReference must be nullable NVARCHAR(300) or wider.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.columns c
        INNER JOIN sys.types t ON t.user_type_id=c.user_type_id
        WHERE c.object_id=OBJECT_ID(N'dbo.WaterSpecifications')
          AND c.name=N'ProfileID'
          AND t.name=N'int'
          AND c.is_nullable=1
    )
        THROW 54606, 'dbo.WaterSpecifications.ProfileID must be nullable INT.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'dbo.WaterSpecifications')
          AND referenced_object_id = OBJECT_ID(N'dbo.WaterTestProfiles')
          AND name = N'FK_WaterSpecifications_Profile_20260907_000'
    )
    BEGIN
        ALTER TABLE dbo.WaterSpecifications WITH CHECK
            ADD CONSTRAINT FK_WaterSpecifications_Profile_20260907_000
            FOREIGN KEY(ProfileID) REFERENCES dbo.WaterTestProfiles(ProfileID);

        ALTER TABLE dbo.WaterSpecifications
            CHECK CONSTRAINT FK_WaterSpecifications_Profile_20260907_000;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id=OBJECT_ID(N'dbo.WaterSpecifications')
          AND referenced_object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
          AND name=N'FK_WaterSpecifications_Profile_20260907_000'
          AND (is_disabled=1 OR is_not_trusted=1)
    )
        ALTER TABLE dbo.WaterSpecifications WITH CHECK CHECK CONSTRAINT FK_WaterSpecifications_Profile_20260907_000;

    IF OBJECT_ID(N'dbo.WaterTestProfileSignatures', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.WaterTestProfileSignatures
        (
            SignatureID BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_WaterTestProfileSignatures PRIMARY KEY,
            ProfileID INT NOT NULL,
            ActionType NVARCHAR(60) NOT NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            ActionReason NVARCHAR(1000) NOT NULL,
            SignedBy NVARCHAR(100) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            SignedAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_WaterTestProfileSignatures_SignedAt DEFAULT SYSUTCDATETIME(),
            SourceWorkstation NVARCHAR(200) NULL,
            CONSTRAINT FK_WaterTestProfileSignatures_Profile
                FOREIGN KEY(ProfileID) REFERENCES dbo.WaterTestProfiles(ProfileID),
            CONSTRAINT CK_WaterTestProfileSignatures_Action
                CHECK(ActionType IN (N'Review',N'Approval'))
        );
    END;

    IF EXISTS
    (
        SELECT required.ColumnName
        FROM (VALUES
            (N'SignatureID',N'bigint',CONVERT(smallint,8),CONVERT(bit,0)),
            (N'ProfileID',N'int',CONVERT(smallint,4),CONVERT(bit,0)),
            (N'ActionType',N'nvarchar',CONVERT(smallint,120),CONVERT(bit,0)),
            (N'MeaningOfSignature',N'nvarchar',CONVERT(smallint,510),CONVERT(bit,0)),
            (N'ActionReason',N'nvarchar',CONVERT(smallint,2000),CONVERT(bit,0)),
            (N'SignedBy',N'nvarchar',CONVERT(smallint,200),CONVERT(bit,0)),
            (N'UserRole',N'nvarchar',CONVERT(smallint,200),CONVERT(bit,1)),
            (N'SignedAt',N'datetime2',CONVERT(smallint,8),CONVERT(bit,0)),
            (N'SourceWorkstation',N'nvarchar',CONVERT(smallint,400),CONVERT(bit,1))
        ) required(ColumnName,TypeName,MinimumLength,NullableValue)
        LEFT JOIN sys.columns c
          ON c.object_id=OBJECT_ID(N'dbo.WaterTestProfileSignatures')
         AND c.name=required.ColumnName
        LEFT JOIN sys.types t ON t.user_type_id=c.user_type_id
        WHERE c.column_id IS NULL
           OR t.name<>required.TypeName
           OR (required.TypeName=N'nvarchar' AND c.max_length<>-1 AND c.max_length<required.MinimumLength)
           OR c.is_nullable<>required.NullableValue
    )
        THROW 54607, 'dbo.WaterTestProfileSignatures schema contract is incomplete or incompatible.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfileSignatures')
          AND referenced_object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
    )
    BEGIN
        ALTER TABLE dbo.WaterTestProfileSignatures WITH CHECK
            ADD CONSTRAINT FK_WaterTestProfileSignatures_Profile_20260907_000
            FOREIGN KEY(ProfileID) REFERENCES dbo.WaterTestProfiles(ProfileID);
    END;

    IF EXISTS
    (
        SELECT UPPER(LTRIM(RTRIM(ProfileCode)))
        FROM dbo.WaterTestProfiles
        WHERE ISNULL(IsActive,0)=1
        GROUP BY UPPER(LTRIM(RTRIM(ProfileCode)))
        HAVING COUNT(*) > 1
    )
        THROW 54603, 'Multiple active Water Test Profile versions exist for the same ProfileCode. Resolve under controlled change before 20260907_000 can enforce the active-version contract.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.WaterTestProfiles')
          AND name = N'UX_WaterTestProfiles_OneActiveCode_20260907_000'
    )
    BEGIN
        CREATE UNIQUE INDEX UX_WaterTestProfiles_OneActiveCode_20260907_000
            ON dbo.WaterTestProfiles(ProfileCode)
            WHERE IsActive = 1;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.WaterSpecifications')
          AND name = N'IX_WaterSpecifications_Profile_Test_Point_20260907_000'
    )
    BEGIN
        CREATE INDEX IX_WaterSpecifications_Profile_Test_Point_20260907_000
            ON dbo.WaterSpecifications(ProfileID, TestID, PointCode, IsActive);
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.WaterTestProfileSignatures')
          AND name = N'IX_WaterTestProfileSignatures_Profile_Action_20260907_000'
    )
    BEGIN
        CREATE INDEX IX_WaterTestProfileSignatures_Profile_Action_20260907_000
            ON dbo.WaterTestProfileSignatures(ProfileID, ActionType, SignedAt DESC);
    END;

    /*
       Signature evidence is append-only. The table may receive INSERTs only.
       Create/alter via dynamic SQL so the trigger definition is isolated in its
       own SQL batch while this migration remains transactional.
    */
    EXEC(N'
CREATE OR ALTER TRIGGER dbo.TRG_WaterTestProfileSignatures_AppendOnly
ON dbo.WaterTestProfileSignatures
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 54604, ''Water Test Profile signature evidence is append-only and cannot be updated or deleted.'', 1;
END;');

    ENABLE TRIGGER dbo.TRG_WaterTestProfileSignatures_AppendOnly
        ON dbo.WaterTestProfileSignatures;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
