SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       v223 correction for the historical 20260906_002 false-legacy-QE branch.

       20260906_002 intentionally remains byte-for-byte unchanged for migration
       ledger compatibility. Its controlled correction branch returned affected
       samples to SampleStatus='In Progress' but assigned ReportStatus='Results Entered'.
       In the PRM model, 'Results Entered' is a sample workflow state; an unissued
       report must use ReportStatus='Not Issued'.

       Only the exact unprogressed state produced by 002 is repaired. Samples that
       have subsequently progressed, been reviewed/approved, or acquired a
       certificate are not rewritten.
    */

    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_TimingQELegacyLinkCorrections', N'U') IS NULL
       OR OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NULL
        THROW 54482, 'Required PRM timing correction tables are missing before 20260906_002A.', 1;

    IF COL_LENGTH(N'dbo.PRM_Samples',N'SampleStatus') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Samples',N'ReportStatus') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Samples',N'ReviewedBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Samples',N'ApprovedBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Samples',N'ModifiedBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Samples',N'ModifiedDate') IS NULL
       OR COL_LENGTH(N'dbo.PRM_TimingQELegacyLinkCorrections',N'SampleID') IS NULL
        THROW 54483, 'PRM report-status correction schema contract is incomplete.', 1;

    UPDATE s
    SET ReportStatus=N'Not Issued',
        ModifiedBy=N'System Migration 20260906_002A',
        ModifiedDate=SYSDATETIME()
    FROM dbo.PRM_Samples s
    INNER JOIN dbo.PRM_TimingQELegacyLinkCorrections c ON c.SampleID=s.SampleID
    WHERE UPPER(LTRIM(RTRIM(ISNULL(s.SampleStatus,N''))))=N'IN PROGRESS'
      AND UPPER(LTRIM(RTRIM(ISNULL(s.ReportStatus,N''))))=N'RESULTS ENTERED'
      AND s.ReviewedBy IS NULL
      AND s.ApprovedBy IS NULL
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.PRM_Certificates cert
          WHERE cert.SampleID=s.SampleID
      );

    IF EXISTS
    (
        SELECT 1
        FROM dbo.PRM_Samples s
        INNER JOIN dbo.PRM_TimingQELegacyLinkCorrections c ON c.SampleID=s.SampleID
        WHERE UPPER(LTRIM(RTRIM(ISNULL(s.SampleStatus,N''))))=N'IN PROGRESS'
          AND UPPER(LTRIM(RTRIM(ISNULL(s.ReportStatus,N''))))=N'RESULTS ENTERED'
          AND s.ReviewedBy IS NULL
          AND s.ApprovedBy IS NULL
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.PRM_Certificates cert
              WHERE cert.SampleID=s.SampleID
          )
    )
        THROW 54484, 'PRM report-status repair did not eliminate the historical In Progress / Results Entered mismatch.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260906_002A' AS MigrationVersion;
