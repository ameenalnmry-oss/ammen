SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS controlled migration 20260810_001
    Purpose:
      - Store external Water and Environmental Monitoring trend files separately
        from native LIMS samples and results.
      - Preserve the original file, SHA-256 hash, validated rows, approval identity,
        and immutable row history.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ExternalTrendImportBatches
        (
            ImportBatchID       INT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_ExternalTrendImportBatches PRIMARY KEY,
            ImportNumber        NVARCHAR(50) NOT NULL,
            ModuleName          NVARCHAR(40) NOT NULL,
            SourceSystem        NVARCHAR(150) NOT NULL,
            OriginalFileName    NVARCHAR(260) NOT NULL,
            FileExtension       NVARCHAR(10) NOT NULL,
            FileSizeBytes       BIGINT NOT NULL,
            FileHashSha256      CHAR(64) NOT NULL,
            OriginalFile        VARBINARY(MAX) NOT NULL,
            ImportReason        NVARCHAR(1000) NOT NULL,
            [RowCount]          INT NOT NULL,
            Status              NVARCHAR(30) NOT NULL
                CONSTRAINT DF_ExternalTrendImportBatches_Status DEFAULT N'Pending Approval',
            ImportedBy          NVARCHAR(100) NOT NULL,
            ImportedRole        NVARCHAR(100) NOT NULL,
            ImportedAt          DATETIMEOFFSET(7) NOT NULL,
            ApprovedBy          NVARCHAR(100) NULL,
            ApprovedRole        NVARCHAR(100) NULL,
            ApprovedAt          DATETIMEOFFSET(7) NULL,
            ApprovalMeaning     NVARCHAR(300) NULL,
            ApprovalReason      NVARCHAR(1000) NULL,
            RejectedBy          NVARCHAR(100) NULL,
            RejectedRole        NVARCHAR(100) NULL,
            RejectedAt          DATETIMEOFFSET(7) NULL,
            RejectionMeaning    NVARCHAR(300) NULL,
            RejectionReason     NVARCHAR(1000) NULL,
            SourceWorkstation   NVARCHAR(200) NOT NULL,
            RowVersion          ROWVERSION NOT NULL,
            CONSTRAINT UQ_ExternalTrendImportBatches_ImportNumber UNIQUE (ImportNumber),
            CONSTRAINT UQ_ExternalTrendImportBatches_FileHash UNIQUE (FileHashSha256),
            CONSTRAINT CK_ExternalTrendImportBatches_Module
                CHECK (ModuleName IN (N'Water', N'Environmental Monitoring')),
            CONSTRAINT CK_ExternalTrendImportBatches_Status
                CHECK (Status IN (N'Pending Approval', N'Approved', N'Rejected')),
            CONSTRAINT CK_ExternalTrendImportBatches_Lifecycle
                CHECK
                (
                    (Status = N'Pending Approval' AND ApprovedBy IS NULL AND ApprovedRole IS NULL AND ApprovedAt IS NULL
                     AND ApprovalMeaning IS NULL AND ApprovalReason IS NULL
                     AND RejectedBy IS NULL AND RejectedRole IS NULL AND RejectedAt IS NULL
                     AND RejectionMeaning IS NULL AND RejectionReason IS NULL)
                    OR
                    (Status = N'Approved' AND ApprovedBy IS NOT NULL AND ApprovedRole IS NOT NULL AND ApprovedAt IS NOT NULL
                     AND ApprovalMeaning IS NOT NULL AND ApprovalReason IS NOT NULL
                     AND RejectedBy IS NULL AND RejectedRole IS NULL AND RejectedAt IS NULL
                     AND RejectionMeaning IS NULL AND RejectionReason IS NULL)
                    OR
                    (Status = N'Rejected' AND RejectedBy IS NOT NULL AND RejectedRole IS NOT NULL AND RejectedAt IS NOT NULL
                     AND RejectionMeaning IS NOT NULL AND RejectionReason IS NOT NULL
                     AND ApprovedBy IS NULL AND ApprovedRole IS NULL AND ApprovedAt IS NULL
                     AND ApprovalMeaning IS NULL AND ApprovalReason IS NULL)
                ),
            CONSTRAINT CK_ExternalTrendImportBatches_RowCount CHECK ([RowCount] > 0),
            CONSTRAINT CK_ExternalTrendImportBatches_FileSize CHECK (FileSizeBytes > 0)
        );
    END;

    IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ExternalTrendImportRows
        (
            ImportRowID         BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_ExternalTrendImportRows PRIMARY KEY,
            ImportBatchID       INT NOT NULL,
            SourceRowNumber     INT NOT NULL,
            SourceRecordID      NVARCHAR(150) NULL,
            RecordDateTime      DATETIMEOFFSET(7) NOT NULL,
            EntityCode          NVARCHAR(150) NOT NULL,
            EntityName          NVARCHAR(300) NULL,
            LocationName        NVARCHAR(300) NULL,
            MethodName          NVARCHAR(200) NULL,
            ParameterName       NVARCHAR(300) NOT NULL,
            ResultValue         DECIMAL(38,10) NOT NULL,
            UnitName            NVARCHAR(100) NOT NULL,
            AlertLimit          DECIMAL(38,10) NULL,
            ActionLimit         DECIMAL(38,10) NULL,
            ResultStatus        NVARCHAR(20) NOT NULL,
            Remarks             NVARCHAR(1000) NULL,
            CreatedAt           DATETIMEOFFSET(7) NOT NULL,
            CONSTRAINT FK_ExternalTrendImportRows_Batch
                FOREIGN KEY (ImportBatchID)
                REFERENCES dbo.ExternalTrendImportBatches(ImportBatchID),
            CONSTRAINT UQ_ExternalTrendImportRows_BatchRow
                UNIQUE (ImportBatchID, SourceRowNumber),
            CONSTRAINT CK_ExternalTrendImportRows_Status
                CHECK (ResultStatus IN (N'PASS', N'ALERT', N'FAIL', N'UNASSESSED'))
        );

        CREATE INDEX IX_ExternalTrendImportRows_Trend
            ON dbo.ExternalTrendImportRows
               (ImportBatchID, ParameterName, RecordDateTime)
            INCLUDE (EntityCode, ResultValue, UnitName, AlertLimit, ActionLimit, ResultStatus);

        CREATE UNIQUE INDEX UX_ExternalTrendImportRows_SourceRecord
            ON dbo.ExternalTrendImportRows (ImportBatchID, SourceRecordID)
            WHERE SourceRecordID IS NOT NULL;
    END;

    IF OBJECT_ID(N'dbo.TR_ExternalTrendImportRows_Immutable', N'TR') IS NULL
    BEGIN
        EXEC(N'
CREATE TRIGGER dbo.TR_ExternalTrendImportRows_Immutable
ON dbo.ExternalTrendImportRows
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51080, ''Approved or staged external trend rows are immutable. Create a new import batch for corrections.'', 1;
END;');
    END;

    IF OBJECT_ID(N'dbo.TR_ExternalTrendImportBatches_NoDelete', N'TR') IS NULL
    BEGIN
        EXEC(N'
CREATE TRIGGER dbo.TR_ExternalTrendImportBatches_NoDelete
ON dbo.ExternalTrendImportBatches
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51081, ''External trend import batches cannot be deleted.'', 1;
END;');
    END;

    IF OBJECT_ID(N'dbo.TR_ExternalTrendImportBatches_ProtectSource', N'TR') IS NULL
    BEGIN
        EXEC(N'
CREATE TRIGGER dbo.TR_ExternalTrendImportBatches_ProtectSource
ON dbo.ExternalTrendImportBatches
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(ImportNumber) OR UPDATE(ModuleName) OR UPDATE(SourceSystem)
       OR UPDATE(OriginalFileName) OR UPDATE(FileExtension) OR UPDATE(FileSizeBytes)
       OR UPDATE(FileHashSha256) OR UPDATE(OriginalFile) OR UPDATE(ImportReason)
       OR UPDATE([RowCount]) OR UPDATE(ImportedBy) OR UPDATE(ImportedRole) OR UPDATE(ImportedAt)
       OR UPDATE(SourceWorkstation)
    BEGIN
        THROW 51082, ''External trend import source metadata is immutable.'', 1;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN deleted d ON d.ImportBatchID = i.ImportBatchID
        WHERE d.Status <> N''Pending Approval''
           OR i.Status NOT IN (N''Approved'', N''Rejected'')
    )
    BEGIN
        THROW 51083, ''External trend import approval/rejection is a single immutable lifecycle transition.'', 1;
    END;
END;');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT
    N'20260810_001' AS MigrationVersion,
    OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') AS BatchTableObjectID,
    OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') AS RowTableObjectID;
