SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /* PRM: controlled minimum elapsed time is owned by the approved specification
       and frozen into each assigned sample-test row. */
    IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
        THROW 54000, 'Required table dbo.PRM_SpecificationTests is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
        THROW 54001, 'Required table dbo.PRM_SampleTests is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'MinimumElapsedHours') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD MinimumElapsedHours DECIMAL(9,2) NULL;
    IF COL_LENGTH(N'dbo.PRM_SampleTests', N'MinimumElapsedHours') IS NULL
        ALTER TABLE dbo.PRM_SampleTests ADD MinimumElapsedHours DECIMAL(9,2) NULL;

    /* Existing PRM profiles in this Microbiology module receive a conservative
       controlled baseline. New/revised profiles can define the exact approved
       minimum per method/test before QA activation. */
    UPDATE dbo.PRM_SpecificationTests
    SET MinimumElapsedHours =
        CASE
            WHEN UPPER(LTRIM(RTRIM(ISNULL(TestCode,N'')))) = N'TAMC' THEN CONVERT(DECIMAL(9,2),72.00)
            ELSE CONVERT(DECIMAL(9,2),120.00)
        END
    WHERE MinimumElapsedHours IS NULL;

    UPDATE sampleTest
    SET MinimumElapsedHours = COALESCE(
        specificationTest.MinimumElapsedHours,
        CASE
            WHEN UPPER(LTRIM(RTRIM(ISNULL(sampleTest.TestCode,N'')))) = N'TAMC' THEN CONVERT(DECIMAL(9,2),72.00)
            ELSE CONVERT(DECIMAL(9,2),120.00)
        END)
    FROM dbo.PRM_SampleTests sampleTest
    LEFT JOIN dbo.PRM_SpecificationTests specificationTest
      ON specificationTest.SpecificationTestID = sampleTest.SourceSpecificationTestID
    WHERE sampleTest.MinimumElapsedHours IS NULL;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name = N'CK_PRM_SpecificationTests_MinimumElapsedHours_20260905'
    )
    BEGIN
        ALTER TABLE dbo.PRM_SpecificationTests WITH CHECK
        ADD CONSTRAINT CK_PRM_SpecificationTests_MinimumElapsedHours_20260905
            CHECK (MinimumElapsedHours IS NULL OR MinimumElapsedHours >= 0);
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.PRM_SampleTests')
          AND name = N'CK_PRM_SampleTests_MinimumElapsedHours_20260905'
    )
    BEGIN
        ALTER TABLE dbo.PRM_SampleTests WITH CHECK
        ADD CONSTRAINT CK_PRM_SampleTests_MinimumElapsedHours_20260905
            CHECK (MinimumElapsedHours IS NULL OR MinimumElapsedHours >= 0);
    END;

    /* Culture Media: explicit qualification start plus a frozen approved
       incubation-duration snapshot prevents same-session GPT release. */
    IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NULL
        THROW 54002, 'Required table dbo.MediaQualifications is missing.', 1;
    IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') IS NULL
        THROW 54003, 'Required table dbo.CultureMediaQualificationRequirements is missing.', 1;

    IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'MinimumIncubationHours') IS NULL
        ALTER TABLE dbo.CultureMediaQualificationRequirements ADD MinimumIncubationHours DECIMAL(9,2) NULL;

    IF COL_LENGTH(N'dbo.MediaQualifications', N'QualificationStartedAt') IS NULL
        ALTER TABLE dbo.MediaQualifications ADD QualificationStartedAt DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.MediaQualifications', N'MinimumIncubationHoursSnapshot') IS NULL
        ALTER TABLE dbo.MediaQualifications ADD MinimumIncubationHoursSnapshot DECIMAL(9,2) NULL;
    IF COL_LENGTH(N'dbo.MediaQualifications', N'IncubationCompletedAt') IS NULL
        ALTER TABLE dbo.MediaQualifications ADD IncubationCompletedAt DATETIME2(0) NULL;

    UPDATE dbo.CultureMediaQualificationRequirements
    SET MinimumIncubationHours =
        CASE
            WHEN UPPER(LTRIM(RTRIM(TestName))) = N'PH CHECK' THEN CONVERT(DECIMAL(9,2),0.00)
            WHEN UPPER(LTRIM(RTRIM(TestName))) = N'PREINCUBATION CHECK' THEN CONVERT(DECIMAL(9,2),24.00)
            ELSE CONVERT(DECIMAL(9,2),120.00)
        END
    WHERE MinimumIncubationHours IS NULL;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name = N'CK_CultureMediaQualificationRequirements_MinHours_20260905'
    )
    BEGIN
        ALTER TABLE dbo.CultureMediaQualificationRequirements WITH CHECK
        ADD CONSTRAINT CK_CultureMediaQualificationRequirements_MinHours_20260905
            CHECK (MinimumIncubationHours IS NULL OR MinimumIncubationHours >= 0);
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.MediaQualifications')
          AND name = N'CK_MediaQualifications_MinHoursSnapshot_20260905'
    )
    BEGIN
        ALTER TABLE dbo.MediaQualifications WITH CHECK
        ADD CONSTRAINT CK_MediaQualifications_MinHoursSnapshot_20260905
            CHECK (MinimumIncubationHoursSnapshot IS NULL OR MinimumIncubationHoursSnapshot >= 0);
    END;

    /* Historical completed records are not re-authored. Their timing fields stay
       NULL unless a controlled start/completion record already existed. Runtime
       gates apply to new In Progress qualifications created by this release. */

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260905_001' AS MigrationVersion;
