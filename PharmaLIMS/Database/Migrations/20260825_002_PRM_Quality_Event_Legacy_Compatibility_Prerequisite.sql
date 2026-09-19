SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled prerequisite for 20260824_003 on legacy Development databases.

    This migration does not change test results, interpretations, limits,
    signatures, investigation conclusions, or dispositions. It only prepares
    additive PRM source-link columns and preserves evidence when an old event
    contains duplicate affected-result links that cannot satisfy the new
    source-link uniqueness control.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
        THROW 53520, 'Required Quality Events table is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
        THROW 53521, 'Required Quality Event affected-results table is missing.', 1;

    IF COL_LENGTH(N'dbo.QualityEvents', N'QualityEventID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults', N'AffectedResultID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults', N'QualityEventID') IS NULL
        THROW 53522, 'Quality Event identity columns are incomplete.', 1;

    IF COL_LENGTH(N'dbo.QualityEvents', N'SourceModule') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceModule NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'SourceRecordID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceRecordID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'CurrentStatus') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CurrentStatus NVARCHAR(60) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'SampleID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SampleID INT NULL;');

    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SampleTestID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SampleTestID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceModule') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SourceModule NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceResultID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SourceResultID INT NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'SampleID'
          AND system_type_id<>TYPE_ID(N'int')
    )
        THROW 53523, 'The Quality Event sample identity type requires controlled reconciliation.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SampleTestID'
          AND system_type_id<>TYPE_ID(N'int')
    )
        THROW 53524, 'The affected-result sample-test identity type requires controlled reconciliation.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'SampleID'
          AND is_nullable=0
    )
        EXEC(N'ALTER TABLE dbo.QualityEvents ALTER COLUMN SampleID INT NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SampleTestID'
          AND is_nullable=0
    )
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN SampleTestID INT NULL;');

    IF OBJECT_ID(N'dbo.PRM_QualityEventLinkReconciliation', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_QualityEventLinkReconciliation
        (
            ReconciliationID BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_PRM_QualityEventLinkReconciliation PRIMARY KEY,
            AffectedResultID INT NOT NULL,
            QualityEventID INT NOT NULL,
            OriginalSampleTestID INT NULL,
            OriginalSourceModule NVARCHAR(80) NULL,
            OriginalSourceResultID INT NULL,
            ReconciliationReason NVARCHAR(240) NOT NULL,
            RecordedAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_PRM_QualityEventLinkReconciliation_RecordedAt DEFAULT SYSUTCDATETIME(),
            CONSTRAINT UQ_PRM_QualityEventLinkReconciliation_Affected UNIQUE(AffectedResultID)
        );
    END;

    /*
       Populate the PRM source link before 20260824_003 performs its uniqueness
       check. Then reconcile only repeated source identities. Every affected
       row and its result snapshot remain in place; the original link values are
       copied to the reconciliation evidence table before an extra duplicate is
       assigned a distinct legacy-only source identity.
    */
    EXEC sys.sp_executesql N'
UPDATE dbo.QualityEvents
SET SampleID=NULL
WHERE UPPER(LTRIM(RTRIM(ISNULL(SourceModule,N''''))))=N''PRM''
  AND SampleID IS NOT NULL;

UPDATE affected
SET SourceModule=N''PRM'',
    SourceResultID=affected.SampleTestID
FROM dbo.QualityEventAffectedResults affected
INNER JOIN dbo.QualityEvents qualityEvent
    ON qualityEvent.QualityEventID=affected.QualityEventID
WHERE UPPER(LTRIM(RTRIM(ISNULL(qualityEvent.SourceModule,N''''))))=N''PRM''
  AND affected.SourceResultID IS NULL
  AND affected.SampleTestID IS NOT NULL;

;WITH Candidate AS
(
    SELECT
        affected.AffectedResultID,
        affected.QualityEventID,
        affected.SampleTestID,
        affected.SourceModule,
        affected.SourceResultID,
        ROW_NUMBER() OVER
        (
            PARTITION BY affected.QualityEventID,
                         affected.SourceModule,
                         affected.SourceResultID
            ORDER BY affected.AffectedResultID
        ) AS LinkRank
    FROM dbo.QualityEventAffectedResults affected
    WHERE affected.SourceModule IS NOT NULL
      AND affected.SourceResultID IS NOT NULL
), DuplicateEvidence AS
(
    SELECT * FROM Candidate WHERE LinkRank>1
)
INSERT dbo.PRM_QualityEventLinkReconciliation
(
    AffectedResultID,QualityEventID,OriginalSampleTestID,
    OriginalSourceModule,OriginalSourceResultID,ReconciliationReason
)
SELECT
    duplicate.AffectedResultID,duplicate.QualityEventID,duplicate.SampleTestID,
    duplicate.SourceModule,duplicate.SourceResultID,
    N''Duplicate legacy affected-result source link retained outside the authoritative PRM source key.''
FROM DuplicateEvidence duplicate
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.PRM_QualityEventLinkReconciliation evidence
    WHERE evidence.AffectedResultID=duplicate.AffectedResultID
);

;WITH Candidate AS
(
    SELECT
        affected.AffectedResultID,
        affected.SampleTestID,
        affected.SourceResultID,
        ROW_NUMBER() OVER
        (
            PARTITION BY affected.QualityEventID,
                         affected.SourceModule,
                         affected.SourceResultID
            ORDER BY affected.AffectedResultID
        ) AS LinkRank
    FROM dbo.QualityEventAffectedResults affected
    WHERE affected.SourceModule IS NOT NULL
      AND affected.SourceResultID IS NOT NULL
)
UPDATE affected
SET SourceModule=CASE WHEN candidate.LinkRank=1 THEN affected.SourceModule ELSE N''LEGACY-DUPLICATE'' END,
    SourceResultID=CASE
        WHEN candidate.LinkRank=1 THEN affected.SourceResultID
        ELSE -affected.AffectedResultID
    END
FROM dbo.QualityEventAffectedResults affected
INNER JOIN Candidate candidate ON candidate.AffectedResultID=affected.AffectedResultID;
';

    IF EXISTS
    (
        SELECT 1
        FROM dbo.QualityEventAffectedResults
        WHERE SourceModule=N'LEGACY-DUPLICATE'
          AND SourceResultID>=0
    )
        THROW 53525, 'Legacy affected-result duplicate classification is incomplete.', 1;

    /*
       Create the exact unique source-key control expected by 20260824_003.
       Preserved duplicate evidence now has its own non-authoritative legacy key.
    */
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'UX_QualityEventAffectedResults_Source_20260824_003'
    )
    BEGIN
        EXEC(N'CREATE UNIQUE INDEX UX_QualityEventAffectedResults_Source_20260824_003
            ON dbo.QualityEventAffectedResults(QualityEventID,SourceModule,SourceResultID)
            WHERE SourceModule IS NOT NULL AND SourceResultID IS NOT NULL;');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
