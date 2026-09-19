SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled prerequisite for 20260825_002 on legacy databases.

    Some older PharmaLIMS databases contain no dbo.QualityEventAffectedResults
    table at all, or contain a legacy table without the canonical
    AffectedResultID / QualityEventID projection.  Migration 20260825_002 must
    not guess through that condition because it uses those columns as the
    immutable evidence key and event link.

    This migration creates only the missing canonical projection needed by the
    later controlled Quality Event migrations. Existing legacy columns and rows
    are retained. No microbiology result, specification, interpretation,
    signature, investigation conclusion, disposition, or certificate evidence
    is changed.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
       OR COL_LENGTH(N'dbo.QualityEvents', N'QualityEventID') IS NULL
        THROW 53560, 'The controlled Quality Event identity baseline is missing.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'QualityEventID'
          AND system_type_id<>TYPE_ID(N'int')
    )
        THROW 53561, 'The Quality Event identity type requires controlled reconciliation.', 1;

    IF OBJECT_ID(N'dbo.PRM_QualityEventAffectedStructureReconciliation', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_QualityEventAffectedStructureReconciliation
        (
            ReconciliationID BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_PRM_QualityEventAffectedStructureReconciliation PRIMARY KEY,
            TableName NVARCHAR(128) NOT NULL,
            CanonicalColumn NVARCHAR(128) NOT NULL,
            LegacySourceColumn NVARCHAR(128) NULL,
            ReconciliationAction NVARCHAR(300) NOT NULL,
            RecordedAt DATETIME2(0) NOT NULL
                CONSTRAINT DF_PRM_QEAffectedStructureReconciliation_RecordedAt DEFAULT SYSUTCDATETIME()
        );
    END;

    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
    BEGIN
        EXEC(N'
CREATE TABLE dbo.QualityEventAffectedResults
(
    AffectedResultID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventAffectedResults PRIMARY KEY,
    QualityEventID INT NOT NULL,
    SampleTestID INT NULL,
    TestID INT NULL,
    TestName NVARCHAR(200) NULL,
    ResultValue NVARCHAR(200) NULL,
    SpecificationLimit NVARCHAR(500) NULL,
    Unit NVARCHAR(50) NULL,
    FailureType NVARCHAR(120) NULL,
    CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventAffectedResults_Created_20260825_004 DEFAULT SYSDATETIME(),
    CONSTRAINT FK_QualityEventAffectedResults_Event_20260825_004
        FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
);');

        INSERT dbo.PRM_QualityEventAffectedStructureReconciliation
            (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
        VALUES
            (N'QualityEventAffectedResults',N'AffectedResultID / QualityEventID',NULL,
             N'Created the missing canonical affected-result evidence table; no historical row existed in that table to rewrite.');
    END
    ELSE
    BEGIN
        /* --------------------------------------------------------------
           Canonical affected-result surrogate
           -------------------------------------------------------------- */
        IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'AffectedResultID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD AffectedResultID INT NULL;');

            INSERT dbo.PRM_QualityEventAffectedStructureReconciliation
                (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
            VALUES
                (N'QualityEventAffectedResults',N'AffectedResultID',NULL,
                 N'Added an independent canonical surrogate while retaining every legacy identifier and row.');
        END;

        IF EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND name=N'AffectedResultID'
              AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
        )
            THROW 53562, 'The legacy affected-result surrogate is incompatible.', 1;

        DECLARE @DuplicateAffectedIDs BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT AffectedResultID
                  FROM dbo.QualityEventAffectedResults
                  WHERE AffectedResultID IS NOT NULL
                  GROUP BY AffectedResultID
                  HAVING COUNT_BIG(*)>1
              ) SET @HasDuplicates=1;',
            N'@HasDuplicates BIT OUTPUT',
            @HasDuplicates=@DuplicateAffectedIDs OUTPUT;
        IF @DuplicateAffectedIDs=1
            THROW 53563, 'Duplicate legacy affected-result surrogates require controlled review.', 1;

        IF OBJECT_ID(N'dbo.Seq_PRM_QEAffectedResultID_20260825', N'SO') IS NULL
            EXEC(N'CREATE SEQUENCE dbo.Seq_PRM_QEAffectedResultID_20260825 AS INT START WITH 1 INCREMENT BY 1;');

        DECLARE @NextAffectedID BIGINT=1;
        EXEC sys.sp_executesql
            N'SELECT @Next=ISNULL(MAX(CONVERT(BIGINT,AffectedResultID)),0)+1
              FROM dbo.QualityEventAffectedResults;',
            N'@Next BIGINT OUTPUT',
            @Next=@NextAffectedID OUTPUT;
        IF @NextAffectedID>2147483647
            THROW 53564, 'The affected-result surrogate has exhausted the supported range.', 1;

        EXEC(N'ALTER SEQUENCE dbo.Seq_PRM_QEAffectedResultID_20260825 RESTART WITH '
             + CONVERT(NVARCHAR(30),@NextAffectedID) + N';');

        EXEC(N'UPDATE dbo.QualityEventAffectedResults
               SET AffectedResultID=NEXT VALUE FOR dbo.Seq_PRM_QEAffectedResultID_20260825
               WHERE AffectedResultID IS NULL;');

        DECLARE @NullAffectedIDs BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1 FROM dbo.QualityEventAffectedResults
                  WHERE AffectedResultID IS NULL
              ) SET @HasNull=1;',
            N'@HasNull BIT OUTPUT',
            @HasNull=@NullAffectedIDs OUTPUT;
        IF @NullAffectedIDs=1
            THROW 53565, 'The canonical affected-result surrogate could not be completed.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND name=N'AffectedResultID'
              AND is_nullable=1
        )
            EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN AffectedResultID INT NOT NULL;');

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes i
            WHERE i.object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND i.is_unique=1
              AND i.is_disabled=0
              AND i.has_filter=0
              AND
              (
                  SELECT COUNT(*)
                  FROM sys.index_columns ic
                  WHERE ic.object_id=i.object_id
                    AND ic.index_id=i.index_id
                    AND ic.key_ordinal>0
              )=1
              AND EXISTS
              (
                  SELECT 1
                  FROM sys.index_columns ic
                  INNER JOIN sys.columns c
                      ON c.object_id=ic.object_id
                     AND c.column_id=ic.column_id
                  WHERE ic.object_id=i.object_id
                    AND ic.index_id=i.index_id
                    AND ic.key_ordinal=1
                    AND c.name=N'AffectedResultID'
              )
        )
            EXEC(N'CREATE UNIQUE INDEX UX_QualityEventAffectedResults_CanonicalID_20260825
                   ON dbo.QualityEventAffectedResults(AffectedResultID);');

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND name=N'AffectedResultID'
              AND is_identity=1
        )
        AND NOT EXISTS
        (
            SELECT 1
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id=d.parent_object_id
               AND c.column_id=d.parent_column_id
            WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND c.name=N'AffectedResultID'
        )
            EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults
                   ADD CONSTRAINT DF_QualityEventAffectedResults_CanonicalID_20260825
                   DEFAULT (NEXT VALUE FOR dbo.Seq_PRM_QEAffectedResultID_20260825)
                   FOR AffectedResultID;');

        /* --------------------------------------------------------------
           Canonical event link
           -------------------------------------------------------------- */
        IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'QualityEventID') IS NULL
        BEGIN
            EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD QualityEventID INT NULL;');

            IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'EventID') IS NOT NULL
            BEGIN
                IF EXISTS
                (
                    SELECT 1
                    FROM sys.columns
                    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
                      AND name=N'EventID'
                      AND system_type_id<>TYPE_ID(N'int')
                )
                    THROW 53566, 'The legacy affected-result event-link type is incompatible.', 1;

                EXEC(N'UPDATE dbo.QualityEventAffectedResults
                       SET QualityEventID=EventID
                       WHERE QualityEventID IS NULL;');

                INSERT dbo.PRM_QualityEventAffectedStructureReconciliation
                    (TableName,CanonicalColumn,LegacySourceColumn,ReconciliationAction)
                VALUES
                    (N'QualityEventAffectedResults',N'QualityEventID',N'EventID',
                     N'Copied the legacy event link into the canonical link without changing the legacy column.');
            END;
        END;

        IF EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND name=N'QualityEventID'
              AND system_type_id<>TYPE_ID(N'int')
        )
            THROW 53567, 'The canonical affected-result event-link type is incompatible.', 1;

        DECLARE @UnmappedAffectedRows BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1
                  FROM dbo.QualityEventAffectedResults
                  WHERE QualityEventID IS NULL
              ) SET @HasUnmapped=1;',
            N'@HasUnmapped BIT OUTPUT',
            @HasUnmapped=@UnmappedAffectedRows OUTPUT;
        IF @UnmappedAffectedRows=1
            THROW 53568, 'Legacy affected-result rows have no controlled Quality Event link mapping.', 1;

        DECLARE @OrphanAffectedLinks BIT=0;
        EXEC sys.sp_executesql
            N'IF EXISTS
              (
                  SELECT 1
                  FROM dbo.QualityEventAffectedResults a
                  LEFT JOIN dbo.QualityEvents e ON e.QualityEventID=a.QualityEventID
                  WHERE e.QualityEventID IS NULL
              ) SET @HasOrphans=1;',
            N'@HasOrphans BIT OUTPUT',
            @HasOrphans=@OrphanAffectedLinks OUTPUT;
        IF @OrphanAffectedLinks=1
            THROW 53569, 'Orphan legacy affected-result event links require controlled review.', 1;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
