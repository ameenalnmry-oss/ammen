SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS 2026.8.27.183
    PRM investigation evidence version binding.

    Purpose:
    - Bind each PRM Quality Event affected-result record to the exact numeric
      specification snapshot that existed when the investigation was opened.
    - Mark new evidence rows with a controlled evidence schema version.
    - Leave legacy evidence at version 0 so approval/closure fails closed until
      the evidence is reconciled by a new controlled investigation.

    Historical result values are never rewritten by this migration.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
        THROW 53710, 'Required table dbo.QualityEventAffectedResults is missing.', 1;

    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SpecificationNumericLimit') IS NULL
    BEGIN
        ALTER TABLE dbo.QualityEventAffectedResults
            ADD SpecificationNumericLimit DECIMAL(18,3) NULL;
    END;

    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'EvidenceSchemaVersion') IS NULL
    BEGIN
        ALTER TABLE dbo.QualityEventAffectedResults
            ADD EvidenceSchemaVersion TINYINT NOT NULL
                CONSTRAINT DF_QualityEventAffectedResults_EvidenceSchemaVersion_20260827_001
                DEFAULT (0) WITH VALUES;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name = N'CK_QualityEventAffectedResults_EvidenceSchemaVersion_20260827_001'
    )
    BEGIN
        ALTER TABLE dbo.QualityEventAffectedResults WITH CHECK
            ADD CONSTRAINT CK_QualityEventAffectedResults_EvidenceSchemaVersion_20260827_001
                CHECK (EvidenceSchemaVersion IN (0,1));
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name = N'IX_QEAffected_PRM_EvidenceBinding_20260827_001'
    )
    BEGIN
        CREATE INDEX IX_QEAffected_PRM_EvidenceBinding_20260827_001
            ON dbo.QualityEventAffectedResults(SourceModule, SourceResultID, QualityEventID)
            INCLUDE(TestName, ResultValue, SpecificationLimit, SpecificationNumericLimit,
                    Unit, FailureType, EvidenceSchemaVersion);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
