SET NOCOUNT ON;

DECLARE @Missing TABLE (ObjectName NVARCHAR(200) NOT NULL);

IF COL_LENGTH(N'dbo.EM_Events', N'MonitoringCategory') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.MonitoringCategory');
IF COL_LENGTH(N'dbo.EM_Events', N'DispensingBooth') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.DispensingBooth');
IF COL_LENGTH(N'dbo.EM_Events', N'MaterialName') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.MaterialName');
IF COL_LENGTH(N'dbo.EM_Events', N'BatchNo') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.BatchNo');
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeId') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.EmployeeId');
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeName') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.EmployeeName');
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeDepartment') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.EmployeeDepartment');
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeShift') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.EmployeeShift');
IF COL_LENGTH(N'dbo.EM_Events', N'SamplingStage') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.SamplingStage');
IF COL_LENGTH(N'dbo.EM_Events', N'SurfaceLocation') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.SurfaceLocation');
IF COL_LENGTH(N'dbo.EM_Events', N'SurfaceType') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.SurfaceType');
IF COL_LENGTH(N'dbo.EM_Events', N'SurfaceAreaCm2') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.SurfaceAreaCm2');
IF COL_LENGTH(N'dbo.EM_Events', N'SwabKitLot') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.SwabKitLot');
IF COL_LENGTH(N'dbo.EM_Events', N'DiluentLot') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.DiluentLot');
IF COL_LENGTH(N'dbo.EM_Events', N'RecoveryVolumeMl') IS NULL INSERT INTO @Missing VALUES (N'EM_Events.RecoveryVolumeMl');
IF COL_LENGTH(N'dbo.EM_EventPlates', N'SampleSite') IS NULL INSERT INTO @Missing VALUES (N'EM_EventPlates.SampleSite');
IF COL_LENGTH(N'dbo.EM_EventPlates', N'SurfaceType') IS NULL INSERT INTO @Missing VALUES (N'EM_EventPlates.SurfaceType');
IF COL_LENGTH(N'dbo.EM_EventPlates', N'PersonnelSide') IS NULL INSERT INTO @Missing VALUES (N'EM_EventPlates.PersonnelSide');
IF COL_LENGTH(N'dbo.EM_EventPlates', N'SurfaceAreaCm2') IS NULL INSERT INTO @Missing VALUES (N'EM_EventPlates.SurfaceAreaCm2');
IF COL_LENGTH(N'dbo.EM_EventPlates', N'RecoveryVolumeMl') IS NULL INSERT INTO @Missing VALUES (N'EM_EventPlates.RecoveryVolumeMl');

IF EXISTS (SELECT 1 FROM @Missing)
BEGIN
    SELECT ObjectName AS MissingObject FROM @Missing ORDER BY ObjectName;
    THROW 51250, 'Environmental Monitoring extension verification failed.', 1;
END;

SELECT N'PASS' AS VerificationStatus,
       N'EM Surface and Personnel schema is installed for dynamic area-based registration.' AS VerificationMessage;
