SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Minimal legacy Quality Event identity baseline.

    This prerequisite is intentionally narrower than 20260825_004. It repairs
    only the identity objects required before the later PRM Quality Event
    reconciliation path can run:

      - dbo.QualityEvents.QualityEventID must be an INT, non-null, unique key.
      - dbo.QualityEventAffectedResults must exist with canonical
        AffectedResultID and QualityEventID columns.

    Existing result values, specifications, interpretations, signatures,
    investigation conclusions, dispositions, and certificate evidence are not
    changed. Legacy rows are never guessed into a Quality Event relationship.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
        THROW 53570, 'Required Quality Events table is missing.', 1;

    IF COL_LENGTH(N'dbo.QualityEvents', N'QualityEventID') IS NULL
        THROW 53571, 'The Quality Event identity column is missing.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'QualityEventID'
          AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
    )
        THROW 53572, 'The Quality Event identity type requires controlled reconciliation.', 1;

    IF EXISTS (SELECT 1 FROM dbo.QualityEvents WHERE QualityEventID IS NULL)
        THROW 53573, 'Null Quality Event identities require controlled reconciliation.', 1;

    IF EXISTS
    (
        SELECT QualityEventID
        FROM dbo.QualityEvents
        GROUP BY QualityEventID
        HAVING COUNT_BIG(*)>1
    )
        THROW 53574, 'Duplicate Quality Event identities require controlled reconciliation.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'QualityEventID'
          AND is_nullable=1
    )
        ALTER TABLE dbo.QualityEvents ALTER COLUMN QualityEventID INT NOT NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes i
        WHERE i.object_id=OBJECT_ID(N'dbo.QualityEvents')
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
                AND c.name=N'QualityEventID'
          )
    )
        CREATE UNIQUE INDEX UX_QualityEvents_QualityEventID_20260825_005
            ON dbo.QualityEvents(QualityEventID);

    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventAffectedResults
        (
            AffectedResultID INT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_QualityEventAffectedResults PRIMARY KEY,
            QualityEventID INT NOT NULL,
            SampleTestID INT NULL,
            TestID INT NULL,
            TestName NVARCHAR(200) NULL,
            ResultValue NVARCHAR(200) NULL,
            SpecificationLimit NVARCHAR(500) NULL,
            Unit NVARCHAR(50) NULL,
            FailureType NVARCHAR(120) NULL,
            CreatedDate DATETIME2(0) NOT NULL
                CONSTRAINT DF_QualityEventAffectedResults_Created_20260825_005 DEFAULT SYSDATETIME(),
            CONSTRAINT FK_QualityEventAffectedResults_Event_20260825_005
                FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
    END
    ELSE
    BEGIN
        IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'AffectedResultID') IS NULL
        BEGIN
            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.columns
                WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
                  AND is_identity=1
            )
            BEGIN
                ALTER TABLE dbo.QualityEventAffectedResults
                    ADD AffectedResultID INT IDENTITY(1,1) NOT NULL;
            END
            ELSE
            BEGIN
                /*
                   A second IDENTITY column cannot be added. Keep this extreme
                   legacy shape fail-closed so a human can map its existing
                   identity column without silently changing authoritative keys.
                */
                THROW 53575, 'The affected-result table has a different legacy identity column and requires controlled reconciliation.', 1;
            END;
        END;

        IF EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND name=N'AffectedResultID'
              AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1)
        )
            THROW 53576, 'The affected-result identity type requires controlled reconciliation.', 1;

        IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'QualityEventID') IS NULL
        BEGIN
            ALTER TABLE dbo.QualityEventAffectedResults ADD QualityEventID INT NULL;

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
                    THROW 53577, 'The legacy affected-result event identity type requires controlled reconciliation.', 1;

                EXEC(N'UPDATE dbo.QualityEventAffectedResults
                       SET QualityEventID=EventID
                       WHERE QualityEventID IS NULL;');
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
            THROW 53578, 'The affected-result Quality Event link type requires controlled reconciliation.', 1;

        IF EXISTS (SELECT 1 FROM dbo.QualityEventAffectedResults WHERE AffectedResultID IS NULL)
            THROW 53579, 'Null affected-result identities require controlled reconciliation.', 1;

        IF EXISTS
        (
            SELECT AffectedResultID
            FROM dbo.QualityEventAffectedResults
            GROUP BY AffectedResultID
            HAVING COUNT_BIG(*)>1
        )
            THROW 53580, 'Duplicate affected-result identities require controlled reconciliation.', 1;

        IF EXISTS (SELECT 1 FROM dbo.QualityEventAffectedResults WHERE QualityEventID IS NULL)
            THROW 53581, 'Legacy affected-result rows have no controlled Quality Event link mapping.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM dbo.QualityEventAffectedResults affected
            LEFT JOIN dbo.QualityEvents qualityEvent
                ON qualityEvent.QualityEventID=affected.QualityEventID
            WHERE qualityEvent.QualityEventID IS NULL
        )
            THROW 53582, 'Orphan legacy affected-result event links require controlled review.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND name=N'AffectedResultID'
              AND is_nullable=1
        )
            ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN AffectedResultID INT NOT NULL;

        IF EXISTS
        (
            SELECT 1
            FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND name=N'QualityEventID'
              AND is_nullable=1
        )
            ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN QualityEventID INT NOT NULL;

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
            CREATE UNIQUE INDEX UX_QualityEventAffectedResults_AffectedResultID_20260825_005
                ON dbo.QualityEventAffectedResults(AffectedResultID);

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.foreign_key_columns fkc
            INNER JOIN sys.foreign_keys fk
                ON fk.object_id=fkc.constraint_object_id
            INNER JOIN sys.columns parentColumn
                ON parentColumn.object_id=fkc.parent_object_id
               AND parentColumn.column_id=fkc.parent_column_id
            INNER JOIN sys.columns referencedColumn
                ON referencedColumn.object_id=fkc.referenced_object_id
               AND referencedColumn.column_id=fkc.referenced_column_id
            WHERE fk.parent_object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
              AND fkc.referenced_object_id=OBJECT_ID(N'dbo.QualityEvents')
              AND parentColumn.name=N'QualityEventID'
              AND referencedColumn.name=N'QualityEventID'
        )
            ALTER TABLE dbo.QualityEventAffectedResults WITH CHECK
                ADD CONSTRAINT FK_QualityEventAffectedResults_Event_20260825_005
                FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
