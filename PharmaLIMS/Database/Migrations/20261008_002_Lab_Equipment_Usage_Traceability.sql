SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.LabEquipmentUsage',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LabEquipmentUsage
    (
        UsageID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipmentUsage PRIMARY KEY,
        Module NVARCHAR(20) NOT NULL,
        ParentRecordID INT NOT NULL,
        ResultRecordID INT NOT NULL,
        EquipmentID INT NOT NULL,
        AssignedBy NVARCHAR(100) NOT NULL,
        AssignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipmentUsage_AssignedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_LabEquipmentUsage_Equipment FOREIGN KEY(EquipmentID) REFERENCES dbo.LabEquipment(EquipmentID),
        CONSTRAINT CK_LabEquipmentUsage_Module CHECK(Module IN(N'WATER',N'EM',N'PRM')),
        CONSTRAINT UQ_LabEquipmentUsage_ModuleResult UNIQUE(Module,ResultRecordID)
    );

    CREATE INDEX IX_LabEquipmentUsage_Parent
        ON dbo.LabEquipmentUsage(Module,ParentRecordID,ResultRecordID);
END;

IF OBJECT_ID(N'dbo.LabEquipmentUsageHistory',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LabEquipmentUsageHistory
    (
        HistoryID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipmentUsageHistory PRIMARY KEY,
        Module NVARCHAR(20) NOT NULL,
        ParentRecordID INT NOT NULL,
        ResultRecordID INT NOT NULL,
        PreviousEquipmentID INT NULL,
        EquipmentID INT NULL,
        EquipmentCodeSnapshot NVARCHAR(50) NULL,
        EquipmentNameSnapshot NVARCHAR(200) NULL,
        EquipmentTypeSnapshot NVARCHAR(100) NULL,
        EquipmentStatusSnapshot NVARCHAR(30) NULL,
        QualificationStatusSnapshot NVARCHAR(30) NULL,
        CalibrationStatusSnapshot NVARCHAR(30) NULL,
        NextQualificationDateSnapshot DATE NULL,
        NextCalibrationDateSnapshot DATE NULL,
        ChangeType NVARCHAR(20) NOT NULL,
        MeaningOfSignature NVARCHAR(250) NOT NULL,
        ActionReason NVARCHAR(500) NOT NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipmentUsageHistory_SignedAt DEFAULT SYSUTCDATETIME(),
        SourceWorkstation NVARCHAR(100) NULL,
        CONSTRAINT CK_LabEquipmentUsageHistory_Module CHECK(Module IN(N'WATER',N'EM',N'PRM')),
        CONSTRAINT CK_LabEquipmentUsageHistory_ChangeType CHECK(ChangeType IN(N'ASSIGN',N'REASSIGN',N'CLEAR'))
    );

    CREATE INDEX IX_LabEquipmentUsageHistory_Result
        ON dbo.LabEquipmentUsageHistory(Module,ResultRecordID,SignedAt DESC,HistoryID DESC);
END;

EXEC(N'
CREATE OR ALTER TRIGGER dbo.TRG_LabEquipmentUsageHistory_AppendOnly
ON dbo.LabEquipmentUsageHistory
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 56411, ''Laboratory equipment usage history is append-only and cannot be updated or deleted.'', 1;
END;');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.LabEquipmentUsage')
      AND name=N'UQ_LabEquipmentUsage_ModuleResult'
      AND is_unique=1
)
    THROW 56412, 'LabEquipmentUsage module/result uniqueness contract is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.triggers tr
    JOIN sys.sql_modules sm ON sm.object_id=tr.object_id
    WHERE tr.parent_id=OBJECT_ID(N'dbo.LabEquipmentUsageHistory')
      AND tr.name=N'TRG_LabEquipmentUsageHistory_AppendOnly'
      AND tr.is_disabled=0
      AND LOWER(sm.definition) LIKE N'%laboratory equipment usage history is append-only and cannot be updated or deleted%'
)
    THROW 56413, 'Laboratory equipment usage history append-only trigger is missing or invalid.', 1;
