SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.CultureMediaLots', N'U') IS NULL
        THROW 54120, 'Required table dbo.CultureMediaLots is missing.', 1;
    IF COL_LENGTH(N'dbo.CultureMediaLots', N'MediaLotID') IS NULL
       OR COL_LENGTH(N'dbo.CultureMediaLots', N'ReceiptStatus') IS NULL
       OR COL_LENGTH(N'dbo.CultureMediaLots', N'ExpiryDate') IS NULL
        THROW 54121, 'dbo.CultureMediaLots is missing the controlled final-release columns.', 1;

    /* Database-level defense in depth: only the transition INTO Released is gated.
       Already released historical lots remain queryable even after their later expiry. */
    EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_CultureMediaLots_FinalReleaseExpiryGate_20260906
ON dbo.CultureMediaLots
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        LEFT JOIN deleted d ON d.MediaLotID=i.MediaLotID
        WHERE UPPER(LTRIM(RTRIM(ISNULL(i.ReceiptStatus,N''''))))=N''RELEASED''
          AND
          (
              d.MediaLotID IS NULL
              OR UPPER(LTRIM(RTRIM(ISNULL(d.ReceiptStatus,N''''))))<>N''RELEASED''
          )
          AND
          (
              i.ExpiryDate IS NULL
              OR CONVERT(date,i.ExpiryDate)<CONVERT(date,SYSDATETIME())
          )
    )
        THROW 54122, ''Culture Media final release is blocked because the manufacturer expiry is missing or expired according to SQL Server time.'', 1;
END;');

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory', N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence', N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NULL
       OR OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
       OR OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
       OR OBJECT_ID(N'dbo.Samples', N'U') IS NULL
       OR OBJECT_ID(N'dbo.SampleTests', N'U') IS NULL
        THROW 54123, 'Required PRM timing/Quality Event governance tables are missing.', 1;

    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceModule') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceResultID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SampleTestID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults',N'QualityEventID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents',N'QualityEventID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents',N'SampleID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents',N'SampleNumber') IS NULL
       OR COL_LENGTH(N'dbo.Samples',N'SampleID') IS NULL
       OR COL_LENGTH(N'dbo.Samples',N'SampleNumber') IS NULL
       OR COL_LENGTH(N'dbo.SampleTests',N'SampleTestID') IS NULL
       OR COL_LENGTH(N'dbo.SampleTests',N'SampleID') IS NULL
        THROW 54124, 'Quality Event/general-sample source evidence does not expose the columns required for safe PRM source disambiguation.', 1;

    /* Optional evidence from an older upgrade path. Fresh installs legitimately do not
       contain this retired compatibility table. A local temp table lets dynamic SQL read
       it only when it actually exists without creating a hard schema dependency. */
    CREATE TABLE #LegacyPrmLinks
    (
        AffectedResultID INT NOT NULL,
        SampleTestID INT NOT NULL,
        PRIMARY KEY(AffectedResultID,SampleTestID)
    );

    IF OBJECT_ID(N'dbo.PRM_QualityEventLinkReconciliation', N'U') IS NOT NULL
    BEGIN
        EXEC(N'
INSERT #LegacyPrmLinks(AffectedResultID,SampleTestID)
SELECT legacyLink.AffectedResultID,
       COALESCE(legacyLink.OriginalSourceResultID,legacyLink.OriginalSampleTestID)
FROM dbo.PRM_QualityEventLinkReconciliation legacyLink
WHERE UPPER(LTRIM(RTRIM(ISNULL(legacyLink.OriginalSourceModule,N''''))))=N''PRM''
  AND COALESCE(legacyLink.OriginalSourceResultID,legacyLink.OriginalSampleTestID) IS NOT NULL;');
    END;

    /* Positive non-PRM evidence. Never infer a false positive merely from a blank module.
       Current explicit module links are accepted directly. Older general SampleTests links
       are reconstructed only when the affected result, parent Quality Event and general
       sample identity all agree. Ambiguous rows remain fail-closed. */
    CREATE TABLE #ProvenNonPrmLinks
    (
        AffectedResultID INT NOT NULL,
        SampleTestID INT NOT NULL,
        PRIMARY KEY(AffectedResultID,SampleTestID)
    );

    INSERT #ProvenNonPrmLinks(AffectedResultID,SampleTestID)
    SELECT qear.AffectedResultID,qear.SampleTestID
    FROM dbo.QualityEventAffectedResults qear
    WHERE qear.SampleTestID IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(qear.SourceModule,N''))),N'') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(qear.SourceModule))) NOT IN (N'PRM',N'LEGACY-DUPLICATE');

    INSERT #ProvenNonPrmLinks(AffectedResultID,SampleTestID)
    SELECT qear.AffectedResultID,qear.SampleTestID
    FROM dbo.QualityEventAffectedResults qear
    INNER JOIN dbo.QualityEvents qe ON qe.QualityEventID=qear.QualityEventID
    WHERE qear.SampleTestID IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(qear.SourceModule,N''))),N'') IS NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(qe.SourceModule,N''))),N'') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(qe.SourceModule))) NOT IN (N'PRM',N'LEGACY-DUPLICATE')
      AND NOT EXISTS
      (
          SELECT 1 FROM #ProvenNonPrmLinks p
          WHERE p.AffectedResultID=qear.AffectedResultID AND p.SampleTestID=qear.SampleTestID
      );

    INSERT #ProvenNonPrmLinks(AffectedResultID,SampleTestID)
    SELECT qear.AffectedResultID,qear.SampleTestID
    FROM dbo.QualityEventAffectedResults qear
    INNER JOIN dbo.QualityEvents qe ON qe.QualityEventID=qear.QualityEventID
    INNER JOIN dbo.SampleTests generalTest ON generalTest.SampleTestID=qear.SampleTestID
    INNER JOIN dbo.Samples generalSample ON generalSample.SampleID=generalTest.SampleID
    WHERE qear.SampleTestID IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(qear.SourceModule,N''))),N'') IS NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(qe.SourceModule,N''))),N'') IS NULL
      AND qe.SampleID=generalSample.SampleID
      AND NULLIF(LTRIM(RTRIM(ISNULL(qe.SampleNumber,N''))),N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(generalSample.SampleNumber,N''))),N'') IS NOT NULL
      AND UPPER(LTRIM(RTRIM(qe.SampleNumber)))=UPPER(LTRIM(RTRIM(generalSample.SampleNumber)))
      AND NOT EXISTS
      (
          SELECT 1 FROM #ProvenNonPrmLinks p
          WHERE p.AffectedResultID=qear.AffectedResultID AND p.SampleTestID=qear.SampleTestID
      );

    /* v212 evidence is immutable. If its legacy SampleTestID-only detector classified a
       numerically-colliding non-PRM affected result as PRM evidence, preserve the v212 row
       and append a correction record rather than rewriting history. */
    IF OBJECT_ID(N'dbo.PRM_TimingQELegacyLinkCorrections', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_TimingQELegacyLinkCorrections
        (
            CorrectionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_TimingQELegacyLinkCorrections PRIMARY KEY,
            SampleID INT NOT NULL,
            TimingMigrationHistoryID INT NOT NULL,
            OriginalDisposition NVARCHAR(80) NULL,
            CorrectedDisposition NVARCHAR(80) NOT NULL,
            CorrectionReason NVARCHAR(1000) NOT NULL,
            CorrectedAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_TimingQELegacyLinkCorrections_CorrectedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_PRM_TimingQELegacyLinkCorrections_Sample FOREIGN KEY(SampleID) REFERENCES dbo.PRM_Samples(SampleID),
            CONSTRAINT FK_PRM_TimingQELegacyLinkCorrections_History FOREIGN KEY(TimingMigrationHistoryID) REFERENCES dbo.PRM_TimingMigrationHistory(TimingMigrationHistoryID)
        );
        CREATE UNIQUE INDEX UX_PRM_TimingQELegacyLinkCorrections_Sample
            ON dbo.PRM_TimingQELegacyLinkCorrections(SampleID);
    END;

    DECLARE @CorrectedSamples TABLE
    (
        SampleID INT NOT NULL PRIMARY KEY
    );

    ;WITH FalsePositive AS
    (
        SELECT
            h.SampleID,
            h.TimingMigrationHistoryID,
            h.ReconciliationDisposition
        FROM dbo.PRM_TimingMigrationHistory h WITH(UPDLOCK,HOLDLOCK)
        INNER JOIN dbo.PRM_Samples s WITH(UPDLOCK,HOLDLOCK) ON s.SampleID=h.SampleID
        WHERE h.HasIssuedCertificate=0
          AND h.HasControlledQualityEventEvidence=1
          AND UPPER(LTRIM(RTRIM(ISNULL(s.TimingReconciliationStatus,N''))))=N'HISTORICAL CLOSED'
          AND UPPER(LTRIM(RTRIM(ISNULL(s.SampleStatus,N''))))<>N'CERTIFICATE ISSUED'
          AND NOT EXISTS
          (
              SELECT 1 FROM dbo.PRM_Certificates c WHERE c.SampleID=h.SampleID
          )
          /* A correction requires positive non-PRM provenance for the collision. If any
             matching SampleTestID-only affected row is still ambiguous, keep the sample
             Historical Closed rather than risk rewriting genuine Quality Event evidence. */
          AND EXISTS
          (
              SELECT 1
              FROM #ProvenNonPrmLinks proven
              INNER JOIN dbo.PRM_SampleTests st ON st.SampleID=h.SampleID
              WHERE proven.SampleTestID=st.SampleTestID
          )
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.QualityEventAffectedResults qear
              INNER JOIN dbo.PRM_SampleTests st ON st.SampleID=h.SampleID
              WHERE qear.SampleTestID=st.SampleTestID
                AND NOT EXISTS
                (
                    SELECT 1 FROM #ProvenNonPrmLinks proven
                    WHERE proven.AffectedResultID=qear.AffectedResultID
                      AND proven.SampleTestID=st.SampleTestID
                )
                AND NOT EXISTS
                (
                    SELECT 1 FROM #LegacyPrmLinks legacyLink
                    WHERE legacyLink.AffectedResultID=qear.AffectedResultID
                      AND legacyLink.SampleTestID=st.SampleTestID
                )
                AND UPPER(LTRIM(RTRIM(ISNULL(qear.SourceModule,N''))))<>N'PRM'
          )
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.QualityEventAffectedResults qear
              INNER JOIN dbo.PRM_SampleTests st ON st.SampleID=h.SampleID
              WHERE
              (
                  UPPER(LTRIM(RTRIM(ISNULL(qear.SourceModule,N''))))=N'PRM'
                  AND
                  (
                      qear.SourceResultID=st.SampleTestID
                      OR qear.SampleTestID=st.SampleTestID
                  )
              )
              OR EXISTS
              (
                  SELECT 1
                  FROM #LegacyPrmLinks legacyLink
                  WHERE legacyLink.AffectedResultID=qear.AffectedResultID
                    AND legacyLink.SampleTestID=st.SampleTestID
              )
          )
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.PRM_TimingQELegacyLinkCorrections c
              WHERE c.SampleID=h.SampleID
          )
    )
    INSERT dbo.PRM_TimingQELegacyLinkCorrections
    (
        SampleID, TimingMigrationHistoryID, OriginalDisposition,
        CorrectedDisposition, CorrectionReason
    )
    OUTPUT inserted.SampleID INTO @CorrectedSamples(SampleID)
    SELECT
        f.SampleID,
        f.TimingMigrationHistoryID,
        f.ReconciliationDisposition,
        N'False legacy QE link removed - controlled timing reconciliation resumed',
        N'v213 correction: the v212 legacy SampleTestID-only detector matched an affected-result numeric ID with positive non-PRM provenance. No matching PRM or ambiguous legacy affected-result evidence remains for this PRM SampleTestID; immutable v212 evidence is retained and the active sample is returned to controlled timing reconciliation.'
    FROM FalsePositive f;

    /* Reapply the same fail-closed operational disposition v212 would have used if the
       false PRM Quality Event classification had not occurred. Original invalid result
       evidence remains preserved in PRM_TimingMigrationTestEvidence. */
    UPDATE t
    SET ResultValue=NULL,
        Interpretation=N'Pending',
        Remarks=NULL,
        EnteredBy=NULL,
        EnteredDate=NULL
    FROM dbo.PRM_SampleTests t
    INNER JOIN dbo.PRM_TimingMigrationTestEvidence e ON e.SampleTestID=t.SampleTestID
    INNER JOIN @CorrectedSamples c ON c.SampleID=t.SampleID;

    UPDATE s
    SET SampleStatus=N'In Progress',
        ReportStatus=N'Results Entered',
        ReviewedBy=NULL,
        ReviewedDate=NULL,
        ApprovedBy=NULL,
        ApprovedDate=NULL,
        AnalysisCompletedDate=NULL,
        AnalysisStartedDate=CASE WHEN h.AnalysisStartProvenanceIssue IS NOT NULL THEN NULL ELSE s.AnalysisStartedDate END,
        TimingReconciliationStatus=CASE WHEN EXISTS
        (
            SELECT 1 FROM dbo.PRM_TimingMigrationTestEvidence e WHERE e.SampleID=s.SampleID
        ) THEN N'Required' ELSE N'Not Required' END,
        TimingReconciledBy=NULL,
        TimingReconciledAt=NULL,
        TimingReconciliationReason=NULL,
        ModifiedBy=N'System Migration 20260906_002',
        ModifiedDate=SYSDATETIME()
    FROM dbo.PRM_Samples s
    INNER JOIN dbo.PRM_TimingMigrationHistory h ON h.SampleID=s.SampleID
    INNER JOIN @CorrectedSamples c ON c.SampleID=s.SampleID;

    EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_PRM_TimingQELegacyLinkCorrections_AppendOnly_20260906
ON dbo.PRM_TimingQELegacyLinkCorrections
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 54125, ''PRM timing Quality Event legacy-link correction history is append-only.'', 1;
END;');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260906_002' AS MigrationVersion;
