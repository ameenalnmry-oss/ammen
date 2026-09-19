SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
        THROW 53810, 'Required table dbo.QualityEvents is missing.', 1;

    IF OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventInvestigationEvidenceHistory
        (
            HistoryID BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_QualityEventInvestigationEvidenceHistory_20260828_001 PRIMARY KEY,
            QualityEventID INT NOT NULL,
            EvidenceTable NVARCHAR(100) NOT NULL,
            OldRowsJson NVARCHAR(MAX) NOT NULL,
            NewRowsJson NVARCHAR(MAX) NOT NULL,
            ChangedBy NVARCHAR(120) NOT NULL,
            ChangedAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_QEInvestigationEvidenceHistory_ChangedAt_20260828_001 DEFAULT SYSDATETIME(),
            ChangeReason NVARCHAR(1000) NOT NULL,
            CONSTRAINT FK_QEInvestigationEvidenceHistory_Event_20260828_001
                FOREIGN KEY (QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT CK_QEInvestigationEvidenceHistory_Table_20260828_001 CHECK
            (
                EvidenceTable IN
                (
                    N'QualityEventRootCauseWhys',
                    N'QualityEventImpactAssessments',
                    N'QualityEventCAPAItems',
                    N'QualityEventRetesting',
                    N'QualityEventDistribution'
                )
            ),
            CONSTRAINT CK_QEInvestigationEvidenceHistory_OldJson_20260828_001 CHECK (ISJSON(OldRowsJson) = 1),
            CONSTRAINT CK_QEInvestigationEvidenceHistory_NewJson_20260828_001 CHECK (ISJSON(NewRowsJson) = 1)
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'HistoryID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'QualityEventID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'EvidenceTable') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'OldRowsJson') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'NewRowsJson') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'ChangedBy') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'ChangedAt') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'ChangeReason') IS NULL
        THROW 53811, 'Existing dbo.QualityEventInvestigationEvidenceHistory does not match the controlled 20260828_001 structure.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory')
          AND name = N'IX_QEInvestigationEvidenceHistory_Event_20260828_001'
    )
    BEGIN
        CREATE INDEX IX_QEInvestigationEvidenceHistory_Event_20260828_001
            ON dbo.QualityEventInvestigationEvidenceHistory(QualityEventID, ChangedAt, HistoryID)
            INCLUDE (EvidenceTable, ChangedBy);
    END;

    EXEC sys.sp_executesql N'
        CREATE OR ALTER TRIGGER dbo.TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001
        ON dbo.QualityEventInvestigationEvidenceHistory
        AFTER UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 53812, ''Quality Event structured-evidence history is append-only and cannot be updated or deleted.'', 1;
        END;';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
