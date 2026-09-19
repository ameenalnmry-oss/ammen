SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.LIMS_SchemaVersions',N'U') IS NULL
    THROW 52100, 'Required schema version table is missing.', 1;
IF OBJECT_ID(N'dbo.EM_Schedules',N'U') IS NULL OR OBJECT_ID(N'dbo.EM_PlanSamples',N'U') IS NULL
    THROW 52101, 'Run the EM planning migration before this migration.', 1;

IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovalStatus') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ApprovalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_EM_Schedules_ApprovalStatus DEFAULT N'Draft';
IF COL_LENGTH(N'dbo.EM_Schedules',N'VersionNo') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD VersionNo INT NOT NULL CONSTRAINT DF_EM_Schedules_VersionNo DEFAULT(1);
IF COL_LENGTH(N'dbo.EM_Schedules',N'EffectiveFrom') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD EffectiveFrom DATE NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'ReviewedBy') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ReviewedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'ReviewedAt') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ReviewedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovedBy') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ApprovedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovedAt') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ApprovedAt DATETIME2(0) NULL;

IF COL_LENGTH(N'dbo.EM_PlanSamples',N'MediaPreparationID') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD MediaPreparationID INT NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'OperationalState') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD OperationalState NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'ActivityBatchNo') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD ActivityBatchNo NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'PersonnelCount') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD PersonnelCount INT NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'TransportStart') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD TransportStart DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'TransportEnd') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD TransportEnd DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1Start') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1Start DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1End') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1End DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1Temp') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1Temp NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2Start') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2Start DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2End') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2End DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2Temp') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2Temp NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'ControlReadBy') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD ControlReadBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'ControlReadAt') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD ControlReadAt DATETIME2(0) NULL;

IF OBJECT_ID(N'dbo.MediaPreparations',N'U') IS NOT NULL AND NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys WHERE name=N'FK_EM_PlanSamples_MediaPreparation'
)
    EXEC sys.sp_executesql N'
    ALTER TABLE dbo.EM_PlanSamples WITH NOCHECK
    ADD CONSTRAINT FK_EM_PlanSamples_MediaPreparation
    FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID);';

IF NOT EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260721_001')
    INSERT dbo.LIMS_SchemaVersions(VersionKey,Description)
    VALUES(N'20260721_001',N'Global EM compliance hardening: approved schedules, prepared-media traceability, two-stage incubation, control reading, and operational context.');

COMMIT TRANSACTION;
