SET NOCOUNT ON;

DECLARE @Failures TABLE(CheckName NVARCHAR(200), Details NVARCHAR(500));

IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovedPointCount') IS NULL
    INSERT @Failures VALUES(N'EM schedule snapshot',N'EM_Schedules.ApprovedPointCount is missing.');
IF COL_LENGTH(N'dbo.EM_Schedules',N'MediaPreparationID') IS NULL
    INSERT @Failures VALUES(N'EM media traceability',N'EM_Schedules.MediaPreparationID is missing.');
IF OBJECT_ID(N'dbo.EM_ScheduleSignatures',N'U') IS NULL
    INSERT @Failures VALUES(N'EM schedule signatures',N'EM_ScheduleSignatures is missing.');
IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2CompletedAt') IS NULL
    INSERT @Failures VALUES(N'EM incubation phases',N'Phase completion fields are missing.');
IF COL_LENGTH(N'dbo.Samples',N'IncubationStartedDateTime') IS NULL
    INSERT @Failures VALUES(N'Water timestamps',N'Samples.IncubationStartedDateTime is missing.');
IF OBJECT_ID(N'dbo.CertificateDocumentSnapshots',N'U') IS NULL
    INSERT @Failures VALUES(N'Certificate snapshots',N'CertificateDocumentSnapshots is missing.');
IF OBJECT_ID(N'dbo.PRM_CertificateSnapshots',N'U') IS NULL
    INSERT @Failures VALUES(N'PRM certificate snapshots',N'PRM_CertificateSnapshots is missing.');
IF NOT EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260722_003')
    INSERT @Failures VALUES(N'Schema version',N'20260722_003 is not recorded.');

SELECT CheckName,Details FROM @Failures ORDER BY CheckName;
IF EXISTS(SELECT 1 FROM @Failures)
    THROW 51350, 'PharmaLIMS Batch 22 schema verification failed. Review the returned checks.', 1;

SELECT N'PASS' AS VerificationResult,
       DB_NAME() AS DatabaseName,
       N'2026.7.22.45' AS ApplicationVersion,
       SYSDATETIME() AS VerifiedAt;
