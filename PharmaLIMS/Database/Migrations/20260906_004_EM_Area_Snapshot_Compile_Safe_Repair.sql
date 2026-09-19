SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.EM_Events',N'U') IS NULL
        THROW 54240, 'Required table dbo.EM_Events is missing before 20260906_004.', 1;
    IF OBJECT_ID(N'dbo.EM_Areas',N'U') IS NULL
        THROW 54241, 'Required table dbo.EM_Areas is missing before 20260906_004.', 1;

    /*
       Compile-safe replacement for retired migration 20260906_003.
       New columns are created in independent dynamic statements. Every later
       statement that references those columns is also compiled dynamically,
       after the columns exist. This avoids SQL Server error 207 on databases
       with EM historical-context schema drift.
    */
    IF COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ADD AreaCodeSnapshot NVARCHAR(100) NULL;';
    IF COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ADD AreaNameSnapshot NVARCHAR(200) NULL;';
    IF COL_LENGTH(N'dbo.EM_Events',N'GradeSnapshot') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ADD GradeSnapshot NVARCHAR(100) NULL;';
    IF COL_LENGTH(N'dbo.EM_Events',N'AreaSnapshotSource') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ADD AreaSnapshotSource NVARCHAR(160) NULL;';

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.EM_Events')
          AND name=N'AreaCodeSnapshot'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>200)
    )
        THROW 54242, 'Existing dbo.EM_Events.AreaCodeSnapshot has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.EM_Events')
          AND name=N'AreaNameSnapshot'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>400)
    )
        THROW 54243, 'Existing dbo.EM_Events.AreaNameSnapshot has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.EM_Events')
          AND name=N'GradeSnapshot'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>200)
    )
        THROW 54244, 'Existing dbo.EM_Events.GradeSnapshot has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.EM_Events')
          AND name=N'AreaSnapshotSource'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>320)
    )
        THROW 54245, 'Existing dbo.EM_Events.AreaSnapshotSource has an incompatible schema.', 1;

    /* If an older immutable-snapshot trigger survived the drift, suspend it
       only inside this transaction so the controlled missing-value backfill
       can complete. It is recreated and explicitly enabled below. */
    IF OBJECT_ID(N'dbo.TRG_EM_Events_ProtectAreaSnapshot_20260830',N'TR') IS NOT NULL
        DISABLE TRIGGER dbo.TRG_EM_Events_ProtectAreaSnapshot_20260830 ON dbo.EM_Events;

    EXEC sys.sp_executesql N'
UPDATE eventRecord
   SET AreaCodeSnapshot = COALESCE(NULLIF(eventRecord.AreaCodeSnapshot,N''''), area.AreaCode),
       AreaNameSnapshot = COALESCE(NULLIF(eventRecord.AreaNameSnapshot,N''''), area.AreaName),
       GradeSnapshot = COALESCE(NULLIF(eventRecord.GradeSnapshot,N''''), area.Grade),
       AreaSnapshotSource = COALESCE(
           NULLIF(eventRecord.AreaSnapshotSource,N''''),
           N''Legacy current-master reconciliation - verify historical identity'')
FROM dbo.EM_Events eventRecord
INNER JOIN dbo.EM_Areas area ON area.Id = eventRecord.AreaId
WHERE NULLIF(eventRecord.AreaCodeSnapshot,N'''') IS NULL
   OR NULLIF(eventRecord.AreaNameSnapshot,N'''') IS NULL
   OR NULLIF(eventRecord.GradeSnapshot,N'''') IS NULL
   OR NULLIF(eventRecord.AreaSnapshotSource,N'''') IS NULL;';

    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_EM_Events_ProtectAreaSnapshot_20260830
ON dbo.EM_Events
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN deleted d ON d.Id=i.Id
        WHERE ISNULL(i.AreaCodeSnapshot,N''<NULL>'')<>ISNULL(d.AreaCodeSnapshot,N''<NULL>'')
           OR ISNULL(i.AreaNameSnapshot,N''<NULL>'')<>ISNULL(d.AreaNameSnapshot,N''<NULL>'')
           OR ISNULL(i.GradeSnapshot,N''<NULL>'')<>ISNULL(d.GradeSnapshot,N''<NULL>'')
           OR ISNULL(i.AreaSnapshotSource,N''<NULL>'')<>ISNULL(d.AreaSnapshotSource,N''<NULL>'')
    )
        THROW 54231, ''EM historical area snapshots are immutable. Correct historical identity only through a controlled reconciliation release.'', 1;
END;';

    ENABLE TRIGGER dbo.TRG_EM_Events_ProtectAreaSnapshot_20260830 ON dbo.EM_Events;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
