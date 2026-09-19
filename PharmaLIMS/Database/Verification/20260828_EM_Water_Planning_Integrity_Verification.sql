SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.EM_SchedulePointSnapshots',N'U') IS NULL
    THROW 53620, 'EM_SchedulePointSnapshots is missing.', 1;
IF OBJECT_ID(N'dbo.TRG_Water_PlanSampleTests_FreezeDistributed_20260828',N'TR') IS NULL
    THROW 53621, 'Water Plan test-freeze trigger is missing.', 1;
IF OBJECT_ID(N'dbo.TRG_Water_PlanSamples_ProtectDistributed_20260828',N'TR') IS NULL
    THROW 53622, 'Water Plan distributed-point protection trigger is missing.', 1;
IF OBJECT_ID(N'dbo.TRG_EM_SchedulePointSnapshots_AppendOnly_20260828',N'TR') IS NULL
    THROW 53627, 'EM schedule snapshot append-only trigger is missing.', 1;
IF OBJECT_ID(N'dbo.TRG_EM_EventPlates_FreezeLimits_20260828',N'TR') IS NULL
    THROW 53628, 'EM plate limit freeze trigger is missing.', 1;
IF OBJECT_ID(N'dbo.TRG_EM_EventPlates_ProtectLimits_20260828',N'TR') IS NULL
    THROW 53629, 'EM plate frozen-limit protection trigger is missing.', 1;
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'CollectionExcursion') IS NULL
   OR COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1Excursion') IS NULL
   OR COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2Excursion') IS NULL
    THROW 53623, 'EM excursion evidence columns are incomplete.', 1;
IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.Water_PlanSampleTests')
      AND referenced_object_id=OBJECT_ID(N'dbo.Tests')
)
    THROW 53624, 'Water_PlanSampleTests -> Tests foreign key is missing.', 1;
IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.Water_PlanSampleAttempts')
      AND referenced_object_id=OBJECT_ID(N'dbo.Samples')
)
    THROW 53625, 'Water_PlanSampleAttempts -> Samples foreign key is missing.', 1;
IF NOT EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260828_002')
    THROW 53626, 'Migration ledger entry 20260828_002 is missing.', 1;

SELECT N'PASS' AS VerificationStatus,
       N'20260828_002 EM/Water planning integrity controls are present.' AS Details;
