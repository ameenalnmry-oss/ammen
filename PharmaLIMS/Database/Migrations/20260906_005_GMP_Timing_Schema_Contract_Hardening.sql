SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       v221 timing-schema contract hardening.

       The compile-safe 20260905_000 / 20260906_000 migrations remain byte-for-byte
       unchanged so databases that already recorded v220 can upgrade without checksum
       drift. This migration verifies the complete nullability/type contract and repairs
       only a missing safe default constraint that older drifted databases could have
       omitted while still passing the v220 prerequisite checks.
    */

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
        THROW 54440, 'Required table dbo.PRM_SpecificationTests is missing before 20260906_005.', 1;
    IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
        THROW 54441, 'Required table dbo.PRM_SampleTests is missing before 20260906_005.', 1;
    IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') IS NULL
        THROW 54442, 'Required table dbo.CultureMediaQualificationRequirements is missing before 20260906_005.', 1;
    IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NULL
        THROW 54443, 'Required table dbo.MediaQualifications is missing before 20260906_005.', 1;
    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 54444, 'Required table dbo.PRM_Samples is missing before 20260906_005.', 1;

    /* 20260905_000 contract: all additive timing values are nullable snapshots/settings. */
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name=N'MinimumElapsedHours'
          AND system_type_id=TYPE_ID(N'decimal') AND precision=9 AND scale=2 AND is_nullable=1
    )
        THROW 54445, 'dbo.PRM_SpecificationTests.MinimumElapsedHours must be DECIMAL(9,2) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
          AND name=N'MinimumElapsedHours'
          AND system_type_id=TYPE_ID(N'decimal') AND precision=9 AND scale=2 AND is_nullable=1
    )
        THROW 54446, 'dbo.PRM_SampleTests.MinimumElapsedHours must be DECIMAL(9,2) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'MinimumIncubationHours'
          AND system_type_id=TYPE_ID(N'decimal') AND precision=9 AND scale=2 AND is_nullable=1
    )
        THROW 54447, 'dbo.CultureMediaQualificationRequirements.MinimumIncubationHours must be DECIMAL(9,2) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications')
          AND name=N'QualificationStartedAt'
          AND system_type_id=TYPE_ID(N'datetime2') AND scale=0 AND is_nullable=1
    )
        THROW 54448, 'dbo.MediaQualifications.QualificationStartedAt must be DATETIME2(0) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications')
          AND name=N'MinimumIncubationHoursSnapshot'
          AND system_type_id=TYPE_ID(N'decimal') AND precision=9 AND scale=2 AND is_nullable=1
    )
        THROW 54449, 'dbo.MediaQualifications.MinimumIncubationHoursSnapshot must be DECIMAL(9,2) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications')
          AND name=N'IncubationCompletedAt'
          AND system_type_id=TYPE_ID(N'datetime2') AND scale=0 AND is_nullable=1
    )
        THROW 54450, 'dbo.MediaQualifications.IncubationCompletedAt must be DATETIME2(0) NULL.', 1;

    /* 20260906_000 PRM timing reconciliation contract. */
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'TimingReconciliationStatus'
          AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=60 AND is_nullable=0
    )
        THROW 54451, 'dbo.PRM_Samples.TimingReconciliationStatus must be NVARCHAR(30) NOT NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'TimingReconciledBy'
          AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=240 AND is_nullable=1
    )
        THROW 54452, 'dbo.PRM_Samples.TimingReconciledBy must be NVARCHAR(120) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'TimingReconciledAt'
          AND system_type_id=TYPE_ID(N'datetime2') AND scale=0 AND is_nullable=1
    )
        THROW 54453, 'dbo.PRM_Samples.TimingReconciledAt must be DATETIME2(0) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'TimingReconciliationReason'
          AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=2000 AND is_nullable=1
    )
        THROW 54454, 'dbo.PRM_Samples.TimingReconciliationReason must be NVARCHAR(1000) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'TimingConfirmedMinimumIncubationHours'
          AND system_type_id=TYPE_ID(N'decimal') AND precision=9 AND scale=2 AND is_nullable=1
    )
        THROW 54455, 'Culture Media TimingConfirmedMinimumIncubationHours must be DECIMAL(9,2) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'TimingConfirmedBy'
          AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=200 AND is_nullable=1
    )
        THROW 54456, 'Culture Media TimingConfirmedBy must be NVARCHAR(100) NULL.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'TimingConfirmedAt'
          AND system_type_id=TYPE_ID(N'datetime2') AND scale=0 AND is_nullable=1
    )
        THROW 54457, 'Culture Media TimingConfirmedAt must be DATETIME2(0) NULL.', 1;

    /* A missing default is safe to repair; a conflicting default is blocked for review. */
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c
            ON c.object_id=dc.parent_object_id
           AND c.column_id=dc.parent_column_id
        WHERE dc.parent_object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND c.name=N'TimingReconciliationStatus'
    )
    BEGIN
        ALTER TABLE dbo.PRM_Samples
            ADD CONSTRAINT DF_PRM_Samples_TimingReconciliationStatus_20260906_005
            DEFAULT N'Not Required' FOR TimingReconciliationStatus;
    END
    ELSE IF NOT EXISTS
    (
        SELECT 1
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c
            ON c.object_id=dc.parent_object_id
           AND c.column_id=dc.parent_column_id
        WHERE dc.parent_object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND c.name=N'TimingReconciliationStatus'
          AND UPPER(REPLACE(REPLACE(REPLACE(CONVERT(NVARCHAR(MAX),dc.definition),N'(',N''),N')',N''),N' ',N''))
              IN (N'N''NOTREQUIRED''', N'''NOTREQUIRED''')
    )
        THROW 54458, 'dbo.PRM_Samples.TimingReconciliationStatus has a conflicting default; expected Not Required.', 1;

    /* Optional legacy timing tables, when present, must also honor their v220 contract. */
    IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory', N'U') IS NOT NULL
    BEGIN
        IF NOT EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
              AND name=N'PreviousAnalysisStartedDate'
              AND system_type_id=TYPE_ID(N'datetime2') AND scale=0 AND is_nullable=1
        )
            THROW 54459, 'PRM_TimingMigrationHistory.PreviousAnalysisStartedDate must be DATETIME2(0) NULL.', 1;

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
              AND name=N'AnalysisStartSignatureAt'
              AND system_type_id=TYPE_ID(N'datetime2') AND scale=0 AND is_nullable=1
        )
            THROW 54460, 'PRM_TimingMigrationHistory.AnalysisStartSignatureAt must be DATETIME2(0) NULL.', 1;

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
              AND name=N'AnalysisStartProvenanceIssue'
              AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=600 AND is_nullable=1
        )
            THROW 54461, 'PRM_TimingMigrationHistory.AnalysisStartProvenanceIssue must be NVARCHAR(300) NULL.', 1;

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
              AND name=N'HasControlledQualityEventEvidence'
              AND system_type_id=TYPE_ID(N'bit') AND is_nullable=0
        )
            THROW 54462, 'PRM_TimingMigrationHistory.HasControlledQualityEventEvidence must be BIT NOT NULL.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.default_constraints dc
            INNER JOIN sys.columns c
                ON c.object_id=dc.parent_object_id
               AND c.column_id=dc.parent_column_id
            WHERE dc.parent_object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
              AND c.name=N'HasControlledQualityEventEvidence'
        )
        BEGIN
            ALTER TABLE dbo.PRM_TimingMigrationHistory
                ADD CONSTRAINT DF_PRM_TimingMigrationHistory_HasQE_20260906_005
                DEFAULT(0) FOR HasControlledQualityEventEvidence;
        END
        ELSE IF NOT EXISTS
        (
            SELECT 1
            FROM sys.default_constraints dc
            INNER JOIN sys.columns c
                ON c.object_id=dc.parent_object_id
               AND c.column_id=dc.parent_column_id
            WHERE dc.parent_object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
              AND c.name=N'HasControlledQualityEventEvidence'
              AND REPLACE(REPLACE(REPLACE(CONVERT(NVARCHAR(MAX),dc.definition),N'(',N''),N')',N''),N' ',N'')=N'0'
        )
            THROW 54463, 'PRM_TimingMigrationHistory.HasControlledQualityEventEvidence has a conflicting default; expected 0.', 1;
    END;

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence', N'U') IS NOT NULL
    BEGIN
        IF NOT EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence')
              AND name=N'AnalysisStartSignatureAt'
              AND system_type_id=TYPE_ID(N'datetime2') AND scale=0 AND is_nullable=1
        )
            THROW 54464, 'PRM_TimingMigrationTestEvidence.AnalysisStartSignatureAt must be DATETIME2(0) NULL.', 1;

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence')
              AND name=N'AnalysisStartProvenanceIssue'
              AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=600 AND is_nullable=1
        )
            THROW 54465, 'PRM_TimingMigrationTestEvidence.AnalysisStartProvenanceIssue must be NVARCHAR(300) NULL.', 1;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260906_005' AS MigrationVersion;
