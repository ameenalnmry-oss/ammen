SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.LIMS_SchemaVersions',N'U') IS NULL
    THROW 52000, 'Run the application release-hardening migration first.', 1;
IF OBJECT_ID(N'dbo.EM_Areas',N'U') IS NULL OR OBJECT_ID(N'dbo.EM_Events',N'U') IS NULL OR OBJECT_ID(N'dbo.EM_EventPlates',N'U') IS NULL
    THROW 52001, 'Required Environmental Monitoring master tables are missing.', 1;

IF OBJECT_ID(N'dbo.EM_Plans',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.EM_Plans(
  PlanID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_Plans PRIMARY KEY,
  PlanNo NVARCHAR(50) NOT NULL CONSTRAINT UQ_EM_Plans_PlanNo UNIQUE,
  PlanType NVARCHAR(30) NOT NULL, SourceType NVARCHAR(20) NOT NULL,
  LoginDate DATE NOT NULL, SampleDueDate DATE NOT NULL, RequiredDate DATE NOT NULL,
  Status NVARCHAR(40) NOT NULL CONSTRAINT DF_EM_Plans_Status DEFAULT N'Draft',
  BatchName NVARCHAR(150) NULL, BatchNo NVARCHAR(100) NULL, InspectionLotNo NVARCHAR(100) NULL,
  SetNo NVARCHAR(50) NULL, PlanNotes NVARCHAR(MAX) NULL,
  CreatedBy NVARCHAR(100) NOT NULL, CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_Plans_CreatedAt DEFAULT SYSDATETIME(),
  DistributedBy NVARCHAR(100) NULL, DistributedAt DATETIME2(0) NULL,
  CollectedBy NVARCHAR(100) NULL, CollectedAt DATETIME2(0) NULL,
  IncubatedBy NVARCHAR(100) NULL, IncubatedAt DATETIME2(0) NULL,
  CancelledBy NVARCHAR(100) NULL, CancelledAt DATETIME2(0) NULL, CancellationReason NVARCHAR(500) NULL,
  CONSTRAINT CK_EM_Plans_Dates CHECK(RequiredDate>=SampleDueDate AND SampleDueDate>=LoginDate));
END;

IF OBJECT_ID(N'dbo.EM_Schedules',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.EM_Schedules(
  ScheduleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_Schedules PRIMARY KEY,
  ScheduleName NVARCHAR(150) NOT NULL, PlanType NVARCHAR(30) NOT NULL, Frequency NVARCHAR(20) NOT NULL,
  NextDueDate DATE NOT NULL, DaysAhead INT NOT NULL CONSTRAINT DF_EM_Schedules_DaysAhead DEFAULT(0),
  IsActive BIT NOT NULL CONSTRAINT DF_EM_Schedules_Active DEFAULT(1), CreatedBy NVARCHAR(100) NOT NULL,
  CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_Schedules_CreatedAt DEFAULT SYSDATETIME());
END;

IF COL_LENGTH(N'dbo.EM_Schedules',N'Method') IS NULL ALTER TABLE dbo.EM_Schedules ADD Method NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'SamplingLocation') IS NULL ALTER TABLE dbo.EM_Schedules ADD SamplingLocation NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'MediaUsed') IS NULL ALTER TABLE dbo.EM_Schedules ADD MediaUsed NVARCHAR(150) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'MediaLotNo') IS NULL ALTER TABLE dbo.EM_Schedules ADD MediaLotNo NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'IncludeNegativeControl') IS NULL ALTER TABLE dbo.EM_Schedules ADD IncludeNegativeControl BIT NOT NULL CONSTRAINT DF_EM_Schedules_Negative DEFAULT(1);
IF COL_LENGTH(N'dbo.EM_Schedules',N'EmployeeID') IS NULL ALTER TABLE dbo.EM_Schedules ADD EmployeeID NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'EmployeeName') IS NULL ALTER TABLE dbo.EM_Schedules ADD EmployeeName NVARCHAR(150) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules',N'LastGeneratedDate') IS NULL ALTER TABLE dbo.EM_Schedules ADD LastGeneratedDate DATE NULL;
IF OBJECT_ID(N'dbo.EM_ScheduleAreas',N'U') IS NULL
 CREATE TABLE dbo.EM_ScheduleAreas(
  ScheduleAreaID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_ScheduleAreas PRIMARY KEY,
  ScheduleID INT NOT NULL,AreaID INT NOT NULL,
  CONSTRAINT FK_EM_ScheduleAreas_Schedules FOREIGN KEY(ScheduleID) REFERENCES dbo.EM_Schedules(ScheduleID),
  CONSTRAINT UQ_EM_ScheduleAreas UNIQUE(ScheduleID,AreaID));

IF OBJECT_ID(N'dbo.EM_PlanSamples',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.EM_PlanSamples(
  PlanSampleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_PlanSamples PRIMARY KEY,
  PlanID INT NOT NULL, AreaID INT NULL, Method NVARCHAR(100) NOT NULL, SamplingLocation NVARCHAR(200) NULL,
  SampleCode NVARCHAR(80) NOT NULL, IsNegativeControl BIT NOT NULL CONSTRAINT DF_EM_PlanSamples_Negative DEFAULT(0),
  EmployeeID NVARCHAR(50) NULL, EmployeeName NVARCHAR(150) NULL, MediaUsed NVARCHAR(150) NULL, MediaLotNo NVARCHAR(100) NULL,
  Status NVARCHAR(40) NOT NULL CONSTRAINT DF_EM_PlanSamples_Status DEFAULT N'Planned', PlateCondition NVARCHAR(100) NULL,
  KitCondition NVARCHAR(100) NULL, SamplingStart DATETIME2(0) NULL, SamplingEnd DATETIME2(0) NULL,
  TransportMinC DECIMAL(5,2) NULL, TransportMaxC DECIMAL(5,2) NULL, IncubationStart DATETIME2(0) NULL,
  IncubationEnd DATETIME2(0) NULL, BacteriaIncubatorID NVARCHAR(50) NULL, FungiIncubatorID NVARCHAR(50) NULL,
  NegativeControlResult NVARCHAR(50) NULL, EventID INT NULL,
  CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_PlanSamples_CreatedAt DEFAULT SYSDATETIME(),
  CONSTRAINT FK_EM_PlanSamples_Plans FOREIGN KEY(PlanID) REFERENCES dbo.EM_Plans(PlanID),
  CONSTRAINT UQ_EM_PlanSamples_Code UNIQUE(PlanID,SampleCode));
END;

IF OBJECT_ID(N'dbo.EM_PlanSignatures',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.EM_PlanSignatures(
  SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_PlanSignatures PRIMARY KEY,
  PlanID INT NOT NULL, ActionType NVARCHAR(60) NOT NULL, ActionReason NVARCHAR(500) NOT NULL,
  SignedBy NVARCHAR(100) NOT NULL, UserRole NVARCHAR(100) NULL, MeaningOfSignature NVARCHAR(255) NOT NULL,
  SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_PlanSignatures_SignedAt DEFAULT SYSDATETIME(),
  CONSTRAINT FK_EM_PlanSignatures_Plans FOREIGN KEY(PlanID) REFERENCES dbo.EM_Plans(PlanID));
END;

IF COL_LENGTH(N'dbo.EM_Events',N'PlanID') IS NULL ALTER TABLE dbo.EM_Events ADD PlanID INT NULL;
IF COL_LENGTH(N'dbo.EM_Events',N'NegativeControlResult') IS NULL ALTER TABLE dbo.EM_Events ADD NegativeControlResult NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates',N'PlanSampleID') IS NULL ALTER TABLE dbo.EM_EventPlates ADD PlanSampleID INT NULL;
IF COL_LENGTH(N'dbo.EM_Plans',N'ScheduleID') IS NULL ALTER TABLE dbo.EM_Plans ADD ScheduleID INT NULL;
IF COL_LENGTH(N'dbo.EM_Plans',N'CompletedBy') IS NULL ALTER TABLE dbo.EM_Plans ADD CompletedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Plans',N'CompletedAt') IS NULL ALTER TABLE dbo.EM_Plans ADD CompletedAt DATETIME2(0) NULL;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EM_Plans') AND name=N'IX_EM_Plans_StatusDue')
 CREATE INDEX IX_EM_Plans_StatusDue ON dbo.EM_Plans(Status,SampleDueDate);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EM_PlanSamples') AND name=N'IX_EM_PlanSamples_PlanStatus')
 CREATE INDEX IX_EM_PlanSamples_PlanStatus ON dbo.EM_PlanSamples(PlanID,Status);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EM_PlanSamples') AND name=N'IX_EM_PlanSamples_Area')
 CREATE INDEX IX_EM_PlanSamples_Area ON dbo.EM_PlanSamples(AreaID);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EM_Plans') AND name=N'UX_EM_Plans_ScheduleDue')
 EXEC sys.sp_executesql N'CREATE UNIQUE INDEX UX_EM_Plans_ScheduleDue ON dbo.EM_Plans(ScheduleID,SampleDueDate) WHERE ScheduleID IS NOT NULL;';
IF NOT EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260718_002')
 INSERT dbo.LIMS_SchemaVersions(VersionKey,Description) VALUES(N'20260718_002',N'Environmental Monitoring plan, schedule, sample lifecycle, signatures, transport and incubation traceability.');
IF NOT EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260719_001')
 INSERT dbo.LIMS_SchemaVersions(VersionKey,Description) VALUES(N'20260719_001',N'Operational EM schedule configuration, area mappings, duplicate prevention, and due-plan generation.');

COMMIT TRANSACTION;
