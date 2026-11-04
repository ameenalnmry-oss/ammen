-- Ephemeral CI fixture only; never run this fixture on AMEEN or Production.
IF DB_NAME() NOT LIKE N'EQ001[_]UAT[_]%' THROW 56800,'Disposable EQ001 UAT only.',1;
IF OBJECT_ID(N'dbo.PRM_SpecificationContentHistory',N'U') IS NULL
 CREATE TABLE dbo.PRM_SpecificationContentHistory(
 HistoryID bigint IDENTITY PRIMARY KEY,SpecificationNo nvarchar(120) NOT NULL,SampleCategory nvarchar(40) NOT NULL,
 VersionNo int NOT NULL,ActionType nvarchar(60) NOT NULL,OldRowsJson nvarchar(max) NOT NULL,NewRowsJson nvarchar(max) NOT NULL,
 ChangedBy nvarchar(120) NOT NULL,ChangedAt datetime2(0) NOT NULL DEFAULT SYSDATETIME(),ChangeReason nvarchar(1000) NOT NULL,
 SignatureID bigint NULL,ContentHash binary(32) NOT NULL);
IF OBJECT_ID(N'dbo.TRG_PRM_SpecificationContentHistory_AppendOnly',N'TR') IS NULL
 EXEC(N'CREATE TRIGGER dbo.TRG_PRM_SpecificationContentHistory_AppendOnly ON dbo.PRM_SpecificationContentHistory AFTER UPDATE,DELETE AS
 BEGIN SET NOCOUNT ON; THROW 56456,''PRM specification content history is append-only and cannot be updated or deleted.'',1; END;');
