SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS controlled append-only protection hardening.

    Purpose:
    - Reject every UPDATE or DELETE statement against protected evidence tables,
      including zero-row statements such as UPDATE/DELETE ... WHERE 1=0.
    - Preserve the existing INSERT validation rules by moving them to dedicated
      AFTER INSERT triggers where the historical append-only trigger previously
      combined INSERT validation with UPDATE/DELETE protection.
    - Keep historical migration SQL bytes unchanged.
*/
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations', N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_SchedulePointSnapshots', N'U') IS NULL
       OR OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations', N'U') IS NULL
        THROW 55220, 'Required append-only evidence tables are missing before 20260921_001.', 1;

    /* Preserve EM schedule snapshot INSERT validation separately. */
    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_EM_SchedulePointSnapshots_ValidateInsert_20260921
ON dbo.EM_SchedulePointSnapshots
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted snapshotRow
        INNER JOIN dbo.EM_Schedules scheduleRecord
            ON scheduleRecord.ScheduleID = snapshotRow.ScheduleID
        WHERE UPPER(LTRIM(RTRIM(ISNULL(scheduleRecord.ApprovalStatus,N'''')))) = N''APPROVED''
    )
        THROW 53527, ''Points cannot be appended to an approved EM schedule snapshot. Create and approve a new schedule revision instead.'', 1;
END;';

    /* Preserve EM historical-reconciliation INSERT validation separately. */
    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_EM_LimitSnapshotReconciliations_ValidateInsert_20260921
ON dbo.EM_LimitSnapshotReconciliations
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN dbo.EM_EventPlates p ON p.Id = i.PlateID
        WHERE p.AlertLimitSnapshot IS NOT NULL
          AND p.ActionLimitSnapshot IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(ISNULL(p.ResultUnitSnapshot,N''''))),N'''') IS NOT NULL
          AND (UPPER(LTRIM(RTRIM(p.Method))) <> N''ACTIVE AIR SAMPLING'' OR ISNULL(p.AirVolumeLitersSnapshot,0) > 0)
    )
        THROW 53543, ''A native frozen EM snapshot already exists for this plate. Historical reconciliation is not permitted.'', 1;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN dbo.EM_EventPlates p ON p.Id = i.PlateID
        WHERE UPPER(LTRIM(RTRIM(p.Method))) = N''ACTIVE AIR SAMPLING''
          AND ISNULL(i.AirVolumeLitersSnapshot,0) <= 0
    )
        THROW 53544, ''Active Air historical reconciliation requires a positive frozen air-volume value.'', 1;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN dbo.EM_LimitSnapshotReconciliations prior
            ON prior.ReconciliationID = i.SupersedesReconciliationID
        WHERE prior.PlateID <> i.PlateID
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
            WHERE prior.PlateID = i.PlateID
              AND prior.ReconciliationID <> i.ReconciliationID
            ORDER BY prior.ReconciliationID DESC
        ) latestPrior
        WHERE (latestPrior.ReconciliationID IS NULL AND i.SupersedesReconciliationID IS NOT NULL)
           OR (latestPrior.ReconciliationID IS NOT NULL AND ISNULL(i.SupersedesReconciliationID,0) <> latestPrior.ReconciliationID)
    )
        THROW 53546, ''A new EM reconciliation must explicitly supersede the latest prior reconciliation for that plate.'', 1;
END;';

    /*
       Statement-level append-only protection.
       INSTEAD OF triggers intentionally THROW without inspecting inserted/deleted,
       so even a syntactically valid zero-row UPDATE or DELETE is rejected.
    */
    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_EM_SchedulePointSnapshots_AppendOnly_20260828
ON dbo.EM_SchedulePointSnapshots
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- Approved EM schedule point snapshots are immutable
    THROW 53526, ''Approved EM schedule point snapshots are immutable. Create and approve a new schedule revision instead.'', 1;
END;';

    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828
ON dbo.EM_LimitSnapshotReconciliations
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- historical limit reconciliations are append-only
    THROW 53542, ''EM historical limit reconciliations are append-only. Record a signed superseding reconciliation instead of editing or deleting evidence.'', 1;
END;';

    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_LegacyCertificateEvidenceReconciliations_AppendOnly_20260908
ON dbo.LegacyCertificateEvidenceReconciliations
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- legacy certificate reconciliation evidence is append-only
    THROW 54822, ''Legacy certificate reconciliation evidence is append-only. Add a signed superseding reconciliation instead of updating or deleting history.'', 1;
END;';

    ENABLE TRIGGER dbo.TRG_EM_SchedulePointSnapshots_ValidateInsert_20260921
        ON dbo.EM_SchedulePointSnapshots;
    ENABLE TRIGGER dbo.TRG_EM_LimitSnapshotReconciliations_ValidateInsert_20260921
        ON dbo.EM_LimitSnapshotReconciliations;
    ENABLE TRIGGER dbo.TRG_EM_SchedulePointSnapshots_AppendOnly_20260828
        ON dbo.EM_SchedulePointSnapshots;
    ENABLE TRIGGER dbo.TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828
        ON dbo.EM_LimitSnapshotReconciliations;
    ENABLE TRIGGER dbo.TRG_LegacyCertificateEvidenceReconciliations_AppendOnly_20260908
        ON dbo.LegacyCertificateEvidenceReconciliations;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
