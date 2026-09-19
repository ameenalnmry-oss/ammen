SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
        THROW 52500, 'Required table dbo.PRM_SpecificationTests is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD ItemCode NVARCHAR(80) NULL;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'CompendialReference') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD CompendialReference NVARCHAR(160) NULL;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name=N'IX_PRM_SpecificationTests_ItemCategoryStatus'
    )
        CREATE INDEX IX_PRM_SpecificationTests_ItemCategoryStatus
        ON dbo.PRM_SpecificationTests(ItemCode,SampleCategory,ApprovalStatus,IsActive,EffectiveDate)
        INCLUDE(SpecificationNo,VersionNo,TestCode);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
