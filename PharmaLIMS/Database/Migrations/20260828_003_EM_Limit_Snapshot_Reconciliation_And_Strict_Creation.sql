SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.EM_EventPlates',N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_Events',N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_Areas',N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_GradeLimits',N'U') IS NULL
       OR OBJECT_ID(N'dbo.LIMS_SchemaVersions',N'U') IS NULL
        THROW 53540, 'Required EM evidence tables are missing. Apply earlier controlled migrations first.', 1;

    IF COL_LENGTH(N'dbo.EM_EventPlates',N'AlertLimitSnapshot') IS NULL
       OR COL_LENGTH(N'dbo.EM_EventPlates',N'ActionLimitSnapshot') IS NULL
       OR COL_LENGTH(N'dbo.EM_EventPlates',N'ResultUnitSnapshot') IS NULL
       OR COL_LENGTH(N'dbo.EM_EventPlates',N'AirVolumeLitersSnapshot') IS NULL
        THROW 53541, 'EM limit snapshot columns are missing. Apply 20260828_002 before 20260828_003.', 1;

    IF OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations',N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EM_LimitSnapshotReconciliations
        (
            ReconciliationID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_LimitSnapshotReconciliations PRIMARY KEY,
            PlateID INT NOT NULL,
            SupersedesReconciliationID INT NULL,
            AlertLimitSnapshot DECIMAL(18,3) NOT NULL,
            ActionLimitSnapshot DECIMAL(18,3) NOT NULL,
            ResultUnitSnapshot NVARCHAR(30) NOT NULL,
            AirVolumeLitersSnapshot INT NULL,
            EvidenceReference NVARCHAR(500) NOT NULL,
            Reason NVARCHAR(MAX) NOT NULL,
            SignedBy NVARCHAR(100) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_LimitSnapshotReconciliations_SignedAt DEFAULT SYSUTCDATETIME(),
            SourceWorkstation NVARCHAR(200) NULL,
            ReconciliationSchemaVersion TINYINT NOT NULL CONSTRAINT DF_EM_LimitSnapshotReconciliations_SchemaVersion DEFAULT(1),
            CONSTRAINT FK_EM_LimitSnapshotReconciliations_Plate FOREIGN KEY(PlateID) REFERENCES dbo.EM_EventPlates(Id),
            CONSTRAINT FK_EM_LimitSnapshotReconciliations_Supersedes FOREIGN KEY(SupersedesReconciliationID) REFERENCES dbo.EM_LimitSnapshotReconciliations(ReconciliationID),
            CONSTRAINT CK_EM_LimitSnapshotReconciliations_Limits CHECK(AlertLimitSnapshot>=0 AND ActionLimitSnapshot>=AlertLimitSnapshot),
            CONSTRAINT CK_EM_LimitSnapshotReconciliations_Unit CHECK(NULLIF(LTRIM(RTRIM(ResultUnitSnapshot)),N'') IS NOT NULL),
            CONSTRAINT CK_EM_LimitSnapshotReconciliations_Evidence CHECK(NULLIF(LTRIM(RTRIM(EvidenceReference)),N'') IS NOT NULL),
            CONSTRAINT CK_EM_LimitSnapshotReconciliations_Reason CHECK(NULLIF(LTRIM(RTRIM(Reason)),N'') IS NOT NULL),
            CONSTRAINT CK_EM_LimitSnapshotReconciliations_SignedBy CHECK(NULLIF(LTRIM(RTRIM(SignedBy)),N'') IS NOT NULL),
            CONSTRAINT CK_EM_LimitSnapshotReconciliations_Meaning CHECK(NULLIF(LTRIM(RTRIM(MeaningOfSignature)),N'') IS NOT NULL),
            CONSTRAINT CK_EM_LimitSnapshotReconciliations_SchemaVersion CHECK(ReconciliationSchemaVersion=1)
        );

        CREATE INDEX IX_EM_LimitSnapshotReconciliations_Plate
            ON dbo.EM_LimitSnapshotReconciliations(PlateID,ReconciliationID DESC);
    END;

    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828
    ON dbo.EM_LimitSnapshotReconciliations
    AFTER INSERT, UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;

        IF EXISTS(SELECT 1 FROM deleted)
            THROW 53542, ''EM historical limit reconciliations are append-only. Record a signed superseding reconciliation instead of editing or deleting evidence.'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM inserted i
            INNER JOIN dbo.EM_EventPlates p ON p.Id=i.PlateID
            WHERE p.AlertLimitSnapshot IS NOT NULL
              AND p.ActionLimitSnapshot IS NOT NULL
              AND NULLIF(LTRIM(RTRIM(ISNULL(p.ResultUnitSnapshot,N''''))),N'''') IS NOT NULL
              AND (UPPER(LTRIM(RTRIM(p.Method)))<>N''ACTIVE AIR SAMPLING'' OR ISNULL(p.AirVolumeLitersSnapshot,0)>0)
        )
            THROW 53543, ''A native frozen EM snapshot already exists for this plate. Historical reconciliation is not permitted.'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM inserted i
            INNER JOIN dbo.EM_EventPlates p ON p.Id=i.PlateID
            WHERE UPPER(LTRIM(RTRIM(p.Method)))=N''ACTIVE AIR SAMPLING''
              AND ISNULL(i.AirVolumeLitersSnapshot,0)<=0
        )
            THROW 53544, ''Active Air historical reconciliation requires a positive frozen air-volume value.'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM inserted i
            INNER JOIN dbo.EM_LimitSnapshotReconciliations prior ON prior.ReconciliationID=i.SupersedesReconciliationID
            WHERE prior.PlateID<>i.PlateID
        )
            THROW 53545, ''A reconciliation may supersede only prior evidence for the same EM plate.'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM inserted i
            OUTER APPLY
            (
                SELECT TOP(1) prior.ReconciliationID
                FROM dbo.EM_LimitSnapshotReconciliations prior WITH(UPDLOCK,HOLDLOCK)
                WHERE prior.PlateID=i.PlateID
                  AND prior.ReconciliationID<>i.ReconciliationID
                ORDER BY prior.ReconciliationID DESC
            ) latestPrior
            WHERE (latestPrior.ReconciliationID IS NULL AND i.SupersedesReconciliationID IS NOT NULL)
               OR (latestPrior.ReconciliationID IS NOT NULL AND ISNULL(i.SupersedesReconciliationID,0)<>latestPrior.ReconciliationID)
        )
            THROW 53546, ''A new EM reconciliation must explicitly supersede the latest prior reconciliation for that plate.'', 1;
    END;';

    /* New EM plates must obtain a complete native snapshot at creation time. If the
       approved Grade/Method limit is missing, the whole plate INSERT is rolled back. */
    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_EM_EventPlates_FreezeLimits_20260828
    ON dbo.EM_EventPlates
    AFTER INSERT
    AS
    BEGIN
        SET NOCOUNT ON;

        UPDATE plate
        SET
            /* Never trust caller-supplied limit evidence for a newly created plate. The
               frozen snapshot is derived only from the current approved Grade/Method master. */
            AlertLimitSnapshot = approvedLimit.AlertLimitTotal,
            ActionLimitSnapshot = approvedLimit.ActionLimitTotal,
            ResultUnitSnapshot = CASE
                WHEN UPPER(LTRIM(RTRIM(plate.Method))) = N''ACTIVE AIR SAMPLING'' THEN N''CFU/m3''
                WHEN UPPER(LTRIM(RTRIM(plate.Method))) IN(N''SETTLE PLATE'',N''CONTACT PLATE'') THEN N''CFU/plate''
                WHEN UPPER(LTRIM(RTRIM(plate.Method))) = N''SURFACE SWAB'' THEN N''CFU/swab''
                WHEN UPPER(LTRIM(RTRIM(plate.Method))) = N''PERSONNEL MONITORING'' THEN N''CFU/glove''
                ELSE N''CFU''
            END,
            AirVolumeLitersSnapshot = CASE
                WHEN UPPER(LTRIM(RTRIM(plate.Method))) = N''ACTIVE AIR SAMPLING''
                    THEN approvedLimit.AirVolumeLiters
                ELSE NULL
            END
        FROM dbo.EM_EventPlates plate
        INNER JOIN inserted insertedPlate ON insertedPlate.Id=plate.Id
        INNER JOIN dbo.EM_Events eventRecord ON eventRecord.Id=plate.EventId
        INNER JOIN dbo.EM_Areas area ON area.Id=eventRecord.AreaId
        OUTER APPLY
        (
            SELECT TOP(1) gradeLimit.AlertLimitTotal,gradeLimit.ActionLimitTotal,gradeLimit.AirVolumeLiters
            FROM dbo.EM_GradeLimits gradeLimit
            WHERE ISNULL(gradeLimit.IsActive,1)=1
              AND UPPER(LTRIM(RTRIM(ISNULL(gradeLimit.Grade,N''''))))=UPPER(LTRIM(RTRIM(ISNULL(area.Grade,N''''))))
              AND UPPER(LTRIM(RTRIM(ISNULL(gradeLimit.Method,N''''))))=UPPER(LTRIM(RTRIM(ISNULL(plate.Method,N''''))))
            ORDER BY gradeLimit.Id DESC
        ) approvedLimit;

        IF EXISTS
        (
            SELECT 1
            FROM dbo.EM_EventPlates plate
            INNER JOIN inserted i ON i.Id=plate.Id
            INNER JOIN dbo.EM_Events eventRecord ON eventRecord.Id=plate.EventId
            INNER JOIN dbo.EM_Areas area ON area.Id=eventRecord.AreaId
            WHERE plate.AlertLimitSnapshot IS NULL
               OR plate.ActionLimitSnapshot IS NULL
               OR NULLIF(LTRIM(RTRIM(ISNULL(plate.ResultUnitSnapshot,N''''))),N'''') IS NULL
               OR (UPPER(LTRIM(RTRIM(plate.Method)))=N''ACTIVE AIR SAMPLING'' AND ISNULL(plate.AirVolumeLitersSnapshot,0)<=0)
        )
            THROW 53547, ''No complete approved EM Grade/Method limit is available. Plate creation was rolled back; configure approved limits before creating the event.'', 1;
    END;';

    IF NOT EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260828_003')
        INSERT dbo.LIMS_SchemaVersions(VersionKey,Description)
        VALUES(N'20260828_003',N'Historical EM limit-snapshot signed reconciliation, strict freeze-at-creation enforcement, and evidence-chain controls.');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
