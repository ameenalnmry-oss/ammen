SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.WaterResultResourceEvidence',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.WaterResultResourceEvidence
 (
  EvidenceID BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
  BatchID UNIQUEIDENTIFIER NOT NULL,
  SampleID INT NOT NULL REFERENCES dbo.Samples(SampleID),
  SampleTestID INT NOT NULL REFERENCES dbo.SampleTests(SampleTestID),
  TestID INT NOT NULL,
  ResourceKind NVARCHAR(20) NOT NULL,
  EquipmentID INT NULL REFERENCES dbo.LabEquipment(EquipmentID),
  EquipmentCodeSnapshot NVARCHAR(50) NULL,
  EquipmentNameSnapshot NVARCHAR(200) NULL,
  KitCode NVARCHAR(100) NULL,
  KitLot NVARCHAR(100) NULL,
  KitExpiry DATE NULL,
  ResultSnapshot NVARCHAR(200) NOT NULL,
  SignedBy NVARCHAR(100) NOT NULL,
  SignedAt DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
  CONSTRAINT CK_WaterResourceKind CHECK(ResourceKind IN(N'INSTRUMENT',N'TEST_KIT',N'NO_INSTRUMENT')),
  CONSTRAINT CK_WaterResourceDetails CHECK(
   (ResourceKind=N'INSTRUMENT' AND EquipmentID IS NOT NULL AND KitCode IS NULL AND KitLot IS NULL AND KitExpiry IS NULL)
   OR (ResourceKind=N'TEST_KIT' AND EquipmentID IS NULL AND LEN(LTRIM(RTRIM(KitCode)))>0 AND LEN(LTRIM(RTRIM(KitLot)))>0 AND KitExpiry IS NOT NULL)
   OR (ResourceKind=N'NO_INSTRUMENT' AND EquipmentID IS NULL AND KitCode IS NULL AND KitLot IS NULL AND KitExpiry IS NULL))
 );
 CREATE INDEX IX_WaterResourceEvidence_Result ON dbo.WaterResultResourceEvidence(SampleTestID,EvidenceID DESC);
END;
EXEC(N'
CREATE OR ALTER TRIGGER dbo.TRG_WaterResourceEvidence_AppendOnly
ON dbo.WaterResultResourceEvidence
AFTER UPDATE, DELETE
AS
BEGIN
 SET NOCOUNT ON;
 THROW 56455, ''Water resource evidence is append-only.'', 1;
END;');
