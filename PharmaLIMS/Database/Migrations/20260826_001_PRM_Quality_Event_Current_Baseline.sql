SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS PRM Quality Event current-schema baseline.

    Design rules:
    - Runtime workflow buttons never change database schema.
    - This migration is applied during controlled Development startup only.
    - Existing PRM samples, results, Quality Events, signatures, actions, answers,
      print history, and audit evidence are preserved.
    - No table, column, constraint, index, or row is dropped.
    - No existing relationship value is cleared or rewritten.
    - Existing canonical columns must have compatible SQL types; incompatible
      structures fail closed with a precise error instead of being guessed.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_NumberSequences', N'U') IS NULL
        THROW 53600, 'Required table dbo.PRM_NumberSequences is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 53601, 'Required table dbo.PRM_Samples is missing.', 1;
    IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
        THROW 53602, 'Required table dbo.PRM_SampleTests is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
        THROW 53603, 'Required table dbo.QualityEvents is missing.', 1;
    IF OBJECT_ID(N'dbo.ElectronicSignatures', N'U') IS NULL
        THROW 53604, 'Required table dbo.ElectronicSignatures is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions', N'U') IS NULL
        THROW 53605, 'Required table dbo.QualityEventChecklistQuestions is missing.', 1;

    IF COL_LENGTH(N'dbo.QualityEvents', N'QualityEventID') IS NULL
        THROW 53606, 'Required column dbo.QualityEvents.QualityEventID is missing.', 1;
    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEvents')
          AND name = N'QualityEventID'
          AND (system_type_id <> TYPE_ID(N'int') OR is_nullable = 1 OR is_computed = 1)
    )
        THROW 53607, 'dbo.QualityEvents.QualityEventID must be a non-null INT identity/key column.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.QualityEvents')
          AND i.is_unique = 1
          AND i.is_disabled = 0
          AND EXISTS
          (
              SELECT 1
              FROM sys.index_columns ic
              INNER JOIN sys.columns c
                ON c.object_id = ic.object_id
               AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal = 1
                AND c.name = N'QualityEventID'
          )
          AND 1 =
          (
              SELECT COUNT(*)
              FROM sys.index_columns ic
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal > 0
          )
    )
        THROW 53608, 'dbo.QualityEvents.QualityEventID is not protected by a single-column unique key.', 1;

    IF COL_LENGTH(N'dbo.PRM_NumberSequences', N'LastUpdated') IS NULL
        ALTER TABLE dbo.PRM_NumberSequences ADD LastUpdated DATETIME2(0) NULL;

    /* Canonical QualityEvents projection used by PRM. */
    IF COL_LENGTH(N'dbo.QualityEvents', N'EventNumber') IS NULL
        ALTER TABLE dbo.QualityEvents ADD EventNumber NVARCHAR(60) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'EventType') IS NULL
        ALTER TABLE dbo.QualityEvents ADD EventType NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'Severity') IS NULL
        ALTER TABLE dbo.QualityEvents ADD Severity NVARCHAR(60) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'SampleID') IS NULL
        ALTER TABLE dbo.QualityEvents ADD SampleID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'SampleNumber') IS NULL
        ALTER TABLE dbo.QualityEvents ADD SampleNumber NVARCHAR(80) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'SourceModule') IS NULL
        ALTER TABLE dbo.QualityEvents ADD SourceModule NVARCHAR(80) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'SourceRecordID') IS NULL
        ALTER TABLE dbo.QualityEvents ADD SourceRecordID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'CurrentStatus') IS NULL
        ALTER TABLE dbo.QualityEvents ADD CurrentStatus NVARCHAR(60) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'DetectedBy') IS NULL
        ALTER TABLE dbo.QualityEvents ADD DetectedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'DetectedDate') IS NULL
        ALTER TABLE dbo.QualityEvents ADD DetectedDate DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'DetectionSource') IS NULL
        ALTER TABLE dbo.QualityEvents ADD DetectionSource NVARCHAR(160) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'InitialDescription') IS NULL
        ALTER TABLE dbo.QualityEvents ADD InitialDescription NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ImmediateAction') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ImmediateAction NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'RootCauseCategory') IS NULL
        ALTER TABLE dbo.QualityEvents ADD RootCauseCategory NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'RootCauseDetails') IS NULL
        ALTER TABLE dbo.QualityEvents ADD RootCauseDetails NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ImpactAssessment') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ImpactAssessment NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'CAPARequired') IS NULL
        ALTER TABLE dbo.QualityEvents ADD CAPARequired BIT NULL CONSTRAINT DF_QualityEvents_CAPA_20260826 DEFAULT (0);
    IF COL_LENGTH(N'dbo.QualityEvents', N'QAConclusion') IS NULL
        ALTER TABLE dbo.QualityEvents ADD QAConclusion NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'FinalDisposition') IS NULL
        ALTER TABLE dbo.QualityEvents ADD FinalDisposition NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ClosedBy') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ClosedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ClosedDate') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ClosedDate DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'CreatedBy') IS NULL
        ALTER TABLE dbo.QualityEvents ADD CreatedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'CreatedDate') IS NULL
        ALTER TABLE dbo.QualityEvents ADD CreatedDate DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ModifiedBy') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ModifiedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ModifiedDate') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ModifiedDate DATETIME2(0) NULL;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEvents')
          AND name IN (N'SampleID', N'SourceRecordID')
          AND system_type_id <> TYPE_ID(N'int')
    )
        THROW 53609, 'A canonical Quality Event record link is not INT.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEvents')
          AND name = N'SampleID'
          AND is_nullable = 0
    )
        THROW 53610, 'dbo.QualityEvents.SampleID must be nullable for PRM polymorphic Quality Events.', 1;

    /* Affected PRM result evidence. */
    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventAffectedResults
        (
            AffectedResultID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventAffectedResults_20260826 PRIMARY KEY,
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
            CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventAffectedResults_Created_20260826 DEFAULT SYSDATETIME(),
            CONSTRAINT FK_QualityEventAffectedResults_Event_20260826 FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'AffectedResultID') IS NULL
        THROW 53611, 'Required column dbo.QualityEventAffectedResults.AffectedResultID is missing.', 1;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'QualityEventID') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD QualityEventID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SampleTestID') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD SampleTestID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceModule') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD SourceModule NVARCHAR(80) NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceResultID') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD SourceResultID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'TestID') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD TestID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'TestName') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD TestName NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'ResultValue') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD ResultValue NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SpecificationLimit') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD SpecificationLimit NVARCHAR(500) NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'Unit') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD Unit NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'FailureType') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD FailureType NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'CreatedDate') IS NULL
        ALTER TABLE dbo.QualityEventAffectedResults ADD CreatedDate DATETIME2(0) NULL;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name IN (N'AffectedResultID', N'QualityEventID', N'SampleTestID', N'SourceResultID', N'TestID')
          AND system_type_id <> TYPE_ID(N'int')
    )
        THROW 53612, 'A canonical affected-result identity/link is not INT.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name = N'SampleTestID'
          AND is_nullable = 0
    )
        THROW 53613, 'dbo.QualityEventAffectedResults.SampleTestID must be nullable for PRM source-result links.', 1;

    /* SourceModule/SourceResultID can be introduced immediately above. Compile the
       data/index statements only after those ALTER TABLE operations have completed. */
    DECLARE @DuplicateAffectedSourceLinks BIGINT=0;
    EXEC sys.sp_executesql
        N'SELECT @DuplicateCount=COUNT_BIG(*)
          FROM (
              SELECT QualityEventID,SourceModule,SourceResultID
              FROM dbo.QualityEventAffectedResults
              WHERE SourceModule IS NOT NULL AND SourceResultID IS NOT NULL
              GROUP BY QualityEventID,SourceModule,SourceResultID
              HAVING COUNT_BIG(*)>1
          ) d;',
        N'@DuplicateCount BIGINT OUTPUT',
        @DuplicateCount=@DuplicateAffectedSourceLinks OUTPUT;
    IF @DuplicateAffectedSourceLinks>0
        THROW 53614, 'Duplicate PRM affected-result source links exist and require data review.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name = N'UX_QualityEventAffectedResults_Source_20260826'
    )
        EXEC(N'CREATE UNIQUE INDEX UX_QualityEventAffectedResults_Source_20260826
               ON dbo.QualityEventAffectedResults(QualityEventID,SourceModule,SourceResultID)
               WHERE SourceModule IS NOT NULL AND SourceResultID IS NOT NULL;');

    /* Action evidence. */
    IF OBJECT_ID(N'dbo.QualityEventActions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventActions
        (
            QualityEventActionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventActions_20260826 PRIMARY KEY,
            QualityEventID INT NOT NULL,
            ActionType NVARCHAR(100) NULL,
            ActionDescription NVARCHAR(MAX) NULL,
            PerformedBy NVARCHAR(100) NULL,
            PerformedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventActions_Date_20260826 DEFAULT SYSDATETIME(),
            ElectronicSignatureID INT NULL,
            CONSTRAINT FK_QualityEventActions_Event_20260826 FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT FK_QualityEventActions_Signature_20260826 FOREIGN KEY(ElectronicSignatureID) REFERENCES dbo.ElectronicSignatures(SignatureID)
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventActions', N'QualityEventActionID') IS NULL
        THROW 53615, 'Required column dbo.QualityEventActions.QualityEventActionID is missing.', 1;
    IF COL_LENGTH(N'dbo.QualityEventActions', N'QualityEventID') IS NULL
        ALTER TABLE dbo.QualityEventActions ADD QualityEventID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEventActions', N'ActionType') IS NULL
        ALTER TABLE dbo.QualityEventActions ADD ActionType NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.QualityEventActions', N'ActionDescription') IS NULL
        ALTER TABLE dbo.QualityEventActions ADD ActionDescription NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEventActions', N'PerformedBy') IS NULL
        ALTER TABLE dbo.QualityEventActions ADD PerformedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.QualityEventActions', N'PerformedDate') IS NULL
        ALTER TABLE dbo.QualityEventActions ADD PerformedDate DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.QualityEventActions', N'ElectronicSignatureID') IS NULL
        ALTER TABLE dbo.QualityEventActions ADD ElectronicSignatureID INT NULL;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventActions')
          AND name IN (N'QualityEventActionID', N'QualityEventID', N'ElectronicSignatureID')
          AND system_type_id <> TYPE_ID(N'int')
    )
        THROW 53616, 'A canonical Quality Event action identity/link is not INT.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventActions')
          AND name = N'QualityEventActionID'
          AND is_identity = 1
    )
    AND NOT EXISTS
    (
        SELECT 1
        FROM sys.default_constraints d
        INNER JOIN sys.columns c
          ON c.object_id = d.parent_object_id
         AND c.column_id = d.parent_column_id
        WHERE d.parent_object_id = OBJECT_ID(N'dbo.QualityEventActions')
          AND c.name = N'QualityEventActionID'
    )
    BEGIN
        IF OBJECT_ID(N'dbo.Seq_QualityEventActionID_20260826', N'SO') IS NULL
            CREATE SEQUENCE dbo.Seq_QualityEventActionID_20260826 AS INT START WITH 1 INCREMENT BY 1;

        DECLARE @NextActionID BIGINT;
        SELECT @NextActionID = ISNULL(MAX(CONVERT(BIGINT, QualityEventActionID)), 0) + 1
        FROM dbo.QualityEventActions;
        IF @NextActionID > 2147483647
            THROW 53617, 'Quality Event action identity range is exhausted.', 1;
        DECLARE @RestartActionSequenceSql NVARCHAR(4000);
        SET @RestartActionSequenceSql=N'ALTER SEQUENCE dbo.Seq_QualityEventActionID_20260826 RESTART WITH '+CONVERT(NVARCHAR(30),@NextActionID)+N';';
        EXEC(@RestartActionSequenceSql);
        ALTER TABLE dbo.QualityEventActions
            ADD CONSTRAINT DF_QualityEventActions_ID_20260826
            DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventActionID_20260826) FOR QualityEventActionID;
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventActions')
          AND name IN (N'IX_QualityEventActions_Event', N'IX_QualityEventActions_Event_20260824_003', N'IX_QualityEventActions_Event_20260826')
    )
        EXEC(N'CREATE INDEX IX_QualityEventActions_Event_20260826
               ON dbo.QualityEventActions(QualityEventID,PerformedDate,QualityEventActionID);');

    /* Structured checklist answers. */
    IF OBJECT_ID(N'dbo.QualityEventChecklistAnswers', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventChecklistAnswers
        (
            AnswerID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventChecklistAnswers_20260826 PRIMARY KEY,
            QualityEventID INT NOT NULL,
            QuestionID INT NOT NULL,
            AnswerValue NVARCHAR(250) NULL,
            Comments NVARCHAR(MAX) NULL,
            AnsweredBy NVARCHAR(100) NULL,
            AnsweredDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventChecklistAnswers_Date_20260826 DEFAULT SYSDATETIME(),
            CONSTRAINT FK_QualityEventChecklistAnswers_Event_20260826 FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT FK_QualityEventChecklistAnswers_Question_20260826 FOREIGN KEY(QuestionID) REFERENCES dbo.QualityEventChecklistQuestions(QuestionID),
            CONSTRAINT UQ_QualityEventChecklistAnswers_20260826 UNIQUE(QualityEventID, QuestionID)
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnswerID') IS NULL
        THROW 53618, 'Required column dbo.QualityEventChecklistAnswers.AnswerID is missing.', 1;
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'QualityEventID') IS NULL
        ALTER TABLE dbo.QualityEventChecklistAnswers ADD QualityEventID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'QuestionID') IS NULL
        ALTER TABLE dbo.QualityEventChecklistAnswers ADD QuestionID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnswerValue') IS NULL
        ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnswerValue NVARCHAR(250) NULL;
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'Comments') IS NULL
        ALTER TABLE dbo.QualityEventChecklistAnswers ADD Comments NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnsweredBy') IS NULL
        ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnsweredBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnsweredDate') IS NULL
        ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnsweredDate DATETIME2(0) NULL;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
          AND name IN (N'QualityEventID', N'QuestionID')
          AND system_type_id <> TYPE_ID(N'int')
    )
        THROW 53619, 'A canonical checklist-answer link is not INT.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
          AND name = N'AnswerID'
          AND system_type_id NOT IN (TYPE_ID(N'int'), TYPE_ID(N'bigint'))
    )
        THROW 53620, 'dbo.QualityEventChecklistAnswers.AnswerID must be INT or BIGINT.', 1;

    DECLARE @DuplicateChecklistAnswerLinks BIGINT=0;
    EXEC sys.sp_executesql
        N'SELECT @DuplicateCount=COUNT_BIG(*)
          FROM (
              SELECT QualityEventID,QuestionID
              FROM dbo.QualityEventChecklistAnswers
              WHERE QualityEventID IS NOT NULL AND QuestionID IS NOT NULL
              GROUP BY QualityEventID,QuestionID
              HAVING COUNT_BIG(*)>1
          ) d;',
        N'@DuplicateCount BIGINT OUTPUT',
        @DuplicateCount=@DuplicateChecklistAnswerLinks OUTPUT;
    IF @DuplicateChecklistAnswerLinks>0
        THROW 53621, 'Duplicate Quality Event checklist answers exist for the same question.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
          AND is_unique = 1
          AND name IN (N'UQ_QualityEventChecklistAnswers', N'UQ_QualityEventChecklistAnswers_20260824_003', N'UQ_QualityEventChecklistAnswers_20260826')
    )
        EXEC(N'CREATE UNIQUE INDEX UQ_QualityEventChecklistAnswers_20260826
               ON dbo.QualityEventChecklistAnswers(QualityEventID,QuestionID)
               WHERE QualityEventID IS NOT NULL AND QuestionID IS NOT NULL;');

    /* Print evidence. */
    IF OBJECT_ID(N'dbo.QualityEventPrintHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventPrintHistory
        (
            QualityEventPrintID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventPrintHistory_20260826 PRIMARY KEY,
            QualityEventID INT NOT NULL,
            PrintedBy NVARCHAR(100) NOT NULL,
            PrintedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventPrintHistory_Date_20260826 DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_QualityEventPrintHistory_Event_20260826 FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'QualityEventPrintID') IS NULL
        THROW 53622, 'Required column dbo.QualityEventPrintHistory.QualityEventPrintID is missing.', 1;
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'QualityEventID') IS NULL
        ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'PrintedBy') IS NULL
        ALTER TABLE dbo.QualityEventPrintHistory ADD PrintedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'PrintedDate') IS NULL
        ALTER TABLE dbo.QualityEventPrintHistory ADD PrintedDate DATETIME2(0) NULL;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND name = N'QualityEventID'
          AND system_type_id <> TYPE_ID(N'int')
    )
        THROW 53623, 'dbo.QualityEventPrintHistory.QualityEventID must be INT.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND name = N'QualityEventPrintID'
          AND system_type_id NOT IN (TYPE_ID(N'int'), TYPE_ID(N'bigint'))
    )
        THROW 53624, 'dbo.QualityEventPrintHistory.QualityEventPrintID must be INT or BIGINT.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND name IN (N'IX_QualityEventPrintHistory_Event', N'IX_QualityEventPrintHistory_Event_20260824_003', N'IX_QualityEventPrintHistory_Event_20260826')
    )
        EXEC(N'CREATE INDEX IX_QualityEventPrintHistory_Event_20260826
               ON dbo.QualityEventPrintHistory(QualityEventID,PrintedDate,QualityEventPrintID);');

    EXEC sys.sp_executesql N'
        CREATE OR ALTER TRIGGER dbo.TRG_QualityEventPrintHistory_AppendOnly
        ON dbo.QualityEventPrintHistory
        AFTER UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 51050, ''Controlled PharmaLIMS compliance records are append-only and cannot be updated or deleted.'', 1;
        END;';

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.QualityEvents')
          AND name IN (N'IX_QualityEvents_SourceStatus_20260824', N'IX_QualityEvents_SourceStatus_20260824_003', N'IX_QualityEvents_SourceStatus_20260826')
    )
        EXEC(N'CREATE INDEX IX_QualityEvents_SourceStatus_20260826
               ON dbo.QualityEvents(SourceModule,SourceRecordID,CurrentStatus,QualityEventID);');

    /* Final current-runtime verification. */
    IF COL_LENGTH(N'dbo.QualityEvents', N'SourceModule') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents', N'SourceRecordID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents', N'CurrentStatus') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents', N'QAConclusion') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents', N'FinalDisposition') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceModule') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceResultID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventActions', N'ActionType') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnswerValue') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventPrintHistory', N'PrintedDate') IS NULL
        THROW 53625, 'Current PRM Quality Event schema verification failed.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
