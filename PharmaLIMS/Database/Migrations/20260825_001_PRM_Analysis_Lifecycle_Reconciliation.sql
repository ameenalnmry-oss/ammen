SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Additive PRM analysis-lifecycle reconciliation for legacy Development
    databases. This migration deliberately does not seed or modify
    specification master data.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 53500, 'Required table dbo.PRM_Samples is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_Samples', N'AnalysisStartedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.PRM_Samples ADD AnalysisStartedDate DATETIME2(0) NULL;');

    IF COL_LENGTH(N'dbo.PRM_Samples', N'AnalysisCompletedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.PRM_Samples ADD AnalysisCompletedDate DATETIME2(0) NULL;');

    EXEC sys.sp_executesql N'
IF OBJECT_ID(N''dbo.PRM_ElectronicSignatures'', N''U'') IS NOT NULL
BEGIN
    UPDATE sample
    SET AnalysisStartedDate = signatureDate.SignedAt
    FROM dbo.PRM_Samples sample
    CROSS APPLY
    (
        SELECT MIN(signature.SignedAt) SignedAt
        FROM dbo.PRM_ElectronicSignatures signature
        WHERE signature.SampleID = sample.SampleID
          AND signature.ActionType = N''Analysis Start''
    ) signatureDate
    WHERE sample.AnalysisStartedDate IS NULL
      AND signatureDate.SignedAt IS NOT NULL;

    UPDATE sample
    SET AnalysisCompletedDate = signatureDate.SignedAt
    FROM dbo.PRM_Samples sample
    CROSS APPLY
    (
        SELECT MAX(signature.SignedAt) SignedAt
        FROM dbo.PRM_ElectronicSignatures signature
        WHERE signature.SampleID = sample.SampleID
          AND signature.ActionType = N''Result Entry''
    ) signatureDate
    WHERE sample.AnalysisCompletedDate IS NULL
      AND signatureDate.SignedAt IS NOT NULL
      AND sample.SampleStatus IN
          (N''Results Entered'',N''Under Review'',N''Reviewed'',N''Approved'',N''Certificate Issued'');
END;';

    IF COL_LENGTH(N'dbo.PRM_Samples', N'AnalysisStartedDate') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Samples', N'AnalysisCompletedDate') IS NULL
        THROW 53501, 'PRM analysis lifecycle reconciliation is incomplete.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260825_001' AS MigrationVersion;
