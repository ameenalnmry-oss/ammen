SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled prerequisite for 20260826_001.

    Purpose:
    - Reconcile legacy Quality Event support tables that already contain a
      different IDENTITY column.
    - Materialize the current canonical surrogate columns without attempting
      to add a second IDENTITY column (SQL Server error 2744).
    - Preserve every legacy identifier and every existing row.
    - Generate canonical IDs with SQL SEQUENCE defaults when the canonical
      column is not itself IDENTITY.

    No microbiology result, investigation conclusion, disposition, signature,
    certificate, or print record is deleted or overwritten.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
        THROW 53800, 'Required table dbo.QualityEvents is missing.', 1;
    IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions', N'U') IS NULL
        THROW 53801, 'Required table dbo.QualityEventChecklistQuestions is missing.', 1;

    /* ------------------------------------------------------------------
       QualityEvents canonical key
       ------------------------------------------------------------------ */
    IF COL_LENGTH(N'dbo.QualityEvents', N'QualityEventID') IS NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD QualityEventID INT NULL;');

        IF COL_LENGTH(N'dbo.QualityEvents', N'EventID') IS NOT NULL
        BEGIN
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'EventID' AND system_type_id<>TYPE_ID(N'int'))
                THROW 53802, 'Legacy dbo.QualityEvents.EventID is not INT.', 1;
            EXEC(N'UPDATE dbo.QualityEvents SET QualityEventID=EventID WHERE QualityEventID IS NULL;');
        END
        ELSE IF EXISTS (SELECT 1 FROM dbo.QualityEvents)
            THROW 53803, 'Legacy QualityEvents rows have no controlled canonical-key mapping.', 1;
    END;

    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'QualityEventID' AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1))
        THROW 53804, 'dbo.QualityEvents.QualityEventID must be a non-computed INT.', 1;
    DECLARE @NullQualityEventIdCount BIGINT=0;
    DECLARE @DuplicateQualityEventIdCount BIGINT=0;
    EXEC sys.sp_executesql
        N'SELECT @NullCount=COUNT_BIG(*) FROM dbo.QualityEvents WHERE QualityEventID IS NULL;
          SELECT @DuplicateCount=COUNT_BIG(*) FROM (SELECT QualityEventID FROM dbo.QualityEvents GROUP BY QualityEventID HAVING COUNT_BIG(*)>1) d;',
        N'@NullCount BIGINT OUTPUT,@DuplicateCount BIGINT OUTPUT',
        @NullCount=@NullQualityEventIdCount OUTPUT,@DuplicateCount=@DuplicateQualityEventIdCount OUTPUT;
    IF @NullQualityEventIdCount>0
        THROW 53805, 'NULL QualityEventID values require controlled data review.', 1;
    IF @DuplicateQualityEventIdCount>0
        THROW 53806, 'Duplicate QualityEventID values require controlled data review.', 1;
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'QualityEventID' AND is_nullable=1)
        EXEC(N'ALTER TABLE dbo.QualityEvents ALTER COLUMN QualityEventID INT NOT NULL;');
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes i
        WHERE i.object_id=OBJECT_ID(N'dbo.QualityEvents') AND i.is_unique=1 AND i.is_disabled=0
          AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=1
          AND EXISTS
          (
              SELECT 1 FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
              WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'QualityEventID'
          )
    )
        EXEC(N'CREATE UNIQUE INDEX UX_QualityEvents_QualityEventID_20260825_007 ON dbo.QualityEvents(QualityEventID);');

    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'QualityEventID' AND is_identity=1)
       AND NOT EXISTS
       (
           SELECT 1 FROM sys.default_constraints d
           JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
           WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEvents') AND c.name=N'QualityEventID'
       )
    BEGIN
        IF OBJECT_ID(N'dbo.Seq_QualityEventID_20260825_007', N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventID_20260825_007 AS INT START WITH 1 INCREMENT BY 1;');
        DECLARE @NextQualityEventID BIGINT;
        EXEC sys.sp_executesql
            N'SELECT @NextValue=ISNULL(MAX(CONVERT(BIGINT,QualityEventID)),0)+1 FROM dbo.QualityEvents;',
            N'@NextValue BIGINT OUTPUT',
            @NextValue=@NextQualityEventID OUTPUT;
        IF @NextQualityEventID>2147483647 THROW 53807, 'QualityEventID range is exhausted.', 1;
        DECLARE @RestartQualityEventSql NVARCHAR(4000);
        SET @RestartQualityEventSql=N'ALTER SEQUENCE dbo.Seq_QualityEventID_20260825_007 RESTART WITH '+CONVERT(NVARCHAR(30),@NextQualityEventID)+N';';
        EXEC(@RestartQualityEventSql);
        EXEC(N'ALTER TABLE dbo.QualityEvents ADD CONSTRAINT DF_QualityEvents_ID_20260825_007 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventID_20260825_007) FOR QualityEventID;');
    END;

    /* ------------------------------------------------------------------
       Affected results canonical surrogate and event link
       ------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'AffectedResultID') IS NULL
            EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD AffectedResultID INT NULL;');
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'AffectedResultID' AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1))
            THROW 53810, 'AffectedResultID must be a non-computed INT.', 1;
        DECLARE @DuplicateAffectedResultIdCount BIGINT=0;
        EXEC sys.sp_executesql
            N'SELECT @DuplicateCount=COUNT_BIG(*) FROM (SELECT AffectedResultID FROM dbo.QualityEventAffectedResults WHERE AffectedResultID IS NOT NULL GROUP BY AffectedResultID HAVING COUNT_BIG(*)>1) d;',
            N'@DuplicateCount BIGINT OUTPUT',
            @DuplicateCount=@DuplicateAffectedResultIdCount OUTPUT;
        IF @DuplicateAffectedResultIdCount>0
            THROW 53811, 'Duplicate AffectedResultID values require controlled review.', 1;
        DECLARE @AffectedResultIsIdentity BIT=0;
        SELECT @AffectedResultIsIdentity=CASE WHEN is_identity=1 THEN 1 ELSE 0 END
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'AffectedResultID';

        /* Never UPDATE an existing IDENTITY column (SQL Server error 8102).
           Only sequence-backfill a canonical column that is not IDENTITY. */
        IF @AffectedResultIsIdentity=0
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_AffectedResultID_20260825_007', N'SO') IS NULL
                EXEC(N'CREATE SEQUENCE dbo.Seq_AffectedResultID_20260825_007 AS INT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextAffectedResultID BIGINT;
            EXEC sys.sp_executesql
                N'SELECT @NextValue=ISNULL(MAX(CONVERT(BIGINT,AffectedResultID)),0)+1 FROM dbo.QualityEventAffectedResults;',
                N'@NextValue BIGINT OUTPUT',
                @NextValue=@NextAffectedResultID OUTPUT;
            IF @NextAffectedResultID>2147483647 THROW 53812, 'AffectedResultID range is exhausted.', 1;
            DECLARE @RestartAffectedResultSql NVARCHAR(4000);
            SET @RestartAffectedResultSql=N'ALTER SEQUENCE dbo.Seq_AffectedResultID_20260825_007 RESTART WITH '+CONVERT(NVARCHAR(30),@NextAffectedResultID)+N';';
            EXEC(@RestartAffectedResultSql);
            EXEC(N'UPDATE dbo.QualityEventAffectedResults SET AffectedResultID=NEXT VALUE FOR dbo.Seq_AffectedResultID_20260825_007 WHERE AffectedResultID IS NULL;');
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'AffectedResultID' AND is_nullable=1)
                EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN AffectedResultID INT NOT NULL;');
            IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND c.name=N'AffectedResultID')
                EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD CONSTRAINT DF_QualityEventAffectedResults_ID_20260825_007 DEFAULT (NEXT VALUE FOR dbo.Seq_AffectedResultID_20260825_007) FOR AffectedResultID;');
        END;
        DECLARE @NullAffectedResultIdCount BIGINT=0;
        EXEC sys.sp_executesql
            N'SELECT @NullCount=COUNT_BIG(*) FROM dbo.QualityEventAffectedResults WHERE AffectedResultID IS NULL;',
            N'@NullCount BIGINT OUTPUT',
            @NullCount=@NullAffectedResultIdCount OUTPUT;
        IF @NullAffectedResultIdCount>0
            THROW 53813, 'AffectedResultID reconciliation is incomplete.', 1;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND is_unique=1 AND name=N'UX_QualityEventAffectedResults_ID_20260825_007')
            EXEC(N'CREATE UNIQUE INDEX UX_QualityEventAffectedResults_ID_20260825_007 ON dbo.QualityEventAffectedResults(AffectedResultID);');

        IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'QualityEventID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD QualityEventID INT NULL;');
            IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'EventID') IS NOT NULL
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'EventID' AND system_type_id<>TYPE_ID(N'int'))
                    THROW 53814, 'Legacy affected-result EventID is not INT.', 1;
                EXEC(N'UPDATE dbo.QualityEventAffectedResults SET QualityEventID=EventID WHERE QualityEventID IS NULL;');
            END
            ELSE IF EXISTS (SELECT 1 FROM dbo.QualityEventAffectedResults)
                THROW 53815, 'Affected-result rows have no controlled Quality Event link mapping.', 1;
        END;

        /* Actual site legacy schema uses DECIMAL ResultValue and shorter textual
           columns. PRM Quality Event evidence must also store qualitative results.
           Widen these columns before the current baseline is compiled. */
        IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'ResultValue') IS NOT NULL
           AND EXISTS
           (
               SELECT 1 FROM sys.columns
               WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
                 AND name=N'ResultValue'
                 AND NOT (system_type_id=TYPE_ID(N'nvarchar') AND (max_length=-1 OR max_length>=400))
           )
            EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN ResultValue NVARCHAR(200) NULL;');
        IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SpecificationLimit') IS NOT NULL
           AND EXISTS
           (
               SELECT 1 FROM sys.columns
               WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
                 AND name=N'SpecificationLimit'
                 AND NOT (system_type_id=TYPE_ID(N'nvarchar') AND (max_length=-1 OR max_length>=1000))
           )
            EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN SpecificationLimit NVARCHAR(500) NULL;');
        IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'FailureType') IS NOT NULL
           AND EXISTS
           (
               SELECT 1 FROM sys.columns
               WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
                 AND name=N'FailureType'
                 AND NOT (system_type_id=TYPE_ID(N'nvarchar') AND (max_length=-1 OR max_length>=240))
           )
            EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN FailureType NVARCHAR(120) NULL;');
    END;

    /* ------------------------------------------------------------------
       Action canonical surrogate and event link
       ------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.QualityEventActions', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.QualityEventActions', N'QualityEventActionID') IS NULL
            EXEC(N'ALTER TABLE dbo.QualityEventActions ADD QualityEventActionID INT NULL;');
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions') AND name=N'QualityEventActionID' AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1))
            THROW 53820, 'QualityEventActionID must be a non-computed INT.', 1;
        DECLARE @DuplicateActionIdCount BIGINT=0;
        EXEC sys.sp_executesql
            N'SELECT @DuplicateCount=COUNT_BIG(*) FROM (SELECT QualityEventActionID FROM dbo.QualityEventActions WHERE QualityEventActionID IS NOT NULL GROUP BY QualityEventActionID HAVING COUNT_BIG(*)>1) d;',
            N'@DuplicateCount BIGINT OUTPUT',
            @DuplicateCount=@DuplicateActionIdCount OUTPUT;
        IF @DuplicateActionIdCount>0
            THROW 53821, 'Duplicate QualityEventActionID values require controlled review.', 1;
        DECLARE @ActionIdIsIdentity BIT=0;
        SELECT @ActionIdIsIdentity=CASE WHEN is_identity=1 THEN 1 ELSE 0 END
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions') AND name=N'QualityEventActionID';
        IF @ActionIdIsIdentity=0
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_QualityEventActionID_20260825_007', N'SO') IS NULL
                EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventActionID_20260825_007 AS INT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextActionID BIGINT;
            EXEC sys.sp_executesql
                N'SELECT @NextValue=ISNULL(MAX(CONVERT(BIGINT,QualityEventActionID)),0)+1 FROM dbo.QualityEventActions;',
                N'@NextValue BIGINT OUTPUT',
                @NextValue=@NextActionID OUTPUT;
            IF @NextActionID>2147483647 THROW 53822, 'QualityEventActionID range is exhausted.', 1;
            DECLARE @RestartActionSql NVARCHAR(4000);
            SET @RestartActionSql=N'ALTER SEQUENCE dbo.Seq_QualityEventActionID_20260825_007 RESTART WITH '+CONVERT(NVARCHAR(30),@NextActionID)+N';';
            EXEC(@RestartActionSql);
            EXEC(N'UPDATE dbo.QualityEventActions SET QualityEventActionID=NEXT VALUE FOR dbo.Seq_QualityEventActionID_20260825_007 WHERE QualityEventActionID IS NULL;');
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions') AND name=N'QualityEventActionID' AND is_nullable=1)
                EXEC(N'ALTER TABLE dbo.QualityEventActions ALTER COLUMN QualityEventActionID INT NOT NULL;');
            IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventActions') AND c.name=N'QualityEventActionID')
                EXEC(N'ALTER TABLE dbo.QualityEventActions ADD CONSTRAINT DF_QualityEventActions_ID_20260825_007 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventActionID_20260825_007) FOR QualityEventActionID;');
        END;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions') AND is_unique=1 AND name=N'UX_QualityEventActions_ID_20260825_007')
            EXEC(N'CREATE UNIQUE INDEX UX_QualityEventActions_ID_20260825_007 ON dbo.QualityEventActions(QualityEventActionID);');

        IF COL_LENGTH(N'dbo.QualityEventActions', N'QualityEventID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventActions ADD QualityEventID INT NULL;');
            IF COL_LENGTH(N'dbo.QualityEventActions', N'EventID') IS NOT NULL
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions') AND name=N'EventID' AND system_type_id<>TYPE_ID(N'int'))
                    THROW 53823, 'Legacy action EventID is not INT.', 1;
                EXEC(N'UPDATE dbo.QualityEventActions SET QualityEventID=EventID WHERE QualityEventID IS NULL;');
            END
            ELSE IF EXISTS (SELECT 1 FROM dbo.QualityEventActions)
                THROW 53824, 'Quality Event action rows have no controlled event-link mapping.', 1;
        END;
    END;

    /* ------------------------------------------------------------------
       Checklist answer canonical surrogate and links
       ------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.QualityEventChecklistAnswers', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnswerID') IS NULL
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnswerID BIGINT NULL;');
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'AnswerID' AND (system_type_id NOT IN(TYPE_ID(N'int'),TYPE_ID(N'bigint')) OR is_computed=1))
            THROW 53830, 'AnswerID must be a non-computed INT or BIGINT.', 1;
        DECLARE @DuplicateAnswerIdCount BIGINT=0;
        EXEC sys.sp_executesql
            N'SELECT @DuplicateCount=COUNT_BIG(*) FROM (SELECT AnswerID FROM dbo.QualityEventChecklistAnswers WHERE AnswerID IS NOT NULL GROUP BY AnswerID HAVING COUNT_BIG(*)>1) d;',
            N'@DuplicateCount BIGINT OUTPUT',
            @DuplicateCount=@DuplicateAnswerIdCount OUTPUT;
        IF @DuplicateAnswerIdCount>0
            THROW 53831, 'Duplicate AnswerID values require controlled review.', 1;
        DECLARE @AnswerIsBigInt BIT=0;
        DECLARE @AnswerIsIdentity BIT=0;
        SELECT
            @AnswerIsBigInt=CASE WHEN system_type_id=TYPE_ID(N'bigint') THEN 1 ELSE 0 END,
            @AnswerIsIdentity=CASE WHEN is_identity=1 THEN 1 ELSE 0 END
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'AnswerID';
        IF @AnswerIsIdentity=0 AND @AnswerIsBigInt=1
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_QualityEventAnswerID_Big_20260825_007', N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventAnswerID_Big_20260825_007 AS BIGINT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextAnswerBig BIGINT;
            EXEC sys.sp_executesql
                N'SELECT @NextValue=ISNULL(MAX(CONVERT(BIGINT,AnswerID)),0)+1 FROM dbo.QualityEventChecklistAnswers;',
                N'@NextValue BIGINT OUTPUT',
                @NextValue=@NextAnswerBig OUTPUT;
            DECLARE @RestartAnswerBigSql NVARCHAR(4000);
            SET @RestartAnswerBigSql=N'ALTER SEQUENCE dbo.Seq_QualityEventAnswerID_Big_20260825_007 RESTART WITH '+CONVERT(NVARCHAR(30),@NextAnswerBig)+N';';
            EXEC(@RestartAnswerBigSql);
            EXEC(N'UPDATE dbo.QualityEventChecklistAnswers SET AnswerID=NEXT VALUE FOR dbo.Seq_QualityEventAnswerID_Big_20260825_007 WHERE AnswerID IS NULL;');
            IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND c.name=N'AnswerID')
                EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD CONSTRAINT DF_QualityEventChecklistAnswers_ID_Big_20260825_007 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventAnswerID_Big_20260825_007) FOR AnswerID;');
        END
        ELSE IF @AnswerIsIdentity=0
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_QualityEventAnswerID_Int_20260825_007', N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventAnswerID_Int_20260825_007 AS INT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextAnswerInt BIGINT;
            EXEC sys.sp_executesql
                N'SELECT @NextValue=ISNULL(MAX(CONVERT(BIGINT,AnswerID)),0)+1 FROM dbo.QualityEventChecklistAnswers;',
                N'@NextValue BIGINT OUTPUT',
                @NextValue=@NextAnswerInt OUTPUT;
            IF @NextAnswerInt>2147483647 THROW 53832, 'AnswerID range is exhausted.', 1;
            DECLARE @RestartAnswerIntSql NVARCHAR(4000);
            SET @RestartAnswerIntSql=N'ALTER SEQUENCE dbo.Seq_QualityEventAnswerID_Int_20260825_007 RESTART WITH '+CONVERT(NVARCHAR(30),@NextAnswerInt)+N';';
            EXEC(@RestartAnswerIntSql);
            EXEC(N'UPDATE dbo.QualityEventChecklistAnswers SET AnswerID=NEXT VALUE FOR dbo.Seq_QualityEventAnswerID_Int_20260825_007 WHERE AnswerID IS NULL;');
            IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND c.name=N'AnswerID')
                EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD CONSTRAINT DF_QualityEventChecklistAnswers_ID_Int_20260825_007 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventAnswerID_Int_20260825_007) FOR AnswerID;');
        END;
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND name=N'AnswerID' AND is_nullable=1)
        BEGIN
            IF @AnswerIsBigInt=1 EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ALTER COLUMN AnswerID BIGINT NOT NULL;');
            ELSE EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ALTER COLUMN AnswerID INT NOT NULL;');
        END;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers') AND is_unique=1 AND name=N'UX_QualityEventChecklistAnswers_ID_20260825_007')
            EXEC(N'CREATE UNIQUE INDEX UX_QualityEventChecklistAnswers_ID_20260825_007 ON dbo.QualityEventChecklistAnswers(AnswerID);');

        IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'QualityEventID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD QualityEventID INT NULL;');
            IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'EventID') IS NOT NULL
                EXEC(N'UPDATE dbo.QualityEventChecklistAnswers SET QualityEventID=EventID WHERE QualityEventID IS NULL;');
            ELSE IF EXISTS (SELECT 1 FROM dbo.QualityEventChecklistAnswers)
                THROW 53833, 'Checklist answers have no controlled Quality Event link mapping.', 1;
        END;
        IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'QuestionID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD QuestionID INT NULL;');
            IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'ChecklistQuestionID') IS NOT NULL
                EXEC(N'UPDATE dbo.QualityEventChecklistAnswers SET QuestionID=ChecklistQuestionID WHERE QuestionID IS NULL;');
            ELSE IF EXISTS (SELECT 1 FROM dbo.QualityEventChecklistAnswers)
                THROW 53834, 'Checklist answers have no controlled question-link mapping.', 1;
        END;
    END;

    /* ------------------------------------------------------------------
       Print history canonical surrogate and event link
       ------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.QualityEventPrintHistory', N'U') IS NOT NULL
    BEGIN
        DECLARE @PrintTriggerWasEnabled BIT=0;
        IF EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'TRG_QualityEventPrintHistory_AppendOnly' AND is_disabled=0)
        BEGIN
            SET @PrintTriggerWasEnabled=1;
            EXEC(N'DISABLE TRIGGER dbo.TRG_QualityEventPrintHistory_AppendOnly ON dbo.QualityEventPrintHistory;');
        END;

        IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'QualityEventPrintID') IS NULL
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventPrintID BIGINT NULL;');
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'QualityEventPrintID' AND (system_type_id NOT IN(TYPE_ID(N'int'),TYPE_ID(N'bigint')) OR is_computed=1))
            THROW 53840, 'QualityEventPrintID must be a non-computed INT or BIGINT.', 1;
        /* QualityEventPrintID may have been added above in this same migration.
           Keep all data references in a separately compiled dynamic batch so SQL Server
           never binds the new column before ALTER TABLE has completed (error 207). */
        DECLARE @DuplicatePrintIdCount BIGINT=0;
        EXEC sys.sp_executesql
            N'SELECT @DuplicateCount=COUNT_BIG(*)
              FROM (
                  SELECT QualityEventPrintID
                  FROM dbo.QualityEventPrintHistory
                  WHERE QualityEventPrintID IS NOT NULL
                  GROUP BY QualityEventPrintID
                  HAVING COUNT_BIG(*)>1
              ) d;',
            N'@DuplicateCount BIGINT OUTPUT',
            @DuplicateCount=@DuplicatePrintIdCount OUTPUT;
        IF @DuplicatePrintIdCount>0
            THROW 53841, 'Duplicate QualityEventPrintID values require controlled review.', 1;

        DECLARE @PrintIsBigInt BIT=0;
        DECLARE @PrintIsIdentity BIT=0;
        SELECT
            @PrintIsBigInt=CASE WHEN system_type_id=TYPE_ID(N'bigint') THEN 1 ELSE 0 END,
            @PrintIsIdentity=CASE WHEN is_identity=1 THEN 1 ELSE 0 END
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'QualityEventPrintID';
        IF @PrintIsIdentity=0 AND @PrintIsBigInt=1
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_QualityEventPrintID_Big_20260825_007', N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventPrintID_Big_20260825_007 AS BIGINT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextPrintBig BIGINT;
            EXEC sys.sp_executesql
                N'SELECT @NextValue=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID)),0)+1 FROM dbo.QualityEventPrintHistory;',
                N'@NextValue BIGINT OUTPUT',
                @NextValue=@NextPrintBig OUTPUT;
            DECLARE @RestartPrintBigSql NVARCHAR(4000);
            SET @RestartPrintBigSql=N'ALTER SEQUENCE dbo.Seq_QualityEventPrintID_Big_20260825_007 RESTART WITH '+CONVERT(NVARCHAR(30),@NextPrintBig)+N';';
            EXEC(@RestartPrintBigSql);
            EXEC(N'UPDATE dbo.QualityEventPrintHistory SET QualityEventPrintID=NEXT VALUE FOR dbo.Seq_QualityEventPrintID_Big_20260825_007 WHERE QualityEventPrintID IS NULL;');
            IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND c.name=N'QualityEventPrintID')
                EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD CONSTRAINT DF_QualityEventPrintHistory_ID_Big_20260825_007 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventPrintID_Big_20260825_007) FOR QualityEventPrintID;');
        END
        ELSE IF @PrintIsIdentity=0
        BEGIN
            IF OBJECT_ID(N'dbo.Seq_QualityEventPrintID_Int_20260825_007', N'SO') IS NULL EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventPrintID_Int_20260825_007 AS INT START WITH 1 INCREMENT BY 1;');
            DECLARE @NextPrintInt BIGINT;
            EXEC sys.sp_executesql
                N'SELECT @NextValue=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID)),0)+1 FROM dbo.QualityEventPrintHistory;',
                N'@NextValue BIGINT OUTPUT',
                @NextValue=@NextPrintInt OUTPUT;
            IF @NextPrintInt>2147483647 THROW 53842, 'QualityEventPrintID range is exhausted.', 1;
            DECLARE @RestartPrintIntSql NVARCHAR(4000);
            SET @RestartPrintIntSql=N'ALTER SEQUENCE dbo.Seq_QualityEventPrintID_Int_20260825_007 RESTART WITH '+CONVERT(NVARCHAR(30),@NextPrintInt)+N';';
            EXEC(@RestartPrintIntSql);
            EXEC(N'UPDATE dbo.QualityEventPrintHistory SET QualityEventPrintID=NEXT VALUE FOR dbo.Seq_QualityEventPrintID_Int_20260825_007 WHERE QualityEventPrintID IS NULL;');
            IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND c.name=N'QualityEventPrintID')
                EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD CONSTRAINT DF_QualityEventPrintHistory_ID_Int_20260825_007 DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventPrintID_Int_20260825_007) FOR QualityEventPrintID;');
        END;
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND name=N'QualityEventPrintID' AND is_nullable=1)
        BEGIN
            IF @PrintIsBigInt=1 EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ALTER COLUMN QualityEventPrintID BIGINT NOT NULL;');
            ELSE EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ALTER COLUMN QualityEventPrintID INT NOT NULL;');
        END;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory') AND is_unique=1 AND name=N'UX_QualityEventPrintHistory_ID_20260825_007')
            EXEC(N'CREATE UNIQUE INDEX UX_QualityEventPrintHistory_ID_20260825_007 ON dbo.QualityEventPrintHistory(QualityEventPrintID);');

        IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'QualityEventID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventID INT NULL;');
            IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'EventID') IS NOT NULL
                EXEC(N'UPDATE dbo.QualityEventPrintHistory SET QualityEventID=EventID WHERE QualityEventID IS NULL;');
            ELSE IF EXISTS (SELECT 1 FROM dbo.QualityEventPrintHistory)
                THROW 53843, 'Print-history rows have no controlled Quality Event link mapping.', 1;
        END;

        IF @PrintTriggerWasEnabled=1
            EXEC(N'ENABLE TRIGGER dbo.TRG_QualityEventPrintHistory_AppendOnly ON dbo.QualityEventPrintHistory;');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
