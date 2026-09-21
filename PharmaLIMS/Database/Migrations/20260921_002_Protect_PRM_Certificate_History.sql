SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Protect PRM certificate lifecycle history as append-only evidence.

    PRM_CertificateHistory records controlled issuance, cancellation and reissue
    lifecycle actions. Existing rows must never be rewritten or deleted; new
    lifecycle facts are recorded as additional rows.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_CertificateHistory', N'U') IS NULL
        THROW 55230, 'Required table dbo.PRM_CertificateHistory is missing before 20260921_002.', 1;

    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_PRM_CertificateHistory_AppendOnly_20260921
ON dbo.PRM_CertificateHistory
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- PRM certificate lifecycle history is append-only and cannot be updated or deleted
    THROW 55231, ''PRM certificate lifecycle history is append-only and cannot be updated or deleted. Record a new controlled lifecycle action instead.'', 1;
END;';

    ENABLE TRIGGER dbo.TRG_PRM_CertificateHistory_AppendOnly_20260921
        ON dbo.PRM_CertificateHistory;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
