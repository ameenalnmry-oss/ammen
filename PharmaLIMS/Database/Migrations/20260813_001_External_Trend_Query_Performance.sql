SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
  Performance indexes for the controlled external-trend review screen.
  They support only approved-batch, area/method/parameter/date reads and do
  not change any imported result, approval, audit, or signature record.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportBatches')
             AND name = N'IX_ExternalTrendImportBatches_ModuleStatus'
       )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportBatches_ModuleStatus
            ON dbo.ExternalTrendImportBatches(ModuleName, Status, ImportBatchID);
    END;

    IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
             AND name = N'IX_ExternalTrendImportRows_ReviewLookup'
       )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportRows_ReviewLookup
            ON dbo.ExternalTrendImportRows
               (ImportBatchID, EntityCode, MethodName, ParameterName, RecordDateTime)
            INCLUDE
               (SourceRowNumber, EntityName, AreaClassification, ResultValue, UnitName,
                AlertLimit, ActionLimit, SourceRecordID, Remarks);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260813_001' AS MigrationVersion;
