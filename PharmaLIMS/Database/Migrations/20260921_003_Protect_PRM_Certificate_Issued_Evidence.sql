SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Protect issued PRM certificate records against tampering while preserving
    the controlled Active -> Cancelled lifecycle transition used by PharmaLIMS.

    Immutable issue fields may never be updated after insert.
    Certificate rows may never be deleted.
    The only permitted UPDATE is a one-row controlled cancellation that sets
    CertificateStatus, IsCancelled, CancelledBy, CancelledDate and
    CancellationReason together.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NULL
        THROW 55240, 'Required table dbo.PRM_Certificates is missing before 20260921_003.', 1;

    IF COL_LENGTH(N'dbo.PRM_Certificates', N'CertificateID') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'CertificateNumber') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'SampleID') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'CertificateType') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'ReportTitle') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'IssueDate') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'IssuedBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'CertificateStatus') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'RevisionNo') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'IsCancelled') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'CancelledBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'CancelledDate') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'CancellationReason') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'ReissuedFromCertificateID') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'VerificationCode') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'ReportHash') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'CreatedBy') IS NULL
       OR COL_LENGTH(N'dbo.PRM_Certificates', N'CreatedDate') IS NULL
        THROW 55241, 'dbo.PRM_Certificates does not satisfy the issued-certificate protection contract.', 1;

    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_PRM_Certificates_ProtectIssuedEvidence_20260921
ON dbo.PRM_Certificates
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
        THROW 55242, ''Issued PRM certificate records cannot be deleted. Use the controlled cancellation/reissue workflow.'', 1;

    IF NOT EXISTS (SELECT 1 FROM inserted)
        RETURN;

    IF UPDATE(CertificateID)
       OR UPDATE(CertificateNumber)
       OR UPDATE(SampleID)
       OR UPDATE(CertificateType)
       OR UPDATE(ReportTitle)
       OR UPDATE(IssueDate)
       OR UPDATE(IssuedBy)
       OR UPDATE(RevisionNo)
       OR UPDATE(ReissuedFromCertificateID)
       OR UPDATE(VerificationCode)
       OR UPDATE(ReportHash)
       OR UPDATE(CreatedBy)
       OR UPDATE(CreatedDate)
        THROW 55243, ''Issued PRM certificate identity/document evidence is immutable and cannot be updated.'', 1;

    IF NOT
    (
        UPDATE(CertificateStatus)
        OR UPDATE(IsCancelled)
        OR UPDATE(CancelledBy)
        OR UPDATE(CancelledDate)
        OR UPDATE(CancellationReason)
    )
        THROW 55244, ''Only the controlled PRM certificate cancellation fields may be updated after issuance.'', 1;

    IF (SELECT COUNT_BIG(1) FROM inserted) <> 1
       OR (SELECT COUNT_BIG(1) FROM deleted) <> 1
        THROW 55245, ''Controlled PRM certificate cancellation must affect exactly one issued certificate.'', 1;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN deleted d ON d.CertificateID=i.CertificateID
        WHERE d.CertificateStatus<>N''Active''
           OR ISNULL(d.IsCancelled,0)<>0
           OR i.CertificateStatus<>N''Cancelled''
           OR ISNULL(i.IsCancelled,0)<>1
           OR NULLIF(LTRIM(RTRIM(ISNULL(i.CancelledBy,N''''))),N'''') IS NULL
           OR i.CancelledDate IS NULL
           OR NULLIF(LTRIM(RTRIM(ISNULL(i.CancellationReason,N''''))),N'''') IS NULL
    )
        THROW 55246, ''Only the controlled Active-to-Cancelled PRM certificate transition with complete cancellation evidence is permitted.'', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN deleted d ON d.CertificateID=i.CertificateID
    )
        THROW 55247, ''Controlled PRM certificate cancellation could not bind the updated row to its prior issued record.'', 1;
END;';

    ENABLE TRIGGER dbo.TRG_PRM_Certificates_ProtectIssuedEvidence_20260921
        ON dbo.PRM_Certificates;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
