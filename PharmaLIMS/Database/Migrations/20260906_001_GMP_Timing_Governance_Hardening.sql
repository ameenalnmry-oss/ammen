SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /* ---------------------------------------------------------------------
       PRM timing governance and legacy-result reconciliation
       --------------------------------------------------------------------- */
    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 54100, 'Required table dbo.PRM_Samples is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
        THROW 54101, 'Required table dbo.PRM_SampleTests is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
        THROW 54102, 'Required table dbo.PRM_SpecificationTests is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NULL
        THROW 54105, 'Required table dbo.PRM_Certificates is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_ElectronicSignatures', N'U') IS NULL
        THROW 54108, 'Required table dbo.PRM_ElectronicSignatures is missing.', 1;
    IF COL_LENGTH(N'dbo.PRM_ElectronicSignatures', N'ActionType') IS NULL
       OR COL_LENGTH(N'dbo.PRM_ElectronicSignatures', N'SignedAt') IS NULL
        THROW 54109, 'PRM electronic-signature timing evidence is incomplete.', 1;
    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
        THROW 54106, 'Required table dbo.QualityEventAffectedResults is missing.', 1;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SampleTestID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceModule') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceResultID') IS NULL
        THROW 54107, 'QualityEventAffectedResults is missing controlled PRM source-link columns.', 1;

    IF COL_LENGTH(N'dbo.PRM_Samples', N'TimingReconciliationStatus') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD TimingReconciliationStatus NVARCHAR(30) NOT NULL
            CONSTRAINT DF_PRM_Samples_TimingReconciliationStatus_20260906 DEFAULT N'Not Required';
    IF COL_LENGTH(N'dbo.PRM_Samples', N'TimingReconciledBy') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD TimingReconciledBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.PRM_Samples', N'TimingReconciledAt') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD TimingReconciledAt DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.PRM_Samples', N'TimingReconciliationReason') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD TimingReconciliationReason NVARCHAR(1000) NULL;

    IF OBJECT_ID(N'dbo.PRM_TimingGovernanceMigrationState', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_TimingGovernanceMigrationState
        (
            StateKey NVARCHAR(100) NOT NULL CONSTRAINT PK_PRM_TimingGovernanceMigrationState PRIMARY KEY,
            CompletedAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_TimingGovernanceMigrationState_Completed DEFAULT SYSDATETIME(),
            Notes NVARCHAR(500) NULL
        );
    END;

    IF EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'CK_PRM_Samples_TimingReconciliation_20260906'
    )
        ALTER TABLE dbo.PRM_Samples DROP CONSTRAINT CK_PRM_Samples_TimingReconciliation_20260906;

    ALTER TABLE dbo.PRM_Samples WITH CHECK
    ADD CONSTRAINT CK_PRM_Samples_TimingReconciliation_20260906 CHECK
    (
        (TimingReconciliationStatus=N'Not Required' AND TimingReconciledBy IS NULL AND TimingReconciledAt IS NULL)
        OR
        (TimingReconciliationStatus=N'Required' AND TimingReconciledBy IS NULL AND TimingReconciledAt IS NULL)
        OR
        (TimingReconciliationStatus=N'Reconciled' AND TimingReconciledBy IS NOT NULL AND TimingReconciledAt IS NOT NULL
             AND NULLIF(LTRIM(RTRIM(ISNULL(TimingReconciliationReason,N''))),N'') IS NOT NULL)
        OR
        (TimingReconciliationStatus=N'Historical Closed')
    );

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_TimingMigrationHistory
        (
            TimingMigrationHistoryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_TimingMigrationHistory PRIMARY KEY,
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
            ReconciliationDisposition NVARCHAR(40) NOT NULL,
            EvidenceSummary NVARCHAR(1000) NOT NULL,
            CapturedAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_TimingMigrationHistory_CapturedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_PRM_TimingMigrationHistory_Sample FOREIGN KEY(SampleID) REFERENCES dbo.PRM_Samples(SampleID)
        );
        CREATE UNIQUE INDEX UX_PRM_TimingMigrationHistory_Sample
            ON dbo.PRM_TimingMigrationHistory(SampleID);
    END;
    ELSE
    BEGIN
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'PreviousAnalysisStartedDate') IS NULL
            ALTER TABLE dbo.PRM_TimingMigrationHistory ADD PreviousAnalysisStartedDate DATETIME2(0) NULL;
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'AnalysisStartSignatureAt') IS NULL
            ALTER TABLE dbo.PRM_TimingMigrationHistory ADD AnalysisStartSignatureAt DATETIME2(0) NULL;
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'AnalysisStartProvenanceIssue') IS NULL
            ALTER TABLE dbo.PRM_TimingMigrationHistory ADD AnalysisStartProvenanceIssue NVARCHAR(300) NULL;
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'HasControlledQualityEventEvidence') IS NULL
            ALTER TABLE dbo.PRM_TimingMigrationHistory
                ADD HasControlledQualityEventEvidence BIT NOT NULL
                    CONSTRAINT DF_PRM_TimingMigrationHistory_HasQE_20260906 DEFAULT(0) WITH VALUES;
    END;

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_TimingMigrationTestEvidence
        (
            TimingMigrationTestEvidenceID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_TimingMigrationTestEvidence PRIMARY KEY,
            SampleID INT NOT NULL,
            SampleTestID INT NOT NULL,
            TestCode NVARCHAR(80) NULL,
            TestName NVARCHAR(200) NULL,
            OriginalResultValue NVARCHAR(200) NULL,
            OriginalInterpretation NVARCHAR(60) NULL,
            OriginalRemarks NVARCHAR(500) NULL,
            OriginalEnteredBy NVARCHAR(120) NULL,
            OriginalEnteredDate DATETIME2(0) NULL,
            AnalysisStartedDate DATETIME2(0) NULL,
            AnalysisStartSignatureAt DATETIME2(0) NULL,
            AnalysisStartProvenanceIssue NVARCHAR(300) NULL,
            MinimumElapsedHours DECIMAL(9,2) NULL,
            EligibleAt DATETIME2(0) NULL,
            EvidenceIssue NVARCHAR(300) NOT NULL,
            CapturedAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_TimingMigrationTestEvidence_CapturedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_PRM_TimingMigrationTestEvidence_Sample FOREIGN KEY(SampleID) REFERENCES dbo.PRM_Samples(SampleID),
            CONSTRAINT FK_PRM_TimingMigrationTestEvidence_Test FOREIGN KEY(SampleTestID) REFERENCES dbo.PRM_SampleTests(SampleTestID)
        );
        CREATE UNIQUE INDEX UX_PRM_TimingMigrationTestEvidence_Test
            ON dbo.PRM_TimingMigrationTestEvidence(SampleTestID);
    END;
    ELSE
    BEGIN
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence',N'AnalysisStartSignatureAt') IS NULL
            ALTER TABLE dbo.PRM_TimingMigrationTestEvidence ADD AnalysisStartSignatureAt DATETIME2(0) NULL;
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence',N'AnalysisStartProvenanceIssue') IS NULL
            ALTER TABLE dbo.PRM_TimingMigrationTestEvidence ADD AnalysisStartProvenanceIssue NVARCHAR(300) NULL;
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.PRM_TimingGovernanceMigrationState WITH(UPDLOCK,HOLDLOCK)
        WHERE StateKey=N'InitialLegacyPrmTimingEvidenceCaptured'
    )
    BEGIN
    ;WITH StartEvidence AS
    (
        SELECT
            s.SampleID,
            s.AnalysisStartedDate,
            signatureEvidence.SignedAt AS AnalysisStartSignatureAt,
            CASE
                WHEN s.AnalysisStartedDate IS NULL THEN N'Missing Analysis Start timestamp'
                WHEN signatureEvidence.SignedAt IS NULL THEN N'Missing Analysis Start electronic signature'
                WHEN s.AnalysisStartedDate < DATEADD(MINUTE,-5,signatureEvidence.SignedAt)
                    THEN N'Retrospective/backdated Analysis Start predates its electronic signature beyond the five-minute recording tolerance'
                WHEN s.AnalysisStartedDate > DATEADD(MINUTE,5,signatureEvidence.SignedAt)
                    THEN N'Analysis Start timestamp is later than its electronic signature beyond the five-minute recording tolerance'
                ELSE NULL
            END AS AnalysisStartProvenanceIssue
        FROM dbo.PRM_Samples s
        OUTER APPLY
        (
            SELECT TOP(1) es.SignedAt
            FROM dbo.PRM_ElectronicSignatures es
            WHERE es.SampleID=s.SampleID
              AND UPPER(LTRIM(RTRIM(ISNULL(es.ActionType,N''))))=N'ANALYSIS START'
            ORDER BY es.SignedAt DESC, es.SignatureID DESC
        ) signatureEvidence
    ), TimingEvidence AS
    (
        SELECT
            s.SampleID,
            startEvidence.AnalysisStartSignatureAt,
            startEvidence.AnalysisStartProvenanceIssue,
            MAX(CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(t.ResultValue,N''))),N'') IS NOT NULL THEN 1 ELSE 0 END) AS HasEnteredResult,
            MAX(CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(t.ResultValue,N''))),N'') IS NOT NULL
                      AND
                      (
                          startEvidence.AnalysisStartProvenanceIssue IS NOT NULL
                          OR t.MinimumElapsedHours IS NULL
                          OR t.EnteredDate IS NULL
                          OR t.EnteredDate < DATEADD(SECOND,
                                CONVERT(INT, CEILING(CONVERT(DECIMAL(18,4),t.MinimumElapsedHours) * 3600.0)),
                                s.AnalysisStartedDate)
                      )
                     THEN 1 ELSE 0 END) AS HasInvalidTimingEvidence
        FROM dbo.PRM_Samples s
        INNER JOIN StartEvidence startEvidence ON startEvidence.SampleID=s.SampleID
        LEFT JOIN dbo.PRM_SampleTests t ON t.SampleID=s.SampleID AND ISNULL(t.RequiredTest,1)=1
        GROUP BY s.SampleID, startEvidence.AnalysisStartSignatureAt, startEvidence.AnalysisStartProvenanceIssue
    ), Affected AS
    (
        SELECT s.*, evidence.HasEnteredResult, evidence.HasInvalidTimingEvidence,
               evidence.AnalysisStartSignatureAt,
               evidence.AnalysisStartProvenanceIssue,
               CASE WHEN EXISTS
               (
                   SELECT 1 FROM dbo.PRM_Certificates c
                   WHERE c.SampleID=s.SampleID
               ) OR UPPER(LTRIM(RTRIM(ISNULL(s.SampleStatus,N''))))=N'CERTIFICATE ISSUED'
               THEN 1 ELSE 0 END AS HasIssuedCertificate,
               CASE WHEN EXISTS
               (
                   SELECT 1
                   FROM dbo.QualityEventAffectedResults qear
                   WHERE qear.SampleTestID IN
                     (
                         SELECT st.SampleTestID FROM dbo.PRM_SampleTests st
                         WHERE st.SampleID=s.SampleID
                     )
                      OR
                     (
                         qear.SourceModule=N'PRM'
                         AND qear.SourceResultID IN
                         (
                             SELECT st.SampleTestID FROM dbo.PRM_SampleTests st
                             WHERE st.SampleID=s.SampleID
                         )
                     )
               ) THEN 1 ELSE 0 END AS HasControlledQualityEventEvidence
        FROM dbo.PRM_Samples s
        INNER JOIN TimingEvidence evidence ON evidence.SampleID=s.SampleID
        WHERE (evidence.HasEnteredResult=1 AND evidence.HasInvalidTimingEvidence=1)
           OR (evidence.HasEnteredResult=0 AND s.AnalysisStartedDate IS NOT NULL AND evidence.AnalysisStartProvenanceIssue IS NOT NULL)
    )
    INSERT INTO dbo.PRM_TimingMigrationHistory
    (
        SampleID, SampleNumber, PreviousSampleStatus, PreviousReportStatus,
        PreviousReviewedBy, PreviousReviewedDate, PreviousApprovedBy, PreviousApprovedDate,
        PreviousAnalysisStartedDate, AnalysisStartSignatureAt, AnalysisStartProvenanceIssue,
        HasIssuedCertificate, HasControlledQualityEventEvidence, ReconciliationDisposition, EvidenceSummary
    )
    SELECT
        a.SampleID, a.SampleNumber, a.SampleStatus, a.ReportStatus,
        a.ReviewedBy, a.ReviewedDate, a.ApprovedBy, a.ApprovedDate,
        a.AnalysisStartedDate, a.AnalysisStartSignatureAt, a.AnalysisStartProvenanceIssue,
        a.HasIssuedCertificate, a.HasControlledQualityEventEvidence,
        CASE WHEN a.HasIssuedCertificate=1 THEN N'Historical Closed - Certificate'
             WHEN a.HasControlledQualityEventEvidence=1 THEN N'Historical Closed - Quality Event Evidence'
             WHEN a.HasEnteredResult=0 THEN N'Controlled Restart Required'
             ELSE N'Requires Controlled Re-entry' END,
        CASE WHEN a.AnalysisStartProvenanceIssue IS NOT NULL
             THEN N'PRM Analysis Start provenance is not acceptable under v212 timing governance: ' + a.AnalysisStartProvenanceIssue + N'. Original evidence captured by migration 20260906_001.'
             ELSE N'One or more required PRM results pre-date their frozen MinimumElapsedHours eligibility or lack complete timing evidence. Captured by migration 20260906_001.' END
    FROM Affected a
    WHERE NOT EXISTS (SELECT 1 FROM dbo.PRM_TimingMigrationHistory h WHERE h.SampleID=a.SampleID);

    INSERT INTO dbo.PRM_TimingMigrationTestEvidence
    (
        SampleID, SampleTestID, TestCode, TestName, OriginalResultValue, OriginalInterpretation,
        OriginalRemarks, OriginalEnteredBy, OriginalEnteredDate, AnalysisStartedDate,
        AnalysisStartSignatureAt, AnalysisStartProvenanceIssue, MinimumElapsedHours, EligibleAt, EvidenceIssue
    )
    SELECT
        s.SampleID, t.SampleTestID, t.TestCode, t.TestName, t.ResultValue, t.Interpretation,
        t.Remarks, t.EnteredBy, t.EnteredDate, s.AnalysisStartedDate, h.AnalysisStartSignatureAt, h.AnalysisStartProvenanceIssue, t.MinimumElapsedHours,
        CASE WHEN s.AnalysisStartedDate IS NULL OR t.MinimumElapsedHours IS NULL THEN NULL
             ELSE DATEADD(SECOND,
                    CONVERT(INT, CEILING(CONVERT(DECIMAL(18,4),t.MinimumElapsedHours) * 3600.0)),
                    s.AnalysisStartedDate)
        END,
        CASE
            WHEN h.AnalysisStartProvenanceIssue IS NOT NULL THEN h.AnalysisStartProvenanceIssue
            WHEN s.AnalysisStartedDate IS NULL THEN N'Missing Analysis Start'
            WHEN t.MinimumElapsedHours IS NULL THEN N'Missing frozen MinimumElapsedHours'
            WHEN t.EnteredDate IS NULL THEN N'Missing result EnteredDate'
            ELSE N'Result entered before frozen eligible time'
        END
    FROM dbo.PRM_Samples s
    INNER JOIN dbo.PRM_TimingMigrationHistory h ON h.SampleID=s.SampleID
    INNER JOIN dbo.PRM_SampleTests t ON t.SampleID=s.SampleID AND ISNULL(t.RequiredTest,1)=1
    WHERE NULLIF(LTRIM(RTRIM(ISNULL(t.ResultValue,N''))),N'') IS NOT NULL
      AND
      (
          h.AnalysisStartProvenanceIssue IS NOT NULL
          OR s.AnalysisStartedDate IS NULL
          OR t.MinimumElapsedHours IS NULL
          OR t.EnteredDate IS NULL
          OR t.EnteredDate < DATEADD(SECOND,
                CONVERT(INT, CEILING(CONVERT(DECIMAL(18,4),t.MinimumElapsedHours) * 3600.0)),
                s.AnalysisStartedDate)
      )
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.PRM_TimingMigrationTestEvidence e
          WHERE e.SampleTestID=t.SampleTestID
      );

    /* Issued immutable history is not rewritten. It is explicitly labelled as historical. */
    UPDATE s
    SET TimingReconciliationStatus=N'Historical Closed',
        TimingReconciledBy=NULL,
        TimingReconciledAt=NULL,
        TimingReconciliationReason=CASE WHEN h.HasIssuedCertificate=1
             THEN N'Historical issued record captured by migration 20260906_001; immutable certificate history was not rewritten.'
             ELSE N'Historical record contains controlled Quality Event result evidence; result evidence was not rewritten by migration 20260906_001.' END
    FROM dbo.PRM_Samples s
    INNER JOIN dbo.PRM_TimingMigrationHistory h ON h.SampleID=s.SampleID
    WHERE h.HasIssuedCertificate=1 OR h.HasControlledQualityEventEvidence=1;

    /* Any affected sample that has not already produced an immutable issued certificate is
       returned to controlled result-entry. Prior review/approval identity is retained in the
       migration-history table rather than silently accepted under the new timing controls. */
    UPDATE s
    SET SampleStatus=N'In Progress',
        ReportStatus=N'Not Issued',
        ReviewedBy=NULL,
        ReviewedDate=NULL,
        ApprovedBy=NULL,
        ApprovedDate=NULL,
        AnalysisStartedDate=CASE WHEN h.AnalysisStartProvenanceIssue IS NOT NULL THEN NULL ELSE s.AnalysisStartedDate END,
        AnalysisCompletedDate=NULL,
        TimingReconciliationStatus=CASE WHEN h.ReconciliationDisposition=N'Controlled Restart Required' THEN N'Not Required' ELSE N'Required' END,
        TimingReconciledBy=NULL,
        TimingReconciledAt=NULL,
        TimingReconciliationReason=NULL,
        ModifiedBy=N'System Migration 20260906_001',
        ModifiedDate=SYSDATETIME()
    FROM dbo.PRM_Samples s
    INNER JOIN dbo.PRM_TimingMigrationHistory h ON h.SampleID=s.SampleID
    WHERE h.HasIssuedCertificate=0
      AND h.HasControlledQualityEventEvidence=0
      AND s.TimingReconciliationStatus<>N'Reconciled';

    /* Preserve invalid legacy evidence above, then force controlled re-entry of only the affected tests. */
    UPDATE t
    SET ResultValue=NULL,
        Interpretation=N'Pending',
        Remarks=NULL,
        EnteredBy=NULL,
        EnteredDate=NULL
    FROM dbo.PRM_SampleTests t
    INNER JOIN dbo.PRM_TimingMigrationTestEvidence e ON e.SampleTestID=t.SampleTestID
    INNER JOIN dbo.PRM_TimingMigrationHistory h ON h.SampleID=t.SampleID
    INNER JOIN dbo.PRM_Samples s ON s.SampleID=t.SampleID
    WHERE h.HasIssuedCertificate=0
      AND h.HasControlledQualityEventEvidence=0
      AND s.TimingReconciliationStatus=N'Required';

        INSERT INTO dbo.PRM_TimingGovernanceMigrationState(StateKey,Notes)
        VALUES
        (
            N'InitialLegacyPrmTimingEvidenceCaptured',
            N'Pre-v212 PRM timing evidence was scanned once. Invalid legacy result evidence was preserved fail-closed and active untrusted Analysis Start timestamps were reset for controlled restart/re-entry.'
        );
    END;

    /* Timing was introduced after the prior PRM specification approval. Re-open only the
       specifications that existed when this governance migration is first applied. A durable
       migration-state row prevents any later controlled replay from reopening specifications
       that QA approved under the v212 timing-aware workflow. */
    IF OBJECT_ID(N'dbo.PRM_SpecificationTimingReapprovalHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_SpecificationTimingReapprovalHistory
        (
            ReapprovalHistoryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_SpecificationTimingReapprovalHistory PRIMARY KEY,
            SpecificationTestID INT NOT NULL,
            SpecificationNo NVARCHAR(120) NOT NULL,
            SampleCategory NVARCHAR(40) NOT NULL,
            VersionNo INT NOT NULL,
            TestCode NVARCHAR(80) NULL,
            MinimumElapsedHours DECIMAL(9,2) NULL,
            PreviousReviewedBy NVARCHAR(120) NULL,
            PreviousReviewedDate DATETIME2(0) NULL,
            PreviousApprovedBy NVARCHAR(120) NULL,
            PreviousApprovedDate DATETIME2(0) NULL,
            CapturedAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_SpecTimingReapproval_CapturedAt DEFAULT SYSDATETIME()
        );
        CREATE UNIQUE INDEX UX_PRM_SpecTimingReapproval_Test
            ON dbo.PRM_SpecificationTimingReapprovalHistory(SpecificationTestID);
        CREATE INDEX IX_PRM_SpecTimingReapproval_Version
            ON dbo.PRM_SpecificationTimingReapprovalHistory(SpecificationNo,SampleCategory,VersionNo);
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.PRM_TimingGovernanceMigrationState WITH (UPDLOCK,HOLDLOCK)
        WHERE StateKey=N'InitialSpecificationTimingReapprovalCaptured'
    )
    BEGIN
        INSERT INTO dbo.PRM_SpecificationTimingReapprovalHistory
        (
            SpecificationTestID, SpecificationNo, SampleCategory, VersionNo, TestCode,
            MinimumElapsedHours, PreviousReviewedBy, PreviousReviewedDate, PreviousApprovedBy, PreviousApprovedDate
        )
        SELECT
            t.SpecificationTestID, t.SpecificationNo, t.SampleCategory, t.VersionNo, t.TestCode,
            t.MinimumElapsedHours, t.ReviewedBy, t.ReviewedDate, t.ApprovedBy, t.ApprovedDate
        FROM dbo.PRM_SpecificationTests t
        WHERE t.ApprovalStatus=N'Approved' AND t.IsActive=1
          AND NOT EXISTS
          (
              SELECT 1 FROM dbo.PRM_SpecificationTimingReapprovalHistory h
              WHERE h.SpecificationTestID=t.SpecificationTestID
          );

        UPDATE t
        SET ApprovalStatus=N'Draft',
            IsActive=0,
            ReviewedBy=NULL,
            ReviewedDate=NULL,
            ApprovedBy=NULL,
            ApprovedDate=NULL
        FROM dbo.PRM_SpecificationTests t
        INNER JOIN dbo.PRM_SpecificationTimingReapprovalHistory h
            ON h.SpecificationTestID=t.SpecificationTestID
        WHERE t.ApprovalStatus=N'Approved' AND t.IsActive=1
          AND ISNULL(t.ApprovedBy,N'')=ISNULL(h.PreviousApprovedBy,N'')
          AND ((t.ApprovedDate=h.PreviousApprovedDate) OR (t.ApprovedDate IS NULL AND h.PreviousApprovedDate IS NULL));

        INSERT INTO dbo.PRM_TimingGovernanceMigrationState(StateKey,Notes)
        VALUES
        (
            N'InitialSpecificationTimingReapprovalCaptured',
            N'Pre-v212 active approved PRM specifications were captured once and reopened for controlled Review + Approve of MinimumElapsedHours.'
        );
    END;

    /* ---------------------------------------------------------------------
       Culture Media timing governance and qualification snapshots
       --------------------------------------------------------------------- */
    IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') IS NULL
        THROW 54103, 'Required table dbo.CultureMediaQualificationRequirements is missing.', 1;
    IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NULL
        THROW 54104, 'Required table dbo.MediaQualifications is missing.', 1;

    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedMinimumIncubationHours') IS NULL
        ALTER TABLE dbo.CultureMediaQualificationRequirements ADD TimingConfirmedMinimumIncubationHours DECIMAL(9,2) NULL;
    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedBy') IS NULL
        ALTER TABLE dbo.CultureMediaQualificationRequirements ADD TimingConfirmedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedAt') IS NULL
        ALTER TABLE dbo.CultureMediaQualificationRequirements ADD TimingConfirmedAt DATETIME2(0) NULL;

    IF EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906'
    )
        ALTER TABLE dbo.CultureMediaQualificationRequirements
            DROP CONSTRAINT CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906;

    ALTER TABLE dbo.CultureMediaQualificationRequirements WITH CHECK
    ADD CONSTRAINT CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906 CHECK
    (
        (TimingConfirmedBy IS NULL AND TimingConfirmedAt IS NULL AND TimingConfirmedMinimumIncubationHours IS NULL)
        OR
        (TimingConfirmedBy IS NOT NULL AND TimingConfirmedAt IS NOT NULL
         AND TimingConfirmedMinimumIncubationHours IS NOT NULL
         AND TimingConfirmedMinimumIncubationHours >= 0)
    );

    /* New confirmation columns are NULL on first migration; Production remains fail-closed until QA confirms them. */

    IF OBJECT_ID(N'dbo.MediaQualificationRequirementSnapshots', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MediaQualificationRequirementSnapshots
        (
            SnapshotID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MediaQualificationRequirementSnapshots PRIMARY KEY,
            MediaQualificationID INT NOT NULL,
            RequirementID INT NOT NULL,
            TestName NVARCHAR(100) NOT NULL,
            MinimumRecoveryPercent DECIMAL(9,2) NULL,
            MaximumRecoveryPercent DECIMAL(9,2) NULL,
            MinimumIncubationHoursSnapshot DECIMAL(9,2) NOT NULL,
            RequirementApprovedBy NVARCHAR(100) NOT NULL,
            RequirementApprovedAt DATETIME2(0) NOT NULL,
            TimingConfirmedBy NVARCHAR(100) NOT NULL,
            TimingConfirmedAt DATETIME2(0) NOT NULL,
            CapturedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MediaQualificationReqSnapshots_CapturedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_MediaQualificationReqSnapshots_Qualification FOREIGN KEY(MediaQualificationID)
                REFERENCES dbo.MediaQualifications(MediaQualificationID),
            CONSTRAINT CK_MediaQualificationReqSnapshots_MinHours CHECK(MinimumIncubationHoursSnapshot >= 0)
        );
        CREATE UNIQUE INDEX UX_MediaQualificationReqSnapshots_QualificationRequirement
            ON dbo.MediaQualificationRequirementSnapshots(MediaQualificationID, RequirementID);
        CREATE INDEX IX_MediaQualificationReqSnapshots_Qualification
            ON dbo.MediaQualificationRequirementSnapshots(MediaQualificationID, TestName);
    END;

    /* Immutable migration and qualification evidence. */
    EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_PRM_TimingMigrationHistory_AppendOnly_20260906
ON dbo.PRM_TimingMigrationHistory
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 54110, ''PRM timing migration history is append-only.'', 1;
END;');

    EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_PRM_TimingMigrationTestEvidence_AppendOnly_20260906
ON dbo.PRM_TimingMigrationTestEvidence
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 54111, ''PRM timing migration test evidence is append-only.'', 1;
END;');

    EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_PRM_SpecTimingReapprovalHistory_AppendOnly_20260906
ON dbo.PRM_SpecificationTimingReapprovalHistory
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 54112, ''PRM specification timing reapproval history is append-only.'', 1;
END;');

    EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_MediaQualificationRequirementSnapshots_AppendOnly_20260906
ON dbo.MediaQualificationRequirementSnapshots
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 54113, ''Culture Media qualification requirement snapshots are append-only.'', 1;
END;');

    EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_PRM_TimingGovernanceMigrationState_AppendOnly_20260906
ON dbo.PRM_TimingGovernanceMigrationState
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 54114, ''PRM timing governance migration state is append-only.'', 1;
END;');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260906_001' AS MigrationVersion;
