SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations',N'U') IS NULL
       OR OBJECT_ID(N'dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909',N'FN') IS NULL
        THROW 54860, 'Legacy certificate reconciliation evidence-quality prerequisite is incomplete; apply 20260909_000 first.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.LIMS_SchemaVersions
        WHERE VersionKey=N'20260909_000' AND AppliedAt IS NOT NULL
    )
        THROW 54861, 'Legacy certificate evidence-quality prerequisite 20260909_000 is not recorded as applied.', 1;

    /*
       v243 signed-evidence quality hardening:
       - placeholder/non-evidence text is rejected for BOTH legacy-certificate dispositions;
       - CONTROLLED_REISSUE_REQUIRED is fail-closed, but still requires a meaningful
         Evidence Reference, Evidence Summary and Reconciliation Reason;
       - prior signed rows are not changed; weak historical rows remain immutable and
         may be corrected only by a signed superseding reconciliation;
       - certificate eligibility, identity, native snapshot/hash and append-only chain
         controls remain unchanged.
       No issued certificate, report hash, native snapshot, result or prior reconciliation is modified.
    */

    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_LegacyCertificateEvidenceReconciliations_EvidenceQuality_20260909
    ON dbo.LegacyCertificateEvidenceReconciliations
    AFTER INSERT
    AS
    BEGIN
        SET NOCOUNT ON;

        DECLARE @BadCertificate nvarchar(100)=NULL;
        DECLARE @BadField nvarchar(100)=NULL;
        DECLARE @BadDisposition nvarchar(80)=NULL;

        SELECT TOP(1)
            @BadCertificate=COALESCE(NULLIF(LTRIM(RTRIM(i.CertificateNumberSnapshot)),N''''),i.CertificateModule+N'' ID ''+CONVERT(nvarchar(20),i.CertificateID)),
            @BadDisposition=ISNULL(NULLIF(LTRIM(RTRIM(i.Disposition)),N''''),N''(blank)''),
            @BadField=
                CASE
                    WHEN dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(i.EvidenceReference)=1 THEN N''Controlled Evidence Reference''
                    WHEN dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(i.EvidenceSummary)=1 THEN N''Evidence Summary''
                    ELSE N''Reconciliation Reason''
                END
        FROM inserted i
        WHERE dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(i.EvidenceReference)=1
           OR dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(i.EvidenceSummary)=1
           OR dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(i.Reason)=1;

        IF @BadCertificate IS NOT NULL
        BEGIN
            DECLARE @Message nvarchar(2048)=
                N''Legacy-certificate reconciliation rejected for certificate '' + @BadCertificate +
                N'' (Disposition '' + ISNULL(@BadDisposition,N''(blank)'') + N''): '' +
                ISNULL(@BadField,N''evidence field'') +
                N'' contains placeholder/non-evidence text. Every signed Retain or Reissue disposition must document the actual reviewed record/condition and the QA reason.'';
            THROW 54862, @Message, 1;
        END;
    END;';

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.triggers
        WHERE parent_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
          AND name=N'TRG_LegacyCertificateEvidenceReconciliations_EvidenceQuality_20260909'
          AND is_disabled=0
    )
        THROW 54863, 'Legacy-certificate evidence-quality trigger was not created/enabled.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
