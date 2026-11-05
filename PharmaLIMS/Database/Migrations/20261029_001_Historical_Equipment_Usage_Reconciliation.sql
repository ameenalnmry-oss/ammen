SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.LabEquipmentUsageReconciliations',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LabEquipmentUsageReconciliations
    (
        ReconciliationID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipmentUsageReconciliations PRIMARY KEY,
        Module NVARCHAR(20) NOT NULL,
        ParentRecordID INT NOT NULL,
        ResultRecordID INT NOT NULL,
        EquipmentID INT NOT NULL,
        EquipmentCodeSnapshot NVARCHAR(50) NOT NULL,
        EquipmentNameSnapshot NVARCHAR(200) NOT NULL,
        EquipmentTypeSnapshot NVARCHAR(100) NOT NULL,
        ResultEnteredAtSnapshot DATETIME2(0) NULL,
        GapType NVARCHAR(60) NOT NULL,
        EvidenceReference NVARCHAR(250) NOT NULL,
        EvidenceSummary NVARCHAR(1500) NOT NULL,
        MeaningOfSignature NVARCHAR(250) NOT NULL,
        ActionReason NVARCHAR(700) NOT NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipmentUsageReconciliations_SignedAt DEFAULT SYSUTCDATETIME(),
        SourceWorkstation NVARCHAR(100) NULL,
        SupersedesReconciliationID BIGINT NULL,
        CONSTRAINT FK_LabEquipmentUsageReconciliations_Equipment
            FOREIGN KEY(EquipmentID) REFERENCES dbo.LabEquipment(EquipmentID),
        CONSTRAINT FK_LabEquipmentUsageReconciliations_Supersedes
            FOREIGN KEY(SupersedesReconciliationID) REFERENCES dbo.LabEquipmentUsageReconciliations(ReconciliationID),
        CONSTRAINT CK_LabEquipmentUsageReconciliations_Module
            CHECK(Module IN(N'WATER',N'EM',N'PRM')),
        CONSTRAINT CK_LabEquipmentUsageReconciliations_GapType
            CHECK(GapType IN(N'MISSING_HISTORICAL_ASSIGNMENT',N'MISSING_SIGNED_HISTORY')),
        CONSTRAINT CK_LabEquipmentUsageReconciliations_Evidence
            CHECK(LEN(LTRIM(RTRIM(EvidenceReference)))>=4
              AND LEN(LTRIM(RTRIM(EvidenceSummary)))>=12
              AND LEN(LTRIM(RTRIM(MeaningOfSignature)))>=4
              AND LEN(LTRIM(RTRIM(ActionReason)))>=12
              AND LEN(LTRIM(RTRIM(SignedBy)))>=1)
    );

    CREATE INDEX IX_LabEquipmentUsageReconciliations_Result
        ON dbo.LabEquipmentUsageReconciliations(Module,ResultRecordID,ReconciliationID DESC);

    CREATE UNIQUE INDEX UX_LabEquipmentUsageReconciliations_Root
        ON dbo.LabEquipmentUsageReconciliations(Module,ResultRecordID)
        WHERE SupersedesReconciliationID IS NULL;

    CREATE UNIQUE INDEX UX_LabEquipmentUsageReconciliations_Supersedes
        ON dbo.LabEquipmentUsageReconciliations(SupersedesReconciliationID)
        WHERE SupersedesReconciliationID IS NOT NULL;
END;

EXEC(N'
CREATE OR ALTER TRIGGER dbo.TRG_LabEquipmentUsageReconciliations_AppendOnly_20261029
ON dbo.LabEquipmentUsageReconciliations
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 56831, ''Historical equipment-use reconciliations are append-only and cannot be updated or deleted.'', 1;
END;');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.LabEquipmentUsageReconciliations')
      AND name=N'TRG_LabEquipmentUsageReconciliations_AppendOnly_20261029'
      AND is_disabled=0
)
    THROW 56832, 'Historical equipment-use reconciliation append-only trigger is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.LabEquipmentUsageReconciliations')
      AND name=N'CK_LabEquipmentUsageReconciliations_Evidence'
      AND is_disabled=0 AND is_not_trusted=0
)
    THROW 56833, 'Historical equipment-use reconciliation evidence constraint is missing or untrusted.', 1;
