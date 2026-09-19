SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS controlled migration 20260809_001

    Purpose:
      Make audit, electronic-signature, print-history, lifecycle-audit, and
      issued-document snapshot tables append-only at the database boundary.

    Notes:
      - Missing optional tables are skipped so the migration remains compatible
        with installations that do not use every PharmaLIMS module.
      - INSERT remains allowed. UPDATE and DELETE are rejected.
      - A DBA can still perform controlled maintenance by explicitly disabling
        a trigger under an approved change-control procedure.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @ProtectedTables TABLE
    (
        TableName sysname NOT NULL PRIMARY KEY
    );

    INSERT INTO @ProtectedTables (TableName)
    VALUES
        (N'AuditTrail'),
        (N'CertificateDocumentSnapshots'),
        (N'CertificateLifecycleAudit'),
        (N'CertificatePrintHistory'),
        (N'CultureMediaPrintHistory'),
        (N'CultureMediaSignatures'),
        (N'ElectronicSignatures'),
        (N'EM_EventSignatures'),
        (N'EM_PlanSignatures'),
        (N'EM_ScheduleSignatures'),
        (N'PRM_CertificateSnapshots'),
        (N'PRM_ElectronicSignatures'),
        (N'PRM_SpecificationSignatures'),
        (N'QualityEventPrintHistory'),
        (N'QualityEventSignatures'),
        (N'Water_PlanSignatures');

    DECLARE @TableName sysname;
    DECLARE @TriggerName sysname;
    DECLARE @Sql nvarchar(max);

    DECLARE protected_table_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT TableName
        FROM @ProtectedTables
        ORDER BY TableName;

    OPEN protected_table_cursor;
    FETCH NEXT FROM protected_table_cursor INTO @TableName;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF OBJECT_ID(N'dbo.' + @TableName, N'U') IS NOT NULL
        BEGIN
            SET @TriggerName = N'TRG_' + @TableName + N'_AppendOnly';
            SET @Sql =
                N'CREATE OR ALTER TRIGGER dbo.' + QUOTENAME(@TriggerName) + N'
                  ON dbo.' + QUOTENAME(@TableName) + N'
                  AFTER UPDATE, DELETE
                  AS
                  BEGIN
                      SET NOCOUNT ON;
                      THROW 51050, ''Controlled PharmaLIMS compliance records are append-only and cannot be updated or deleted.'', 1;
                  END;';

            EXEC sys.sp_executesql @Sql;
        END;

        FETCH NEXT FROM protected_table_cursor INTO @TableName;
    END;

    CLOSE protected_table_cursor;
    DEALLOCATE protected_table_cursor;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF CURSOR_STATUS('local', 'protected_table_cursor') >= 0
        CLOSE protected_table_cursor;

    IF CURSOR_STATUS('local', 'protected_table_cursor') > -3
        DEALLOCATE protected_table_cursor;

    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
