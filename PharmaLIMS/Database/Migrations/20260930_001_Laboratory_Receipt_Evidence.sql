SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 IF OBJECT_ID(N'dbo.LaboratoryReceipts',N'U') IS NULL
 BEGIN
  CREATE TABLE dbo.LaboratoryReceipts(
   ReceiptID int IDENTITY PRIMARY KEY,
   PrmSampleID int NULL REFERENCES dbo.PRM_Samples(SampleID),
   EmEventID int NULL REFERENCES dbo.EM_Events(Id),
   SampleNumber nvarchar(100) NOT NULL,
   ReceivedDateTime datetime2(0) NOT NULL,
   ReceivedBy nvarchar(100) NOT NULL,
   ReceiptDecision nvarchar(50) NOT NULL CHECK(ReceiptDecision=N'Accepted'),
   ActionReason nvarchar(1000) NOT NULL CHECK(LEN(LTRIM(RTRIM(ActionReason)))>=10),
   MeaningOfSignature nvarchar(255) NOT NULL CHECK(LEN(LTRIM(RTRIM(MeaningOfSignature)))>=3),
   UserRole nvarchar(100) NOT NULL,
   RecordedAt datetime2(0) NOT NULL DEFAULT SYSDATETIME(),
   SourceWorkstation nvarchar(200) NOT NULL,
   CONSTRAINT CK_LaboratoryReceipts_Source CHECK(
    (PrmSampleID IS NOT NULL AND EmEventID IS NULL) OR
    (PrmSampleID IS NULL AND EmEventID IS NOT NULL)),
   CONSTRAINT CK_LaboratoryReceipts_Signer CHECK(LEN(LTRIM(RTRIM(ReceivedBy)))>0)
  );
  CREATE UNIQUE INDEX UX_LaboratoryReceipts_PRM ON dbo.LaboratoryReceipts(PrmSampleID) WHERE PrmSampleID IS NOT NULL;
  CREATE UNIQUE INDEX UX_LaboratoryReceipts_EM ON dbo.LaboratoryReceipts(EmEventID) WHERE EmEventID IS NOT NULL;
 END;
 EXEC sys.sp_executesql N'CREATE OR ALTER TRIGGER dbo.TRG_LaboratoryReceipts_AppendOnly
 ON dbo.LaboratoryReceipts INSTEAD OF UPDATE, DELETE AS
 BEGIN
 SET NOCOUNT ON;
 THROW 55301, ''Laboratory receipt evidence is append-only and cannot be updated or deleted.'',1;
 END;';
 ENABLE TRIGGER dbo.TRG_LaboratoryReceipts_AppendOnly ON dbo.LaboratoryReceipts;
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
