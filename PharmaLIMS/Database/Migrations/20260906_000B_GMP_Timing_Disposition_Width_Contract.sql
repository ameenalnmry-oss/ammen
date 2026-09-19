SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       v223 pre-20260906_001 width contract.

       v222 introduced 20260906_000A with a minimum NVARCHAR(60) contract to
       prevent SQL Server 2628 for the 42-character controlled disposition.
       Keep 000A byte-for-byte for ledger compatibility and strengthen the
       contract here to NVARCHAR(80) before the historical 001 batch executes.
    */

    IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory', N'U') IS NULL
        THROW 54477, 'Required dbo.PRM_TimingMigrationHistory is missing before 20260906_000B.', 1;

    IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory', N'ReconciliationDisposition') IS NULL
        THROW 54478, 'dbo.PRM_TimingMigrationHistory.ReconciliationDisposition is missing before 20260906_000B.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
          AND name=N'ReconciliationDisposition'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR is_nullable<>0)
    )
        THROW 54479, 'dbo.PRM_TimingMigrationHistory.ReconciliationDisposition must be NOT NULL NVARCHAR before width hardening.', 1;

    /* NVARCHAR max_length is bytes: 160 bytes = 80 Unicode characters. */
    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
          AND name=N'ReconciliationDisposition'
          AND max_length<>-1
          AND max_length<160
    )
        ALTER TABLE dbo.PRM_TimingMigrationHistory
            ALTER COLUMN ReconciliationDisposition NVARCHAR(80) NOT NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
          AND name=N'ReconciliationDisposition'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND is_nullable=0
          AND (max_length=-1 OR max_length>=160)
    )
        THROW 54480, 'dbo.PRM_TimingMigrationHistory.ReconciliationDisposition must hold at least 80 Unicode characters before 20260906_001.', 1;

    IF LEN(N'Historical Closed - Quality Event Evidence') > 80
        THROW 54481, 'Controlled PRM reconciliation disposition exceeds the v223 history-column contract.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260906_000B' AS MigrationVersion;
