SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NULL
    THROW 51200, 'Required table dbo.EM_Events does not exist.', 1;

IF OBJECT_ID(N'dbo.EM_EventPlates', N'U') IS NULL
    THROW 51201, 'Required table dbo.EM_EventPlates does not exist.', 1;

IF COL_LENGTH(N'dbo.EM_Events', N'MonitoringCategory') IS NULL
    ALTER TABLE dbo.EM_Events ADD MonitoringCategory NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'DispensingBooth') IS NULL
    ALTER TABLE dbo.EM_Events ADD DispensingBooth NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'MaterialName') IS NULL
    ALTER TABLE dbo.EM_Events ADD MaterialName NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'BatchNo') IS NULL
    ALTER TABLE dbo.EM_Events ADD BatchNo NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeId') IS NULL
    ALTER TABLE dbo.EM_Events ADD EmployeeId NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeName') IS NULL
    ALTER TABLE dbo.EM_Events ADD EmployeeName NVARCHAR(150) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeDepartment') IS NULL
    ALTER TABLE dbo.EM_Events ADD EmployeeDepartment NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeShift') IS NULL
    ALTER TABLE dbo.EM_Events ADD EmployeeShift NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SamplingStage') IS NULL
    ALTER TABLE dbo.EM_Events ADD SamplingStage NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SurfaceLocation') IS NULL
    ALTER TABLE dbo.EM_Events ADD SurfaceLocation NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SurfaceAreaCm2') IS NULL
    ALTER TABLE dbo.EM_Events ADD SurfaceAreaCm2 DECIMAL(10,2) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SwabKitLot') IS NULL
    ALTER TABLE dbo.EM_Events ADD SwabKitLot NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'DiluentLot') IS NULL
    ALTER TABLE dbo.EM_Events ADD DiluentLot NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'RecoveryVolumeMl') IS NULL
    ALTER TABLE dbo.EM_Events ADD RecoveryVolumeMl DECIMAL(10,2) NULL;

IF COL_LENGTH(N'dbo.EM_EventPlates', N'SampleSite') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD SampleSite NVARCHAR(150) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'PersonnelSide') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD PersonnelSide NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'SurfaceAreaCm2') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD SurfaceAreaCm2 DECIMAL(10,2) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'RecoveryVolumeMl') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD RecoveryVolumeMl DECIMAL(10,2) NULL;

IF OBJECT_ID(N'dbo.EM_MonitoringMethods', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_MonitoringMethods
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_MonitoringMethods PRIMARY KEY,
        MethodName NVARCHAR(100) NOT NULL,
        Category NVARCHAR(50) NOT NULL,
        DefaultUnit NVARCHAR(30) NOT NULL,
        RequiresEmployee BIT NOT NULL CONSTRAINT DF_EM_MonitoringMethods_RequiresEmployee DEFAULT (0),
        RequiresSurfaceDetails BIT NOT NULL CONSTRAINT DF_EM_MonitoringMethods_RequiresSurfaceDetails DEFAULT (0),
        IsActive BIT NOT NULL CONSTRAINT DF_EM_MonitoringMethods_IsActive DEFAULT (1),
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_MonitoringMethods_CreatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT UQ_EM_MonitoringMethods_MethodName UNIQUE (MethodName)
    );
END;

MERGE dbo.EM_MonitoringMethods AS target
USING (VALUES
    (N'Active Air Sampling', N'Air', N'CFU/m3', 0, 0),
    (N'Settle Plate', N'Air', N'CFU/plate', 0, 0),
    (N'Contact Plate', N'Surface', N'CFU/plate', 0, 1),
    (N'Surface Swab', N'Surface', N'CFU/swab', 0, 1),
    (N'Personnel Monitoring', N'Personnel', N'CFU/glove', 1, 0)
) AS source(MethodName, Category, DefaultUnit, RequiresEmployee, RequiresSurfaceDetails)
ON target.MethodName = source.MethodName
WHEN MATCHED THEN UPDATE SET
    target.Category = source.Category,
    target.DefaultUnit = source.DefaultUnit,
    target.RequiresEmployee = source.RequiresEmployee,
    target.RequiresSurfaceDetails = source.RequiresSurfaceDetails,
    target.IsActive = 1
WHEN NOT MATCHED THEN INSERT
    (MethodName, Category, DefaultUnit, RequiresEmployee, RequiresSurfaceDetails, IsActive)
    VALUES
    (source.MethodName, source.Category, source.DefaultUnit, source.RequiresEmployee, source.RequiresSurfaceDetails, 1);

IF OBJECT_ID(N'dbo.EM_Personnel', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_Personnel
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_Personnel PRIMARY KEY,
        EmployeeId NVARCHAR(50) NOT NULL,
        EmployeeName NVARCHAR(150) NOT NULL,
        Department NVARCHAR(100) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_EM_Personnel_IsActive DEFAULT (1),
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_Personnel_CreatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT UQ_EM_Personnel_EmployeeId UNIQUE (EmployeeId)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EM_Events_MonitoringCategory' AND object_id = OBJECT_ID(N'dbo.EM_Events'))
    EXEC(N'CREATE INDEX IX_EM_Events_MonitoringCategory ON dbo.EM_Events(MonitoringCategory, EventDate);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EM_Events_EmployeeId' AND object_id = OBJECT_ID(N'dbo.EM_Events'))
    EXEC(N'CREATE INDEX IX_EM_Events_EmployeeId ON dbo.EM_Events(EmployeeId, EventDate) WHERE EmployeeId IS NOT NULL;');

COMMIT TRANSACTION;
