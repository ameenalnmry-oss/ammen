SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled PRM Quality Event reconciliation 20260824_003

    - Repairs additive columns required by the PRM investigation workflow.
    - Gives affected PRM results an explicit polymorphic source identity.
    - Keeps PRM records out of the water/general SampleID relationship.
    - Creates the three structured-evidence tables used directly by the PRM UI
      when a legacy database contains only a partial Quality Event schema.

    No historical result value, interpretation, signature, or disposition is
    created or substituted by this migration.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_NumberSequences', N'U') IS NULL
        THROW 53400, 'Required table dbo.PRM_NumberSequences is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 53401, 'Required table dbo.PRM_Samples is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
        THROW 53402, 'Required table dbo.PRM_SampleTests is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
        THROW 53403, 'Required table dbo.QualityEvents is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions', N'U') IS NULL
        THROW 53405, 'Required table dbo.QualityEventChecklistQuestions is missing. Apply 20260823_003 first.', 1;
    IF OBJECT_ID(N'dbo.ElectronicSignatures', N'U') IS NULL
        THROW 53406, 'Required table dbo.ElectronicSignatures is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_NumberSequences', N'SequenceName') IS NULL
       OR COL_LENGTH(N'dbo.PRM_NumberSequences', N'Prefix') IS NULL
       OR COL_LENGTH(N'dbo.PRM_NumberSequences', N'CurrentYear') IS NULL
       OR COL_LENGTH(N'dbo.PRM_NumberSequences', N'LastNumber') IS NULL
        THROW 53407, 'The PRM number-sequence table is structurally incomplete.', 1;

    IF COL_LENGTH(N'dbo.QualityEvents', N'QualityEventID') IS NULL
        THROW 53408, 'The Quality Event identity column is missing.', 1;
    IF COL_LENGTH(N'dbo.PRM_Samples', N'SampleID') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SampleTests', N'SampleTestID') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SampleTests', N'SampleID') IS NULL
       OR COL_LENGTH(N'dbo.PRM_SampleTests', N'Interpretation') IS NULL
        THROW 53416, 'The PRM sample or result identity schema is incomplete.', 1;
    IF COL_LENGTH(N'dbo.ElectronicSignatures', N'SignatureID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventChecklistQuestions', N'QuestionID') IS NULL
        THROW 53417, 'The electronic-signature or checklist-question identity schema is incomplete.', 1;
    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventAffectedResults
        (
            AffectedResultID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventAffectedResults PRIMARY KEY,
            QualityEventID INT NOT NULL,
            SampleTestID INT NULL,
            SourceModule NVARCHAR(80) NULL,
            SourceResultID INT NULL,
            TestID INT NULL,
            TestName NVARCHAR(200) NULL,
            ResultValue NVARCHAR(200) NULL,
            SpecificationLimit NVARCHAR(500) NULL,
            Unit NVARCHAR(50) NULL,
            FailureType NVARCHAR(120) NULL,
            CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventAffectedResults_Created_20260824_003 DEFAULT SYSDATETIME(),
            CONSTRAINT FK_QualityEventAffectedResults_Event_20260824_003 FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'AffectedResultID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults', N'QualityEventID') IS NULL
        THROW 53409, 'The affected-result identity or Quality Event link is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_NumberSequences', N'LastUpdated') IS NULL
        EXEC(N'ALTER TABLE dbo.PRM_NumberSequences ADD LastUpdated DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_NumberSequences_LastUpdated_20260824_003 DEFAULT SYSDATETIME();');

    IF COL_LENGTH(N'dbo.QualityEvents', N'EventNumber') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD EventNumber NVARCHAR(60) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'EventType') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD EventType NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'Severity') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD Severity NVARCHAR(60) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'SampleID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SampleID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'SampleNumber') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SampleNumber NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'SourceModule') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceModule NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'SourceRecordID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceRecordID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'CurrentStatus') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CurrentStatus NVARCHAR(60) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'DetectedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'DetectedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'DetectionSource') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectionSource NVARCHAR(160) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'InitialDescription') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD InitialDescription NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'ImmediateAction') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ImmediateAction NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'RootCauseCategory') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD RootCauseCategory NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'RootCauseDetails') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD RootCauseDetails NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'ImpactAssessment') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ImpactAssessment NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'CAPARequired') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CAPARequired BIT NULL CONSTRAINT DF_QualityEvents_CAPA_20260824_003 DEFAULT (0);');
    IF COL_LENGTH(N'dbo.QualityEvents', N'QAConclusion') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD QAConclusion NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'FinalDisposition') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD FinalDisposition NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'ClosedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ClosedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'ClosedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ClosedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'CreatedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CreatedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'CreatedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CreatedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'ModifiedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ModifiedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents', N'ModifiedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ModifiedDate DATETIME2(0) NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEvents')
          AND name = N'SampleID'
          AND system_type_id <> TYPE_ID(N'int')
    )
        THROW 53411, 'The existing QualityEvents.SampleID column is not INT.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEvents')
          AND name = N'SampleID'
          AND is_nullable = 0
    )
        EXEC(N'ALTER TABLE dbo.QualityEvents ALTER COLUMN SampleID INT NULL;');

    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SampleTestID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SampleTestID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'TestID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD TestID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'TestName') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD TestName NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'ResultValue') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD ResultValue NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SpecificationLimit') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SpecificationLimit NVARCHAR(500) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'Unit') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD Unit NVARCHAR(50) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'FailureType') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD FailureType NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'CreatedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD CreatedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceModule') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SourceModule NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceResultID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SourceResultID INT NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name = N'SampleTestID'
          AND system_type_id <> TYPE_ID(N'int')
    )
        THROW 53412, 'The existing affected-result SampleTestID column is not INT.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name = N'SampleTestID'
          AND is_nullable = 0
    )
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN SampleTestID INT NULL;');

    IF OBJECT_ID(N'dbo.QualityEventActions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventActions
        (
            QualityEventActionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventActions PRIMARY KEY,
            QualityEventID INT NOT NULL,
            ActionType NVARCHAR(100) NOT NULL,
            ActionDescription NVARCHAR(MAX) NULL,
            PerformedBy NVARCHAR(100) NULL,
            PerformedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventActions_Date_20260824_003 DEFAULT SYSUTCDATETIME(),
            ElectronicSignatureID INT NULL,
            CONSTRAINT FK_QualityEventActions_Event_20260824_003 FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT FK_QualityEventActions_Signature_20260824_003 FOREIGN KEY(ElectronicSignatureID) REFERENCES dbo.ElectronicSignatures(SignatureID)
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventActions', N'QualityEventActionID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventActions', N'QualityEventID') IS NULL
        THROW 53413, 'The Quality Event action identity or event link is missing.', 1;
    IF COL_LENGTH(N'dbo.QualityEventActions', N'ActionType') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD ActionType NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions', N'ActionDescription') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD ActionDescription NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions', N'PerformedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD PerformedBy NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions', N'PerformedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD PerformedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions', N'ElectronicSignatureID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD ElectronicSignatureID INT NULL;');

    IF OBJECT_ID(N'dbo.QualityEventChecklistAnswers', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventChecklistAnswers
        (
            AnswerID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventChecklistAnswers PRIMARY KEY,
            QualityEventID INT NOT NULL,
            QuestionID INT NOT NULL,
            AnswerValue NVARCHAR(250) NULL,
            Comments NVARCHAR(MAX) NULL,
            AnsweredBy NVARCHAR(100) NULL,
            AnsweredDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventChecklistAnswers_Date_20260824_003 DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_QualityEventChecklistAnswers_Event_20260824_003 FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT FK_QualityEventChecklistAnswers_Question_20260824_003 FOREIGN KEY(QuestionID) REFERENCES dbo.QualityEventChecklistQuestions(QuestionID),
            CONSTRAINT UQ_QualityEventChecklistAnswers_20260824_003 UNIQUE(QualityEventID, QuestionID)
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnswerID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'QualityEventID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'QuestionID') IS NULL
        THROW 53414, 'The checklist-answer identity, event link, or question link is missing.', 1;
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnswerValue') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnswerValue NVARCHAR(250) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'Comments') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD Comments NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnsweredBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnsweredBy NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnsweredDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnsweredDate DATETIME2(0) NULL;');

    IF OBJECT_ID(N'dbo.QualityEventPrintHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventPrintHistory
        (
            QualityEventPrintID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventPrintHistory PRIMARY KEY,
            QualityEventID INT NOT NULL,
            PrintedBy NVARCHAR(100) NOT NULL,
            PrintedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventPrintHistory_Date_20260824_003 DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_QualityEventPrintHistory_Event_20260824_003 FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'QualityEventPrintID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventPrintHistory', N'QualityEventID') IS NULL
        THROW 53415, 'The Quality Event print-history identity or event link is missing.', 1;
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'PrintedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD PrintedBy NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'PrintedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD PrintedDate DATETIME2(0) NULL;');

    /* PRM rows use the polymorphic source key. Never make them water/general samples. */
    EXEC sys.sp_executesql N'
UPDATE dbo.QualityEvents
SET SampleID = NULL
WHERE UPPER(LTRIM(RTRIM(ISNULL(SourceModule, N'''')))) = N''PRM''
  AND SampleID IS NOT NULL;

UPDATE affected
SET SourceModule = N''PRM'',
    SourceResultID = affected.SampleTestID
FROM dbo.QualityEventAffectedResults AS affected
INNER JOIN dbo.QualityEvents AS qualityEvent
    ON qualityEvent.QualityEventID = affected.QualityEventID
WHERE UPPER(LTRIM(RTRIM(ISNULL(qualityEvent.SourceModule, N'''')))) = N''PRM''
  AND affected.SourceResultID IS NULL
  AND affected.SampleTestID IS NOT NULL;

IF EXISTS
(
    SELECT QualityEventID, SourceModule, SourceResultID
    FROM dbo.QualityEventAffectedResults
    WHERE SourceModule IS NOT NULL AND SourceResultID IS NOT NULL
    GROUP BY QualityEventID, SourceModule, SourceResultID
    HAVING COUNT_BIG(*) > 1
)
    THROW 53410, ''Duplicate affected-result source links require controlled reconciliation.'', 1;';

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEvents')
          AND name IN (N'IX_QualityEvents_SourceStatus_20260824', N'IX_QualityEvents_SourceStatus_20260824_003')
    )
        EXEC(N'CREATE INDEX IX_QualityEvents_SourceStatus_20260824_003 ON dbo.QualityEvents(SourceModule,SourceRecordID,CurrentStatus,QualityEventID);');

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name = N'UX_QualityEventAffectedResults_Source_20260824_003'
    )
        EXEC(N'CREATE UNIQUE INDEX UX_QualityEventAffectedResults_Source_20260824_003 ON dbo.QualityEventAffectedResults(QualityEventID,SourceModule,SourceResultID) WHERE SourceModule IS NOT NULL AND SourceResultID IS NOT NULL;');

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventActions')
          AND name IN (N'IX_QualityEventActions_Event', N'IX_QualityEventActions_Event_20260824_003')
    )
        EXEC(N'CREATE INDEX IX_QualityEventActions_Event_20260824_003 ON dbo.QualityEventActions(QualityEventID,PerformedDate,QualityEventActionID);');

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND name IN (N'IX_QualityEventPrintHistory_Event', N'IX_QualityEventPrintHistory_Event_20260824_003')
    )
        EXEC(N'CREATE INDEX IX_QualityEventPrintHistory_Event_20260824_003 ON dbo.QualityEventPrintHistory(QualityEventID,PrintedDate,QualityEventPrintID);');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
