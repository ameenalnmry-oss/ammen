SET NOCOUNT ON;
SET XACT_ABORT ON;
IF COL_LENGTH(N'dbo.WaterResultResourceEvidence',N'ExecutionDate') IS NULL
 ALTER TABLE dbo.WaterResultResourceEvidence ADD ExecutionDate date NULL;
IF COL_LENGTH(N'dbo.MediaPreparations',N'WorkflowRowVersion') IS NULL
 ALTER TABLE dbo.MediaPreparations ADD WorkflowRowVersion rowversion;
IF OBJECT_ID(N'dbo.PRM_SpecificationContentHistory',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.PRM_SpecificationContentHistory(
 HistoryID bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,SpecificationNo nvarchar(120) NOT NULL,
 SampleCategory nvarchar(40) NOT NULL,VersionNo int NOT NULL,ActionType nvarchar(60) NOT NULL,
 OldRowsJson nvarchar(max) NOT NULL CHECK(ISJSON(OldRowsJson)=1),NewRowsJson nvarchar(max) NOT NULL CHECK(ISJSON(NewRowsJson)=1),
 ChangedBy nvarchar(120) NOT NULL,ChangedAt datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),ChangeReason nvarchar(1000) NOT NULL,
 SignatureID bigint NULL REFERENCES dbo.PRM_SpecificationSignatures(SignatureID),ContentHash binary(32) NOT NULL);
 CREATE INDEX IX_PRM_SpecificationContentHistory_Scope ON dbo.PRM_SpecificationContentHistory(SpecificationNo,SampleCategory,VersionNo,HistoryID);
 CREATE UNIQUE INDEX UX_PRM_SpecificationContentHistory_Signature ON dbo.PRM_SpecificationContentHistory(SignatureID) WHERE SignatureID IS NOT NULL;
END;
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_PRM_SpecificationContentHistory_AppendOnly ON dbo.PRM_SpecificationContentHistory AFTER UPDATE,DELETE AS
BEGIN SET NOCOUNT ON; THROW 56456,''PRM specification content history is append-only and cannot be updated or deleted.'',1; END;');
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_CultureMedia_ReferencedIdentity ON dbo.CultureMedia AFTER UPDATE,DELETE AS
BEGIN SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.MediaID=d.MediaID
 WHERE EXISTS(SELECT 1 FROM dbo.CultureMediaLots l WHERE l.MediaID=d.MediaID)
 AND (i.MediaID IS NULL OR EXISTS
 (SELECT CONVERT(varbinary(max),i.MediaCode),CONVERT(varbinary(max),i.MediaName),CONVERT(varbinary(max),i.MediaType),CONVERT(varbinary(max),i.Manufacturer),CONVERT(varbinary(max),i.StorageCondition)
 EXCEPT SELECT CONVERT(varbinary(max),d.MediaCode),CONVERT(varbinary(max),d.MediaName),CONVERT(varbinary(max),d.MediaType),CONVERT(varbinary(max),d.Manufacturer),CONVERT(varbinary(max),d.StorageCondition))))
 THROW 56457,''Referenced culture media identity is frozen; use controlled QA correction.'',1;
END;');
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_CultureMediaLots_ReferencedIdentity ON dbo.CultureMediaLots AFTER UPDATE,DELETE AS
BEGIN SET NOCOUNT ON;
 -- A stock/status update must not read preparation/qualification rows: their
 -- workflows already hold those rows before updating stock on this lot.
 IF EXISTS(SELECT 1 FROM inserted) AND NOT EXISTS
 (SELECT i.MediaLotID,i.MediaID,CONVERT(varbinary(max),i.LotNumber),CONVERT(varbinary(max),i.ManufacturerLot),CONVERT(varbinary(max),i.Supplier),CONVERT(varbinary(max),i.COANumber),i.ReceivedDate,i.ExpiryDate,CONVERT(varbinary(max),i.QuantityReceived),CONVERT(varbinary(max),i.ReceivedBy) FROM inserted i
 EXCEPT SELECT d.MediaLotID,d.MediaID,CONVERT(varbinary(max),d.LotNumber),CONVERT(varbinary(max),d.ManufacturerLot),CONVERT(varbinary(max),d.Supplier),CONVERT(varbinary(max),d.COANumber),d.ReceivedDate,d.ExpiryDate,CONVERT(varbinary(max),d.QuantityReceived),CONVERT(varbinary(max),d.ReceivedBy) FROM deleted d)
 RETURN;
 IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.MediaLotID=d.MediaLotID
 WHERE (EXISTS(SELECT 1 FROM dbo.MediaQualifications q WHERE q.MediaLotID=d.MediaLotID) OR EXISTS(SELECT 1 FROM dbo.MediaPreparations p WHERE p.MediaLotID=d.MediaLotID))
 AND (i.MediaLotID IS NULL OR EXISTS
 (SELECT i.MediaID,CONVERT(varbinary(max),i.LotNumber),CONVERT(varbinary(max),i.ManufacturerLot),CONVERT(varbinary(max),i.Supplier),CONVERT(varbinary(max),i.COANumber),i.ReceivedDate,i.ExpiryDate,CONVERT(varbinary(max),i.QuantityReceived),CONVERT(varbinary(max),i.ReceivedBy)
 EXCEPT SELECT d.MediaID,CONVERT(varbinary(max),d.LotNumber),CONVERT(varbinary(max),d.ManufacturerLot),CONVERT(varbinary(max),d.Supplier),CONVERT(varbinary(max),d.COANumber),d.ReceivedDate,d.ExpiryDate,CONVERT(varbinary(max),d.QuantityReceived),CONVERT(varbinary(max),d.ReceivedBy))))
 THROW 56458,''Qualified/prepared culture media lot identity is frozen; use controlled QA correction.'',1;
END;');
IF OBJECT_ID(N'dbo.CK_QEInvestigationEvidenceHistory_Table_20260828_001',N'C') IS NOT NULL
 ALTER TABLE dbo.QualityEventInvestigationEvidenceHistory DROP CONSTRAINT CK_QEInvestigationEvidenceHistory_Table_20260828_001;
IF OBJECT_ID(N'dbo.CK_QEInvestigationEvidenceHistory_Table_20261010_001',N'C') IS NULL
 ALTER TABLE dbo.QualityEventInvestigationEvidenceHistory WITH CHECK ADD CONSTRAINT CK_QEInvestigationEvidenceHistory_Table_20261010_001 CHECK
 (EvidenceTable IN(N'QualityEventRootCauseWhys',N'QualityEventImpactAssessments',N'QualityEventCAPAItems',N'QualityEventRetesting',N'QualityEventDistribution',N'QualityEvents',N'QualityEventChecklistAnswers'));
