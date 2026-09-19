SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       v222 pre-20260906_001 compatibility prerequisite.

       Historical migration 20260906_001 intentionally remains byte-for-byte
       unchanged. That migration can persist the controlled disposition
       N'Historical Closed - Quality Event Evidence' (42 characters), while its
       original CREATE TABLE branch declared ReconciliationDisposition as
       NVARCHAR(40). When a legacy PRM sample has controlled Quality Event
       evidence, SQL Server raises error 2628 and rolls the migration back.

       This prerequisite runs before 20260906_001. It creates the migration-owned
       history table with the corrected width when the table is absent, or safely
       widens only the affected NVARCHAR column when an earlier controlled run
       already created the table. No evidence values are truncated or rewritten.
    */

    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 54470, 'Required table dbo.PRM_Samples is missing before 20260906_000A.', 1;

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_TimingMigrationHistory
        (
            TimingMigrationHistoryID INT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_PRM_TimingMigrationHistory PRIMARY KEY,
            SampleID INT NOT NULL,
            SampleNumber NVARCHAR(120) NULL,
            PreviousSampleStatus NVARCHAR(60) NULL,
            PreviousReportStatus NVARCHAR(60) NULL,
            PreviousReviewedBy NVARCHAR(120) NULL,
            PreviousReviewedDate DATETIME2(0) NULL,
            PreviousApprovedBy NVARCHAR(120) NULL,
            PreviousApprovedDate DATETIME2(0) NULL,
            PreviousAnalysisStartedDate DATETIME2(0) NULL,
            AnalysisStartSignatureAt DATETIME2(0) NULL,
            AnalysisStartProvenanceIssue NVARCHAR(300) NULL,
            HasIssuedCertificate BIT NOT NULL,
            HasControlledQualityEventEvidence BIT NOT NULL,
            ReconciliationDisposition NVARCHAR(60) NOT NULL,
            EvidenceSummary NVARCHAR(1000) NOT NULL,
            CapturedAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_PRM_TimingMigrationHistory_CapturedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_PRM_TimingMigrationHistory_Sample
                FOREIGN KEY(SampleID) REFERENCES dbo.PRM_Samples(SampleID)
        );

        CREATE UNIQUE INDEX UX_PRM_TimingMigrationHistory_Sample
            ON dbo.PRM_TimingMigrationHistory(SampleID);
    END
    ELSE
    BEGIN
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory', N'ReconciliationDisposition') IS NULL
            THROW 54471, 'Existing dbo.PRM_TimingMigrationHistory is missing ReconciliationDisposition; controlled evidence review is required.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
              AND name=N'ReconciliationDisposition'
              AND (system_type_id<>TYPE_ID(N'nvarchar') OR is_nullable<>0)
        )
            THROW 54472, 'Existing dbo.PRM_TimingMigrationHistory.ReconciliationDisposition is not a NOT NULL NVARCHAR column.', 1;

        /* NVARCHAR max_length is stored in bytes. Widening preserves every value. */
        IF EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
              AND name=N'ReconciliationDisposition'
              AND max_length<>-1
              AND max_length<120
        )
            ALTER TABLE dbo.PRM_TimingMigrationHistory
                ALTER COLUMN ReconciliationDisposition NVARCHAR(60) NOT NULL;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
          AND name=N'ReconciliationDisposition'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND is_nullable=0
          AND (max_length=-1 OR max_length>=120)
    )
        THROW 54473, 'dbo.PRM_TimingMigrationHistory.ReconciliationDisposition must hold at least 60 Unicode characters before 20260906_001.', 1;

    /*
       Guard the exact controlled literal that exposed SQL Server error 2628.
       This remains an executable contract instead of relying on a comment.
    */
    IF LEN(N'Historical Closed - Quality Event Evidence') > 60
        THROW 54474, 'Controlled PRM reconciliation disposition exceeds the v222 history-column contract.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260906_000A' AS MigrationVersion;
