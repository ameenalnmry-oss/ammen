SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Adds a controlled area classification to external trend rows so
    Classified and Unclassified EM results are never merged silently.

    External trend rows are append-only. Do not UPDATE existing rows here:
    the immutable-row trigger correctly prevents it. A partially applied older
    version can leave an all-NULL nullable column; recreate only that empty
    column and apply the default as part of the ADD operation.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NULL
        THROW 51090, 'Apply migration 20260810_001 before 20260811_001.', 1;

    IF COL_LENGTH(N'dbo.ExternalTrendImportRows', N'AreaClassification') IS NOT NULL
       AND EXISTS
       (
           SELECT 1
           FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
             AND name = N'AreaClassification'
             AND is_nullable = 1
       )
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM dbo.ExternalTrendImportRows
            WHERE AreaClassification IS NOT NULL
        )
            THROW 51091, 'AreaClassification is partially populated. Stop and obtain a controlled data-repair script; do not overwrite existing classifications.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
              AND name = N'IX_ExternalTrendImportRows_ClassificationTrend'
        )
            DROP INDEX IX_ExternalTrendImportRows_ClassificationTrend ON dbo.ExternalTrendImportRows;

        IF EXISTS
        (
            SELECT 1
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
              AND name = N'CK_ExternalTrendImportRows_AreaClassification'
        )
            ALTER TABLE dbo.ExternalTrendImportRows
                DROP CONSTRAINT CK_ExternalTrendImportRows_AreaClassification;

        IF EXISTS
        (
            SELECT 1
            FROM sys.default_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
              AND name = N'DF_ExternalTrendImportRows_AreaClassification'
        )
            ALTER TABLE dbo.ExternalTrendImportRows
                DROP CONSTRAINT DF_ExternalTrendImportRows_AreaClassification;

        ALTER TABLE dbo.ExternalTrendImportRows
            DROP COLUMN AreaClassification;
    END;

    IF COL_LENGTH(N'dbo.ExternalTrendImportRows', N'AreaClassification') IS NULL
    BEGIN
        ALTER TABLE dbo.ExternalTrendImportRows
            ADD AreaClassification NVARCHAR(30) NOT NULL
                CONSTRAINT DF_ExternalTrendImportRows_AreaClassification
                DEFAULT (N'Unspecified');
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'CK_ExternalTrendImportRows_AreaClassification'
    )
    BEGIN
        ALTER TABLE dbo.ExternalTrendImportRows
            ADD CONSTRAINT CK_ExternalTrendImportRows_AreaClassification
            CHECK (AreaClassification IN
                (N'Classified', N'Unclassified', N'Not Applicable', N'Unspecified'));
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name = N'IX_ExternalTrendImportRows_ClassificationTrend'
    )
    BEGIN
        CREATE INDEX IX_ExternalTrendImportRows_ClassificationTrend
            ON dbo.ExternalTrendImportRows
               (ImportBatchID, AreaClassification, ParameterName, RecordDateTime)
            INCLUDE (EntityCode, ResultValue, UnitName, ResultStatus);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT
    N'20260811_001' AS MigrationVersion,
    COL_LENGTH(N'dbo.ExternalTrendImportRows', N'AreaClassification') AS AreaClassificationColumnLength;
