SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       Compile-safe schema prerequisite for 20260905_001.
       This migration commits the additive timing columns before the legacy
       checksum-controlled migration is compiled. It intentionally performs no
       DML or CHECK-constraint work that references the new columns directly.
    */
    IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
        THROW 54400, 'Required table dbo.PRM_SpecificationTests is missing before 20260905_000.', 1;
    IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
        THROW 54401, 'Required table dbo.PRM_SampleTests is missing before 20260905_000.', 1;
    IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') IS NULL
        THROW 54402, 'Required table dbo.CultureMediaQualificationRequirements is missing before 20260905_000.', 1;
    IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NULL
        THROW 54403, 'Required table dbo.MediaQualifications is missing before 20260905_000.', 1;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'MinimumElapsedHours') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_SpecificationTests ADD MinimumElapsedHours DECIMAL(9,2) NULL;';

    IF COL_LENGTH(N'dbo.PRM_SampleTests', N'MinimumElapsedHours') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_SampleTests ADD MinimumElapsedHours DECIMAL(9,2) NULL;';

    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'MinimumIncubationHours') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD MinimumIncubationHours DECIMAL(9,2) NULL;';

    IF COL_LENGTH(N'dbo.MediaQualifications', N'QualificationStartedAt') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.MediaQualifications ADD QualificationStartedAt DATETIME2(0) NULL;';

    IF COL_LENGTH(N'dbo.MediaQualifications', N'MinimumIncubationHoursSnapshot') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.MediaQualifications ADD MinimumIncubationHoursSnapshot DECIMAL(9,2) NULL;';

    IF COL_LENGTH(N'dbo.MediaQualifications', N'IncubationCompletedAt') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.MediaQualifications ADD IncubationCompletedAt DATETIME2(0) NULL;';

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name=N'MinimumElapsedHours'
          AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2)
    )
        THROW 54404, 'Existing dbo.PRM_SpecificationTests.MinimumElapsedHours has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
          AND name=N'MinimumElapsedHours'
          AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2)
    )
        THROW 54405, 'Existing dbo.PRM_SampleTests.MinimumElapsedHours has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'MinimumIncubationHours'
          AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2)
    )
        THROW 54406, 'Existing dbo.CultureMediaQualificationRequirements.MinimumIncubationHours has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications')
          AND name=N'MinimumIncubationHoursSnapshot'
          AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2)
    )
        THROW 54407, 'Existing dbo.MediaQualifications.MinimumIncubationHoursSnapshot has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications')
          AND name IN (N'QualificationStartedAt',N'IncubationCompletedAt')
          AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0)
    )
        THROW 54408, 'Existing dbo.MediaQualifications timing timestamps have an incompatible schema.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260905_000' AS MigrationVersion;
