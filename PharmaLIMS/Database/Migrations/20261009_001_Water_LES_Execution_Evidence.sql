SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.WaterResultExecutionEvidence',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WaterResultExecutionEvidence
    (
        EvidenceID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WaterResultExecutionEvidence PRIMARY KEY,
        SampleID INT NOT NULL,
        SampleTestID INT NOT NULL,
        TestID INT NOT NULL,
        ProcedureReference NVARCHAR(100) NOT NULL,
        MethodGuidanceSnapshot NVARCHAR(1000) NOT NULL,
        SampleTemperatureC DECIMAL(5,2) NULL,
        VerificationReference NVARCHAR(200) NULL,
        VerificationConfirmed BIT NOT NULL,
        ExecutionRemarks NVARCHAR(1000) NULL,
        RawResultSnapshot NVARCHAR(200) NOT NULL,
        EquipmentID INT NOT NULL,
        EquipmentCodeSnapshot NVARCHAR(50) NOT NULL,
        EquipmentNameSnapshot NVARCHAR(200) NOT NULL,
        EquipmentTypeSnapshot NVARCHAR(100) NOT NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        MeaningOfSignature NVARCHAR(250) NOT NULL,
        ActionReason NVARCHAR(500) NOT NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_WaterResultExecutionEvidence_SignedAt DEFAULT SYSUTCDATETIME(),
        SourceWorkstation NVARCHAR(100) NULL,
        CONSTRAINT FK_WaterResultExecutionEvidence_Sample FOREIGN KEY(SampleID) REFERENCES dbo.Samples(SampleID),
        CONSTRAINT FK_WaterResultExecutionEvidence_SampleTest FOREIGN KEY(SampleTestID) REFERENCES dbo.SampleTests(SampleTestID),
        CONSTRAINT FK_WaterResultExecutionEvidence_Equipment FOREIGN KEY(EquipmentID) REFERENCES dbo.LabEquipment(EquipmentID),
        CONSTRAINT CK_WaterResultExecutionEvidence_Verification CHECK(VerificationConfirmed IN(0,1))
    );

    CREATE INDEX IX_WaterResultExecutionEvidence_Result
        ON dbo.WaterResultExecutionEvidence(SampleTestID,SignedAt DESC,EvidenceID DESC);
END;

EXEC(N'
CREATE OR ALTER TRIGGER dbo.TRG_WaterResultExecutionEvidence_AppendOnly
ON dbo.WaterResultExecutionEvidence
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 56441, ''Water LES execution evidence is append-only and cannot be updated or deleted.'', 1;
END;');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.triggers tr
    JOIN sys.sql_modules sm ON sm.object_id=tr.object_id
    WHERE tr.parent_id=OBJECT_ID(N'dbo.WaterResultExecutionEvidence')
      AND tr.name=N'TRG_WaterResultExecutionEvidence_AppendOnly'
      AND tr.is_disabled=0
      AND LOWER(sm.definition) LIKE N'%water les execution evidence is append-only and cannot be updated or deleted%'
)
    THROW 56442, 'Water LES execution evidence append-only trigger is missing or invalid.', 1;
