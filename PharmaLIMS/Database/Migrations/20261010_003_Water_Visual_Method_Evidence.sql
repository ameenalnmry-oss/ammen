SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.WaterVisualMethodEvidence',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.WaterVisualMethodEvidence(
 EvidenceID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_WaterVisualMethodEvidence PRIMARY KEY,
 SampleID int NOT NULL,
 SampleTestID int NOT NULL,
 ResourceEvidenceID bigint NOT NULL,
 VisualMethodReference nvarchar(200) NOT NULL,
 ObservationDescription nvarchar(1000) NOT NULL,
 ExecutedBy nvarchar(100) NOT NULL,
 SignedBy nvarchar(100) NOT NULL,
 SignedAt datetime2(3) NOT NULL CONSTRAINT DF_WaterVisualMethodEvidence_SignedAt DEFAULT SYSUTCDATETIME(),
 IsVoid bit NOT NULL CONSTRAINT DF_WaterVisualMethodEvidence_IsVoid DEFAULT 0,
 CONSTRAINT FK_WaterVisualMethodEvidence_Sample FOREIGN KEY(SampleID) REFERENCES dbo.Samples(SampleID),
 CONSTRAINT FK_WaterVisualMethodEvidence_Resource FOREIGN KEY(ResourceEvidenceID) REFERENCES dbo.WaterResultResourceEvidence(EvidenceID),
 CONSTRAINT CK_WaterVisualMethodEvidence_Method CHECK(LEN(LTRIM(RTRIM(VisualMethodReference)))>0),
 CONSTRAINT CK_WaterVisualMethodEvidence_Observation CHECK(LEN(LTRIM(RTRIM(ObservationDescription)))>0)
 );
 CREATE INDEX IX_WaterVisualMethodEvidence_Link ON dbo.WaterVisualMethodEvidence(SampleTestID,SampleID,ResourceEvidenceID);
END;
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_WaterVisualMethodEvidence_Immutable ON dbo.WaterVisualMethodEvidence AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 56542,''Visual method evidence cannot be updated or deleted.'',1; END;');
