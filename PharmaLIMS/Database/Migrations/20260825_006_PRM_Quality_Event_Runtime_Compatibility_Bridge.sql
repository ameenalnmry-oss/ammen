SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PRM Quality Event runtime compatibility bridge.

    Purpose:
    - Repair only the canonical columns required by the current PRM Quality Event runtime.
    - Preserve legacy tables, keys, rows, result evidence, signatures, conclusions, and print evidence.
    - Accept legacy support tables that use different surrogate-key names.
    - Preserve unmapped legacy links in a reconciliation ledger instead of blocking the complete PRM module.

    This migration is additive except for setting an invalid canonical link to NULL after its original
    value has been captured in PRM_QualityEventRuntimeBridgeEvidence. Legacy source columns are retained.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_NumberSequences', N'U') IS NULL
        THROW 53560, 'The PRM number-sequence baseline is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents', N'QualityEventID') IS NULL
        THROW 53561, 'The Quality Event identity baseline is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults', N'AffectedResultID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults', N'QualityEventID') IS NULL
        THROW 53562, 'The affected-result identity baseline is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions', N'U') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventChecklistQuestions', N'QuestionID') IS NULL
        THROW 53563, 'The Quality Event checklist baseline is missing.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'QualityEventID'
          AND system_type_id<>TYPE_ID(N'int')
    )
        THROW 53564, 'The Quality Event identity type is not compatible with the PRM runtime.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
          AND name=N'QuestionID'
          AND system_type_id<>TYPE_ID(N'int')
    )
        THROW 53565, 'The checklist-question identity type is not compatible with the PRM runtime.', 1;

    IF OBJECT_ID(N'dbo.PRM_QualityEventRuntimeBridgeEvidence', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_QualityEventRuntimeBridgeEvidence
        (
            EvidenceID BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_PRM_QualityEventRuntimeBridgeEvidence PRIMARY KEY,
            SourceTable NVARCHAR(128) NOT NULL,
            CanonicalRowID NVARCHAR(200) NULL,
            CanonicalLinkColumn NVARCHAR(128) NOT NULL,
            OriginalLinkValue NVARCHAR(400) NULL,
            ReconciliationAction NVARCHAR(400) NOT NULL,
            RecordedAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_PRM_QualityEventRuntimeBridgeEvidence_RecordedAt DEFAULT SYSUTCDATETIME()
        );
    END;

    IF COL_LENGTH(N'dbo.PRM_NumberSequences', N'LastUpdated') IS NULL
        EXEC(N'ALTER TABLE dbo.PRM_NumberSequences ADD LastUpdated DATETIME2(0) NULL;');

    /* QualityEvents additive runtime projection. */
    IF COL_LENGTH(N'dbo.QualityEvents',N'EventNumber') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD EventNumber NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'EventType') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD EventType NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'Severity') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD Severity NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'SampleID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SampleID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'SampleNumber') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SampleNumber NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceModule NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceRecordID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CurrentStatus NVARCHAR(60) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'DetectedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'DetectedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'DetectionSource') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectionSource NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'InitialDescription') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD InitialDescription NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ImmediateAction') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ImmediateAction NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'RootCauseCategory') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD RootCauseCategory NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'RootCauseDetails') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD RootCauseDetails NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ImpactAssessment') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ImpactAssessment NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'CAPARequired') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CAPARequired BIT NULL CONSTRAINT DF_QualityEvents_CAPA_20260825_006 DEFAULT (0);');
    IF COL_LENGTH(N'dbo.QualityEvents',N'QAConclusion') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD QAConclusion NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'FinalDisposition') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD FinalDisposition NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ClosedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ClosedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ClosedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ClosedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'CreatedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CreatedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'CreatedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CreatedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ModifiedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ModifiedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ModifiedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD ModifiedDate DATETIME2(0) NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'SampleID'
          AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
    )
        THROW 53566, 'The Quality Event sample link is not compatible with the PRM runtime.', 1;
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'SampleID'
          AND is_nullable=0
    )
        EXEC(N'ALTER TABLE dbo.QualityEvents ALTER COLUMN SampleID INT NULL;');

    /* Affected-result additive runtime projection. */
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SampleTestID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SampleTestID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'TestID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD TestID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'TestName') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD TestName NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'ResultValue') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD ResultValue NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SpecificationLimit') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SpecificationLimit NVARCHAR(500) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'Unit') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD Unit NVARCHAR(50) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'FailureType') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD FailureType NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'CreatedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD CreatedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceModule') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SourceModule NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceResultID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SourceResultID INT NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SampleTestID'
          AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
    )
        THROW 53567, 'The affected-result sample-test link is not compatible with the PRM runtime.', 1;
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SampleTestID'
          AND is_nullable=0
    )
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN SampleTestID INT NULL;');

    /* Actions table. */
    IF OBJECT_ID(N'dbo.QualityEventActions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventActions
        (
            QualityEventActionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventActions_20260825_006 PRIMARY KEY,
            QualityEventID INT NULL,
            ActionType NVARCHAR(100) NULL,
            ActionDescription NVARCHAR(MAX) NULL,
            PerformedBy NVARCHAR(100) NULL,
            PerformedDate DATETIME2(0) NULL,
            ElectronicSignatureID INT NULL
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventActions',N'QualityEventActionID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD QualityEventActionID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'QualityEventID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD QualityEventID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'ActionType') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD ActionType NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'ActionDescription') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD ActionDescription NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'PerformedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD PerformedBy NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'PerformedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD PerformedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'ElectronicSignatureID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD ElectronicSignatureID INT NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
          AND name=N'QualityEventActionID'
          AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
    )
        THROW 53568, 'The Quality Event action identity is not compatible with the PRM runtime.', 1;
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
          AND name=N'QualityEventID'
          AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
    )
        THROW 53569, 'The Quality Event action link is not compatible with the PRM runtime.', 1;

    IF COL_LENGTH(N'dbo.QualityEventActions', N'EventID') IS NOT NULL
    BEGIN
        EXEC(N'
UPDATE a
SET QualityEventID=TRY_CONVERT(INT,a.EventID)
FROM dbo.QualityEventActions a
WHERE a.QualityEventID IS NULL
  AND TRY_CONVERT(INT,a.EventID) IS NOT NULL
  AND EXISTS
      (SELECT 1 FROM dbo.QualityEvents e WHERE e.QualityEventID=TRY_CONVERT(INT,a.EventID));');
    END;

    IF OBJECT_ID(N'dbo.Seq_PRM_QualityEventActionID_20260825_006', N'SO') IS NULL
        EXEC(N'CREATE SEQUENCE dbo.Seq_PRM_QualityEventActionID_20260825_006 AS INT START WITH 1 INCREMENT BY 1;');
    DECLARE @NextActionID BIGINT=1;
    EXEC sys.sp_executesql
        N'SELECT @Next=ISNULL(MAX(CONVERT(BIGINT,QualityEventActionID)),0)+1 FROM dbo.QualityEventActions;',
        N'@Next BIGINT OUTPUT', @Next=@NextActionID OUTPUT;
    IF @NextActionID>2147483647
        THROW 53570, 'The Quality Event action identity range is exhausted.', 1;
    EXEC(N'ALTER SEQUENCE dbo.Seq_PRM_QualityEventActionID_20260825_006 RESTART WITH ' + CONVERT(NVARCHAR(30),@NextActionID) + N';');
    EXEC(N'UPDATE dbo.QualityEventActions
           SET QualityEventActionID=NEXT VALUE FOR dbo.Seq_PRM_QualityEventActionID_20260825_006
           WHERE QualityEventActionID IS NULL;');

    IF EXISTS
    (
        SELECT QualityEventActionID FROM dbo.QualityEventActions
        GROUP BY QualityEventActionID HAVING COUNT_BIG(*)>1
    )
        THROW 53571, 'Duplicate Quality Event action identities require controlled review.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns c
        WHERE c.object_id=OBJECT_ID(N'dbo.QualityEventActions')
          AND c.name=N'QualityEventActionID' AND c.is_identity=1
    )
    AND NOT EXISTS
    (
        SELECT 1 FROM sys.default_constraints d
        INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
        WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventActions') AND c.name=N'QualityEventActionID'
    )
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD CONSTRAINT DF_QualityEventActions_RuntimeBridgeID DEFAULT (NEXT VALUE FOR dbo.Seq_PRM_QualityEventActionID_20260825_006) FOR QualityEventActionID;');

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
          AND name=N'UX_QualityEventActions_RuntimeBridgeID'
    )
        EXEC(N'CREATE UNIQUE INDEX UX_QualityEventActions_RuntimeBridgeID ON dbo.QualityEventActions(QualityEventActionID);');

    INSERT dbo.PRM_QualityEventRuntimeBridgeEvidence
        (SourceTable,CanonicalRowID,CanonicalLinkColumn,OriginalLinkValue,ReconciliationAction)
    SELECT N'QualityEventActions',CONVERT(NVARCHAR(200),a.QualityEventActionID),N'QualityEventID',CONVERT(NVARCHAR(400),a.QualityEventID),
           N'Invalid canonical event link retained as migration evidence; runtime link cleared.'
    FROM dbo.QualityEventActions a
    LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=a.QualityEventID
    WHERE a.QualityEventID IS NOT NULL AND e.QualityEventID IS NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.PRM_QualityEventRuntimeBridgeEvidence x
          WHERE x.SourceTable=N'QualityEventActions'
            AND x.CanonicalRowID=CONVERT(NVARCHAR(200),a.QualityEventActionID)
            AND x.CanonicalLinkColumn=N'QualityEventID'
            AND x.OriginalLinkValue=CONVERT(NVARCHAR(400),a.QualityEventID)
      );

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
          AND name=N'QualityEventID' AND is_nullable=0
    )
        EXEC(N'ALTER TABLE dbo.QualityEventActions ALTER COLUMN QualityEventID INT NULL;');
    EXEC(N'UPDATE a SET QualityEventID=NULL
           FROM dbo.QualityEventActions a
           LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=a.QualityEventID
           WHERE a.QualityEventID IS NOT NULL AND e.QualityEventID IS NULL;');

    /* Checklist answers table. */
    IF OBJECT_ID(N'dbo.QualityEventChecklistAnswers', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventChecklistAnswers
        (
            AnswerID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventChecklistAnswers_20260825_006 PRIMARY KEY,
            QualityEventID INT NULL,
            QuestionID INT NULL,
            AnswerValue NVARCHAR(250) NULL,
            Comments NVARCHAR(MAX) NULL,
            AnsweredBy NVARCHAR(100) NULL,
            AnsweredDate DATETIME2(0) NULL
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnswerID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnswerID BIGINT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'QualityEventID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD QualityEventID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'QuestionID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD QuestionID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnswerValue') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnswerValue NVARCHAR(250) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'Comments') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD Comments NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnsweredBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnsweredBy NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnsweredDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnsweredDate DATETIME2(0) NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
          AND name=N'AnswerID'
          AND system_type_id NOT IN(TYPE_ID(N'int'),TYPE_ID(N'bigint'))
    )
        THROW 53572, 'The checklist-answer identity is not compatible with the PRM runtime.', 1;
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
          AND name IN(N'QualityEventID',N'QuestionID')
          AND system_type_id<>TYPE_ID(N'int')
    )
        THROW 53573, 'A checklist-answer link is not compatible with the PRM runtime.', 1;

    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'EventID') IS NOT NULL
        EXEC(N'UPDATE a SET QualityEventID=TRY_CONVERT(INT,a.EventID)
               FROM dbo.QualityEventChecklistAnswers a
               WHERE a.QualityEventID IS NULL
                 AND TRY_CONVERT(INT,a.EventID) IS NOT NULL
                 AND EXISTS(SELECT 1 FROM dbo.QualityEvents e WHERE e.QualityEventID=TRY_CONVERT(INT,a.EventID));');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'ChecklistQuestionID') IS NOT NULL
        EXEC(N'UPDATE a SET QuestionID=TRY_CONVERT(INT,a.ChecklistQuestionID)
               FROM dbo.QualityEventChecklistAnswers a
               WHERE a.QuestionID IS NULL
                 AND TRY_CONVERT(INT,a.ChecklistQuestionID) IS NOT NULL
                 AND EXISTS(SELECT 1 FROM dbo.QualityEventChecklistQuestions q WHERE q.QuestionID=TRY_CONVERT(INT,a.ChecklistQuestionID));');

    DECLARE @AnswerIdentityIsInt BIT=0;
    SELECT @AnswerIdentityIsInt=CASE WHEN system_type_id=TYPE_ID(N'int') THEN 1 ELSE 0 END
    FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'AnswerID';

    IF @AnswerIdentityIsInt=1
    BEGIN
        IF OBJECT_ID(N'dbo.Seq_PRM_QualityEventAnswerID_Int_20260825_006', N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_PRM_QualityEventAnswerID_Int_20260825_006 AS INT START WITH 1 INCREMENT BY 1;');
        DECLARE @NextAnswerInt BIGINT=1;
        EXEC sys.sp_executesql N'SELECT @Next=ISNULL(MAX(CONVERT(BIGINT,AnswerID)),0)+1 FROM dbo.QualityEventChecklistAnswers;',N'@Next BIGINT OUTPUT',@Next=@NextAnswerInt OUTPUT;
        IF @NextAnswerInt>2147483647
            THROW 53574, 'The checklist-answer identity range is exhausted.', 1;
        EXEC(N'ALTER SEQUENCE dbo.Seq_PRM_QualityEventAnswerID_Int_20260825_006 RESTART WITH ' + CONVERT(NVARCHAR(30),@NextAnswerInt) + N';');
        EXEC(N'UPDATE dbo.QualityEventChecklistAnswers SET AnswerID=NEXT VALUE FOR dbo.Seq_PRM_QualityEventAnswerID_Int_20260825_006 WHERE AnswerID IS NULL;');
    END
    ELSE
    BEGIN
        IF OBJECT_ID(N'dbo.Seq_PRM_QualityEventAnswerID_Big_20260825_006', N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_PRM_QualityEventAnswerID_Big_20260825_006 AS BIGINT START WITH 1 INCREMENT BY 1;');
        DECLARE @NextAnswerBig BIGINT=1;
        EXEC sys.sp_executesql N'SELECT @Next=ISNULL(MAX(CONVERT(BIGINT,AnswerID)),0)+1 FROM dbo.QualityEventChecklistAnswers;',N'@Next BIGINT OUTPUT',@Next=@NextAnswerBig OUTPUT;
        EXEC(N'ALTER SEQUENCE dbo.Seq_PRM_QualityEventAnswerID_Big_20260825_006 RESTART WITH ' + CONVERT(NVARCHAR(30),@NextAnswerBig) + N';');
        EXEC(N'UPDATE dbo.QualityEventChecklistAnswers SET AnswerID=NEXT VALUE FOR dbo.Seq_PRM_QualityEventAnswerID_Big_20260825_006 WHERE AnswerID IS NULL;');
    END;

    IF EXISTS
    (
        SELECT AnswerID FROM dbo.QualityEventChecklistAnswers
        GROUP BY AnswerID HAVING COUNT_BIG(*)>1
    )
        THROW 53575, 'Duplicate checklist-answer identities require controlled review.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns c
        WHERE c.object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
          AND c.name=N'AnswerID' AND c.is_identity=1
    )
    AND NOT EXISTS
    (
        SELECT 1 FROM sys.default_constraints d
        INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
        WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND c.name=N'AnswerID'
    )
    BEGIN
        IF @AnswerIdentityIsInt=1
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD CONSTRAINT DF_QualityEventChecklistAnswers_RuntimeBridgeID DEFAULT (NEXT VALUE FOR dbo.Seq_PRM_QualityEventAnswerID_Int_20260825_006) FOR AnswerID;');
        ELSE
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD CONSTRAINT DF_QualityEventChecklistAnswers_RuntimeBridgeID DEFAULT (NEXT VALUE FOR dbo.Seq_PRM_QualityEventAnswerID_Big_20260825_006) FOR AnswerID;');
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
          AND name=N'UX_QualityEventChecklistAnswers_RuntimeBridgeID'
    )
        EXEC(N'CREATE UNIQUE INDEX UX_QualityEventChecklistAnswers_RuntimeBridgeID ON dbo.QualityEventChecklistAnswers(AnswerID);');

    INSERT dbo.PRM_QualityEventRuntimeBridgeEvidence
        (SourceTable,CanonicalRowID,CanonicalLinkColumn,OriginalLinkValue,ReconciliationAction)
    SELECT N'QualityEventChecklistAnswers',CONVERT(NVARCHAR(200),a.AnswerID),N'QualityEventID',CONVERT(NVARCHAR(400),a.QualityEventID),
           N'Invalid canonical event link retained as migration evidence; runtime link cleared.'
    FROM dbo.QualityEventChecklistAnswers a
    LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=a.QualityEventID
    WHERE a.QualityEventID IS NOT NULL AND e.QualityEventID IS NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.PRM_QualityEventRuntimeBridgeEvidence x
          WHERE x.SourceTable=N'QualityEventChecklistAnswers'
            AND x.CanonicalRowID=CONVERT(NVARCHAR(200),a.AnswerID)
            AND x.CanonicalLinkColumn=N'QualityEventID'
            AND x.OriginalLinkValue=CONVERT(NVARCHAR(400),a.QualityEventID)
      );

    INSERT dbo.PRM_QualityEventRuntimeBridgeEvidence
        (SourceTable,CanonicalRowID,CanonicalLinkColumn,OriginalLinkValue,ReconciliationAction)
    SELECT N'QualityEventChecklistAnswers',CONVERT(NVARCHAR(200),a.AnswerID),N'QuestionID',CONVERT(NVARCHAR(400),a.QuestionID),
           N'Invalid canonical question link retained as migration evidence; runtime link cleared.'
    FROM dbo.QualityEventChecklistAnswers a
    LEFT JOIN dbo.QualityEventChecklistQuestions q ON q.QuestionID=a.QuestionID
    WHERE a.QuestionID IS NOT NULL AND q.QuestionID IS NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.PRM_QualityEventRuntimeBridgeEvidence x
          WHERE x.SourceTable=N'QualityEventChecklistAnswers'
            AND x.CanonicalRowID=CONVERT(NVARCHAR(200),a.AnswerID)
            AND x.CanonicalLinkColumn=N'QuestionID'
            AND x.OriginalLinkValue=CONVERT(NVARCHAR(400),a.QuestionID)
      );

    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'QualityEventID' AND is_nullable=0)
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ALTER COLUMN QualityEventID INT NULL;');
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'QuestionID' AND is_nullable=0)
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ALTER COLUMN QuestionID INT NULL;');
    EXEC(N'UPDATE a SET QualityEventID=NULL
           FROM dbo.QualityEventChecklistAnswers a
           LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=a.QualityEventID
           WHERE a.QualityEventID IS NOT NULL AND e.QualityEventID IS NULL;');
    EXEC(N'UPDATE a SET QuestionID=NULL
           FROM dbo.QualityEventChecklistAnswers a
           LEFT JOIN dbo.QualityEventChecklistQuestions q ON q.QuestionID=a.QuestionID
           WHERE a.QuestionID IS NOT NULL AND q.QuestionID IS NULL;');

    /* Print history table. */
    IF OBJECT_ID(N'dbo.QualityEventPrintHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventPrintHistory
        (
            QualityEventPrintID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventPrintHistory_20260825_006 PRIMARY KEY,
            QualityEventID INT NULL,
            PrintedBy NVARCHAR(100) NULL,
            PrintedDate DATETIME2(0) NULL
        );
    END;

    DECLARE @PrintAppendOnlyWasEnabled BIT=0;
    IF EXISTS
    (
        SELECT 1 FROM sys.triggers
        WHERE parent_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND name=N'TRG_QualityEventPrintHistory_AppendOnly'
          AND is_disabled=0
    )
    BEGIN
        SET @PrintAppendOnlyWasEnabled=1;
        EXEC(N'DISABLE TRIGGER dbo.TRG_QualityEventPrintHistory_AppendOnly ON dbo.QualityEventPrintHistory;');
    END;

    IF COL_LENGTH(N'dbo.QualityEventPrintHistory',N'QualityEventPrintID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventPrintID BIGINT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory',N'QualityEventID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory',N'PrintedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD PrintedBy NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory',N'PrintedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD PrintedDate DATETIME2(0) NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND name=N'QualityEventPrintID'
          AND system_type_id NOT IN(TYPE_ID(N'int'),TYPE_ID(N'bigint'))
    )
        THROW 53576, 'The Quality Event print identity is not compatible with the PRM runtime.', 1;
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND name=N'QualityEventID'
          AND system_type_id<>TYPE_ID(N'int')
    )
        THROW 53577, 'The Quality Event print link is not compatible with the PRM runtime.', 1;

    IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'EventID') IS NOT NULL
        EXEC(N'UPDATE p SET QualityEventID=TRY_CONVERT(INT,p.EventID)
               FROM dbo.QualityEventPrintHistory p
               WHERE p.QualityEventID IS NULL
                 AND TRY_CONVERT(INT,p.EventID) IS NOT NULL
                 AND EXISTS(SELECT 1 FROM dbo.QualityEvents e WHERE e.QualityEventID=TRY_CONVERT(INT,p.EventID));');

    DECLARE @PrintIdentityIsInt BIT=0;
    SELECT @PrintIdentityIsInt=CASE WHEN system_type_id=TYPE_ID(N'int') THEN 1 ELSE 0 END
    FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'QualityEventPrintID';

    IF @PrintIdentityIsInt=1
    BEGIN
        IF OBJECT_ID(N'dbo.Seq_PRM_QualityEventPrintID_Int_20260825_006', N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_PRM_QualityEventPrintID_Int_20260825_006 AS INT START WITH 1 INCREMENT BY 1;');
        DECLARE @NextPrintInt BIGINT=1;
        EXEC sys.sp_executesql N'SELECT @Next=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID)),0)+1 FROM dbo.QualityEventPrintHistory;',N'@Next BIGINT OUTPUT',@Next=@NextPrintInt OUTPUT;
        IF @NextPrintInt>2147483647
            THROW 53578, 'The Quality Event print identity range is exhausted.', 1;
        EXEC(N'ALTER SEQUENCE dbo.Seq_PRM_QualityEventPrintID_Int_20260825_006 RESTART WITH ' + CONVERT(NVARCHAR(30),@NextPrintInt) + N';');
        EXEC(N'UPDATE dbo.QualityEventPrintHistory SET QualityEventPrintID=NEXT VALUE FOR dbo.Seq_PRM_QualityEventPrintID_Int_20260825_006 WHERE QualityEventPrintID IS NULL;');
    END
    ELSE
    BEGIN
        IF OBJECT_ID(N'dbo.Seq_PRM_QualityEventPrintID_Big_20260825_006', N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_PRM_QualityEventPrintID_Big_20260825_006 AS BIGINT START WITH 1 INCREMENT BY 1;');
        DECLARE @NextPrintBig BIGINT=1;
        EXEC sys.sp_executesql N'SELECT @Next=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID)),0)+1 FROM dbo.QualityEventPrintHistory;',N'@Next BIGINT OUTPUT',@Next=@NextPrintBig OUTPUT;
        EXEC(N'ALTER SEQUENCE dbo.Seq_PRM_QualityEventPrintID_Big_20260825_006 RESTART WITH ' + CONVERT(NVARCHAR(30),@NextPrintBig) + N';');
        EXEC(N'UPDATE dbo.QualityEventPrintHistory SET QualityEventPrintID=NEXT VALUE FOR dbo.Seq_PRM_QualityEventPrintID_Big_20260825_006 WHERE QualityEventPrintID IS NULL;');
    END;

    IF EXISTS
    (
        SELECT QualityEventPrintID FROM dbo.QualityEventPrintHistory
        GROUP BY QualityEventPrintID HAVING COUNT_BIG(*)>1
    )
        THROW 53579, 'Duplicate Quality Event print identities require controlled review.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns c
        WHERE c.object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND c.name=N'QualityEventPrintID' AND c.is_identity=1
    )
    AND NOT EXISTS
    (
        SELECT 1 FROM sys.default_constraints d
        INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
        WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND c.name=N'QualityEventPrintID'
    )
    BEGIN
        IF @PrintIdentityIsInt=1
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD CONSTRAINT DF_QualityEventPrintHistory_RuntimeBridgeID DEFAULT (NEXT VALUE FOR dbo.Seq_PRM_QualityEventPrintID_Int_20260825_006) FOR QualityEventPrintID;');
        ELSE
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD CONSTRAINT DF_QualityEventPrintHistory_RuntimeBridgeID DEFAULT (NEXT VALUE FOR dbo.Seq_PRM_QualityEventPrintID_Big_20260825_006) FOR QualityEventPrintID;');
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
          AND name=N'UX_QualityEventPrintHistory_RuntimeBridgeID'
    )
        EXEC(N'CREATE UNIQUE INDEX UX_QualityEventPrintHistory_RuntimeBridgeID ON dbo.QualityEventPrintHistory(QualityEventPrintID);');

    INSERT dbo.PRM_QualityEventRuntimeBridgeEvidence
        (SourceTable,CanonicalRowID,CanonicalLinkColumn,OriginalLinkValue,ReconciliationAction)
    SELECT N'QualityEventPrintHistory',CONVERT(NVARCHAR(200),p.QualityEventPrintID),N'QualityEventID',CONVERT(NVARCHAR(400),p.QualityEventID),
           N'Invalid canonical event link retained as migration evidence; runtime link cleared.'
    FROM dbo.QualityEventPrintHistory p
    LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=p.QualityEventID
    WHERE p.QualityEventID IS NOT NULL AND e.QualityEventID IS NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.PRM_QualityEventRuntimeBridgeEvidence x
          WHERE x.SourceTable=N'QualityEventPrintHistory'
            AND x.CanonicalRowID=CONVERT(NVARCHAR(200),p.QualityEventPrintID)
            AND x.CanonicalLinkColumn=N'QualityEventID'
            AND x.OriginalLinkValue=CONVERT(NVARCHAR(400),p.QualityEventID)
      );

    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'QualityEventID' AND is_nullable=0)
        EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ALTER COLUMN QualityEventID INT NULL;');
    EXEC(N'UPDATE p SET QualityEventID=NULL
           FROM dbo.QualityEventPrintHistory p
           LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=p.QualityEventID
           WHERE p.QualityEventID IS NOT NULL AND e.QualityEventID IS NULL;');

    IF @PrintAppendOnlyWasEnabled=1
        EXEC(N'ENABLE TRIGGER dbo.TRG_QualityEventPrintHistory_AppendOnly ON dbo.QualityEventPrintHistory;');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
