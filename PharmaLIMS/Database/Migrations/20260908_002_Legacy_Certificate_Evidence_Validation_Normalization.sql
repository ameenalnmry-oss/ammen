SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations',N'U') IS NULL
       OR OBJECT_ID(N'dbo.Certificates',N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_Certificates',N'U') IS NULL
       OR OBJECT_ID(N'dbo.CertificateDocumentSnapshots',N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_CertificateSnapshots',N'U') IS NULL
        THROW 54840, 'Legacy certificate reconciliation schema is incomplete; apply the prior controlled migration first.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.LIMS_SchemaVersions
        WHERE VersionKey=N'20260908_001' AND AppliedAt IS NOT NULL
    )
        THROW 54841, 'Legacy certificate reconciliation prerequisite 20260908_001 is not recorded as applied.', 1;

    /*
       v241 normalization fix:
       - legacy databases may carry certificate issue/status values with a storage shape
         that differs from the reconciliation snapshot column even though the business
         value is the same;
       - validation therefore compares the same normalized values that the application
         reads and stores, while retaining all eligibility, snapshot and chain controls.
       No certificate, report hash or document snapshot data is changed by this migration.
    */
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

        DECLARE @BadPrmCertificate nvarchar(100)=NULL;
        DECLARE @BadPrmReason nvarchar(500)=NULL;

        SELECT TOP(1)
            @BadPrmCertificate=COALESCE(NULLIF(LTRIM(RTRIM(i.CertificateNumberSnapshot)),N''''),CONVERT(nvarchar(20),i.CertificateID)),
            @BadPrmReason=
                CASE
                    WHEN c.CertificateID IS NULL THEN N''source certificate no longer exists''
                    WHEN ISNULL(c.CertificateNumber,N'''')<>ISNULL(i.CertificateNumberSnapshot,N'''') THEN N''certificate number no longer matches the signed snapshot''
                    WHEN ISNULL(c.SampleID,0)<>ISNULL(i.SampleIDSnapshot,0) THEN N''sample identity no longer matches the signed snapshot''
                    WHEN CONVERT(datetime2(0),c.IssueDate)<>CONVERT(datetime2(0),i.IssueDateSnapshot) THEN N''issue date/time no longer matches after controlled second-precision normalization''
                    WHEN UPPER(LTRIM(RTRIM(ISNULL(c.CertificateStatus,N''''))))<>UPPER(LTRIM(RTRIM(ISNULL(i.CertificateStatusSnapshot,N'''')))) THEN N''certificate status no longer matches the signed snapshot''
                    WHEN c.IssueDate>=@SnapshotCutover THEN N''certificate is not pre-cutover legacy evidence''
                    WHEN UPPER(LTRIM(RTRIM(ISNULL(c.CertificateStatus,N''''))))<>N''ACTIVE'' THEN N''certificate is no longer Active''
                    WHEN LEN(LTRIM(RTRIM(ISNULL(c.ReportHash,N''''))))=64
                         AND EXISTS(SELECT 1 FROM dbo.PRM_CertificateSnapshots s WHERE s.CertificateID=c.CertificateID)
                        THEN N''certificate now has complete native hash and issue-snapshot evidence''
                    ELSE N''certificate does not satisfy the controlled legacy-evidence contract''
                END
        FROM inserted i
        LEFT JOIN dbo.PRM_Certificates c ON c.CertificateID=i.CertificateID
        WHERE i.CertificateModule=N''PRM''
          AND
          (
              c.CertificateID IS NULL
              OR ISNULL(c.CertificateNumber,N'''')<>ISNULL(i.CertificateNumberSnapshot,N'''')
              OR ISNULL(c.SampleID,0)<>ISNULL(i.SampleIDSnapshot,0)
              OR CONVERT(datetime2(0),c.IssueDate)<>CONVERT(datetime2(0),i.IssueDateSnapshot)
              OR UPPER(LTRIM(RTRIM(ISNULL(c.CertificateStatus,N''''))))<>UPPER(LTRIM(RTRIM(ISNULL(i.CertificateStatusSnapshot,N''''))))
              OR c.IssueDate>=@SnapshotCutover
              OR UPPER(LTRIM(RTRIM(ISNULL(c.CertificateStatus,N''''))))<>N''ACTIVE''
              OR
              (
                  LEN(LTRIM(RTRIM(ISNULL(c.ReportHash,N''''))))=64
                  AND EXISTS(SELECT 1 FROM dbo.PRM_CertificateSnapshots s WHERE s.CertificateID=c.CertificateID)
              )
          );

        IF @BadPrmCertificate IS NOT NULL
        BEGIN
            DECLARE @PrmMessage nvarchar(2048)=
                N''PRM legacy reconciliation validation failed for certificate '' + @BadPrmCertificate + N'': '' + ISNULL(@BadPrmReason,N''eligibility changed'') + N''.'';
            THROW 54824, @PrmMessage, 1;
        END;

        DECLARE @BadWaterCertificate nvarchar(100)=NULL;
        DECLARE @BadWaterReason nvarchar(500)=NULL;

        SELECT TOP(1)
            @BadWaterCertificate=COALESCE(NULLIF(LTRIM(RTRIM(i.CertificateNumberSnapshot)),N''''),CONVERT(nvarchar(20),i.CertificateID)),
            @BadWaterReason=
                CASE
                    WHEN c.CertificateID IS NULL THEN N''source certificate no longer exists''
                    WHEN ISNULL(c.CertificateNumber,N'''')<>ISNULL(i.CertificateNumberSnapshot,N'''') THEN N''certificate number no longer matches the signed snapshot''
                    WHEN ISNULL(c.SampleID,0)<>ISNULL(i.SampleIDSnapshot,0) THEN N''sample identity no longer matches the signed snapshot''
                    WHEN CONVERT(datetime2(0),c.IssueDate)<>CONVERT(datetime2(0),i.IssueDateSnapshot) THEN N''issue date/time no longer matches after controlled second-precision normalization''
                    WHEN UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N''''),ISNULL(c.Status,N'''')))))
                         <>UPPER(LTRIM(RTRIM(ISNULL(i.CertificateStatusSnapshot,N'''')))) THEN N''certificate status no longer matches the signed snapshot''
                    WHEN c.IssueDate>=@SnapshotCutover THEN N''certificate is not pre-cutover legacy evidence''
                    WHEN UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N''''),ISNULL(c.Status,N''''))))) NOT IN(N''ACTIVE'',N''ISSUED'') THEN N''certificate is no longer Active/Issued''
                    WHEN EXISTS(SELECT 1 FROM dbo.CertificateDocumentSnapshots s WHERE s.CertificateID=c.CertificateID) THEN N''certificate now has a native issue snapshot''
                    ELSE N''certificate does not satisfy the controlled legacy-evidence contract''
                END
        FROM inserted i
        LEFT JOIN dbo.Certificates c ON c.CertificateID=i.CertificateID
        WHERE i.CertificateModule=N''WATER''
          AND
          (
              c.CertificateID IS NULL
              OR ISNULL(c.CertificateNumber,N'''')<>ISNULL(i.CertificateNumberSnapshot,N'''')
              OR ISNULL(c.SampleID,0)<>ISNULL(i.SampleIDSnapshot,0)
              OR CONVERT(datetime2(0),c.IssueDate)<>CONVERT(datetime2(0),i.IssueDateSnapshot)
              OR UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N''''),ISNULL(c.Status,N'''')))))
                 <>UPPER(LTRIM(RTRIM(ISNULL(i.CertificateStatusSnapshot,N''''))))
              OR c.IssueDate>=@SnapshotCutover
              OR UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N''''),ISNULL(c.Status,N''''))))) NOT IN(N''ACTIVE'',N''ISSUED'')
              OR EXISTS(SELECT 1 FROM dbo.CertificateDocumentSnapshots s WHERE s.CertificateID=c.CertificateID)
          );

        IF @BadWaterCertificate IS NOT NULL
        BEGIN
            DECLARE @WaterMessage nvarchar(2048)=
                N''Water/general legacy reconciliation validation failed for certificate '' + @BadWaterCertificate + N'': '' + ISNULL(@BadWaterReason,N''eligibility changed'') + N''.'';
            THROW 54825, @WaterMessage, 1;
        END;

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
