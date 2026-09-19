SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Certificates', N'U') IS NULL
        THROW 51300, 'Required table dbo.Certificates does not exist.', 1;

    IF OBJECT_ID(N'dbo.LIMS_NumberSequences', N'U') IS NULL
        THROW 51301, 'Required table dbo.LIMS_NumberSequences does not exist.', 1;

    DECLARE @CertificateMap TABLE
    (
        CertificateID INT NOT NULL PRIMARY KEY,
        OldCertificateNumber NVARCHAR(100) NOT NULL,
        TemporaryCertificateNumber NVARCHAR(100) NOT NULL,
        NewCertificateNumber NVARCHAR(100) NOT NULL
    );

    INSERT INTO @CertificateMap
        (CertificateID, OldCertificateNumber, TemporaryCertificateNumber, NewCertificateNumber)
    SELECT
        CertificateID,
        CertificateNumber,
        N'TEMP-COA-' + CONVERT(NVARCHAR(20), CertificateID),
        N'COA-' + CONVERT(CHAR(8), CAST(IssueDate AS DATE), 112) + N'-' +
            RIGHT(N'0000' + CONVERT(NVARCHAR(20), CertificateID), 4)
    FROM dbo.Certificates WITH (UPDLOCK, HOLDLOCK);

    /* Move to collision-free temporary values before assigning the final sequence. */
    UPDATE c
    SET c.CertificateNumber = m.TemporaryCertificateNumber,
        c.VerificationCode = m.TemporaryCertificateNumber
    FROM dbo.Certificates c
    INNER JOIN @CertificateMap m ON m.CertificateID = c.CertificateID;

    IF OBJECT_ID(N'dbo.CertificateLifecycleAudit', N'U') IS NOT NULL
    BEGIN
        UPDATE a
        SET a.CertificateNumber = m.NewCertificateNumber
        FROM dbo.CertificateLifecycleAudit a
        INNER JOIN @CertificateMap m ON a.CertificateID = m.CertificateID;
    END;

    IF OBJECT_ID(N'dbo.CertificatePrintHistory', N'U') IS NOT NULL
    BEGIN
        UPDATE p
        SET p.CertificateNumber = m.NewCertificateNumber
        FROM dbo.CertificatePrintHistory p
        INNER JOIN @CertificateMap m ON p.CertificateID = m.CertificateID;
    END;

    UPDATE c
    SET c.CertificateNumber = m.NewCertificateNumber,
        c.VerificationCode = m.NewCertificateNumber,
        c.ReportHash = CONVERT(VARCHAR(64), HASHBYTES(
            'SHA2_256',
            CONCAT(
                m.NewCertificateNumber, N'|', c.SampleID, N'|', ISNULL(c.SampleNumber, N''), N'|',
                CONVERT(NVARCHAR(33), c.IssueDate, 126), N'|', ISNULL(c.IssuedBy, N'')
            )), 2)
    FROM dbo.Certificates c
    INNER JOIN @CertificateMap m ON m.CertificateID = c.CertificateID;

    DELETE FROM dbo.LIMS_NumberSequences
    WHERE NumberKey = N'COA'
       OR SequenceKey LIKE N'COA-%';

    INSERT INTO dbo.LIMS_NumberSequences
        (NumberKey, YearNo, LastNumber, Prefix, CreatedAt, UpdatedAt, SequenceKey)
    SELECT
        N'COA',
        YEAR(GETDATE()),
        ISNULL(MAX(CertificateID), 0),
        N'COA',
        GETDATE(),
        GETDATE(),
        N'COA-GLOBAL'
    FROM dbo.Certificates;

    COMMIT TRANSACTION;

    SELECT
        CertificateID,
        CertificateNumber,
        SampleNumber,
        IssueDate,
        RevisionNo,
        CertificateStatus,
        IsCancelled
    FROM dbo.Certificates
    ORDER BY CertificateID;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
