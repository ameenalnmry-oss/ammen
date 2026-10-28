SET NOCOUNT ON;
SET XACT_ABORT ON;

-- Add evidence storage only. Existing specifications and historical facts are untouched.
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
        THROW 56454, 'PRM specification master is required before content history.', 1;
    IF OBJECT_ID(N'dbo.PRM_SpecificationSignatures', N'U') IS NULL
        THROW 56454, 'PRM specification signatures are required before content history.', 1;

    IF OBJECT_ID(N'dbo.PRM_SpecificationContentHistory', N'U') IS NULL
        CREATE TABLE dbo.PRM_SpecificationContentHistory
        (
            HistoryID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_SpecificationContentHistory PRIMARY KEY,
            SpecificationNo nvarchar(120) NOT NULL,
            SampleCategory nvarchar(40) NOT NULL,
            VersionNo int NOT NULL,
            ActionType nvarchar(60) NOT NULL,
            OldRowsJson nvarchar(max) NOT NULL,
            NewRowsJson nvarchar(max) NOT NULL,
            ChangedBy nvarchar(120) NOT NULL,
            ChangedAt datetime2(0) NOT NULL CONSTRAINT DF_PRM_SpecificationContentHistory_ChangedAt DEFAULT SYSDATETIME(),
            ChangeReason nvarchar(1000) NOT NULL,
            SignatureID bigint NULL,
            ContentHash binary(32) NOT NULL
        );

    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationContentHistory') AND name=N'IX_PRM_SpecificationContentHistory_Version')
        CREATE INDEX IX_PRM_SpecificationContentHistory_Version
            ON dbo.PRM_SpecificationContentHistory(SpecificationNo, SampleCategory, VersionNo, HistoryID);

    EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.TRG_PRM_SpecificationContentHistory_AppendOnly
ON dbo.PRM_SpecificationContentHistory
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 56456, ''PRM specification content history is append-only and cannot be updated or deleted.'', 1;
END;';
    ENABLE TRIGGER dbo.TRG_PRM_SpecificationContentHistory_AppendOnly ON dbo.PRM_SpecificationContentHistory;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
