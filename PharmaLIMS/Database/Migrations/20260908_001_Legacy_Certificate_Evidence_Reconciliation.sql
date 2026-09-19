SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       Signed QA disposition for certificates issued before immutable document
       snapshots were introduced. This evidence is deliberately separate from
       issue-time snapshots/hashes: it never fabricates or backfills an original
       certificate snapshot and never changes an issued certificate.
    */

    IF OBJECT_ID(N'dbo.Certificates',N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_Certificates',N'U') IS NULL
       OR OBJECT_ID(N'dbo.CertificateDocumentSnapshots',N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_CertificateSnapshots',N'U') IS NULL
       OR OBJECT_ID(N'dbo.LIMS_SchemaVersions',N'U') IS NULL
        THROW 54820, 'Required certificate/snapshot tables are missing before 20260908_001.', 1;

    IF COL_LENGTH(N'dbo.Certificates',N'CertificateID') IS NULL
       OR COL_LENGTH(N'dbo.Certificates',N'CertificateNumber') IS NULL
       OR COL_LENGTH(N'dbo.Certificates',N'SampleID') IS NULL
       OR COL_LENGTH(N'dbo.Certificates',N'IssueDate') IS NULL
       OR COL_LENGTH(N'dbo.Certificates',N'CertificateStatus') IS NULL
       OR COL_LENGTH(N'dbo.Certificates',N'Status') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates',N'CertificateID') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates',N'CertificateNumber') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates',N'SampleID') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates',N'IssueDate') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates',N'CertificateStatus') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates',N'ReportHash') IS NULL
        THROW 54821, 'Certificate tables do not satisfy the legacy-evidence reconciliation contract.', 1;

    IF OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations',N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.LegacyCertificateEvidenceReconciliations
        (
            ReconciliationID INT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_LegacyCertificateEvidenceReconciliations PRIMARY KEY,
            CertificateModule NVARCHAR(20) NOT NULL,
            CertificateID INT NOT NULL,
            SupersedesReconciliationID INT NULL,
            CertificateNumberSnapshot NVARCHAR(100) NOT NULL,
            SampleIDSnapshot INT NOT NULL,
            IssueDateSnapshot DATETIME2(0) NOT NULL,
            CertificateStatusSnapshot NVARCHAR(60) NOT NULL,
            LegacyConditionSnapshot NVARCHAR(1000) NOT NULL,
            EvidenceReference NVARCHAR(500) NOT NULL,
            EvidenceSummary NVARCHAR(MAX) NOT NULL,
            Disposition NVARCHAR(80) NOT NULL,
            Reason NVARCHAR(MAX) NOT NULL,
            SignedBy NVARCHAR(120) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            SignatureReason NVARCHAR(1000) NULL,
            SignedAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_LegacyCertificateEvidenceReconciliations_SignedAt DEFAULT SYSUTCDATETIME(),
            SourceWorkstation NVARCHAR(200) NULL,
            ReconciliationSchemaVersion TINYINT NOT NULL
                CONSTRAINT DF_LegacyCertificateEvidenceReconciliations_SchemaVersion DEFAULT(1),
            CONSTRAINT FK_LegacyCertificateEvidenceReconciliations_Supersedes
                FOREIGN KEY(SupersedesReconciliationID)
                REFERENCES dbo.LegacyCertificateEvidenceReconciliations(ReconciliationID),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_Module
                CHECK(CertificateModule IN(N'PRM',N'WATER')),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_CertificateID
                CHECK(CertificateID>0),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_SampleID
                CHECK(SampleIDSnapshot>0),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_Number
                CHECK(NULLIF(LTRIM(RTRIM(CertificateNumberSnapshot)),N'') IS NOT NULL),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_Status
                CHECK(NULLIF(LTRIM(RTRIM(CertificateStatusSnapshot)),N'') IS NOT NULL),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_Condition
                CHECK(NULLIF(LTRIM(RTRIM(LegacyConditionSnapshot)),N'') IS NOT NULL),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_EvidenceReference
                CHECK(NULLIF(LTRIM(RTRIM(EvidenceReference)),N'') IS NOT NULL),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_EvidenceSummary
                CHECK(NULLIF(LTRIM(RTRIM(EvidenceSummary)),N'') IS NOT NULL),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_Disposition
                CHECK(Disposition IN(N'LEGACY_HISTORICAL_RECORD_RETAINED',N'CONTROLLED_REISSUE_REQUIRED')),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_Reason
                CHECK(NULLIF(LTRIM(RTRIM(Reason)),N'') IS NOT NULL),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_SignedBy
                CHECK(NULLIF(LTRIM(RTRIM(SignedBy)),N'') IS NOT NULL),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_Meaning
                CHECK(NULLIF(LTRIM(RTRIM(MeaningOfSignature)),N'') IS NOT NULL),
            CONSTRAINT CK_LegacyCertificateEvidenceReconciliations_SchemaVersion
                CHECK(ReconciliationSchemaVersion=1)
        );

    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
          AND name=N'IX_LegacyCertificateEvidenceReconciliations_Certificate'
    )
        CREATE INDEX IX_LegacyCertificateEvidenceReconciliations_Certificate
            ON dbo.LegacyCertificateEvidenceReconciliations(CertificateModule,CertificateID,ReconciliationID DESC);

    /* One immutable reconciliation chain per certificate: one root and one child per prior row. */
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
          AND name=N'UX_LegacyCertificateEvidenceReconciliations_Root'
    )
        CREATE UNIQUE INDEX UX_LegacyCertificateEvidenceReconciliations_Root
            ON dbo.LegacyCertificateEvidenceReconciliations(CertificateModule,CertificateID)
            WHERE SupersedesReconciliationID IS NULL;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
          AND name=N'UX_LegacyCertificateEvidenceReconciliations_Supersedes'
    )
        CREATE UNIQUE INDEX UX_LegacyCertificateEvidenceReconciliations_Supersedes
            ON dbo.LegacyCertificateEvidenceReconciliations(SupersedesReconciliationID)
            WHERE SupersedesReconciliationID IS NOT NULL;

    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_LegacyCertificateEvidenceReconciliations_AppendOnly_20260908
    ON dbo.LegacyCertificateEvidenceReconciliations
    AFTER INSERT, UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;

        IF EXISTS(SELECT 1 FROM deleted)
            THROW 54822, ''Legacy certificate reconciliation evidence is append-only. Add a signed superseding reconciliation instead of updating or deleting history.'', 1;
    END;';

    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_LegacyCertificateEvidenceReconciliations_ValidateInsert_20260908
    ON dbo.LegacyCertificateEvidenceReconciliations
    AFTER INSERT
    AS
    BEGIN
        SET NOCOUNT ON;

        DECLARE @SnapshotCutover DATETIME2(0)=NULL;
        SELECT TOP(1) @SnapshotCutover=AppliedAt
        FROM dbo.LIMS_SchemaVersions
        WHERE VersionKey=N''20260722_003''
        ORDER BY AppliedAt;

        IF @SnapshotCutover IS NULL
            THROW 54823, ''Immutable certificate-snapshot cutover cannot be established; legacy reconciliation is blocked.'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM inserted i
            LEFT JOIN dbo.PRM_Certificates c ON c.CertificateID=i.CertificateID
            WHERE i.CertificateModule=N''PRM''
              AND
              (
                  c.CertificateID IS NULL
                  OR c.CertificateNumber<>i.CertificateNumberSnapshot
                  OR c.SampleID<>i.SampleIDSnapshot
                  OR c.IssueDate<>i.IssueDateSnapshot
                  OR c.CertificateStatus<>i.CertificateStatusSnapshot
                  OR c.IssueDate>=@SnapshotCutover
                  OR UPPER(LTRIM(RTRIM(ISNULL(c.CertificateStatus,N''''))))<>N''ACTIVE''
                  OR
                  (
                      LEN(LTRIM(RTRIM(ISNULL(c.ReportHash,N''''))))=64
                      AND EXISTS(SELECT 1 FROM dbo.PRM_CertificateSnapshots s WHERE s.CertificateID=c.CertificateID)
                  )
              )
        )
            THROW 54824, ''PRM reconciliation is permitted only for an active pre-cutover certificate that still has genuine legacy hash/snapshot evidence limitations.'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM inserted i
            LEFT JOIN dbo.Certificates c ON c.CertificateID=i.CertificateID
            WHERE i.CertificateModule=N''WATER''
              AND
              (
                  c.CertificateID IS NULL
                  OR c.CertificateNumber<>i.CertificateNumberSnapshot
                  OR c.SampleID<>i.SampleIDSnapshot
                  OR c.IssueDate<>i.IssueDateSnapshot
                  OR UPPER(LTRIM(RTRIM(ISNULL(c.CertificateStatus,ISNULL(c.Status,N'''')))))<>UPPER(LTRIM(RTRIM(i.CertificateStatusSnapshot)))
                  OR c.IssueDate>=@SnapshotCutover
                  OR UPPER(LTRIM(RTRIM(ISNULL(c.CertificateStatus,ISNULL(c.Status,N''''))))) NOT IN(N''ACTIVE'',N''ISSUED'')
                  OR EXISTS(SELECT 1 FROM dbo.CertificateDocumentSnapshots s WHERE s.CertificateID=c.CertificateID)
              )
        )
            THROW 54825, ''Water/general reconciliation is permitted only for an active pre-cutover certificate that still lacks its native issue snapshot.'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM inserted i
            INNER JOIN dbo.LegacyCertificateEvidenceReconciliations prior
                ON prior.ReconciliationID=i.SupersedesReconciliationID
            WHERE prior.CertificateModule<>i.CertificateModule
               OR prior.CertificateID<>i.CertificateID
        )
            THROW 54826, ''A certificate reconciliation may supersede only evidence for the same module and certificate.'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM inserted i
            OUTER APPLY
            (
                SELECT TOP(1) prior.ReconciliationID
                FROM dbo.LegacyCertificateEvidenceReconciliations prior WITH(UPDLOCK,HOLDLOCK)
                WHERE prior.CertificateModule=i.CertificateModule
                  AND prior.CertificateID=i.CertificateID
                  AND prior.ReconciliationID<>i.ReconciliationID
                ORDER BY prior.ReconciliationID DESC
            ) latestPrior
            WHERE (latestPrior.ReconciliationID IS NULL AND i.SupersedesReconciliationID IS NOT NULL)
               OR (latestPrior.ReconciliationID IS NOT NULL AND ISNULL(i.SupersedesReconciliationID,0)<>latestPrior.ReconciliationID)
        )
            THROW 54827, ''A new certificate reconciliation must explicitly supersede the latest prior reconciliation for that certificate.'', 1;
    END;';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
