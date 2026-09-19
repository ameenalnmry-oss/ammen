SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
        THROW 52200, 'Required schema version table dbo.LIMS_SchemaVersions is missing.', 1;
    IF OBJECT_ID(N'dbo.CultureMediaStockTransactions', N'U') IS NULL
        THROW 52201, 'Run the culture-media compliance migration before this repair.', 1;
    IF OBJECT_ID(N'dbo.EM_PlanSamples', N'U') IS NULL
        THROW 52202, 'Run the EM planning migration before this repair.', 1;
    IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NULL
        THROW 52203, 'Required table dbo.EM_Events is missing.', 1;

    IF COL_LENGTH(N'dbo.CultureMediaStockTransactions', N'BalanceBeforeG') IS NULL
        ALTER TABLE dbo.CultureMediaStockTransactions ADD BalanceBeforeG DECIMAL(18,3) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples', N'MediaPreparationID') IS NULL
        ALTER TABLE dbo.EM_PlanSamples ADD MediaPreparationID INT NULL;
    IF COL_LENGTH(N'dbo.EM_Events', N'MediaPreparationID') IS NULL
        ALTER TABLE dbo.EM_Events ADD MediaPreparationID INT NULL;

    IF OBJECT_ID(N'dbo.MediaPreparations', N'U') IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1 FROM sys.foreign_keys
           WHERE name = N'FK_EM_PlanSamples_MediaPreparation'
             AND parent_object_id = OBJECT_ID(N'dbo.EM_PlanSamples')
       )
        EXEC sys.sp_executesql N'
        ALTER TABLE dbo.EM_PlanSamples WITH NOCHECK
        ADD CONSTRAINT FK_EM_PlanSamples_MediaPreparation
        FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID);';

    IF OBJECT_ID(N'dbo.MediaPreparations', N'U') IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1 FROM sys.foreign_keys
           WHERE name = N'FK_EM_Events_MediaPreparation'
             AND parent_object_id = OBJECT_ID(N'dbo.EM_Events')
       )
        EXEC sys.sp_executesql N'
        ALTER TABLE dbo.EM_Events WITH CHECK
        ADD CONSTRAINT FK_EM_Events_MediaPreparation
        FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID);';

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE name = N'IX_EM_Events_MediaPreparationID'
          AND object_id = OBJECT_ID(N'dbo.EM_Events')
    )
        EXEC sys.sp_executesql N'
        CREATE INDEX IX_EM_Events_MediaPreparationID
        ON dbo.EM_Events(MediaPreparationID, EventDate DESC)
        WHERE MediaPreparationID IS NOT NULL;';

    IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260722_001')
        INSERT dbo.LIMS_SchemaVersions(VersionKey, Description)
        VALUES(N'20260722_001', N'Startup migration compile-safety repair for newly added EM and culture-media columns.');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
