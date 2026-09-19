SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       v225 legacy-upgrade repair.

       The fresh-install baseline has dbo.Tests.IsActive, but some historical
       pre-baseline PharmaLIMS databases do not. v224's Water controlled-master
       preflight correctly joined dbo.Tests but therefore raised SQL Server 207
       on those legacy databases after all migrations had otherwise completed.

       This migration adds only the global active-state contract. Existing tests
       are marked active because a schema with no IsActive column had no
       representable inactive state. No Water profile/specification membership is
       inferred or seeded here.
    */

    IF OBJECT_ID(N'dbo.Tests', N'U') IS NULL
        THROW 54520, 'Required dependency dbo.Tests is missing before 20260906_007.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Tests')
          AND name=N'TestID'
          AND system_type_id=TYPE_ID(N'int')
          AND is_nullable=0
    )
        THROW 54521, 'Required dependency dbo.Tests.TestID must be INT NOT NULL before 20260906_007.', 1;

    IF COL_LENGTH(N'dbo.Tests', N'IsActive') IS NULL
    BEGIN
        ALTER TABLE dbo.Tests
        ADD IsActive BIT NOT NULL
            CONSTRAINT DF_Tests_IsActive_20260906 DEFAULT (1) WITH VALUES;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Tests')
          AND name=N'IsActive'
          AND system_type_id=TYPE_ID(N'bit')
          AND is_nullable=0
    )
        THROW 54522, 'dbo.Tests.IsActive must be BIT NOT NULL after 20260906_007.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260906_007' AS MigrationVersion;
