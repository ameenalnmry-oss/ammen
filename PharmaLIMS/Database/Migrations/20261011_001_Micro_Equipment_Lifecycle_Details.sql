SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.MicroEquipmentActivities',N'U') IS NULL
 THROW 56520,'Micro equipment activity ledger must be installed first.',1;
IF OBJECT_ID(N'dbo.MicroEquipmentLifecycleDetails',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.MicroEquipmentLifecycleDetails(
  ActivityID BIGINT NOT NULL CONSTRAINT PK_MicroEquipmentLifecycleDetails PRIMARY KEY
   CONSTRAINT FK_MicroEquipmentLifecycleDetails_Activity REFERENCES dbo.MicroEquipmentActivities(ActivityID),
  ProcedureReference NVARCHAR(200) NOT NULL,
  WorkDetails NVARCHAR(MAX) NOT NULL,
  MaterialName NVARCHAR(200) NULL,
  MaterialLot NVARCHAR(100) NULL,
  Concentration NVARCHAR(100) NULL,
  ContactTimeMinutes DECIMAL(10,2) NULL,
  RecordedBy NVARCHAR(100) NOT NULL,
  RecordedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MicroEquipmentLifecycleDetails_Date DEFAULT SYSUTCDATETIME(),
  CONSTRAINT CK_MicroEquipmentLifecycleDetails_Procedure CHECK (LEN(LTRIM(RTRIM(ProcedureReference)))>0),
  CONSTRAINT CK_MicroEquipmentLifecycleDetails_Work CHECK (LEN(LTRIM(RTRIM(WorkDetails)))>0),
  CONSTRAINT CK_MicroEquipmentLifecycleDetails_Contact CHECK (ContactTimeMinutes IS NULL OR ContactTimeMinutes>=0)
 );
END;
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_MicroEquipmentLifecycleDetails_Immutable ON dbo.MicroEquipmentLifecycleDetails AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 56521,''Lifecycle evidence is append-only; use a controlled correction'',1; END;');
