SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.LabEquipmentCompatibilityRules',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LabEquipmentCompatibilityRules
    (
        RuleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipmentCompatibilityRules PRIMARY KEY,
        Module NVARCHAR(20) NOT NULL,
        MatchKey NVARCHAR(220) NOT NULL,
        EquipmentType NVARCHAR(100) NOT NULL,
        RuleDescription NVARCHAR(500) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_LabEquipmentCompatibilityRules_IsActive DEFAULT(1),
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipmentCompatibilityRules_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_LabEquipmentCompatibilityRules_Module CHECK(Module IN(N'WATER',N'EM',N'PRM')),
        CONSTRAINT UQ_LabEquipmentCompatibilityRules UNIQUE(Module,MatchKey,EquipmentType)
    );

    CREATE INDEX IX_LabEquipmentCompatibilityRules_Lookup
        ON dbo.LabEquipmentCompatibilityRules(Module,MatchKey,IsActive,EquipmentType);
END;

MERGE dbo.LabEquipmentCompatibilityRules AS target
USING
(
    SELECT N'WATER' AS Module,N'TESTID:2' AS MatchKey,N'pH Meter' AS EquipmentType,
           N'Water pH result requires a controlled pH meter.' AS RuleDescription
    UNION ALL
    SELECT N'WATER',N'TESTID:3',N'Conductivity Meter',
           N'Water conductivity result requires a controlled conductivity meter.'
    UNION ALL
    SELECT N'EM',N'METHOD:ACTIVE AIR SAMPLING',N'Air Sampler',
           N'Environmental Monitoring Active Air Sampling requires a controlled air sampler.'
) AS source
ON target.Module=source.Module
AND target.MatchKey=source.MatchKey
AND target.EquipmentType=source.EquipmentType
WHEN MATCHED THEN
    UPDATE SET
        RuleDescription=source.RuleDescription,
        IsActive=1
WHEN NOT MATCHED THEN
    INSERT(Module,MatchKey,EquipmentType,RuleDescription,IsActive,CreatedBy)
    VALUES(source.Module,source.MatchKey,source.EquipmentType,source.RuleDescription,1,N'CONTROLLED-MIGRATION');

IF EXISTS
(
    SELECT 1
    FROM dbo.LabEquipmentCompatibilityRules
    WHERE Module=N'WATER' AND MatchKey=N'TESTID:2' AND EquipmentType<>N'pH Meter' AND IsActive=1
)
    THROW 56421, 'Unexpected active equipment compatibility rule exists for Water pH.', 1;

IF EXISTS
(
    SELECT 1
    FROM dbo.LabEquipmentCompatibilityRules
    WHERE Module=N'WATER' AND MatchKey=N'TESTID:3' AND EquipmentType<>N'Conductivity Meter' AND IsActive=1
)
    THROW 56422, 'Unexpected active equipment compatibility rule exists for Water conductivity.', 1;

IF EXISTS
(
    SELECT 1
    FROM dbo.LabEquipmentCompatibilityRules
    WHERE Module=N'EM' AND MatchKey=N'METHOD:ACTIVE AIR SAMPLING' AND EquipmentType<>N'Air Sampler' AND IsActive=1
)
    THROW 56423, 'Unexpected active equipment compatibility rule exists for EM Active Air Sampling.', 1;
