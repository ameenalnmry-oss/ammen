SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
  Controlled master data for area-by-area Environmental Monitoring trending.

  Default policy requested by site:
    - Production is analysed as ISO 8 Production.
    - All other areas are Unclassified by default.
    - Dispensing/Weighing and Sampling are controlled exceptions and require QA
      classification before they are included in a comparable trend population.

  No imported observation is changed.  The profile is prospective, effective-dated
  master data and can be reviewed independently from raw source results.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.EMTrendAreaProfiles', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EMTrendAreaProfiles
        (
            EMTrendAreaProfileID INT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_EMTrendAreaProfiles PRIMARY KEY,
            AreaCode NVARCHAR(150) NOT NULL,
            AreaName NVARCHAR(300) NULL,
            TrendPopulation NVARCHAR(40) NOT NULL,
            AreaClassification NVARCHAR(30) NOT NULL,
            IsComparable BIT NOT NULL CONSTRAINT DF_EMTrendAreaProfiles_IsComparable DEFAULT (1),
            EffectiveFrom DATE NOT NULL,
            EffectiveTo DATE NULL,
            QAComment NVARCHAR(1000) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_EMTrendAreaProfiles_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CreatedBy NVARCHAR(128) NOT NULL CONSTRAINT DF_EMTrendAreaProfiles_CreatedBy DEFAULT (SUSER_SNAME()),
            CONSTRAINT CK_EMTrendAreaProfiles_Population CHECK
            (TrendPopulation IN (N'ISO 8 Production', N'Unclassified', N'Dispensing / Weighing', N'Sampling')),
            CONSTRAINT CK_EMTrendAreaProfiles_Classification CHECK
            (AreaClassification IN (N'Classified', N'Unclassified', N'QA Classification Required')),
            CONSTRAINT CK_EMTrendAreaProfiles_EffectiveDates CHECK
            (EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom)
        );

        CREATE UNIQUE INDEX UX_EMTrendAreaProfiles_ActiveArea
            ON dbo.EMTrendAreaProfiles(AreaCode, EffectiveFrom)
            WHERE EffectiveTo IS NULL;
    END;

    IF OBJECT_ID(N'dbo.EMTrendReviewSnapshots', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EMTrendReviewSnapshots
        (
            EMTrendReviewSnapshotID BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_EMTrendReviewSnapshots PRIMARY KEY,
            AreaCode NVARCHAR(150) NOT NULL,
            ParameterName NVARCHAR(300) NOT NULL,
            Period1Start DATE NOT NULL,
            Period1End DATE NOT NULL,
            Period2Start DATE NOT NULL,
            Period2End DATE NOT NULL,
            Period3Start DATE NOT NULL,
            Period3End DATE NOT NULL,
            SummaryJson NVARCHAR(MAX) NOT NULL,
            ReviewerComment NVARCHAR(2000) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_EMTrendReviewSnapshots_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CreatedBy NVARCHAR(128) NOT NULL CONSTRAINT DF_EMTrendReviewSnapshots_CreatedBy DEFAULT (SUSER_SNAME()),
            CONSTRAINT CK_EMTrendReviewSnapshots_Dates CHECK
            (Period1End >= Period1Start AND Period2End >= Period2Start AND Period3End >= Period3Start)
        );
    END;

    IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NOT NULL
       AND NOT EXISTS
       (
          SELECT 1 FROM sys.indexes
          WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
            AND name = N'IX_ExternalTrendImportRows_AreaParameterDate'
       )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportRows_AreaParameterDate
            ON dbo.ExternalTrendImportRows(EntityCode, ParameterName, RecordDateTime)
            INCLUDE (ImportBatchID, ResultValue, UnitName, AlertLimit, ActionLimit, AreaClassification);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260811_002' AS MigrationVersion;
