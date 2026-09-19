SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled reconciliation before 20260823_001.

    Legacy PRM specification records can predate the independent-review fields
    ReviewedBy / ReviewedDate. Such rows must not be made compliant by inventing
    review evidence. Any legacy row that cannot satisfy the current state model
    is therefore retired as Obsolete and inactive while preserving its original
    approval identity/date and all test-definition content.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
        THROW 53060, 'Required table dbo.PRM_SpecificationTests is missing.', 1;

    /* Use separate dynamic DDL so later references compile against real columns. */
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'ReviewedBy') IS NULL
        EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedBy NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'ReviewedDate') IS NULL
        EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'IsDefaultForCategory') IS NULL
        EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD IsDefaultForCategory BIT NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Default_20260822 DEFAULT (0);');

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name=N'ReviewedBy'
          AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>240)
    )
        THROW 53061, 'PRM specification ReviewedBy has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name=N'ReviewedDate'
          AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0)
    )
        THROW 53062, 'PRM specification ReviewedDate has an incompatible schema.', 1;

    IF EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name=N'IsDefaultForCategory'
          AND (system_type_id<>TYPE_ID(N'bit') OR is_nullable<>0)
    )
        THROW 53063, 'PRM specification IsDefaultForCategory has an incompatible schema.', 1;

    /*
       Compile the data reconciliation only after the additive DDL above has
       materialized the review columns. This avoids SQL Server batch-binding
       failures on databases where those columns were absent before maintenance.
    */
    EXEC sys.sp_executesql N'
UPDATE dbo.PRM_SpecificationTests
SET ApprovalStatus=N''Obsolete'',
    IsActive=0,
    IsDefaultForCategory=0
WHERE NOT
(
    (ApprovalStatus=N''Draft''
        AND IsActive=0
        AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
    OR
    (ApprovalStatus=N''Reviewed''
        AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
        AND IsActive=0
        AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
    OR
    (ApprovalStatus=N''Approved''
        AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
        AND ApprovedBy IS NOT NULL AND ApprovedDate IS NOT NULL)
    OR
    (ApprovalStatus=N''Obsolete'' AND IsActive=0)
);

UPDATE dbo.PRM_SpecificationTests
SET IsDefaultForCategory=0
WHERE ApprovalStatus=N''Obsolete''
  AND IsDefaultForCategory<>0;

IF EXISTS
(
    SELECT 1
    FROM dbo.PRM_SpecificationTests
    WHERE NOT
    (
        (ApprovalStatus=N''Draft''
            AND IsActive=0
            AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
        OR
        (ApprovalStatus=N''Reviewed''
            AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
            AND IsActive=0
            AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
        OR
        (ApprovalStatus=N''Approved''
            AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
            AND ApprovedBy IS NOT NULL AND ApprovedDate IS NOT NULL)
        OR
        (ApprovalStatus=N''Obsolete'' AND IsActive=0)
    )
)
    THROW 53064, ''PRM legacy specification states could not be reconciled safely.'', 1;';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
