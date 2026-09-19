SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.EM_Events',N'U') IS NULL OR OBJECT_ID(N'dbo.EM_Areas',N'U') IS NULL
        THROW 53610, 'Required EM event/area tables are missing before 20260830_003.', 1;

    IF COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot') IS NULL
        ALTER TABLE dbo.EM_Events ADD AreaCodeSnapshot NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot') IS NULL
        ALTER TABLE dbo.EM_Events ADD AreaNameSnapshot NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.EM_Events',N'GradeSnapshot') IS NULL
        ALTER TABLE dbo.EM_Events ADD GradeSnapshot NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.EM_Events',N'AreaSnapshotSource') IS NULL
        ALTER TABLE dbo.EM_Events ADD AreaSnapshotSource NVARCHAR(160) NULL;

    /* One-time legacy reconciliation: current master identity is captured and then frozen.
       Source is explicitly marked so the trend report never presents this as native historical evidence. */
    UPDATE eventRecord
       SET AreaCodeSnapshot = COALESCE(NULLIF(eventRecord.AreaCodeSnapshot,N''),area.AreaCode),
           AreaNameSnapshot = COALESCE(NULLIF(eventRecord.AreaNameSnapshot,N''),area.AreaName),
           GradeSnapshot = COALESCE(NULLIF(eventRecord.GradeSnapshot,N''),area.Grade),
           AreaSnapshotSource = COALESCE(NULLIF(eventRecord.AreaSnapshotSource,N''),N'Legacy current-master reconciliation - verify historical identity')
    FROM dbo.EM_Events eventRecord
    INNER JOIN dbo.EM_Areas area ON area.Id=eventRecord.AreaId
    WHERE NULLIF(eventRecord.AreaCodeSnapshot,N'') IS NULL
       OR NULLIF(eventRecord.AreaNameSnapshot,N'') IS NULL
       OR NULLIF(eventRecord.AreaSnapshotSource,N'') IS NULL;

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
            THROW 53611, ''EM historical area snapshots are immutable. Correct historical identity only through a controlled reconciliation release.'', 1;
    END;';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
