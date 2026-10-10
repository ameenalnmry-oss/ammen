SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.LabEquipment', N'U') IS NULL THROW 56501, 'Existing laboratory equipment master is required.', 1;
IF OBJECT_ID(N'dbo.MicroEquipmentActivities', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.MicroEquipmentActivities(
 ActivityID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MicroEquipmentActivities PRIMARY KEY,
 EquipmentID INT NOT NULL,
 ActivityType NVARCHAR(60) NOT NULL,
 ActivityStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_MicroEquipmentActivities_Status DEFAULT(N'Draft'),
 PerformedBy NVARCHAR(100) NOT NULL,
 ActualStartAt DATETIMEOFFSET(0) NULL,
 ActualEndAt DATETIMEOFFSET(0) NULL,
 MethodReference NVARCHAR(200) NULL,
 ActivityReason NVARCHAR(500) NULL,
 EquipmentCodeSnapshot NVARCHAR(50) NOT NULL,
 EquipmentNameSnapshot NVARCHAR(200) NOT NULL,
 CalibrationStatusSnapshot NVARCHAR(30) NULL,
 QualificationStatusSnapshot NVARCHAR(30) NULL,
 CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MicroEquipmentActivities_CreatedAt DEFAULT SYSUTCDATETIME(),
 VersionToken ROWVERSION NOT NULL,
 CONSTRAINT FK_MicroEquipmentActivities_Equipment FOREIGN KEY(EquipmentID) REFERENCES dbo.LabEquipment(EquipmentID),
 CONSTRAINT CK_MicroEquipmentActivities_Type CHECK(ActivityType IN(N'Use',N'Incubation',N'Calibration',N'Periodic Verification',N'Cleaning',N'Sanitization',N'Maintenance',N'Breakdown',N'Repair')),
 CONSTRAINT CK_MicroEquipmentActivities_Status CHECK(ActivityStatus IN(N'Draft',N'Submitted',N'Reviewed',N'Approved',N'Voided')),
 CONSTRAINT CK_MicroEquipmentActivities_Time CHECK(ActualEndAt IS NULL OR ActualStartAt IS NOT NULL AND ActualEndAt>=ActualStartAt)
 );
 CREATE INDEX IX_MicroEquipmentActivities_EquipmentTime ON dbo.MicroEquipmentActivities(EquipmentID,CreatedAt DESC);
END;
IF OBJECT_ID(N'dbo.MicroEquipmentActivityLinks',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.MicroEquipmentActivityLinks(
 LinkID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MicroEquipmentActivityLinks PRIMARY KEY,
 ActivityID BIGINT NOT NULL,
 Module NVARCHAR(20) NOT NULL,
 ParentRecordID INT NOT NULL,
 ResultRecordID INT NULL,
 SampleNumberSnapshot NVARCHAR(100) NULL,
 TestCodeSnapshot NVARCHAR(100) NULL,
 LinkedBy NVARCHAR(100) NOT NULL,
 LinkedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MicroEquipmentActivityLinks_LinkedAt DEFAULT SYSUTCDATETIME(),
 CONSTRAINT FK_MicroEquipmentActivityLinks_Activity FOREIGN KEY(ActivityID) REFERENCES dbo.MicroEquipmentActivities(ActivityID),
 CONSTRAINT CK_MicroEquipmentActivityLinks_Module CHECK(Module IN(N'PRM',N'EM',N'WATER')),
 CONSTRAINT CK_MicroEquipmentActivityLinks_Parent CHECK(ParentRecordID>0),
 CONSTRAINT CK_MicroEquipmentActivityLinks_Result CHECK(ResultRecordID IS NULL OR ResultRecordID>0)
 );
 CREATE INDEX IX_MicroEquipmentActivityLinks_Source ON dbo.MicroEquipmentActivityLinks(Module,ParentRecordID,ResultRecordID);
END;
IF OBJECT_ID(N'dbo.MicroEquipmentActivityAudit',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.MicroEquipmentActivityAudit(
 AuditID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MicroEquipmentActivityAudit PRIMARY KEY,
 ActivityID BIGINT NOT NULL,
 ActionType NVARCHAR(60) NOT NULL,
 OldStateJson NVARCHAR(MAX) NULL,
 NewStateJson NVARCHAR(MAX) NOT NULL,
 ActionReason NVARCHAR(500) NOT NULL,
 ChangedBy NVARCHAR(100) NOT NULL,
 ChangedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MicroEquipmentActivityAudit_ChangedAt DEFAULT SYSUTCDATETIME(),
 CONSTRAINT FK_MicroEquipmentActivityAudit_Activity FOREIGN KEY(ActivityID) REFERENCES dbo.MicroEquipmentActivities(ActivityID)
 );
 CREATE INDEX IX_MicroEquipmentActivityAudit_Activity ON dbo.MicroEquipmentActivityAudit(ActivityID,AuditID);
END;
IF OBJECT_ID(N'dbo.MicroEquipmentActivitySignatures',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.MicroEquipmentActivitySignatures(
 SignatureID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MicroEquipmentActivitySignatures PRIMARY KEY,
 ActivityID BIGINT NOT NULL,
 SignatureMeaning NVARCHAR(200) NOT NULL,
 SignatureReason NVARCHAR(500) NOT NULL,
 ContentHash VARBINARY(32) NOT NULL,
 SignedBy NVARCHAR(100) NOT NULL,
 SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MicroEquipmentActivitySignatures_SignedAt DEFAULT SYSUTCDATETIME(),
 CONSTRAINT FK_MicroEquipmentActivitySignatures_Activity FOREIGN KEY(ActivityID) REFERENCES dbo.MicroEquipmentActivities(ActivityID)
 );
END;
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentActivityAudit_AppendOnly ON dbo.MicroEquipmentActivityAudit AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 56502, ''Micro equipment audit is append-only.'', 1; END;');
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentActivitySignatures_AppendOnly ON dbo.MicroEquipmentActivitySignatures AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 56503, ''Micro equipment signatures are append-only.'', 1; END;');
-- Only registered microbiology equipment may be used; server-side enforcement.
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentActivities_MicroScope ON dbo.MicroEquipmentActivities AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted i LEFT JOIN dbo.LabEquipment e ON e.EquipmentID=i.EquipmentID WHERE e.EquipmentID IS NULL OR LEN(e.EquipmentCode)<>10 OR e.EquipmentCode NOT LIKE N''MIC-EQ-[0-9][0-9][0-9]'' OR TRY_CONVERT(int,SUBSTRING(e.EquipmentCode,8,3)) NOT BETWEEN 1 AND 34) THROW 56504, ''Only registered microbiology equipment may be recorded.'', 1; END;');