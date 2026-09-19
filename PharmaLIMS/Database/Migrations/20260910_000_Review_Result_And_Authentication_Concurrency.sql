SET NOCOUNT ON;
SET XACT_ABORT ON;

-- Review remediation candidate v260. Schema only: no historical measured value,
-- result status, signature, certificate, or reconciliation is recomputed here.
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Users',N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_Events',N'U') IS NULL
       OR OBJECT_ID(N'dbo.EM_EventPlates',N'U') IS NULL
       OR OBJECT_ID(N'dbo.SampleTests',N'U') IS NULL
        THROW 54900, 'Review remediation requires the complete existing core schema.', 1;

    -- SQL Server permits one rowversion per table. Do not replace or reinterpret
    -- a site-specific version column: require controlled schema reconciliation.
    IF COL_LENGTH(N'dbo.Users',N'AuthenticationRowVersion') IS NULL
    BEGIN
        IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.Users') AND system_type_id=189)
            THROW 54901, 'Users already has another rowversion column; reconcile the schema before this migration.', 1;
        EXEC sys.sp_executesql N'ALTER TABLE dbo.Users ADD AuthenticationRowVersion rowversion NOT NULL;';
    END;
    IF COL_LENGTH(N'dbo.EM_Events',N'ResultRowVersion') IS NULL
    BEGIN
        IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_Events') AND system_type_id=189)
            THROW 54902, 'EM_Events already has another rowversion column; reconcile the schema.', 1;
        EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ADD ResultRowVersion rowversion NOT NULL;';
    END;
    IF COL_LENGTH(N'dbo.EM_EventPlates',N'ResultRowVersion') IS NULL
    BEGIN
        IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates') AND system_type_id=189)
            THROW 54903, 'EM_EventPlates already has another rowversion column; reconcile the schema.', 1;
        EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_EventPlates ADD ResultRowVersion rowversion NOT NULL;';
    END;
    IF COL_LENGTH(N'dbo.SampleTests',N'ResultRowVersion') IS NULL
    BEGIN
        IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.SampleTests') AND system_type_id=189)
            THROW 54904, 'SampleTests already has another rowversion column; reconcile the schema.', 1;
        EXEC sys.sp_executesql N'ALTER TABLE dbo.SampleTests ADD ResultRowVersion rowversion NOT NULL;';
    END;

    IF EXISTS
    (
        SELECT 1 FROM (VALUES
            (N'dbo.Users',N'AuthenticationRowVersion'),
            (N'dbo.EM_Events',N'ResultRowVersion'),
            (N'dbo.EM_EventPlates',N'ResultRowVersion'),
            (N'dbo.SampleTests',N'ResultRowVersion')
        ) AS expected(TableName,ColumnName)
        LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(expected.TableName) AND c.name=expected.ColumnName
        WHERE c.column_id IS NULL OR c.system_type_id<>189 OR c.is_nullable<>0
    )
        THROW 54905, 'A required concurrency column is not a non-null SQL Server rowversion.', 1;

    IF COL_LENGTH(N'dbo.EM_EventPlates',N'ResultCalculationVersion') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_EventPlates ADD ResultCalculationVersion smallint NULL;';

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
        AND name=N'ResultCalculationVersion' AND system_type_id=52 AND is_nullable=1
    )
        THROW 54908, 'ResultCalculationVersion must be a nullable smallint.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
        AND name=N'ResultCFU' AND system_type_id IN (48,52,56,106,108,127)
    )
        THROW 54906, 'ResultCFU must have a controlled numeric type before the precision upgrade.', 1;

    EXEC sys.sp_executesql N'
        IF EXISTS(SELECT 1 FROM dbo.EM_EventPlates
                  WHERE ResultCFU IS NOT NULL AND
                  (TRY_CONVERT(decimal(28,12),ResultCFU) IS NULL
                   OR TRY_CONVERT(decimal(28,12),ResultCFU)<>ResultCFU))
            THROW 54907, ''ResultCFU precision upgrade would alter an existing value. Reconcile before migration.'', 1;';

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
        AND name=N'ResultCFU' AND system_type_id IN (106,108) AND precision=28 AND scale=12 AND is_nullable=1
    )
        EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_EventPlates ALTER COLUMN ResultCFU decimal(28,12) NULL;';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
