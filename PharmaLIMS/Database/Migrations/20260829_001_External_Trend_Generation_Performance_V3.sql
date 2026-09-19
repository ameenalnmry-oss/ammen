SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
  External Trend generation performance V3.

  Purpose:
    - support the bounded Method + Parameter + RecordDateTime reads used by the
      three-cycle external environmental-monitoring review;
    - avoid full historical scans when All Areas is selected;
    - preserve imported observations, approvals, audit data, signatures and
      previously approved trend snapshots unchanged.

  This migration creates indexes only. No source/result rows are updated.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') IS NULL
        THROW 53820, 'ExternalTrendImportBatches is missing. Apply the controlled External Trend baseline first.', 1;

    IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NULL
        THROW 53821, 'ExternalTrendImportRows is missing. Apply the controlled External Trend baseline first.', 1;

    IF COL_LENGTH(N'dbo.ExternalTrendImportRows', N'ResultQualifier') IS NULL
        THROW 53822, 'ExternalTrendImportRows.ResultQualifier is missing. Apply External Trend current-state reconciliation first.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportBatches')
          AND name = N'IX_ExternalTrendImportBatches_ApprovedEM_20260829'
    )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportBatches_ApprovedEM_20260829
            ON dbo.ExternalTrendImportBatches(ModuleName, Status, ApprovedAt, ImportBatchID)
            INCLUDE (ImportNumber, SourceSystem, OriginalFileName, FileHashSha256, [RowCount]);
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'IX_ExternalTrendImportRows_MethodParameterDate_20260829'
    )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportRows_MethodParameterDate_20260829
            ON dbo.ExternalTrendImportRows(MethodName, ParameterName, RecordDateTime, ImportBatchID)
            INCLUDE
            (
                EntityCode,
                SourceRowNumber,
                ResultValue,
                ResultQualifier,
                UnitName,
                AlertLimit,
                ActionLimit,
                SourceRecordID,
                LocationName,
                ResultStatus
            );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT
    N'20260829_001' AS MigrationVersion,
    CASE WHEN EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'IX_ExternalTrendImportRows_MethodParameterDate_20260829'
    ) THEN 1 ELSE 0 END AS SeriesReadIndexReady,
    CASE WHEN EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportBatches')
          AND name = N'IX_ExternalTrendImportBatches_ApprovedEM_20260829'
    ) THEN 1 ELSE 0 END AS ApprovedBatchIndexReady;
