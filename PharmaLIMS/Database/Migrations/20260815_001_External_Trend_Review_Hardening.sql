SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
  PharmaLIMS controlled migration 20260815_001
  Purpose:
    - Preserve microbiology result qualifiers (<, <=, >, >=) instead of silently
      converting censored results into exact numeric observations.
    - Make External EM trend review snapshots signed, reproducible and append-only.
    - Store the true number/list of selected periods instead of duplicating dates for
      one- or two-period reviews.
    - Add lookup support for cross-batch SourceRecordID duplicate detection.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NULL
        THROW 51110, 'Apply controlled external-trend migrations before 20260815_001.', 1;

    IF COL_LENGTH(N'dbo.ExternalTrendImportRows', N'ResultQualifier') IS NULL
    BEGIN
        ALTER TABLE dbo.ExternalTrendImportRows
            ADD ResultQualifier NVARCHAR(2) NULL;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'CK_ExternalTrendImportRows_ResultQualifier'
    )
    BEGIN
        ALTER TABLE dbo.ExternalTrendImportRows WITH CHECK
            ADD CONSTRAINT CK_ExternalTrendImportRows_ResultQualifier
            CHECK (ResultQualifier IS NULL OR ResultQualifier IN (N'<', N'<=', N'>', N'>='));
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'IX_ExternalTrendImportRows_SourceRecordGlobal'
    )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportRows_SourceRecordGlobal
            ON dbo.ExternalTrendImportRows(SourceRecordID, ImportBatchID)
            INCLUDE (RecordDateTime, EntityCode, MethodName, ParameterName, ResultValue, ResultQualifier, UnitName)
            WHERE SourceRecordID IS NOT NULL;
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'CK_ExternalTrendImportRows_NonNegative'
    )
    BEGIN
        ALTER TABLE dbo.ExternalTrendImportRows WITH CHECK
            ADD CONSTRAINT CK_ExternalTrendImportRows_NonNegative
            CHECK
            (
                ResultValue >= 0
                AND (AlertLimit IS NULL OR AlertLimit >= 0)
                AND (ActionLimit IS NULL OR ActionLimit >= 0)
                AND (AlertLimit IS NULL OR ActionLimit IS NULL OR ActionLimit >= AlertLimit)
            );
    END;

    IF OBJECT_ID(N'dbo.TR_ExternalTrendImportBatches_PreventDuplicateApprovedRows', N'TR') IS NULL
    BEGIN
        EXEC(N'
CREATE TRIGGER dbo.TR_ExternalTrendImportBatches_PreventDuplicateApprovedRows
ON dbo.ExternalTrendImportBatches
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT UPDATE(Status) RETURN;

    IF EXISTS
    (
        SELECT 1
        FROM inserted approvedBatch
        INNER JOIN dbo.ExternalTrendImportRows candidate
            ON candidate.ImportBatchID = approvedBatch.ImportBatchID
        INNER JOIN dbo.ExternalTrendImportRows existingRow
            ON existingRow.SourceRecordID = candidate.SourceRecordID
           AND existingRow.ImportBatchID <> candidate.ImportBatchID
        INNER JOIN dbo.ExternalTrendImportBatches existingBatch
            ON existingBatch.ImportBatchID = existingRow.ImportBatchID
           AND existingBatch.Status = N''Approved''
           AND existingBatch.ModuleName = approvedBatch.ModuleName
           AND existingBatch.SourceSystem = approvedBatch.SourceSystem
        WHERE approvedBatch.Status = N''Approved''
          AND candidate.SourceRecordID IS NOT NULL
    )
    BEGIN
        THROW 51113, ''An approved external-trend batch already contains the same SourceRecordID. Duplicate source observations cannot be approved.'', 1;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM inserted approvedBatch
        INNER JOIN dbo.ExternalTrendImportRows candidate
            ON candidate.ImportBatchID = approvedBatch.ImportBatchID
           AND candidate.SourceRecordID IS NULL
        INNER JOIN dbo.ExternalTrendImportRows existingRow
            ON existingRow.SourceRecordID IS NULL
           AND existingRow.ImportBatchID <> candidate.ImportBatchID
           AND existingRow.RecordDateTime = candidate.RecordDateTime
           AND existingRow.EntityCode = candidate.EntityCode
           AND ISNULL(existingRow.MethodName, N'''') = ISNULL(candidate.MethodName, N'''')
           AND existingRow.ParameterName = candidate.ParameterName
           AND existingRow.ResultValue = candidate.ResultValue
           AND ISNULL(existingRow.ResultQualifier, N'''') = ISNULL(candidate.ResultQualifier, N'''')
           AND existingRow.UnitName = candidate.UnitName
        INNER JOIN dbo.ExternalTrendImportBatches existingBatch
            ON existingBatch.ImportBatchID = existingRow.ImportBatchID
           AND existingBatch.Status = N''Approved''
           AND existingBatch.ModuleName = approvedBatch.ModuleName
           AND existingBatch.SourceSystem = approvedBatch.SourceSystem
        WHERE approvedBatch.Status = N''Approved''
    )
    BEGIN
        THROW 51114, ''An approved external-trend batch already contains the same source observation fingerprint. Add a controlled SourceRecordID or correct the duplicate before approval.'', 1;
    END;
END;');
    END;

    IF OBJECT_ID(N'dbo.EMTrendReviewSnapshots', N'U') IS NULL
        THROW 51111, 'Apply migration 20260811_002 before 20260815_001.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
          AND name = N'CK_EMTrendReviewSnapshots_Dates'
    )
    BEGIN
        ALTER TABLE dbo.EMTrendReviewSnapshots
            DROP CONSTRAINT CK_EMTrendReviewSnapshots_Dates;
    END;

    ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period2Start DATE NULL;
    ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period2End DATE NULL;
    ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period3Start DATE NULL;
    ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period3End DATE NULL;

    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'MethodName') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD MethodName NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'UnitName') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD UnitName NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'PeriodCount') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD PeriodCount INT NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'PeriodDefinitionJson') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD PeriodDefinitionJson NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceAnchorBatchID') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceAnchorBatchID INT NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceAnchorImportNumber') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceAnchorImportNumber NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceCutoffAt') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceCutoffAt DATETIMEOFFSET(7) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceBatchManifestJson') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceBatchManifestJson NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceBatchManifestSha256') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceBatchManifestSha256 CHAR(64) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SnapshotHashSha256') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD SnapshotHashSha256 CHAR(64) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'ReviewerRole') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD ReviewerRole NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SignatureMeaning') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD SignatureMeaning NVARCHAR(300) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SignatureReason') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD SignatureReason NVARCHAR(1000) NULL;
    IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SignedAt') IS NULL
        ALTER TABLE dbo.EMTrendReviewSnapshots ADD SignedAt DATETIMEOFFSET(7) NULL;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
          AND name = N'FK_EMTrendReviewSnapshots_SourceAnchorBatch'
    )
    BEGIN
        ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
            ADD CONSTRAINT FK_EMTrendReviewSnapshots_SourceAnchorBatch
            FOREIGN KEY (SourceAnchorBatchID)
            REFERENCES dbo.ExternalTrendImportBatches(ImportBatchID);
    END;

    ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
        ADD CONSTRAINT CK_EMTrendReviewSnapshots_Dates
        CHECK
        (
            Period1End >= Period1Start
            AND ((Period2Start IS NULL AND Period2End IS NULL) OR (Period2Start IS NOT NULL AND Period2End >= Period2Start))
            AND ((Period3Start IS NULL AND Period3End IS NULL) OR (Period3Start IS NOT NULL AND Period3End >= Period3Start))
            AND (Period3Start IS NULL OR Period2Start IS NOT NULL)
        );

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
          AND name = N'CK_EMTrendReviewSnapshots_ControlledV2'
    )
    BEGIN
        ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
            ADD CONSTRAINT CK_EMTrendReviewSnapshots_ControlledV2
            CHECK
            (
                SnapshotHashSha256 IS NULL
                OR
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
            );
    END;

    IF OBJECT_ID(N'dbo.TR_EMTrendReviewSnapshots_Immutable', N'TR') IS NULL
    BEGIN
        EXEC(N'
CREATE TRIGGER dbo.TR_EMTrendReviewSnapshots_Immutable
ON dbo.EMTrendReviewSnapshots
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51112, ''External EM trend review snapshots are append-only. Create a new signed snapshot for a revised review.'', 1;
END;');
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
          AND name = N'IX_EMTrendReviewSnapshots_SourceAnchor'
    )
    BEGIN
        CREATE INDEX IX_EMTrendReviewSnapshots_SourceAnchor
            ON dbo.EMTrendReviewSnapshots(SourceAnchorBatchID, CreatedAt DESC)
            INCLUDE (AreaCode, ParameterName, SnapshotHashSha256, CreatedBy, SignedAt);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260815_001' AS MigrationVersion;
