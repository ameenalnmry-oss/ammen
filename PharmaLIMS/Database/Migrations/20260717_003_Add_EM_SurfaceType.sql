SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NULL
        THROW 51400, 'Required table dbo.EM_Events does not exist.', 1;
    IF OBJECT_ID(N'dbo.EM_EventPlates', N'U') IS NULL
        THROW 51401, 'Required table dbo.EM_EventPlates does not exist.', 1;

    IF COL_LENGTH(N'dbo.EM_Events', N'SurfaceType') IS NULL
        ALTER TABLE dbo.EM_Events ADD SurfaceType NVARCHAR(80) NULL;

    IF COL_LENGTH(N'dbo.EM_EventPlates', N'SurfaceType') IS NULL
        ALTER TABLE dbo.EM_EventPlates ADD SurfaceType NVARCHAR(80) NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
