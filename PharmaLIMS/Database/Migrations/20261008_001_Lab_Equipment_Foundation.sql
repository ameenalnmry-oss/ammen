SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.LabEquipment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LabEquipment
    (
        EquipmentID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipment PRIMARY KEY,
        EquipmentCode NVARCHAR(50) NOT NULL,
        EquipmentName NVARCHAR(200) NOT NULL,
        EquipmentType NVARCHAR(100) NOT NULL,
        Department NVARCHAR(100) NULL,
        Location NVARCHAR(200) NULL,
        Manufacturer NVARCHAR(150) NULL,
        Model NVARCHAR(100) NULL,
        SerialNumber NVARCHAR(100) NULL,
        GmpCritical BIT NOT NULL CONSTRAINT DF_LabEquipment_GmpCritical DEFAULT(1),
        EquipmentStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_LabEquipment_Status DEFAULT(N'Active'),
        QualificationStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_LabEquipment_Qualification DEFAULT(N'Not Qualified'),
        LastQualificationDate DATE NULL,
        NextQualificationDate DATE NULL,
        CalibrationRequired BIT NOT NULL CONSTRAINT DF_LabEquipment_CalibrationRequired DEFAULT(1),
        CalibrationStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_LabEquipment_CalibrationStatus DEFAULT(N'Not Calibrated'),
        LastCalibrationDate DATE NULL,
        NextCalibrationDate DATE NULL,
        MethodReference NVARCHAR(200) NULL,
        Notes NVARCHAR(500) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_LabEquipment_IsActive DEFAULT(1),
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipment_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedBy NVARCHAR(100) NULL,
        UpdatedAt DATETIME2(0) NULL,
        RowVersion ROWVERSION NOT NULL,
        CONSTRAINT UQ_LabEquipment_EquipmentCode UNIQUE(EquipmentCode),
        CONSTRAINT CK_LabEquipment_Status CHECK(EquipmentStatus IN
            (N'Active',N'Out of Service',N'Under Maintenance',N'Quarantine',N'Retired')),
        CONSTRAINT CK_LabEquipment_QualificationStatus CHECK(QualificationStatus IN
            (N'Qualified',N'Due Soon',N'Expired',N'Not Qualified',N'Not Required')),
        CONSTRAINT CK_LabEquipment_CalibrationStatus CHECK(CalibrationStatus IN
            (N'Calibrated',N'Due Soon',N'Expired',N'Not Calibrated',N'Not Required')),
        CONSTRAINT CK_LabEquipment_QualificationDates CHECK
            (NextQualificationDate IS NULL OR LastQualificationDate IS NULL OR NextQualificationDate >= LastQualificationDate),
        CONSTRAINT CK_LabEquipment_CalibrationDates CHECK
            (NextCalibrationDate IS NULL OR LastCalibrationDate IS NULL OR NextCalibrationDate >= LastCalibrationDate)
    );

    CREATE INDEX IX_LabEquipment_StatusDue
        ON dbo.LabEquipment(IsActive, EquipmentStatus, NextCalibrationDate, NextQualificationDate);
END;

IF OBJECT_ID(N'dbo.LabEquipmentSignatures', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LabEquipmentSignatures
    (
        SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipmentSignatures PRIMARY KEY,
        EquipmentID INT NOT NULL,
        ActionType NVARCHAR(50) NOT NULL,
        MeaningOfSignature NVARCHAR(250) NOT NULL,
        ActionReason NVARCHAR(500) NOT NULL,
        OldStateJson NVARCHAR(MAX) NULL,
        NewStateJson NVARCHAR(MAX) NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipmentSignatures_SignedAt DEFAULT SYSUTCDATETIME(),
        SourceWorkstation NVARCHAR(100) NULL,
        CONSTRAINT FK_LabEquipmentSignatures_Equipment
            FOREIGN KEY(EquipmentID) REFERENCES dbo.LabEquipment(EquipmentID)
    );

    CREATE INDEX IX_LabEquipmentSignatures_Equipment
        ON dbo.LabEquipmentSignatures(EquipmentID, SignedAt DESC, SignatureID DESC);
END;

EXEC(N'
CREATE OR ALTER TRIGGER dbo.TRG_LabEquipmentSignatures_AppendOnly
ON dbo.LabEquipmentSignatures
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 56401, ''Laboratory equipment signature evidence is append-only and cannot be updated or deleted.'', 1;
END;');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.LabEquipment')
      AND name = N'RowVersion'
      AND system_type_id = 189
      AND is_nullable = 0
)
    THROW 56402, 'dbo.LabEquipment.RowVersion must be ROWVERSION NOT NULL.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.triggers tr
    JOIN sys.sql_modules sm ON sm.object_id = tr.object_id
    WHERE tr.parent_id = OBJECT_ID(N'dbo.LabEquipmentSignatures')
      AND tr.name = N'TRG_LabEquipmentSignatures_AppendOnly'
      AND tr.is_disabled = 0
      AND LOWER(sm.definition) LIKE N'%laboratory equipment signature evidence is append-only and cannot be updated or deleted%'
)
    THROW 56403, 'Laboratory equipment append-only signature trigger is missing or invalid.', 1;
