/*
    PharmaLIMS Runtime System Preflight - read-only SQL companion
    Version family: 2026.8.22.123
    Purpose: independent SQL Server verification of critical runtime invariants.
    This script does not modify schema or regulated data.
*/
SET NOCOUNT ON;

DECLARE @Findings TABLE
(
    Severity nvarchar(20) NOT NULL,
    Area nvarchar(100) NOT NULL,
    Details nvarchar(1000) NOT NULL
);

IF DB_NAME() IS NULL
    INSERT @Findings VALUES (N'BLOCKER', N'Database', N'Database identity could not be resolved.');

;WITH RequiredTables AS
(
    SELECT TableName FROM (VALUES
        (N'Users'), (N'Samples'), (N'SampleTests'), (N'Tests'),
        (N'Certificates'), (N'CertificateDocumentSnapshots'), (N'CertificateLifecycleAudit'),
        (N'AuditTrail'), (N'ElectronicSignatures'), (N'QualityEvents'),
        (N'WaterSamplingPoints'), (N'EM_Events'), (N'EM_EventPlates'), (N'EM_EventSignatures'),
        (N'MediaPreparations'), (N'MediaQualifications'), (N'CultureMediaSignatures'),
        (N'PRM_NumberSequences'), (N'PRM_Samples'), (N'PRM_SampleTests'),
        (N'PRM_ElectronicSignatures'), (N'PRM_Certificates'), (N'PRM_CertificateSnapshots'),
        (N'LIMS_SchemaVersions')
    ) v(TableName)
)
INSERT @Findings(Severity, Area, Details)
SELECT N'BLOCKER', N'Database Schema', N'Missing dbo.' + TableName
FROM RequiredTables
WHERE OBJECT_ID(N'dbo.' + TableName, N'U') IS NULL;

IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
BEGIN
    INSERT @Findings
    SELECT N'BLOCKER', N'Identity', N'Blank username exists on UserID ' + CONVERT(nvarchar(20), UserID)
    FROM dbo.Users
    WHERE NULLIF(LTRIM(RTRIM(Username)), N'') IS NULL;

    INSERT @Findings
    SELECT N'BLOCKER', N'Identity', N'Duplicate normalized username: ' + LOWER(LTRIM(RTRIM(Username)))
    FROM dbo.Users
    WHERE NULLIF(LTRIM(RTRIM(Username)), N'') IS NOT NULL
    GROUP BY LOWER(LTRIM(RTRIM(Username)))
    HAVING COUNT_BIG(*) > 1;
END;

IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NOT NULL
BEGIN
    INSERT @Findings
    SELECT N'BLOCKER', N'PRM Certificate', N'SampleID ' + CONVERT(nvarchar(20), SampleID) + N' has multiple active certificates.'
    FROM dbo.PRM_Certificates
    WHERE UPPER(ISNULL(CertificateStatus, N'')) = N'ACTIVE'
    GROUP BY SampleID
    HAVING COUNT_BIG(*) > 1;

    IF OBJECT_ID(N'dbo.PRM_CertificateSnapshots', N'U') IS NOT NULL
    BEGIN
        INSERT @Findings
        SELECT N'BLOCKER', N'PRM Certificate', N'Active certificate ' + ISNULL(c.CertificateNumber, N'(unknown)') + N' has no immutable snapshot.'
        FROM dbo.PRM_Certificates c
        WHERE UPPER(ISNULL(c.CertificateStatus, N'')) = N'ACTIVE'
          AND NOT EXISTS (SELECT 1 FROM dbo.PRM_CertificateSnapshots s WHERE s.CertificateID = c.CertificateID);
    END;
END;

IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PRM_Samples', N'SampleStatus') IS NOT NULL
   AND COL_LENGTH(N'dbo.PRM_Samples', N'ResultInterpretation') IS NOT NULL
BEGIN
    INSERT @Findings
    SELECT N'BLOCKER', N'PRM Workflow', N'Approved sample ' + ISNULL(SampleNumber, CONVERT(nvarchar(20), SampleID)) + N' is not interpreted as Conforms.'
    FROM dbo.PRM_Samples
    WHERE UPPER(ISNULL(SampleStatus, N'')) = N'APPROVED'
      AND UPPER(ISNULL(ResultInterpretation, N'')) NOT IN (N'CONFORMS', N'PASS');
END;

SELECT Severity, Area, Details
FROM @Findings
ORDER BY CASE Severity WHEN N'BLOCKER' THEN 1 WHEN N'WARNING' THEN 2 ELSE 3 END, Area, Details;

IF NOT EXISTS (SELECT 1 FROM @Findings WHERE Severity = N'BLOCKER')
    SELECT N'PASS' AS VerificationResult, N'No blocker detected by the runtime SQL preflight.' AS Details;
ELSE
    SELECT N'BLOCKED' AS VerificationResult, N'One or more blocker findings require reconciliation before regulated workflow use.' AS Details;
