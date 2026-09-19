SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS PRM Quality Event operational-readiness hardening.

    Purpose:
    - Make the already-controlled PRM Quality Event schema safe for normal runtime use.
    - Keep all workflow screens read-only with respect to DDL and migrations.
    - Support either IDENTITY or a controlled SEQUENCE default for surrogate IDs.
    - Preserve every existing Quality Event, result link, answer, action, signature,
      print record, and audit record. Existing business/compliance rows are not deleted
      or rewritten by this migration.
    - Re-seed only the controlled PRM investigation-question master data when missing.

    Execution boundary:
    - Development: only from the explicit signed Database Maintenance action.
    - Production: only from the approved controlled deployment process.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_NumberSequences',N'U') IS NULL
        THROW 53700, 'Required table dbo.PRM_NumberSequences is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEvents',N'U') IS NULL
        THROW 53701, 'Required table dbo.QualityEvents is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NULL
        THROW 53702, 'Required table dbo.QualityEventAffectedResults is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventActions',N'U') IS NULL
        THROW 53703, 'Required table dbo.QualityEventActions is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions',N'U') IS NULL
        THROW 53704, 'Required table dbo.QualityEventChecklistQuestions is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventChecklistAnswers',N'U') IS NULL
        THROW 53705, 'Required table dbo.QualityEventChecklistAnswers is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventPrintHistory',N'U') IS NULL
        THROW 53706, 'Required table dbo.QualityEventPrintHistory is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_NumberSequences',N'LastUpdated') IS NULL
        EXEC(N'ALTER TABLE dbo.PRM_NumberSequences ADD LastUpdated DATETIME2(0) NULL;');

    /* QualityEvents runtime key. The application reads the generated key with
       OUTPUT inserted.QualityEventID, so either IDENTITY or a sequence default
       is valid. No SCOPE_IDENTITY dependency remains. */
    IF COL_LENGTH(N'dbo.QualityEvents',N'QualityEventID') IS NULL
        THROW 53707, 'Required column dbo.QualityEvents.QualityEventID is missing.', 1;
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'QualityEventID'
          AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
    )
        THROW 53708, 'dbo.QualityEvents.QualityEventID must be a non-computed INT runtime key.', 1;
    IF EXISTS(SELECT 1 FROM dbo.QualityEvents WHERE QualityEventID IS NULL)
        THROW 53709, 'NULL Quality Event identities require controlled data review before migration.', 1;
    IF EXISTS
    (
        SELECT QualityEventID
        FROM dbo.QualityEvents
        GROUP BY QualityEventID
        HAVING COUNT_BIG(*)>1
    )
        THROW 53710, 'Duplicate Quality Event identities require controlled data review before migration.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'QualityEventID' AND is_nullable=1
    )
        EXEC(N'ALTER TABLE dbo.QualityEvents ALTER COLUMN QualityEventID INT NOT NULL;');

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'QualityEventID' AND is_identity=1
    )
    AND NOT EXISTS
    (
        SELECT 1
        FROM sys.default_constraints d
        INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
        WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND c.name=N'QualityEventID'
          AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
    )
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEvents')
              AND c.name=N'QualityEventID'
        )
            THROW 53711, 'QualityEvents.QualityEventID has a non-sequence default that cannot guarantee unique generated IDs.', 1;

        IF OBJECT_ID(N'dbo.Seq_QualityEventID_20260826_002',N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventID_20260826_002 AS INT START WITH 1 INCREMENT BY 1;');
        DECLARE @NextQualityEventID BIGINT;
        SELECT @NextQualityEventID=ISNULL(MAX(CONVERT(BIGINT,QualityEventID)),0)+1 FROM dbo.QualityEvents;
        IF @NextQualityEventID>2147483647
            THROW 53712, 'Quality Event identity range is exhausted.', 1;
        DECLARE @RestartQualityEventSql NVARCHAR(4000);
        SET @RestartQualityEventSql=N'ALTER SEQUENCE dbo.Seq_QualityEventID_20260826_002 RESTART WITH '+CONVERT(NVARCHAR(30),@NextQualityEventID)+N';';
        EXEC(@RestartQualityEventSql);
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CONSTRAINT DF_QualityEvents_ID_20260826_002 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventID_20260826_002) FOR QualityEventID;');
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes i
        WHERE i.object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND i.is_unique=1 AND i.is_disabled=0
          AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=1
          AND EXISTS
          (
              SELECT 1 FROM sys.index_columns ic
              INNER JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
              WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'QualityEventID'
          )
    )
        CREATE UNIQUE INDEX UX_QualityEvents_QualityEventID_20260826_002 ON dbo.QualityEvents(QualityEventID);

    /* Canonical PRM event projection. */
    IF COL_LENGTH(N'dbo.QualityEvents',N'EventNumber') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD EventNumber NVARCHAR(60) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'EventType') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD EventType NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'Severity') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD Severity NVARCHAR(60) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'SampleID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD SampleID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'SampleNumber') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD SampleNumber NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceModule NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceRecordID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD CurrentStatus NVARCHAR(60) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'DetectedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'DetectedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'DetectionSource') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectionSource NVARCHAR(160) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'InitialDescription') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD InitialDescription NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ImmediateAction') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ImmediateAction NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'RootCauseCategory') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD RootCauseCategory NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'RootCauseDetails') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD RootCauseDetails NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ImpactAssessment') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ImpactAssessment NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'CAPARequired') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD CAPARequired BIT NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'QAConclusion') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD QAConclusion NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'FinalDisposition') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD FinalDisposition NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ClosedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ClosedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ClosedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ClosedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'CreatedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD CreatedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'CreatedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD CreatedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ModifiedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ModifiedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEvents',N'ModifiedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ModifiedDate DATETIME2(0) NULL;');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'SampleID' AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
    )
        THROW 53713, 'QualityEvents.SampleID is not compatible with PRM polymorphic links.', 1;
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'SampleID' AND is_nullable=0
    )
        EXEC(N'ALTER TABLE dbo.QualityEvents ALTER COLUMN SampleID INT NULL;');

    /* Checklist question key and controlled PRM master questions. */
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'QuestionID') IS NULL
        THROW 53714, 'Required column QualityEventChecklistQuestions.QuestionID is missing.', 1;
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
          AND name=N'QuestionID' AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
    )
        THROW 53715, 'QualityEventChecklistQuestions.QuestionID must be INT.', 1;
    IF EXISTS(SELECT 1 FROM dbo.QualityEventChecklistQuestions WHERE QuestionID IS NULL)
        THROW 53716, 'NULL Quality Event checklist question IDs require controlled review.', 1;
    IF EXISTS(SELECT QuestionID FROM dbo.QualityEventChecklistQuestions GROUP BY QuestionID HAVING COUNT_BIG(*)>1)
        THROW 53717, 'Duplicate Quality Event checklist question IDs require controlled review.', 1;
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions') AND name=N'QuestionID' AND is_nullable=1)
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ALTER COLUMN QuestionID INT NOT NULL;');

    IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions') AND name=N'QuestionID' AND is_identity=1)
    AND NOT EXISTS
    (
        SELECT 1 FROM sys.default_constraints d
        INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
        WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
          AND c.name=N'QuestionID' AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
    )
    BEGIN
        IF EXISTS
        (
            SELECT 1 FROM sys.default_constraints d
            INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions') AND c.name=N'QuestionID'
        )
            THROW 53718, 'Checklist QuestionID has a non-sequence default and cannot safely auto-generate IDs.', 1;
        IF OBJECT_ID(N'dbo.Seq_QualityEventChecklistQuestionID_20260826_002',N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventChecklistQuestionID_20260826_002 AS INT START WITH 1 INCREMENT BY 1;');
        DECLARE @NextQuestionID BIGINT;
        SELECT @NextQuestionID=ISNULL(MAX(CONVERT(BIGINT,QuestionID)),0)+1 FROM dbo.QualityEventChecklistQuestions;
        IF @NextQuestionID>2147483647 THROW 53719, 'Checklist QuestionID range is exhausted.', 1;
        DECLARE @RestartQuestionSql NVARCHAR(4000);
        SET @RestartQuestionSql=N'ALTER SEQUENCE dbo.Seq_QualityEventChecklistQuestionID_20260826_002 RESTART WITH '+CONVERT(NVARCHAR(30),@NextQuestionID)+N';';
        EXEC(@RestartQuestionSql);
        EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD CONSTRAINT DF_QualityEventChecklistQuestions_ID_20260826_002 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventChecklistQuestionID_20260826_002) FOR QuestionID;');
    END;

    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'SectionName') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD SectionName NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'QuestionText') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD QuestionText NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToEventType') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AppliesToEventType NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToSampleType') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AppliesToSampleType NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToTestCategory') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AppliesToTestCategory NVARCHAR(150) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToTestNameKeyword') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AppliesToTestNameKeyword NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AnswerType') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AnswerType NVARCHAR(50) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'IsRequired') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD IsRequired BIT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'ExpectedAnswer') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD ExpectedAnswer NVARCHAR(20) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'QuestionLogic') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD QuestionLogic NVARCHAR(50) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'SortOrder') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD SortOrder INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'IsActive') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD IsActive BIT NULL;');

    DECLARE @Questions TABLE
    (
        SectionName NVARCHAR(200) NOT NULL,
        QuestionText NVARCHAR(1000) NOT NULL,
        SampleType NVARCHAR(100) NOT NULL,
        SortOrder INT NOT NULL
    );
    INSERT @Questions(SectionName,QuestionText,SampleType,SortOrder)
    VALUES
      (N'Phase I - Laboratory Review',N'Were analyst authorization, the current approved method, calculations, transcription, raw data, and audit trail independently reviewed?',N'All',9000),
      (N'Specification Traceability',N'Was the exact material/product code, production stage where applicable, specification number, and frozen specification version verified?',N'All',9010),
      (N'Media / Equipment / Incubation',N'Were media identity and GPT status, equipment status, controls, incubation conditions, and reading conditions acceptable?',N'All',9020),
      (N'Result Confirmation',N'Was the original result confirmed without unauthorized invalidation, undocumented repeat testing, or testing into compliance?',N'All',9030),
      (N'Impact / CAPA / Disposition',N'Were batch or material impact, recurrence, root cause, CAPA requirement, and final disposition scientifically documented?',N'All',9040),
      (N'Raw Material Traceability',N'Were manufacturer, supplier, manufacturer lot, GRN, receipt, storage, expiry/retest, and sampling traceability verified?',N'Raw Material',9100),
      (N'Raw Material Risk',N'Were supplier history, material microbiological risk, prior lots, and related deviations or complaints reviewed?',N'Raw Material',9110),
      (N'Raw Material Disposition',N'Was the effect on material status and any affected product batches assessed before disposition?',N'Raw Material',9120),
      (N'In-Process Traceability',N'Were product code, batch, exact production stage, sampled-from location, machine/line, and sampling time verified?',N'Production / In-Process',9200),
      (N'In-Process Conditions',N'Were process, hold-time, cleaning, environmental, personnel, and equipment conditions around sampling reviewed?',N'Production / In-Process',9210),
      (N'In-Process Impact',N'Were subsequent stages, finished-product risk, affected quantity, and batch disposition assessed?',N'Production / In-Process',9220),
      (N'Finished Product Traceability',N'Were product code, batch, dosage form, manufacturing, packaging, expiry, pack size, and sample identity verified?',N'Finished Product',9300),
      (N'Finished Product Risk',N'Were manufacturing history, environmental/in-process results, packaging, storage, and prior-batch trend reviewed?',N'Finished Product',9310),
      (N'Finished Product Disposition',N'Was the final batch-release impact and any market or stability impact documented before disposition?',N'Finished Product',9320),
      (N'Stability Traceability',N'Were protocol number, chamber number, study type, pull point, storage condition, product, and batch verified?',N'Stability',9400),
      (N'Stability Chamber Review',N'Were chamber mapping, calibration, monitoring records, alarms, excursions, and sample placement history reviewed?',N'Stability',9410),
      (N'Stability Impact',N'Were trend, previous/subsequent pull points, shelf-life impact, and protocol/reporting requirements assessed?',N'Stability',9420);

    UPDATE existing
    SET existing.SectionName=source.SectionName,
        existing.AppliesToEventType=N'All',
        existing.AppliesToSampleType=source.SampleType,
        existing.AppliesToTestCategory=N'PRM Microbiology',
        existing.AppliesToTestNameKeyword=N'All',
        existing.AnswerType=N'YesNoNA',
        existing.IsRequired=1,
        existing.ExpectedAnswer=N'Yes',
        existing.QuestionLogic=N'PositiveCheck',
        existing.SortOrder=source.SortOrder,
        existing.IsActive=1
    FROM dbo.QualityEventChecklistQuestions existing
    INNER JOIN @Questions source ON source.QuestionText=existing.QuestionText;

    INSERT dbo.QualityEventChecklistQuestions
    (
        SectionName,QuestionText,AppliesToEventType,AppliesToSampleType,
        AppliesToTestCategory,AppliesToTestNameKeyword,AnswerType,IsRequired,
        ExpectedAnswer,QuestionLogic,SortOrder,IsActive
    )
    SELECT source.SectionName,source.QuestionText,N'All',source.SampleType,
           N'PRM Microbiology',N'All',N'YesNoNA',1,N'Yes',N'PositiveCheck',source.SortOrder,1
    FROM @Questions source
    WHERE NOT EXISTS
    (
        SELECT 1 FROM dbo.QualityEventChecklistQuestions existing
        WHERE existing.QuestionText=source.QuestionText
    );

    /* PRM affected-result links are polymorphic. PRM rows use SourceResultID and therefore
       SampleTestID must remain a nullable INT; no evidence row is deleted or rewritten. */
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SampleTestID') IS NULL
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SampleTestID INT NULL;');
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SampleTestID'
          AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
    )
        THROW 53750, 'QualityEventAffectedResults.SampleTestID must be a non-computed INT.', 1;
    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SampleTestID'
          AND is_nullable=0
    )
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN SampleTestID INT NULL;');

    /* Surrogate IDs for operational evidence tables. Existing IDs are preserved. */
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'AffectedResultID') IS NULL
        THROW 53720, 'Required column QualityEventAffectedResults.AffectedResultID is missing.', 1;
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'AffectedResultID' AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1))
        THROW 53721, 'AffectedResultID must be INT.', 1;
    IF EXISTS(SELECT 1 FROM dbo.QualityEventAffectedResults WHERE AffectedResultID IS NULL)
        THROW 53722, 'NULL affected-result identities require controlled review.', 1;
    IF EXISTS(SELECT AffectedResultID FROM dbo.QualityEventAffectedResults GROUP BY AffectedResultID HAVING COUNT_BIG(*)>1)
        THROW 53723, 'Duplicate affected-result identities require controlled review.', 1;
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'AffectedResultID' AND is_nullable=1)
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN AffectedResultID INT NOT NULL;');
    IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'AffectedResultID' AND is_identity=1)
    AND NOT EXISTS
    (
        SELECT 1 FROM sys.default_constraints d INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
        WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND c.name=N'AffectedResultID' AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
    )
    BEGIN
        IF EXISTS(SELECT 1 FROM sys.default_constraints d INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND c.name=N'AffectedResultID')
            THROW 53724, 'AffectedResultID has a non-sequence default and cannot safely auto-generate IDs.', 1;
        IF OBJECT_ID(N'dbo.Seq_QualityEventAffectedResultID_20260826_002',N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventAffectedResultID_20260826_002 AS INT START WITH 1 INCREMENT BY 1;');
        DECLARE @NextAffectedID BIGINT;
        SELECT @NextAffectedID=ISNULL(MAX(CONVERT(BIGINT,AffectedResultID)),0)+1 FROM dbo.QualityEventAffectedResults;
        IF @NextAffectedID>2147483647 THROW 53725, 'AffectedResultID range is exhausted.', 1;
        DECLARE @RestartAffectedSql NVARCHAR(4000);
        SET @RestartAffectedSql=N'ALTER SEQUENCE dbo.Seq_QualityEventAffectedResultID_20260826_002 RESTART WITH '+CONVERT(NVARCHAR(30),@NextAffectedID)+N';';
        EXEC(@RestartAffectedSql);
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD CONSTRAINT DF_QualityEventAffectedResults_ID_20260826_002 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventAffectedResultID_20260826_002) FOR AffectedResultID;');
    END;

    IF COL_LENGTH(N'dbo.QualityEventActions',N'QualityEventActionID') IS NULL THROW 53726, 'Required QualityEventActionID is missing.', 1;
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions') AND name=N'QualityEventActionID' AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)) THROW 53727, 'QualityEventActionID must be INT.', 1;
    IF EXISTS(SELECT 1 FROM dbo.QualityEventActions WHERE QualityEventActionID IS NULL) THROW 53728, 'NULL Quality Event action identities require controlled review.', 1;
    IF EXISTS(SELECT QualityEventActionID FROM dbo.QualityEventActions GROUP BY QualityEventActionID HAVING COUNT_BIG(*)>1) THROW 53729, 'Duplicate Quality Event action identities require controlled review.', 1;
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions') AND name=N'QualityEventActionID' AND is_nullable=1) EXEC(N'ALTER TABLE dbo.QualityEventActions ALTER COLUMN QualityEventActionID INT NOT NULL;');
    IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions') AND name=N'QualityEventActionID' AND is_identity=1)
    AND NOT EXISTS(SELECT 1 FROM sys.default_constraints d INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventActions') AND c.name=N'QualityEventActionID' AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%')
    BEGIN
        IF EXISTS(SELECT 1 FROM sys.default_constraints d INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventActions') AND c.name=N'QualityEventActionID') THROW 53730, 'QualityEventActionID has a non-sequence default.', 1;
        IF OBJECT_ID(N'dbo.Seq_QualityEventActionID_20260826_002',N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventActionID_20260826_002 AS INT START WITH 1 INCREMENT BY 1;');
        DECLARE @NextActionID2 BIGINT;
        SELECT @NextActionID2=ISNULL(MAX(CONVERT(BIGINT,QualityEventActionID)),0)+1 FROM dbo.QualityEventActions;
        IF @NextActionID2>2147483647 THROW 53731, 'QualityEventActionID range is exhausted.', 1;
        DECLARE @RestartActionSql NVARCHAR(4000);
        SET @RestartActionSql=N'ALTER SEQUENCE dbo.Seq_QualityEventActionID_20260826_002 RESTART WITH '+CONVERT(NVARCHAR(30),@NextActionID2)+N';';
        EXEC(@RestartActionSql);
        EXEC(N'ALTER TABLE dbo.QualityEventActions ADD CONSTRAINT DF_QualityEventActions_ID_20260826_002 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventActionID_20260826_002) FOR QualityEventActionID;');
    END;

    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnswerID') IS NULL THROW 53732, 'Required checklist AnswerID is missing.', 1;
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'AnswerID' AND (system_type_id NOT IN(TYPE_ID(N'int'),TYPE_ID(N'bigint')) OR is_computed=1)) THROW 53733, 'Checklist AnswerID must be INT or BIGINT.', 1;
    IF EXISTS(SELECT 1 FROM dbo.QualityEventChecklistAnswers WHERE AnswerID IS NULL) THROW 53734, 'NULL checklist answer identities require controlled review.', 1;
    IF EXISTS(SELECT AnswerID FROM dbo.QualityEventChecklistAnswers GROUP BY AnswerID HAVING COUNT_BIG(*)>1) THROW 53735, 'Duplicate checklist answer identities require controlled review.', 1;
    DECLARE @AnswerIdIsBigInt BIT=0;
    SELECT @AnswerIdIsBigInt=CASE WHEN system_type_id=TYPE_ID(N'bigint') THEN 1 ELSE 0 END FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'AnswerID';
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'AnswerID' AND is_nullable=1)
    BEGIN
        IF @AnswerIdIsBigInt=1 EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ALTER COLUMN AnswerID BIGINT NOT NULL;');
        ELSE EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ALTER COLUMN AnswerID INT NOT NULL;');
    END;
    IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'AnswerID' AND is_identity=1)
    AND NOT EXISTS(SELECT 1 FROM sys.default_constraints d INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND c.name=N'AnswerID' AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%')
    BEGIN
        IF EXISTS(SELECT 1 FROM sys.default_constraints d INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND c.name=N'AnswerID') THROW 53736, 'Checklist AnswerID has a non-sequence default.', 1;
        IF @AnswerIdIsBigInt=1
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_QualityEventAnswerID_Big_20260826_002',N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventAnswerID_Big_20260826_002 AS BIGINT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextAnswerBig BIGINT; SELECT @NextAnswerBig=ISNULL(MAX(CONVERT(BIGINT,AnswerID)),0)+1 FROM dbo.QualityEventChecklistAnswers;
            DECLARE @RestartAnswerBigSql NVARCHAR(4000);
            SET @RestartAnswerBigSql=N'ALTER SEQUENCE dbo.Seq_QualityEventAnswerID_Big_20260826_002 RESTART WITH '+CONVERT(NVARCHAR(30),@NextAnswerBig)+N';';
            EXEC(@RestartAnswerBigSql);
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD CONSTRAINT DF_QualityEventChecklistAnswers_ID_20260826_002 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventAnswerID_Big_20260826_002) FOR AnswerID;');
        END
        ELSE
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_QualityEventAnswerID_Int_20260826_002',N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventAnswerID_Int_20260826_002 AS INT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextAnswerInt BIGINT; SELECT @NextAnswerInt=ISNULL(MAX(CONVERT(BIGINT,AnswerID)),0)+1 FROM dbo.QualityEventChecklistAnswers;
            IF @NextAnswerInt>2147483647 THROW 53737, 'Checklist AnswerID range is exhausted.', 1;
            DECLARE @RestartAnswerIntSql NVARCHAR(4000);
            SET @RestartAnswerIntSql=N'ALTER SEQUENCE dbo.Seq_QualityEventAnswerID_Int_20260826_002 RESTART WITH '+CONVERT(NVARCHAR(30),@NextAnswerInt)+N';';
            EXEC(@RestartAnswerIntSql);
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD CONSTRAINT DF_QualityEventChecklistAnswers_ID_20260826_002 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventAnswerID_Int_20260826_002) FOR AnswerID;');
        END;
    END;

    IF COL_LENGTH(N'dbo.QualityEventPrintHistory',N'QualityEventPrintID') IS NULL THROW 53738, 'Required QualityEventPrintID is missing.', 1;
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'QualityEventPrintID' AND (system_type_id NOT IN(TYPE_ID(N'int'),TYPE_ID(N'bigint')) OR is_computed=1)) THROW 53739, 'QualityEventPrintID must be INT or BIGINT.', 1;
    IF EXISTS(SELECT 1 FROM dbo.QualityEventPrintHistory WHERE QualityEventPrintID IS NULL) THROW 53740, 'NULL Quality Event print identities require controlled review.', 1;
    IF EXISTS(SELECT QualityEventPrintID FROM dbo.QualityEventPrintHistory GROUP BY QualityEventPrintID HAVING COUNT_BIG(*)>1) THROW 53741, 'Duplicate Quality Event print identities require controlled review.', 1;
    DECLARE @PrintIdIsBigInt BIT=0;
    SELECT @PrintIdIsBigInt=CASE WHEN system_type_id=TYPE_ID(N'bigint') THEN 1 ELSE 0 END FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'QualityEventPrintID';
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'QualityEventPrintID' AND is_nullable=1)
    BEGIN
        IF @PrintIdIsBigInt=1 EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ALTER COLUMN QualityEventPrintID BIGINT NOT NULL;');
        ELSE EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ALTER COLUMN QualityEventPrintID INT NOT NULL;');
    END;
    IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'QualityEventPrintID' AND is_identity=1)
    AND NOT EXISTS(SELECT 1 FROM sys.default_constraints d INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND c.name=N'QualityEventPrintID' AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%')
    BEGIN
        IF EXISTS(SELECT 1 FROM sys.default_constraints d INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND c.name=N'QualityEventPrintID') THROW 53742, 'QualityEventPrintID has a non-sequence default.', 1;
        IF @PrintIdIsBigInt=1
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_QualityEventPrintID_Big_20260826_002',N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventPrintID_Big_20260826_002 AS BIGINT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextPrintBig2 BIGINT; SELECT @NextPrintBig2=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID)),0)+1 FROM dbo.QualityEventPrintHistory;
            DECLARE @RestartPrintBigSql NVARCHAR(4000);
            SET @RestartPrintBigSql=N'ALTER SEQUENCE dbo.Seq_QualityEventPrintID_Big_20260826_002 RESTART WITH '+CONVERT(NVARCHAR(30),@NextPrintBig2)+N';';
            EXEC(@RestartPrintBigSql);
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD CONSTRAINT DF_QualityEventPrintHistory_ID_20260826_002 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventPrintID_Big_20260826_002) FOR QualityEventPrintID;');
        END
        ELSE
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_QualityEventPrintID_Int_20260826_002',N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventPrintID_Int_20260826_002 AS INT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextPrintInt2 BIGINT; SELECT @NextPrintInt2=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID)),0)+1 FROM dbo.QualityEventPrintHistory;
            IF @NextPrintInt2>2147483647 THROW 53743, 'QualityEventPrintID range is exhausted.', 1;
            DECLARE @RestartPrintIntSql NVARCHAR(4000);
            SET @RestartPrintIntSql=N'ALTER SEQUENCE dbo.Seq_QualityEventPrintID_Int_20260826_002 RESTART WITH '+CONVERT(NVARCHAR(30),@NextPrintInt2)+N';';
            EXEC(@RestartPrintIntSql);
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD CONSTRAINT DF_QualityEventPrintHistory_ID_20260826_002 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventPrintID_Int_20260826_002) FOR QualityEventPrintID;');
        END;
    END;

    /* Runtime uniqueness and lookup indexes. */
    IF EXISTS
    (
        SELECT QualityEventID,SourceModule,SourceResultID
        FROM dbo.QualityEventAffectedResults
        WHERE SourceModule IS NOT NULL AND SourceResultID IS NOT NULL
        GROUP BY QualityEventID,SourceModule,SourceResultID
        HAVING COUNT_BIG(*)>1
    )
        THROW 53744, 'Duplicate PRM affected-result links require controlled data review.', 1;
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name IN(N'UX_QualityEventAffectedResults_Source_20260826',N'UX_QualityEventAffectedResults_Source_20260826_002')
    )
        CREATE UNIQUE INDEX UX_QualityEventAffectedResults_Source_20260826_002
            ON dbo.QualityEventAffectedResults(QualityEventID,SourceModule,SourceResultID)
            WHERE SourceModule IS NOT NULL AND SourceResultID IS NOT NULL;

    IF EXISTS
    (
        SELECT QualityEventID,QuestionID
        FROM dbo.QualityEventChecklistAnswers
        WHERE QualityEventID IS NOT NULL AND QuestionID IS NOT NULL
        GROUP BY QualityEventID,QuestionID
        HAVING COUNT_BIG(*)>1
    )
        THROW 53745, 'Duplicate checklist answers require controlled data review.', 1;
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
          AND is_unique=1
          AND name IN(N'UQ_QualityEventChecklistAnswers',N'UQ_QualityEventChecklistAnswers_20260824_003',N'UQ_QualityEventChecklistAnswers_20260826',N'UQ_QualityEventChecklistAnswers_20260826_002')
    )
        CREATE UNIQUE INDEX UQ_QualityEventChecklistAnswers_20260826_002
            ON dbo.QualityEventChecklistAnswers(QualityEventID,QuestionID)
            WHERE QualityEventID IS NOT NULL AND QuestionID IS NOT NULL;

    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name IN(N'IX_QualityEvents_SourceStatus_20260824',N'IX_QualityEvents_SourceStatus_20260824_003',N'IX_QualityEvents_SourceStatus_20260826',N'IX_QualityEvents_SourceStatus_20260826_002'))
        CREATE INDEX IX_QualityEvents_SourceStatus_20260826_002 ON dbo.QualityEvents(SourceModule,SourceRecordID,CurrentStatus,QualityEventID);
    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions') AND name IN(N'IX_QualityEventActions_Event',N'IX_QualityEventActions_Event_20260824_003',N'IX_QualityEventActions_Event_20260826',N'IX_QualityEventActions_Event_20260826_002'))
        CREATE INDEX IX_QualityEventActions_Event_20260826_002 ON dbo.QualityEventActions(QualityEventID,PerformedDate,QualityEventActionID);
    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name IN(N'IX_QualityEventPrintHistory_Event',N'IX_QualityEventPrintHistory_Event_20260824_003',N'IX_QualityEventPrintHistory_Event_20260826',N'IX_QualityEventPrintHistory_Event_20260826_002'))
        CREATE INDEX IX_QualityEventPrintHistory_Event_20260826_002 ON dbo.QualityEventPrintHistory(QualityEventID,PrintedDate,QualityEventPrintID);

    EXEC sys.sp_executesql N'
        CREATE OR ALTER TRIGGER dbo.TRG_QualityEventPrintHistory_AppendOnly
        ON dbo.QualityEventPrintHistory
        AFTER UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 51050, ''Controlled PharmaLIMS compliance records are append-only and cannot be updated or deleted.'', 1;
        END;';

    /* Final operational verification. */
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'QualityEventID'
          AND system_type_id=TYPE_ID(N'int') AND is_nullable=0 AND is_computed=0
    )
        THROW 53746, 'Final Quality Event identity verification failed.', 1;
    IF NOT
    (
        EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'QualityEventID' AND is_identity=1)
        OR EXISTS
        (
            SELECT 1 FROM sys.default_constraints d
            INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEvents') AND c.name=N'QualityEventID' AND OBJECT_DEFINITION(d.object_id) LIKE N'%NEXT VALUE FOR%'
        )
    )
        THROW 53747, 'Quality Event IDs are not auto-generated by IDENTITY or a controlled sequence.', 1;
    IF COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents',N'QAConclusion') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents',N'FinalDisposition') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceModule') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceResultID') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventActions',N'ActionType') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnswerValue') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventPrintHistory',N'PrintedDate') IS NULL
        THROW 53748, 'Final PRM Quality Event runtime projection verification failed.', 1;
    IF (SELECT COUNT(*) FROM dbo.QualityEventChecklistQuestions WHERE AppliesToTestCategory=N'PRM Microbiology' AND IsActive=1 AND SortOrder BETWEEN 9000 AND 9420)<17
        THROW 53749, 'Controlled PRM investigation checklist is incomplete after migration.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260826_002' AS MigrationVersion;
