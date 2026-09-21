/*
    PharmaLIMS GMP workflow verification (read-only)
    Run after Database/Migrations/20260714_001_Harden_GMP_Workflows.sql.
    Any reported row is a release blocker.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Failures TABLE
(
    CheckName NVARCHAR(160) NOT NULL,
    Details NVARCHAR(1000) NOT NULL
);

;WITH RequiredTables AS
(
    SELECT TableName
    FROM (VALUES
        (N'PRM_NumberSequences'),
        (N'PRM_Samples'),
        (N'PRM_SpecificationTests'),
        (N'PRM_SampleTests'),
        (N'PRM_ElectronicSignatures'),
        (N'PRM_Certificates'),
        (N'QualityEvents'),
        (N'QualityEventAffectedResults'),
        (N'EM_EventSignatures'),
        (N'CultureMediaSopFields'),
        (N'CultureMediaVisualChecks'),
        (N'CultureMediaSignatures')
    ) required(TableName)
)
INSERT INTO @Failures (CheckName, Details)
SELECT N'Required table', N'Missing dbo.' + TableName
FROM RequiredTables
WHERE OBJECT_ID(N'dbo.' + TableName, N'U') IS NULL;

;WITH RequiredColumns AS
(
    SELECT TableName, ColumnName
    FROM (VALUES
        (N'PRM_Samples', N'SampleStatus'),
        (N'PRM_Samples', N'ResultInterpretation'),
        (N'PRM_Samples', N'ReportStatus'),
        (N'PRM_SpecificationTests', N'ApprovalStatus'),
        (N'PRM_SpecificationTests', N'ApprovedBy'),
        (N'PRM_SpecificationTests', N'ApprovedDate'),
        (N'PRM_SpecificationTests', N'IsActive'),
        (N'PRM_SampleTests', N'TestCode'),
        (N'PRM_SampleTests', N'RequiredTest'),
        (N'PRM_ElectronicSignatures', N'ActionType'),
        (N'PRM_ElectronicSignatures', N'SignedBy'),
        (N'PRM_ElectronicSignatures', N'MeaningOfSignature'),
        (N'PRM_ElectronicSignatures', N'ActionReason'),
        (N'PRM_Certificates', N'CertificateStatus'),
        (N'PRM_Certificates', N'ReportHash'),
        (N'QualityEvents', N'SourceModule'),
        (N'QualityEvents', N'SourceRecordID'),
        (N'QualityEvents', N'CurrentStatus'),
        (N'EM_Events', N'ResultsEnteredBy'),
        (N'EM_Events', N'ResultsEnteredDate'),
        (N'EM_Events', N'ReviewedBy'),
        (N'EM_Events', N'ReviewedDate'),
        (N'EM_Events', N'ApprovedBy'),
        (N'EM_Events', N'ApprovedDate')
    ) required(TableName, ColumnName)
)
INSERT INTO @Failures (CheckName, Details)
SELECT N'Required column', N'Missing dbo.' + TableName + N'.' + ColumnName
FROM RequiredColumns
WHERE OBJECT_ID(N'dbo.' + TableName, N'U') IS NULL
   OR COL_LENGTH(N'dbo.' + TableName, ColumnName) IS NULL;

IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.PRM_Certificates')
          AND name = N'UX_PRM_Certificates_OneActivePerSample'
          AND is_unique = 1
          AND has_filter = 1
    )
        INSERT INTO @Failures VALUES
        (
            N'Certificate uniqueness',
            N'Missing filtered unique index UX_PRM_Certificates_OneActivePerSample.'
        );

    INSERT INTO @Failures (CheckName, Details)
    SELECT
        N'Certificate uniqueness',
        N'SampleID ' + CONVERT(NVARCHAR(20), SampleID) + N' has ' + CONVERT(NVARCHAR(20), COUNT(*)) + N' active certificates.'
    FROM dbo.PRM_Certificates
    WHERE CertificateStatus = N'Active'
    GROUP BY SampleID
    HAVING COUNT(*) > 1;

    INSERT INTO @Failures (CheckName, Details)
    SELECT
        N'Certificate hash',
        N'Active certificate ' + ISNULL(CertificateNumber, N'(unknown)') + N' has no SHA-256 report hash.'
    FROM dbo.PRM_Certificates
    WHERE CertificateStatus = N'Active'
      AND (ReportHash IS NULL OR LEN(ReportHash) <> 64);
END;

IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NOT NULL
BEGIN
    INSERT INTO @Failures (CheckName, Details)
    SELECT
        N'Specification approval',
        N'Approved active test ' + SpecificationNo + N'/' + TestCode + N' has incomplete approval evidence.'
    FROM dbo.PRM_SpecificationTests
    WHERE ApprovalStatus = N'Approved'
      AND IsActive = 1
      AND (ApprovedBy IS NULL OR ApprovedDate IS NULL OR EffectiveDate IS NULL);
END;

IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.PRM_ElectronicSignatures', N'U') IS NOT NULL
BEGIN
    INSERT INTO @Failures (CheckName, Details)
    SELECT
        N'Certificate signature chain',
        N'Active certificate ' + ISNULL(c.CertificateNumber, N'(unknown)') + N' has no issuance/reissue electronic signature.'
    FROM dbo.PRM_Certificates c
    WHERE c.CertificateStatus = N'Active'
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.PRM_ElectronicSignatures sig
          WHERE sig.SampleID = c.SampleID
            AND sig.ActionType IN (N'Certificate Issuance', N'Certificate Reissue')
      );
END;

SELECT CheckName, Details
FROM @Failures
ORDER BY CheckName, Details;

IF EXISTS (SELECT 1 FROM @Failures)
    THROW 51020, 'GMP workflow verification failed. Review the result set above.', 1;

SELECT N'PASS' AS VerificationResult,
       N'GMP workflow schema and data invariants passed.' AS Details;

