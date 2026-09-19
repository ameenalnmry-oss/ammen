SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Water_PlanSampleTests', N'U') IS NULL
       OR OBJECT_ID(N'dbo.Water_PlanSampleAttempts', N'U') IS NULL
       OR OBJECT_ID(N'dbo.Tests', N'U') IS NULL
       OR OBJECT_ID(N'dbo.Samples', N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_Schedules', N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_ScheduleAreas', N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_Areas', N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_AreaTemplates', N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_PlanSamples', N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_EventPlates', N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_Events', N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_GradeLimits', N'U') IS NULL
       OR OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
        THROW 53520, 'Required Water/EM planning dependency is missing. Apply earlier controlled migrations first.', 1;

    /* Water planning referential integrity. */
    IF EXISTS
    (
        SELECT 1
        FROM dbo.Water_PlanSampleTests planTest
        LEFT JOIN dbo.Tests test ON test.TestID = planTest.TestID
        WHERE test.TestID IS NULL
    )
        THROW 53521, 'Water_PlanSampleTests contains orphan TestID values. Reconcile data before applying 20260828_002.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'dbo.Water_PlanSampleTests')
          AND referenced_object_id = OBJECT_ID(N'dbo.Tests')
    )
    BEGIN
        ALTER TABLE dbo.Water_PlanSampleTests WITH CHECK
        ADD CONSTRAINT FK_Water_PlanSampleTests_Tests_20260828
            FOREIGN KEY(TestID) REFERENCES dbo.Tests(TestID);
        ALTER TABLE dbo.Water_PlanSampleTests CHECK CONSTRAINT FK_Water_PlanSampleTests_Tests_20260828;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM dbo.Water_PlanSampleAttempts attempt
        LEFT JOIN dbo.Samples sample ON sample.SampleID = attempt.SampleID
        WHERE sample.SampleID IS NULL
    )
        THROW 53522, 'Water_PlanSampleAttempts contains orphan SampleID values. Reconcile data before applying 20260828_002.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'dbo.Water_PlanSampleAttempts')
          AND referenced_object_id = OBJECT_ID(N'dbo.Samples')
    )
    BEGIN
        ALTER TABLE dbo.Water_PlanSampleAttempts WITH CHECK
        ADD CONSTRAINT FK_Water_PlanSampleAttempts_Samples_20260828
            FOREIGN KEY(SampleID) REFERENCES dbo.Samples(SampleID);
        ALTER TABLE dbo.Water_PlanSampleAttempts CHECK CONSTRAINT FK_Water_PlanSampleAttempts_Samples_20260828;
    END;

    /* Defense in depth: once a Water Plan leaves Planned, its controlled point/test instructions are immutable. */
    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_Water_PlanSampleTests_FreezeDistributed_20260828
    ON dbo.Water_PlanSampleTests
    AFTER INSERT, UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        IF EXISTS
        (
            SELECT 1
            FROM
            (
                SELECT WaterPlanSampleID FROM inserted
                UNION
                SELECT WaterPlanSampleID FROM deleted
            ) changed
            INNER JOIN dbo.Water_PlanSamples samplePlan ON samplePlan.WaterPlanSampleID=changed.WaterPlanSampleID
            INNER JOIN dbo.Water_Plans planRecord ON planRecord.WaterPlanID=samplePlan.WaterPlanID
            WHERE UPPER(LTRIM(RTRIM(ISNULL(planRecord.Status,N''''))))<>N''PLANNED''
        )
            THROW 53524, ''Water Plan test assignments are frozen after Distribution. Create a controlled replacement plan instead.'', 1;
    END;';

    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_Water_PlanSamples_ProtectDistributed_20260828
    ON dbo.Water_PlanSamples
    AFTER UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        IF EXISTS
        (
            SELECT 1
            FROM deleted oldRow
            INNER JOIN dbo.Water_Plans planRecord ON planRecord.WaterPlanID=oldRow.WaterPlanID
            LEFT JOIN inserted newRow ON newRow.WaterPlanSampleID=oldRow.WaterPlanSampleID
            WHERE UPPER(LTRIM(RTRIM(ISNULL(planRecord.Status,N''''))))<>N''PLANNED''
              AND
              (
                    newRow.WaterPlanSampleID IS NULL
                 OR ISNULL(newRow.WaterPlanID,-2147483648)<>ISNULL(oldRow.WaterPlanID,-2147483648)
                 OR ISNULL(newRow.PointID,-2147483648)<>ISNULL(oldRow.PointID,-2147483648)
                 OR ISNULL(newRow.PointCode,N''<NULL>'')<>ISNULL(oldRow.PointCode,N''<NULL>'')
                 OR ISNULL(newRow.PointName,N''<NULL>'')<>ISNULL(oldRow.PointName,N''<NULL>'')
                 OR ISNULL(newRow.Location,N''<NULL>'')<>ISNULL(oldRow.Location,N''<NULL>'')
                 OR ISNULL(newRow.AnalysisProfile,N''<NULL>'')<>ISNULL(oldRow.AnalysisProfile,N''<NULL>'')
              )
        )
            THROW 53525, ''Distributed Water Plan point/instruction identity is immutable. Create a controlled replacement plan instead.'', 1;
    END;';

    /* Immutable approved EM schedule point identity. */
    IF OBJECT_ID(N'dbo.EM_SchedulePointSnapshots', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EM_SchedulePointSnapshots
        (
            SnapshotID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_SchedulePointSnapshots PRIMARY KEY,
            ScheduleID INT NOT NULL,
            SnapshotSequence INT NOT NULL,
            AreaID INT NOT NULL,
            AreaCodeSnapshot NVARCHAR(100) NOT NULL,
            AreaNameSnapshot NVARCHAR(200) NOT NULL,
            GradeSnapshot NVARCHAR(100) NULL,
            TemplateID INT NULL,
            TemplateSequenceNo INT NULL,
            MethodSnapshot NVARCHAR(100) NOT NULL,
            LocationSnapshot NVARCHAR(200) NOT NULL,
            CapturedBy NVARCHAR(100) NOT NULL,
            CapturedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_SchedulePointSnapshots_CapturedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_EM_SchedulePointSnapshots_Schedule FOREIGN KEY(ScheduleID) REFERENCES dbo.EM_Schedules(ScheduleID),
            CONSTRAINT UQ_EM_SchedulePointSnapshots_Sequence UNIQUE(ScheduleID, SnapshotSequence)
        );
        CREATE INDEX IX_EM_SchedulePointSnapshots_Schedule
            ON dbo.EM_SchedulePointSnapshots(ScheduleID, AreaID, SnapshotSequence);
    END;

    /* Historical approved schedules are intentionally NOT reconstructed from current master data.
       Their exact approved point identity is unknowable if master data changed before this release.
       Automated generation will fail closed until a new schedule revision is reviewed and approved,
       at which point the application captures a native immutable snapshot. */

    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_EM_SchedulePointSnapshots_AppendOnly_20260828
    ON dbo.EM_SchedulePointSnapshots
    AFTER INSERT, UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;

        IF EXISTS(SELECT 1 FROM deleted)
            THROW 53526, ''Approved EM schedule point snapshots are immutable. Create and approve a new schedule revision instead.'', 1;

        IF EXISTS
        (
            SELECT 1
            FROM inserted snapshotRow
            INNER JOIN dbo.EM_Schedules scheduleRecord ON scheduleRecord.ScheduleID=snapshotRow.ScheduleID
            WHERE UPPER(LTRIM(RTRIM(ISNULL(scheduleRecord.ApprovalStatus,N''Draft''))))=N''APPROVED''
        )
            THROW 53527, ''Points cannot be appended to an approved EM schedule snapshot. Create and approve a new schedule revision instead.'', 1;
    END;';

    /* Record actual EM excursions instead of rejecting the observation. */
    IF COL_LENGTH(N'dbo.EM_PlanSamples', N'CollectionExcursion') IS NULL
        ALTER TABLE dbo.EM_PlanSamples ADD CollectionExcursion BIT NOT NULL CONSTRAINT DF_EM_PlanSamples_CollectionExcursion_20260828 DEFAULT(0) WITH VALUES;
    IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase1Excursion') IS NULL
        ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1Excursion BIT NOT NULL CONSTRAINT DF_EM_PlanSamples_Phase1Excursion_20260828 DEFAULT(0) WITH VALUES;
    IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase2Excursion') IS NULL
        ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2Excursion BIT NOT NULL CONSTRAINT DF_EM_PlanSamples_Phase2Excursion_20260828 DEFAULT(0) WITH VALUES;

    /* Freeze approved EM limits at the moment each result plate is created. */
    IF COL_LENGTH(N'dbo.EM_EventPlates', N'AlertLimitSnapshot') IS NULL
        ALTER TABLE dbo.EM_EventPlates ADD AlertLimitSnapshot DECIMAL(18,3) NULL;
    IF COL_LENGTH(N'dbo.EM_EventPlates', N'ActionLimitSnapshot') IS NULL
        ALTER TABLE dbo.EM_EventPlates ADD ActionLimitSnapshot DECIMAL(18,3) NULL;
    IF COL_LENGTH(N'dbo.EM_EventPlates', N'ResultUnitSnapshot') IS NULL
        ALTER TABLE dbo.EM_EventPlates ADD ResultUnitSnapshot NVARCHAR(30) NULL;
    IF COL_LENGTH(N'dbo.EM_EventPlates', N'AirVolumeLitersSnapshot') IS NULL
        ALTER TABLE dbo.EM_EventPlates ADD AirVolumeLitersSnapshot INT NULL;

    /* Historical EM_EventPlates with missing snapshots are intentionally NOT backfilled from
       current EM_GradeLimits. Current master limits are not evidence of the historical approved
       limit. EM Results Entry therefore fails closed for an incomplete historical snapshot and
       requires controlled reconciliation. New plates are frozen by the trigger below. */

    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_EM_EventPlates_FreezeLimits_20260828
    ON dbo.EM_EventPlates
    AFTER INSERT
    AS
    BEGIN
        SET NOCOUNT ON;
        UPDATE plate
        SET
            AlertLimitSnapshot = COALESCE(plate.AlertLimitSnapshot, approvedLimit.AlertLimitTotal),
            ActionLimitSnapshot = COALESCE(plate.ActionLimitSnapshot, approvedLimit.ActionLimitTotal),
            ResultUnitSnapshot = COALESCE(NULLIF(plate.ResultUnitSnapshot,N''''),
                CASE
                    WHEN UPPER(LTRIM(RTRIM(plate.Method))) = N''ACTIVE AIR SAMPLING'' THEN N''CFU/m3''
                    WHEN UPPER(LTRIM(RTRIM(plate.Method))) IN(N''SETTLE PLATE'',N''CONTACT PLATE'') THEN N''CFU/plate''
                    WHEN UPPER(LTRIM(RTRIM(plate.Method))) = N''SURFACE SWAB'' THEN N''CFU/swab''
                    WHEN UPPER(LTRIM(RTRIM(plate.Method))) = N''PERSONNEL MONITORING'' THEN N''CFU/glove''
                    ELSE N''CFU''
                END),
            AirVolumeLitersSnapshot = CASE
                WHEN UPPER(LTRIM(RTRIM(plate.Method))) = N''ACTIVE AIR SAMPLING''
                    THEN COALESCE(plate.AirVolumeLitersSnapshot, approvedLimit.AirVolumeLiters)
                ELSE plate.AirVolumeLitersSnapshot
            END
        FROM dbo.EM_EventPlates plate
        INNER JOIN inserted insertedPlate ON insertedPlate.Id = plate.Id
        INNER JOIN dbo.EM_Events eventRecord ON eventRecord.Id = plate.EventId
        INNER JOIN dbo.EM_Areas area ON area.Id = eventRecord.AreaId
        OUTER APPLY
        (
            SELECT TOP(1) gradeLimit.AlertLimitTotal, gradeLimit.ActionLimitTotal, gradeLimit.AirVolumeLiters
            FROM dbo.EM_GradeLimits gradeLimit
            WHERE ISNULL(gradeLimit.IsActive,1)=1
              AND UPPER(LTRIM(RTRIM(ISNULL(gradeLimit.Grade,N'''')))) = UPPER(LTRIM(RTRIM(ISNULL(area.Grade,N''''))))
              AND UPPER(LTRIM(RTRIM(ISNULL(gradeLimit.Method,N'''')))) = UPPER(LTRIM(RTRIM(ISNULL(plate.Method,N''''))))
            ORDER BY gradeLimit.Id DESC
        ) approvedLimit;
    END;';

    EXEC sys.sp_executesql N'
    CREATE OR ALTER TRIGGER dbo.TRG_EM_EventPlates_ProtectLimits_20260828
    ON dbo.EM_EventPlates
    AFTER UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        /* The INSERT freeze trigger performs one nested UPDATE to populate the initial snapshot. */
        IF TRIGGER_NESTLEVEL()>1 RETURN;

        IF EXISTS
        (
            SELECT 1
            FROM inserted newRow
            INNER JOIN deleted oldRow ON oldRow.Id=newRow.Id
            WHERE
                   (newRow.AlertLimitSnapshot<>oldRow.AlertLimitSnapshot OR (newRow.AlertLimitSnapshot IS NULL AND oldRow.AlertLimitSnapshot IS NOT NULL) OR (newRow.AlertLimitSnapshot IS NOT NULL AND oldRow.AlertLimitSnapshot IS NULL))
                OR (newRow.ActionLimitSnapshot<>oldRow.ActionLimitSnapshot OR (newRow.ActionLimitSnapshot IS NULL AND oldRow.ActionLimitSnapshot IS NOT NULL) OR (newRow.ActionLimitSnapshot IS NOT NULL AND oldRow.ActionLimitSnapshot IS NULL))
                OR (ISNULL(newRow.ResultUnitSnapshot,N''<NULL>'')<>ISNULL(oldRow.ResultUnitSnapshot,N''<NULL>''))
                OR (ISNULL(newRow.AirVolumeLitersSnapshot,-2147483648)<>ISNULL(oldRow.AirVolumeLitersSnapshot,-2147483648))
        )
            THROW 53528, ''Frozen EM limit snapshots cannot be changed during result entry or by direct update.'', 1;
    END;';

    IF NOT EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260828_002')
        INSERT dbo.LIMS_SchemaVersions(VersionKey,Description)
        VALUES(N'20260828_002',N'Water-plan immutability/referential integrity, approved EM schedule point snapshots, excursion recording, and EM limit freeze-at-creation.');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
