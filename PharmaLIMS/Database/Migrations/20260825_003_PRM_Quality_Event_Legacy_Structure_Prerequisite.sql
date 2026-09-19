SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled structural prerequisite for 20260824_003.

    Some legacy Development databases already contain the three Quality Event
    support tables, but use an older surrogate-key or event-link layout.  The
    original 20260824_003 migration intentionally fails closed for that shape.

    This prerequisite creates only the canonical surrogate/link projection
    required by the current PRM workflow.  Existing legacy columns and rows are
    retained.  No result, specification, interpretation, signature, conclusion,
    disposition, or printed-document evidence is deleted or substituted.

    QualityEventPrintHistory is append-only.  Its known protection trigger is
    disabled only inside this migration transaction while new compatibility
    columns are populated, then re-enabled before commit.  Any failure rolls the
    complete transaction back, including the trigger state.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents', N'QualityEventID') IS NULL
        THROW 53530, 'The controlled Quality Event identity baseline is missing.', 1;

    IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions', N'U') IS NULL
       OR COL_LENGTH(N'dbo.QualityEventChecklistQuestions', N'QuestionID') IS NULL
        THROW 53531, 'The controlled checklist-question identity baseline is missing.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'QualityEventID'
          AND system_type_id<>TYPE_ID(N'int')
    )
        THROW 53532, 'The Quality Event identity type requires controlled reconciliation.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
          AND name=N'QuestionID'
          AND system_type_id<>TYPE_ID(N'int')
    )
        THROW 53533, 'The checklist-question identity type requires controlled reconciliation.', 1;

    IF OBJECT_ID(N'dbo.PRM_QualityEventStructureReconciliation', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_QualityEventStructureReconciliation
        (
            ReconciliationID BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_PRM_QualityEventStructureReconciliation PRIMARY KEY,
            TableName NVARCHAR(128) NOT NULL,
            CanonicalColumn NVARCHAR(128) NOT NULL,
            LegacySourceColumn NVARCHAR(128) NULL,
            ReconciliationAction NVARCHAR(240) NOT NULL,
            RecordedAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_PRM_QualityEventStructureReconciliation_RecordedAt DEFAULT SYSUTCDATETIME()
        );
    END;

    DECLARE @PrintAppendOnlyWasEnabled BIT=0;
    IF OBJECT_ID(N'dbo.QualityEventPrintHistory', N'U') IS NOT NULL
       AND EXISTS
       (
           SELECT 1 FROM sys.triggers
           WHERE parent_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
             AND name=N'TRG_QualityEventPrintHistory_AppendOnly'
             AND is_disabled=0
       )
    BEGIN
        SET @PrintAppendOnlyWasEnabled=1;
        EXEC(N'DISABLE TRIGGER dbo.TRG_QualityEventPrintHistory_AppendOnly
               ON dbo.QualityEventPrintHistory;');
    END;

    /* ------------------------------------------------------------------
       Legacy action table
       ------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.QualityEventActions', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.QualityEventActions', N'QualityEventActionID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventActions ADD QualityEventActionID INT NULL;');
            INSERT dbo.PRM_QualityEventStructureReconciliation
                (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
            VALUES
                (N'QualityEventActions',N'QualityEventActionID',NULL,
                 N'Added an independent canonical surrogate while retaining every legacy identifier.');
        END;

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
              AND name=N'QualityEventActionID'
              AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
        )
            THROW 53534, 'The legacy Quality Event action surrogate is incompatible.', 1;

        IF OBJECT_ID(N'dbo.Seq_PRM_QualityEventActionID_20260825', N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_PRM_QualityEventActionID_20260825 AS INT START WITH 1 INCREMENT BY 1;');

        DECLARE @NextActionID BIGINT=1;
        EXEC sys.sp_executesql
            N'SELECT @Next=ISNULL(MAX(CONVERT(BIGINT,QualityEventActionID)),0)+1 FROM dbo.QualityEventActions;',
            N'@Next BIGINT OUTPUT',
            @Next=@NextActionID OUTPUT;
        IF @NextActionID>2147483647
            THROW 53535, 'The Quality Event action surrogate has exhausted the supported range.', 1;
        EXEC(N'ALTER SEQUENCE dbo.Seq_PRM_QualityEventActionID_20260825 RESTART WITH '
             + CONVERT(NVARCHAR(30),@NextActionID) + N';');
        EXEC(N'UPDATE dbo.QualityEventActions
               SET QualityEventActionID=NEXT VALUE FOR dbo.Seq_PRM_QualityEventActionID_20260825
               WHERE QualityEventActionID IS NULL;');

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
              AND name=N'QualityEventActionID'
              AND is_nullable=1
        )
            EXEC(N'ALTER TABLE dbo.QualityEventActions ALTER COLUMN QualityEventActionID INT NOT NULL;');

        DECLARE @DuplicateActionIDs BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1 FROM dbo.QualityEventActions
                  GROUP BY QualityEventActionID
                  HAVING COUNT_BIG(*)>1
              ) SET @HasDuplicates=1;',
            N'@HasDuplicates BIT OUTPUT',
            @HasDuplicates=@DuplicateActionIDs OUTPUT;
        IF @DuplicateActionIDs=1
            THROW 53536, 'Duplicate canonical Quality Event action surrogates were detected.', 1;

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.indexes
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
              AND name=N'UX_QualityEventActions_CanonicalID_20260825'
        )
            EXEC(N'CREATE UNIQUE INDEX UX_QualityEventActions_CanonicalID_20260825
                   ON dbo.QualityEventActions(QualityEventActionID);');

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.columns c
            WHERE c.object_id=OBJECT_ID(N'dbo.QualityEventActions')
              AND c.name=N'QualityEventActionID'
              AND c.is_identity=1
        )
        AND NOT EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id=d.parent_object_id
               AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventActions')
              AND c.name=N'QualityEventActionID'
        )
            EXEC(N'ALTER TABLE dbo.QualityEventActions
                   ADD CONSTRAINT DF_QualityEventActions_CanonicalID_20260825
                   DEFAULT (NEXT VALUE FOR dbo.Seq_PRM_QualityEventActionID_20260825)
                   FOR QualityEventActionID;');

        IF COL_LENGTH(N'dbo.QualityEventActions', N'QualityEventID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventActions ADD QualityEventID INT NULL;');

            IF COL_LENGTH(N'dbo.QualityEventActions', N'EventID') IS NOT NULL
            BEGIN
                IF EXISTS
                (
                    SELECT 1 FROM sys.columns
                    WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
                      AND name=N'EventID'
                      AND system_type_id<>TYPE_ID(N'int')
                )
                    THROW 53537, 'The legacy Quality Event action link type is incompatible.', 1;

                EXEC(N'UPDATE dbo.QualityEventActions SET QualityEventID=EventID WHERE QualityEventID IS NULL;');
                INSERT dbo.PRM_QualityEventStructureReconciliation
                    (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
                VALUES
                    (N'QualityEventActions',N'QualityEventID',N'EventID',
                     N'Copied the legacy event link into the canonical link without changing the legacy column.');
            END
            ELSE IF EXISTS(SELECT 1 FROM dbo.QualityEventActions)
                THROW 53538, 'Legacy Quality Event actions have no controlled event-link mapping.', 1;
        END;

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventActions')
              AND name=N'QualityEventID'
              AND system_type_id<>TYPE_ID(N'int')
        )
            THROW 53539, 'The canonical Quality Event action link type is incompatible.', 1;

        DECLARE @OrphanActionLinks BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1
                  FROM dbo.QualityEventActions a
                  LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=a.QualityEventID
                  WHERE a.QualityEventID IS NOT NULL AND e.QualityEventID IS NULL
              ) SET @HasOrphans=1;',
            N'@HasOrphans BIT OUTPUT',
            @HasOrphans=@OrphanActionLinks OUTPUT;
        IF @OrphanActionLinks=1
            THROW 53540, 'Orphan legacy Quality Event action links require controlled review.', 1;
    END;

    /* ------------------------------------------------------------------
       Legacy checklist-answer table
       ------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.QualityEventChecklistAnswers', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'AnswerID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnswerID BIGINT NULL;');
            INSERT dbo.PRM_QualityEventStructureReconciliation
                (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
            VALUES
                (N'QualityEventChecklistAnswers',N'AnswerID',NULL,
                 N'Added an independent canonical surrogate while retaining every legacy identifier.');
        END;

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
              AND name=N'AnswerID'
              AND (system_type_id<>TYPE_ID(N'bigint') OR is_computed=1)
        )
            THROW 53541, 'The legacy checklist-answer surrogate is incompatible.', 1;

        IF OBJECT_ID(N'dbo.Seq_PRM_QualityEventAnswerID_20260825', N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_PRM_QualityEventAnswerID_20260825 AS BIGINT START WITH 1 INCREMENT BY 1;');

        DECLARE @NextAnswerID BIGINT=1;
        EXEC sys.sp_executesql
            N'SELECT @Next=ISNULL(MAX(AnswerID),0)+1 FROM dbo.QualityEventChecklistAnswers;',
            N'@Next BIGINT OUTPUT',
            @Next=@NextAnswerID OUTPUT;
        EXEC(N'ALTER SEQUENCE dbo.Seq_PRM_QualityEventAnswerID_20260825 RESTART WITH '
             + CONVERT(NVARCHAR(30),@NextAnswerID) + N';');
        EXEC(N'UPDATE dbo.QualityEventChecklistAnswers
               SET AnswerID=NEXT VALUE FOR dbo.Seq_PRM_QualityEventAnswerID_20260825
               WHERE AnswerID IS NULL;');

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
              AND name=N'AnswerID'
              AND is_nullable=1
        )
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ALTER COLUMN AnswerID BIGINT NOT NULL;');

        DECLARE @DuplicateAnswerIDs BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1 FROM dbo.QualityEventChecklistAnswers
                  GROUP BY AnswerID
                  HAVING COUNT_BIG(*)>1
              ) SET @HasDuplicates=1;',
            N'@HasDuplicates BIT OUTPUT',
            @HasDuplicates=@DuplicateAnswerIDs OUTPUT;
        IF @DuplicateAnswerIDs=1
            THROW 53542, 'Duplicate canonical checklist-answer surrogates were detected.', 1;

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.indexes
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
              AND name=N'UX_QualityEventChecklistAnswers_CanonicalID_20260825'
        )
            EXEC(N'CREATE UNIQUE INDEX UX_QualityEventChecklistAnswers_CanonicalID_20260825
                   ON dbo.QualityEventChecklistAnswers(AnswerID);');

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
              AND name=N'AnswerID'
              AND is_identity=1
        )
        AND NOT EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id=d.parent_object_id
               AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
              AND c.name=N'AnswerID'
        )
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers
                   ADD CONSTRAINT DF_QualityEventChecklistAnswers_CanonicalID_20260825
                   DEFAULT (NEXT VALUE FOR dbo.Seq_PRM_QualityEventAnswerID_20260825)
                   FOR AnswerID;');

        IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'QualityEventID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD QualityEventID INT NULL;');
            IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'EventID') IS NOT NULL
            BEGIN
                IF EXISTS
                (
                    SELECT 1 FROM sys.columns
                    WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
                      AND name=N'EventID'
                      AND system_type_id<>TYPE_ID(N'int')
                )
                    THROW 53543, 'The legacy checklist-answer event link type is incompatible.', 1;
                EXEC(N'UPDATE dbo.QualityEventChecklistAnswers SET QualityEventID=EventID WHERE QualityEventID IS NULL;');
                INSERT dbo.PRM_QualityEventStructureReconciliation
                    (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
                VALUES
                    (N'QualityEventChecklistAnswers',N'QualityEventID',N'EventID',
                     N'Copied the legacy event link into the canonical link without changing the legacy column.');
            END
            ELSE IF EXISTS(SELECT 1 FROM dbo.QualityEventChecklistAnswers)
                THROW 53544, 'Legacy checklist answers have no controlled event-link mapping.', 1;
        END;

        IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'QuestionID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD QuestionID INT NULL;');
            IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers', N'ChecklistQuestionID') IS NOT NULL
            BEGIN
                IF EXISTS
                (
                    SELECT 1 FROM sys.columns
                    WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
                      AND name=N'ChecklistQuestionID'
                      AND system_type_id<>TYPE_ID(N'int')
                )
                    THROW 53545, 'The legacy checklist-question link type is incompatible.', 1;
                EXEC(N'UPDATE dbo.QualityEventChecklistAnswers
                       SET QuestionID=ChecklistQuestionID WHERE QuestionID IS NULL;');
                INSERT dbo.PRM_QualityEventStructureReconciliation
                    (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
                VALUES
                    (N'QualityEventChecklistAnswers',N'QuestionID',N'ChecklistQuestionID',
                     N'Copied the legacy question link into the canonical link without changing the legacy column.');
            END
            ELSE IF EXISTS(SELECT 1 FROM dbo.QualityEventChecklistAnswers)
                THROW 53546, 'Legacy checklist answers have no controlled question-link mapping.', 1;
        END;

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistAnswers')
              AND name IN(N'QualityEventID',N'QuestionID')
              AND system_type_id<>TYPE_ID(N'int')
        )
            THROW 53547, 'A canonical checklist-answer link type is incompatible.', 1;

        DECLARE @OrphanAnswerEventLinks BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1
                  FROM dbo.QualityEventChecklistAnswers a
                  LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=a.QualityEventID
                  WHERE a.QualityEventID IS NOT NULL AND e.QualityEventID IS NULL
              ) SET @HasOrphans=1;',
            N'@HasOrphans BIT OUTPUT',
            @HasOrphans=@OrphanAnswerEventLinks OUTPUT;
        IF @OrphanAnswerEventLinks=1
            THROW 53548, 'Orphan legacy checklist-answer event links require controlled review.', 1;

        DECLARE @OrphanAnswerQuestionLinks BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1
                  FROM dbo.QualityEventChecklistAnswers a
                  LEFT JOIN dbo.QualityEventChecklistQuestions q ON q.QuestionID=a.QuestionID
                  WHERE a.QuestionID IS NOT NULL AND q.QuestionID IS NULL
              ) SET @HasOrphans=1;',
            N'@HasOrphans BIT OUTPUT',
            @HasOrphans=@OrphanAnswerQuestionLinks OUTPUT;
        IF @OrphanAnswerQuestionLinks=1
            THROW 53549, 'Orphan legacy checklist-answer question links require controlled review.', 1;
    END;

    /* ------------------------------------------------------------------
       Legacy print-history table
       ------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.QualityEventPrintHistory', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'QualityEventPrintID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventPrintID BIGINT NULL;');
            INSERT dbo.PRM_QualityEventStructureReconciliation
                (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
            VALUES
                (N'QualityEventPrintHistory',N'QualityEventPrintID',NULL,
                 N'Added an independent canonical surrogate while retaining every legacy identifier and print row.');
        END;

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
              AND name=N'QualityEventPrintID'
              AND (system_type_id<>TYPE_ID(N'bigint') OR is_computed=1)
        )
            THROW 53550, 'The legacy Quality Event print surrogate is incompatible.', 1;

        IF OBJECT_ID(N'dbo.Seq_PRM_QualityEventPrintID_20260825', N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_PRM_QualityEventPrintID_20260825 AS BIGINT START WITH 1 INCREMENT BY 1;');

        DECLARE @NextPrintID BIGINT=1;
        EXEC sys.sp_executesql
            N'SELECT @Next=ISNULL(MAX(QualityEventPrintID),0)+1 FROM dbo.QualityEventPrintHistory;',
            N'@Next BIGINT OUTPUT',
            @Next=@NextPrintID OUTPUT;
        EXEC(N'ALTER SEQUENCE dbo.Seq_PRM_QualityEventPrintID_20260825 RESTART WITH '
             + CONVERT(NVARCHAR(30),@NextPrintID) + N';');
        EXEC(N'UPDATE dbo.QualityEventPrintHistory
               SET QualityEventPrintID=NEXT VALUE FOR dbo.Seq_PRM_QualityEventPrintID_20260825
               WHERE QualityEventPrintID IS NULL;');

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
              AND name=N'QualityEventPrintID'
              AND is_nullable=1
        )
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ALTER COLUMN QualityEventPrintID BIGINT NOT NULL;');

        DECLARE @DuplicatePrintIDs BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1 FROM dbo.QualityEventPrintHistory
                  GROUP BY QualityEventPrintID
                  HAVING COUNT_BIG(*)>1
              ) SET @HasDuplicates=1;',
            N'@HasDuplicates BIT OUTPUT',
            @HasDuplicates=@DuplicatePrintIDs OUTPUT;
        IF @DuplicatePrintIDs=1
            THROW 53551, 'Duplicate canonical Quality Event print surrogates were detected.', 1;

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.indexes
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
              AND name=N'UX_QualityEventPrintHistory_CanonicalID_20260825'
        )
            EXEC(N'CREATE UNIQUE INDEX UX_QualityEventPrintHistory_CanonicalID_20260825
                   ON dbo.QualityEventPrintHistory(QualityEventPrintID);');

        IF NOT EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
              AND name=N'QualityEventPrintID'
              AND is_identity=1
        )
        AND NOT EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id=d.parent_object_id
               AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
              AND c.name=N'QualityEventPrintID'
        )
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory
                   ADD CONSTRAINT DF_QualityEventPrintHistory_CanonicalID_20260825
                   DEFAULT (NEXT VALUE FOR dbo.Seq_PRM_QualityEventPrintID_20260825)
                   FOR QualityEventPrintID;');

        IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'QualityEventID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventID INT NULL;');
            IF COL_LENGTH(N'dbo.QualityEventPrintHistory', N'EventID') IS NOT NULL
            BEGIN
                IF EXISTS
                (
                    SELECT 1 FROM sys.columns
                    WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
                      AND name=N'EventID'
                      AND system_type_id<>TYPE_ID(N'int')
                )
                    THROW 53552, 'The legacy Quality Event print link type is incompatible.', 1;
                EXEC(N'UPDATE dbo.QualityEventPrintHistory SET QualityEventID=EventID WHERE QualityEventID IS NULL;');
                INSERT dbo.PRM_QualityEventStructureReconciliation
                    (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
                VALUES
                    (N'QualityEventPrintHistory',N'QualityEventID',N'EventID',
                     N'Copied the legacy event link into the canonical link without changing the legacy column or print evidence.');
            END
            ELSE IF EXISTS(SELECT 1 FROM dbo.QualityEventPrintHistory)
                THROW 53553, 'Legacy Quality Event print rows have no controlled event-link mapping.', 1;
        END;

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventPrintHistory')
              AND name=N'QualityEventID'
              AND system_type_id<>TYPE_ID(N'int')
        )
            THROW 53554, 'The canonical Quality Event print link type is incompatible.', 1;

        DECLARE @OrphanPrintLinks BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1
                  FROM dbo.QualityEventPrintHistory p
                  LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=p.QualityEventID
                  WHERE p.QualityEventID IS NOT NULL AND e.QualityEventID IS NULL
              ) SET @HasOrphans=1;',
            N'@HasOrphans BIT OUTPUT',
            @HasOrphans=@OrphanPrintLinks OUTPUT;
        IF @OrphanPrintLinks=1
            THROW 53555, 'Orphan legacy Quality Event print links require controlled review.', 1;
    END;

    IF @PrintAppendOnlyWasEnabled=1
        EXEC(N'ENABLE TRIGGER dbo.TRG_QualityEventPrintHistory_AppendOnly
               ON dbo.QualityEventPrintHistory;');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
