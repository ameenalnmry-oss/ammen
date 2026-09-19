SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
   Reconciles installations where 20260815_001 was recorded by an older
   deployment but its additive snapshot columns were not materialized.
   No imported observation or approved snapshot data is rewritten.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.EMTrendReviewSnapshots', N'U') IS NULL
        THROW 53600, 'Required table dbo.EMTrendReviewSnapshots is missing.', 1;
    IF OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') IS NULL
        THROW 53601, 'Required table dbo.ExternalTrendImportBatches is missing.', 1;

    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name=N'Period2Start' AND is_nullable=0)
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period2Start DATE NULL;');
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name=N'Period2End' AND is_nullable=0)
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period2End DATE NULL;');
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name=N'Period3Start' AND is_nullable=0)
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period3Start DATE NULL;');
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name=N'Period3End' AND is_nullable=0)
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period3End DATE NULL;');

    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'MethodName') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD MethodName NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'UnitName') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD UnitName NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'PeriodCount') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD PeriodCount INT NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'PeriodDefinitionJson') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD PeriodDefinitionJson NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceAnchorBatchID') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceAnchorBatchID INT NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceAnchorImportNumber') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceAnchorImportNumber NVARCHAR(50) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceCutoffAt') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceCutoffAt DATETIMEOFFSET(7) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceBatchManifestJson') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceBatchManifestJson NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceBatchManifestSha256') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceBatchManifestSha256 CHAR(64) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SnapshotHashSha256') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SnapshotHashSha256 CHAR(64) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'ReviewerRole') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD ReviewerRole NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SignatureMeaning') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SignatureMeaning NVARCHAR(300) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SignatureReason') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SignatureReason NVARCHAR(1000) NULL;');
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SignedAt') IS NULL
        EXEC(N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SignedAt DATETIMEOFFSET(7) NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
          AND name=N'CK_EMTrendReviewSnapshots_Dates'
    )
        ALTER TABLE dbo.EMTrendReviewSnapshots DROP CONSTRAINT CK_EMTrendReviewSnapshots_Dates;

    ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
    ADD CONSTRAINT CK_EMTrendReviewSnapshots_Dates CHECK
    (
        Period1End >= Period1Start
        AND ((Period2Start IS NULL AND Period2End IS NULL) OR (Period2Start IS NOT NULL AND Period2End >= Period2Start))
        AND ((Period3Start IS NULL AND Period3End IS NULL) OR (Period3Start IS NOT NULL AND Period3End >= Period3Start))
        AND (Period3Start IS NULL OR Period2Start IS NOT NULL)
    );

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
          AND name=N'FK_EMTrendReviewSnapshots_SourceAnchorBatch'
    )
        EXEC(N'
            ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
            ADD CONSTRAINT FK_EMTrendReviewSnapshots_SourceAnchorBatch
                FOREIGN KEY(SourceAnchorBatchID) REFERENCES dbo.ExternalTrendImportBatches(ImportBatchID);');

    ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
        CHECK CONSTRAINT FK_EMTrendReviewSnapshots_SourceAnchorBatch;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
          AND name=N'CK_EMTrendReviewSnapshots_ControlledV2'
    )
        EXEC(N'
            ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
            ADD CONSTRAINT CK_EMTrendReviewSnapshots_ControlledV2 CHECK
            (
                SnapshotHashSha256 IS NULL OR
                (
                    PeriodCount BETWEEN 1 AND 3
                    AND PeriodDefinitionJson IS NOT NULL
                    AND MethodName IS NOT NULL
                    AND UnitName IS NOT NULL
                    AND ReviewerComment IS NOT NULL
                    AND LEN(LTRIM(RTRIM(ReviewerComment))) >= 10
                    AND SourceAnchorBatchID IS NOT NULL
                    AND SourceAnchorImportNumber IS NOT NULL
                    AND SourceCutoffAt IS NOT NULL
                    AND SourceBatchManifestJson IS NOT NULL
                    AND SourceBatchManifestSha256 IS NOT NULL
                    AND ReviewerRole IS NOT NULL
                    AND SignatureMeaning IS NOT NULL
                    AND SignatureReason IS NOT NULL
                    AND SignedAt IS NOT NULL
                )
            );');

    ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
        CHECK CONSTRAINT CK_EMTrendReviewSnapshots_ControlledV2;

    EXEC sys.sp_executesql N'
        CREATE OR ALTER TRIGGER dbo.TR_EMTrendReviewSnapshots_Immutable
        ON dbo.EMTrendReviewSnapshots
        AFTER UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 51112, ''External EM trend review snapshots are append-only. Create a new signed snapshot for a revised review.'', 1;
        END;';

    ENABLE TRIGGER dbo.TR_EMTrendReviewSnapshots_Immutable
        ON dbo.EMTrendReviewSnapshots;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
          AND name=N'IX_EMTrendReviewSnapshots_SourceAnchor'
    )
        EXEC(N'
            CREATE INDEX IX_EMTrendReviewSnapshots_SourceAnchor
            ON dbo.EMTrendReviewSnapshots(SourceAnchorBatchID, CreatedAt DESC)
            INCLUDE(AreaCode,ParameterName,SnapshotHashSha256,CreatedBy,SignedAt);');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
