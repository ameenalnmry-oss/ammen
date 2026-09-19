SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS 2026.8.27.184
    Explicit reconciliation of historical PRM investigation evidence.

    Background:
    - Migration 20260827_001 intentionally marks pre-v183 affected-result evidence
      as EvidenceSchemaVersion = 0 because historical rows cannot be proven to
      contain the complete v183 immutable result/specification snapshot.
    - v184 does NOT rewrite those historical rows.
    - A historical event can be superseded only by an explicit, signed relation
      to a later closed controlled investigation that captures version-1 evidence.

    Reconciliation rows are append-only.  Runtime gates revalidate the replacement
    investigation against the CURRENT PRM result/specification snapshot, so a later
    data change invalidates the reconciliation automatically without deleting history.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
        THROW 53720, 'Required table dbo.QualityEvents is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
        THROW 53721, 'Required table dbo.QualityEventAffectedResults is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_ElectronicSignatures', N'U') IS NULL
        THROW 53722, 'Required table dbo.PRM_ElectronicSignatures is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 53723, 'Required table dbo.PRM_Samples is missing.', 1;

    IF OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_QualityEventEvidenceReconciliations
        (
            ReconciliationID INT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_PRM_QEEvidenceReconciliations_20260827_002 PRIMARY KEY,
            LegacyQualityEventID INT NOT NULL,
            ReplacementQualityEventID INT NOT NULL,
            SampleID INT NOT NULL,
            ReconciledBy NVARCHAR(120) NOT NULL,
            ReconciledAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_PRM_QEEvidenceReconciliations_Date_20260827_002 DEFAULT SYSDATETIME(),
            ReconciliationReason NVARCHAR(1000) NOT NULL,
            ElectronicSignatureID INT NOT NULL,
            ReconciliationSchemaVersion TINYINT NOT NULL
                CONSTRAINT DF_PRM_QEEvidenceReconciliations_Version_20260827_002 DEFAULT (1),
            CONSTRAINT CK_PRM_QEEvidenceReconciliations_DifferentEvents_20260827_002
                CHECK (LegacyQualityEventID <> ReplacementQualityEventID),
            CONSTRAINT CK_PRM_QEEvidenceReconciliations_Version_20260827_002
                CHECK (ReconciliationSchemaVersion = 1),
            CONSTRAINT FK_PRM_QEEvidenceReconciliations_LegacyEvent_20260827_002
                FOREIGN KEY (LegacyQualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT FK_PRM_QEEvidenceReconciliations_ReplacementEvent_20260827_002
                FOREIGN KEY (ReplacementQualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT FK_PRM_QEEvidenceReconciliations_Sample_20260827_002
                FOREIGN KEY (SampleID) REFERENCES dbo.PRM_Samples(SampleID),
            CONSTRAINT FK_PRM_QEEvidenceReconciliations_Signature_20260827_002
                FOREIGN KEY (ElectronicSignatureID) REFERENCES dbo.PRM_ElectronicSignatures(SignatureID)
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations')
          AND name = N'UX_PRM_QEEvidenceReconciliation_Pair_20260827_002'
    )
    BEGIN
        CREATE UNIQUE INDEX UX_PRM_QEEvidenceReconciliation_Pair_20260827_002
            ON dbo.PRM_QualityEventEvidenceReconciliations
               (LegacyQualityEventID, ReplacementQualityEventID);
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations')
          AND name = N'IX_PRM_QEEvidenceReconciliation_Sample_20260827_002'
    )
    BEGIN
        CREATE INDEX IX_PRM_QEEvidenceReconciliation_Sample_20260827_002
            ON dbo.PRM_QualityEventEvidenceReconciliations
               (SampleID, LegacyQualityEventID, ReplacementQualityEventID)
            INCLUDE (ElectronicSignatureID, ReconciledAt, ReconciliationSchemaVersion);
    END;

    IF OBJECT_ID(N'dbo.TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002', N'TR') IS NULL
    BEGIN
        EXEC(N'
CREATE TRIGGER dbo.TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002
ON dbo.PRM_QualityEventEvidenceReconciliations
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 53724, ''PRM evidence reconciliation records are immutable. Create a later controlled reconciliation instead of updating or deleting history.'', 1;
END;');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
