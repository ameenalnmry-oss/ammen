SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
  External Trend read-performance hardening.

  Scope:
    - ExternalTrendImportBatches / ExternalTrendImportRows read paths used by
      ExternalTrendThreeCycleService.
    - Effective-dated EMTrendAreaProfiles lookup.

  This migration changes indexes only. It does not update imported observations,
  approval data, audit data, review snapshots, or Internal Trend records.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') IS NULL
        THROW 51120, 'ExternalTrendImportBatches is missing. Apply the controlled External Trend baseline migrations first.', 1;

    IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NULL
        THROW 51121, 'ExternalTrendImportRows is missing. Apply the controlled External Trend baseline migrations first.', 1;

    IF OBJECT_ID(N'dbo.EMTrendAreaProfiles', N'U') IS NULL
        THROW 51122, 'EMTrendAreaProfiles is missing. Apply migration 20260811_002 first.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportBatches')
          AND name = N'IX_ExternalTrendImportBatches_ReviewCutoff'
    )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportBatches_ReviewCutoff
            ON dbo.ExternalTrendImportBatches(ModuleName, Status, ApprovedAt, ImportBatchID)
            INCLUDE (ImportNumber, SourceSystem, FileHashSha256, [RowCount]);
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'IX_ExternalTrendImportRows_AreaCatalog'
    )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportRows_AreaCatalog
            ON dbo.ExternalTrendImportRows(EntityCode, ImportBatchID)
            INCLUDE (EntityName, LocationName, AreaClassification);
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'IX_ExternalTrendImportRows_MethodParameterLookup'
    )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportRows_MethodParameterLookup
            ON dbo.ExternalTrendImportRows(ImportBatchID, EntityCode, MethodName, ParameterName, UnitName)
            INCLUDE (RecordDateTime);
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'IX_ExternalTrendImportRows_SeriesLookupV2'
    )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportRows_SeriesLookupV2
            ON dbo.ExternalTrendImportRows
               (ImportBatchID, EntityCode, MethodName, ParameterName, UnitName, RecordDateTime)
            INCLUDE
               (SourceRowNumber, ResultValue, ResultQualifier, AlertLimit, ActionLimit,
                ResultStatus, SourceRecordID, Remarks);
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.EMTrendAreaProfiles')
          AND name = N'IX_EMTrendAreaProfiles_EffectiveLookup'
    )
    BEGIN
        CREATE INDEX IX_EMTrendAreaProfiles_EffectiveLookup
            ON dbo.EMTrendAreaProfiles(AreaCode, EffectiveFrom DESC, EMTrendAreaProfileID DESC)
            INCLUDE (EffectiveTo, TrendPopulation, AreaClassification, IsComparable);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT
    N'20260816_001' AS MigrationVersion,
    OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') AS ExternalTrendImportBatchesObjectID,
    OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') AS ExternalTrendImportRowsObjectID,
    OBJECT_ID(N'dbo.EMTrendAreaProfiles', N'U') AS EMTrendAreaProfilesObjectID;
