-- PHARMALIMS_STEP: Session setup and baseline prerequisites
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 120000;

/*
  PharmaLIMS controlled current-state reconciliation 20260826_004

  This migration supersedes the live-DDL execution of:
    - 20260815_001 External Trend Review Hardening
    - 20260816_001 External Trend Query Performance V2
    - 20260823_005 External Trend Snapshot Schema Reconciliation

  It is intentionally RESUMABLE and therefore has no encompassing transaction.
  Every guarded DDL statement is idempotent and auto-commits. The application
  records 20260826_004 in LIMS_SchemaVersions only after this script reaches its
  final post-condition verification successfully. If a schema lock is temporarily
  unavailable, already-completed additive steps remain safe and the script can be
  restarted without rewriting imported observations or signed review snapshots.
*/

IF OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') IS NULL
    THROW 53700, 'External Trend import batches are missing. Apply the controlled External Trend baseline first.', 1;
IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NULL
    THROW 53701, 'External Trend import rows are missing. Apply the controlled External Trend baseline first.', 1;
IF OBJECT_ID(N'dbo.EMTrendReviewSnapshots', N'U') IS NULL
    THROW 53702, 'External EM trend review snapshots are missing. Apply the controlled review baseline first.', 1;
IF OBJECT_ID(N'dbo.EMTrendAreaProfiles', N'U') IS NULL
    THROW 53703, 'External EM trend area profiles are missing. Apply the controlled review baseline first.', 1;

-- PHARMALIMS_STEP: Result qualifier schema and validation
/* Result qualifier preservation. */
IF COL_LENGTH(N'dbo.ExternalTrendImportRows', N'ResultQualifier') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.ExternalTrendImportRows ADD ResultQualifier NVARCHAR(2) NULL;';

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
      AND name = N'ResultQualifier'
      AND (system_type_id <> TYPE_ID(N'nvarchar') OR max_length <> 4)
)
    THROW 53704, 'External Trend ResultQualifier has an incompatible type and requires controlled reconciliation.', 1;

EXEC sys.sp_executesql N'
IF EXISTS
(
    SELECT 1
    FROM dbo.ExternalTrendImportRows
    WHERE ResultQualifier IS NOT NULL
      AND ResultQualifier NOT IN (N''<'', N''<='', N''>'', N''>='')
)
    THROW 53705, ''External Trend contains an invalid result qualifier. Correct the controlled source record before schema hardening.'', 1;';

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
      AND name = N'CK_ExternalTrendImportRows_ResultQualifier'
)
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.ExternalTrendImportRows WITH CHECK
        ADD CONSTRAINT CK_ExternalTrendImportRows_ResultQualifier
        CHECK (ResultQualifier IS NULL OR ResultQualifier IN (N''<'', N''<='', N''>'', N''>=''));';
ELSE
    ALTER TABLE dbo.ExternalTrendImportRows WITH CHECK CHECK CONSTRAINT CK_ExternalTrendImportRows_ResultQualifier;

-- PHARMALIMS_STEP: Result and alert/action value integrity
EXEC sys.sp_executesql N'
IF EXISTS
(
    SELECT 1
    FROM dbo.ExternalTrendImportRows
    WHERE ResultValue < 0
       OR (AlertLimit IS NOT NULL AND AlertLimit < 0)
       OR (ActionLimit IS NOT NULL AND ActionLimit < 0)
       OR (AlertLimit IS NOT NULL AND ActionLimit IS NOT NULL AND ActionLimit < AlertLimit)
)
    THROW 53706, ''External Trend contains negative or reversed alert/action values. Reconcile the controlled source data before schema hardening.'', 1;';

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
      AND name = N'CK_ExternalTrendImportRows_NonNegative'
)
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.ExternalTrendImportRows WITH CHECK
        ADD CONSTRAINT CK_ExternalTrendImportRows_NonNegative
        CHECK
        (
            ResultValue >= 0
            AND (AlertLimit IS NULL OR AlertLimit >= 0)
            AND (ActionLimit IS NULL OR ActionLimit >= 0)
            AND (AlertLimit IS NULL OR ActionLimit IS NULL OR ActionLimit >= AlertLimit)
        );';
ELSE
    ALTER TABLE dbo.ExternalTrendImportRows WITH CHECK CHECK CONSTRAINT CK_ExternalTrendImportRows_NonNegative;

-- PHARMALIMS_STEP: Source-record duplicate lookup index
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
      AND name = N'IX_ExternalTrendImportRows_SourceRecordGlobal'
)
    EXEC sys.sp_executesql N'
        CREATE INDEX IX_ExternalTrendImportRows_SourceRecordGlobal
        ON dbo.ExternalTrendImportRows(SourceRecordID, ImportBatchID)
        INCLUDE (RecordDateTime, EntityCode, MethodName, ParameterName, ResultValue, ResultQualifier, UnitName)
        WHERE SourceRecordID IS NOT NULL;';

-- PHARMALIMS_STEP: Duplicate-approved-batch protection trigger
EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TR_ExternalTrendImportBatches_PreventDuplicateApprovedRows
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
        THROW 51113, ''An approved external-trend batch already contains the same SourceRecordID. Duplicate source observations cannot be approved.'', 1;

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
        THROW 51114, ''An approved external-trend batch already contains the same source observation fingerprint. Add a controlled SourceRecordID or correct the duplicate before approval.'', 1;
END;';
ENABLE TRIGGER dbo.TR_ExternalTrendImportBatches_PreventDuplicateApprovedRows ON dbo.ExternalTrendImportBatches;

-- PHARMALIMS_STEP: External EM review snapshot columns
/* Snapshot columns. Every ADD is dynamic so SQL Server never compiles a later
   reference before the guarded column has been materialized. */
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name = N'Period2Start' AND is_nullable = 0)
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period2Start DATE NULL;';
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name = N'Period2End' AND is_nullable = 0)
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period2End DATE NULL;';
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name = N'Period3Start' AND is_nullable = 0)
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period3Start DATE NULL;';
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name = N'Period3End' AND is_nullable = 0)
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ALTER COLUMN Period3End DATE NULL;';

IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'MethodName') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD MethodName NVARCHAR(200) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'UnitName') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD UnitName NVARCHAR(100) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'PeriodCount') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD PeriodCount INT NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'PeriodDefinitionJson') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD PeriodDefinitionJson NVARCHAR(MAX) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceAnchorBatchID') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceAnchorBatchID INT NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceAnchorImportNumber') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceAnchorImportNumber NVARCHAR(50) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceCutoffAt') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceCutoffAt DATETIMEOFFSET(7) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceBatchManifestJson') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceBatchManifestJson NVARCHAR(MAX) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SourceBatchManifestSha256') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SourceBatchManifestSha256 CHAR(64) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SnapshotHashSha256') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SnapshotHashSha256 CHAR(64) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'ReviewerRole') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD ReviewerRole NVARCHAR(100) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SignatureMeaning') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SignatureMeaning NVARCHAR(300) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SignatureReason') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SignatureReason NVARCHAR(1000) NULL;';
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots', N'SignedAt') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.EMTrendReviewSnapshots ADD SignedAt DATETIMEOFFSET(7) NULL;';

-- PHARMALIMS_STEP: External EM review period integrity
EXEC sys.sp_executesql N'
IF EXISTS
(
    SELECT 1
    FROM dbo.EMTrendReviewSnapshots
    WHERE Period1Start IS NULL
       OR Period1End IS NULL
       OR Period1End < Period1Start
       OR (Period2Start IS NULL AND Period2End IS NOT NULL)
       OR (Period2Start IS NOT NULL AND (Period2End IS NULL OR Period2End < Period2Start))
       OR (Period3Start IS NULL AND Period3End IS NOT NULL)
       OR (Period3Start IS NOT NULL AND (Period3End IS NULL OR Period3End < Period3Start))
       OR (Period3Start IS NOT NULL AND Period2Start IS NULL)
)
    THROW 53707, ''Existing External EM review periods are internally inconsistent. Reconcile the affected review record before enabling the current snapshot constraint.'', 1;';

/* Add a V2 dates constraint without dropping the historical constraint. This avoids
   an unprotected interval if maintenance is interrupted between DDL statements. */
IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
      AND name = N'CK_EMTrendReviewSnapshots_DatesV2'
)
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
        ADD CONSTRAINT CK_EMTrendReviewSnapshots_DatesV2 CHECK
        (
            Period1Start IS NOT NULL
            AND Period1End IS NOT NULL
            AND Period1End >= Period1Start
            AND ((Period2Start IS NULL AND Period2End IS NULL) OR (Period2Start IS NOT NULL AND Period2End IS NOT NULL AND Period2End >= Period2Start))
            AND ((Period3Start IS NULL AND Period3End IS NULL) OR (Period3Start IS NOT NULL AND Period3End IS NOT NULL AND Period3End >= Period3Start))
            AND (Period3Start IS NULL OR Period2Start IS NOT NULL)
        );';
ELSE
    ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK CHECK CONSTRAINT CK_EMTrendReviewSnapshots_DatesV2;

-- PHARMALIMS_STEP: External EM source-anchor foreign key
IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
      AND name = N'FK_EMTrendReviewSnapshots_SourceAnchorBatch'
)
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
        ADD CONSTRAINT FK_EMTrendReviewSnapshots_SourceAnchorBatch
        FOREIGN KEY (SourceAnchorBatchID)
        REFERENCES dbo.ExternalTrendImportBatches(ImportBatchID);';
ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK CHECK CONSTRAINT FK_EMTrendReviewSnapshots_SourceAnchorBatch;

-- PHARMALIMS_STEP: Controlled signed snapshot constraint
IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
      AND name = N'CK_EMTrendReviewSnapshots_ControlledV2'
)
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK
        ADD CONSTRAINT CK_EMTrendReviewSnapshots_ControlledV2 CHECK
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
        );';
ALTER TABLE dbo.EMTrendReviewSnapshots WITH CHECK CHECK CONSTRAINT CK_EMTrendReviewSnapshots_ControlledV2;

-- PHARMALIMS_STEP: Immutable external EM snapshot trigger
EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TR_EMTrendReviewSnapshots_Immutable
ON dbo.EMTrendReviewSnapshots
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51112, ''External EM trend review snapshots are append-only. Create a new signed snapshot for a revised review.'', 1;
END;';
ENABLE TRIGGER dbo.TR_EMTrendReviewSnapshots_Immutable ON dbo.EMTrendReviewSnapshots;

-- PHARMALIMS_STEP: External EM snapshot source-anchor index
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots')
      AND name = N'IX_EMTrendReviewSnapshots_SourceAnchor'
)
    EXEC sys.sp_executesql N'
        CREATE INDEX IX_EMTrendReviewSnapshots_SourceAnchor
        ON dbo.EMTrendReviewSnapshots(SourceAnchorBatchID, CreatedAt DESC)
        INCLUDE (AreaCode, ParameterName, SnapshotHashSha256, CreatedBy, SignedAt);';

-- PHARMALIMS_STEP: Current External Trend read index readiness
/*
   Performance-only read indexes are deliberately not a schema-integrity gate.
   index creation can require a long SCH-M lock on a live Development database and
   previously blocked controlled migration completion. Functional correctness,
   auditability, source anchoring, constraints, and immutable snapshots do not
   depend on these indexes. Missing read indexes are therefore reported only and
   may be created later during a dedicated performance-maintenance window.
*/
DECLARE @MissingReadIndexes INT = 0;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportBatches')
      AND name = N'IX_ExternalTrendImportBatches_ReviewCutoff'
) SET @MissingReadIndexes += 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
      AND name = N'IX_ExternalTrendImportRows_AreaCatalog'
) SET @MissingReadIndexes += 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
      AND name = N'IX_ExternalTrendImportRows_MethodParameterLookup'
) SET @MissingReadIndexes += 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
      AND name = N'IX_ExternalTrendImportRows_SeriesLookupV2'
) SET @MissingReadIndexes += 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.EMTrendAreaProfiles')
      AND name = N'IX_EMTrendAreaProfiles_EffectiveLookup'
) SET @MissingReadIndexes += 1;

IF @MissingReadIndexes > 0
    PRINT CONCAT(
        N'PharmaLIMS performance advisory: ',
        @MissingReadIndexes,
        N' optional External Trend read index(es) are not present. Schema reconciliation will continue; create them only in a dedicated performance-maintenance window.');

-- PHARMALIMS_STEP: Final current-state verification
/* Final current-state verification. The ledger is written by the application only
   after this SELECT/THROW block returns successfully. */
IF COL_LENGTH(N'dbo.ExternalTrendImportRows', N'ResultQualifier') IS NULL
    THROW 53720, 'External Trend current-state reconciliation did not materialize ResultQualifier.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows') AND name = N'CK_ExternalTrendImportRows_ResultQualifier' AND is_disabled = 0 AND is_not_trusted = 0)
    THROW 53721, 'External Trend result qualifier constraint is not trusted after reconciliation.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows') AND name = N'CK_ExternalTrendImportRows_NonNegative' AND is_disabled = 0 AND is_not_trusted = 0)
    THROW 53722, 'External Trend non-negative result constraint is not trusted after reconciliation.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id = OBJECT_ID(N'dbo.ExternalTrendImportBatches') AND name = N'TR_ExternalTrendImportBatches_PreventDuplicateApprovedRows' AND is_disabled = 0)
    THROW 53723, 'External Trend duplicate approval protection is not enabled after reconciliation.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name = N'CK_EMTrendReviewSnapshots_DatesV2' AND is_disabled = 0 AND is_not_trusted = 0)
    THROW 53724, 'External EM review date constraint is not trusted after reconciliation.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name = N'CK_EMTrendReviewSnapshots_ControlledV2' AND is_disabled = 0 AND is_not_trusted = 0)
    THROW 53725, 'External EM controlled review constraint is not trusted after reconciliation.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name = N'FK_EMTrendReviewSnapshots_SourceAnchorBatch' AND is_disabled = 0 AND is_not_trusted = 0)
    THROW 53726, 'External EM source anchor relationship is not trusted after reconciliation.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id = OBJECT_ID(N'dbo.EMTrendReviewSnapshots') AND name = N'TR_EMTrendReviewSnapshots_Immutable' AND is_disabled = 0)
    THROW 53727, 'External EM immutable review protection is not enabled after reconciliation.', 1;

SELECT N'20260826_004' AS MigrationVersion;
