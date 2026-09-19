SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       Compile-safe schema prerequisite for 20260906_001.
       Existing governance tables are completed before the legacy migration is
       compiled. Tables owned entirely by 20260906_001 are left for that migration
       to create when absent, preserving its original constraints and indexes.
    */
    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 54420, 'Required table dbo.PRM_Samples is missing before 20260906_000.', 1;
    IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') IS NULL
        THROW 54421, 'Required table dbo.CultureMediaQualificationRequirements is missing before 20260906_000.', 1;

    IF COL_LENGTH(N'dbo.PRM_Samples', N'TimingReconciliationStatus') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_Samples ADD TimingReconciliationStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_PRM_Samples_TimingReconciliationStatus_20260906 DEFAULT N''Not Required'';';
    IF COL_LENGTH(N'dbo.PRM_Samples', N'TimingReconciledBy') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_Samples ADD TimingReconciledBy NVARCHAR(120) NULL;';
    IF COL_LENGTH(N'dbo.PRM_Samples', N'TimingReconciledAt') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_Samples ADD TimingReconciledAt DATETIME2(0) NULL;';
    IF COL_LENGTH(N'dbo.PRM_Samples', N'TimingReconciliationReason') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_Samples ADD TimingReconciliationReason NVARCHAR(1000) NULL;';

    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'TimingConfirmedMinimumIncubationHours') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD TimingConfirmedMinimumIncubationHours DECIMAL(9,2) NULL;';
    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'TimingConfirmedBy') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD TimingConfirmedBy NVARCHAR(100) NULL;';
    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'TimingConfirmedAt') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD TimingConfirmedAt DATETIME2(0) NULL;';

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory', N'PreviousAnalysisStartedDate') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_TimingMigrationHistory ADD PreviousAnalysisStartedDate DATETIME2(0) NULL;';
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory', N'AnalysisStartSignatureAt') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_TimingMigrationHistory ADD AnalysisStartSignatureAt DATETIME2(0) NULL;';
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory', N'AnalysisStartProvenanceIssue') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_TimingMigrationHistory ADD AnalysisStartProvenanceIssue NVARCHAR(300) NULL;';
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory', N'HasControlledQualityEventEvidence') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_TimingMigrationHistory ADD HasControlledQualityEventEvidence BIT NOT NULL CONSTRAINT DF_PRM_TimingMigrationHistory_HasQE_20260906 DEFAULT(0) WITH VALUES;';
    END;

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence', N'AnalysisStartSignatureAt') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_TimingMigrationTestEvidence ADD AnalysisStartSignatureAt DATETIME2(0) NULL;';
        IF COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence', N'AnalysisStartProvenanceIssue') IS NULL
            EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_TimingMigrationTestEvidence ADD AnalysisStartProvenanceIssue NVARCHAR(300) NULL;';
    END;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'TimingReconciliationStatus'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>60 OR is_nullable<>0)
    )
        THROW 54422, 'Existing dbo.PRM_Samples.TimingReconciliationStatus has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'TimingReconciledBy'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>240)
    )
        THROW 54423, 'Existing dbo.PRM_Samples.TimingReconciledBy has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'TimingReconciledAt'
          AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0)
    )
        THROW 54424, 'Existing dbo.PRM_Samples.TimingReconciledAt has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'TimingReconciliationReason'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>2000)
    )
        THROW 54425, 'Existing dbo.PRM_Samples.TimingReconciliationReason has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements') AND name=N'TimingConfirmedMinimumIncubationHours'
          AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2)
    )
        THROW 54426, 'Existing Culture Media confirmed incubation timing has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements') AND name=N'TimingConfirmedBy'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>200)
    )
        THROW 54427, 'Existing Culture Media TimingConfirmedBy has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements') AND name=N'TimingConfirmedAt'
          AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0)
    )
        THROW 54428, 'Existing Culture Media TimingConfirmedAt has an incompatible schema.', 1;

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory', N'U') IS NOT NULL
       AND EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
             AND name IN (N'PreviousAnalysisStartedDate',N'AnalysisStartSignatureAt')
             AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0)
       )
        THROW 54429, 'Existing PRM timing migration history timestamps have an incompatible schema.', 1;

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory', N'U') IS NOT NULL
       AND EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
             AND name=N'AnalysisStartProvenanceIssue'
             AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>600)
       )
        THROW 54430, 'Existing PRM timing migration history provenance text has an incompatible schema.', 1;

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory', N'U') IS NOT NULL
       AND EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
             AND name=N'HasControlledQualityEventEvidence'
             AND (system_type_id<>TYPE_ID(N'bit') OR is_nullable<>0)
       )
        THROW 54431, 'Existing PRM timing migration history Quality Event evidence flag has an incompatible schema.', 1;

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence', N'U') IS NOT NULL
       AND EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence')
             AND name=N'AnalysisStartSignatureAt'
             AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0)
       )
        THROW 54432, 'Existing PRM timing migration test evidence signature timestamp has an incompatible schema.', 1;

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence', N'U') IS NOT NULL
       AND EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence')
             AND name=N'AnalysisStartProvenanceIssue'
             AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>600)
       )
        THROW 54433, 'Existing PRM timing migration test evidence provenance text has an incompatible schema.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260906_000' AS MigrationVersion;
