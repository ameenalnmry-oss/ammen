SET NOCOUNT ON;

DECLARE @Problems TABLE(Problem NVARCHAR(500) NOT NULL);

IF OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations', N'U') IS NULL
    INSERT @Problems VALUES(N'Missing dbo.PRM_QualityEventEvidenceReconciliations.');
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationID') IS NULL
        INSERT @Problems VALUES(N'Missing reconciliation identity.');
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'LegacyQualityEventID') IS NULL
        INSERT @Problems VALUES(N'Missing legacy Quality Event link.');
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReplacementQualityEventID') IS NULL
        INSERT @Problems VALUES(N'Missing replacement Quality Event link.');
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'SampleID') IS NULL
        INSERT @Problems VALUES(N'Missing PRM sample link.');
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ElectronicSignatureID') IS NULL
        INSERT @Problems VALUES(N'Missing electronic-signature link.');
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationSchemaVersion') IS NULL
        INSERT @Problems VALUES(N'Missing reconciliation schema version.');

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.triggers
        WHERE parent_id=OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations')
          AND name=N'TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002'
          AND is_disabled=0
    )
        INSERT @Problems VALUES(N'Immutable reconciliation trigger is missing or disabled.');

    IF EXISTS
    (
        SELECT 1
        FROM dbo.PRM_QualityEventEvidenceReconciliations rec
        LEFT JOIN dbo.QualityEvents legacyEvent
          ON legacyEvent.QualityEventID=rec.LegacyQualityEventID
        LEFT JOIN dbo.QualityEvents replacementEvent
          ON replacementEvent.QualityEventID=rec.ReplacementQualityEventID
        LEFT JOIN dbo.PRM_ElectronicSignatures signatureEvidence
          ON signatureEvidence.SignatureID=rec.ElectronicSignatureID
        WHERE rec.ReconciliationSchemaVersion<>1
           OR legacyEvent.QualityEventID IS NULL
           OR replacementEvent.QualityEventID IS NULL
           OR signatureEvidence.SignatureID IS NULL
           OR rec.LegacyQualityEventID=rec.ReplacementQualityEventID
           OR UPPER(LTRIM(RTRIM(ISNULL(legacyEvent.SourceModule,N''))))<>N'PRM'
           OR UPPER(LTRIM(RTRIM(ISNULL(replacementEvent.SourceModule,N''))))<>N'PRM'
           OR legacyEvent.SourceRecordID<>rec.SampleID
           OR replacementEvent.SourceRecordID<>rec.SampleID
           OR signatureEvidence.SampleID<>rec.SampleID
    )
        INSERT @Problems VALUES(N'Invalid PRM evidence reconciliation relationship detected.');
END;

IF EXISTS(SELECT 1 FROM @Problems)
BEGIN
    SELECT Problem FROM @Problems ORDER BY Problem;
    THROW 53725, 'PRM legacy evidence reconciliation verification failed.', 1;
END;

SELECT N'PASS' AS VerificationStatus;
