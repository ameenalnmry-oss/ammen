SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
        THROW 52400, 'Required table dbo.PRM_SpecificationTests is missing.', 1;

    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedDate') IS NULL
        ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedDate DATETIME2(0) NULL;

    IF OBJECT_ID(N'dbo.PRM_SpecificationSignatures',N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_SpecificationSignatures
        (
            SignatureID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_SpecificationSignatures PRIMARY KEY,
            SpecificationNo NVARCHAR(120) NOT NULL,
            SampleCategory NVARCHAR(40) NOT NULL,
            VersionNo INT NOT NULL,
            ActionType NVARCHAR(60) NOT NULL,
            ActionReason NVARCHAR(MAX) NOT NULL,
            SignedBy NVARCHAR(120) NOT NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_SpecificationSignatures_SignedAt DEFAULT SYSDATETIME()
        );
        CREATE INDEX IX_PRM_SpecificationSignatures_Record
            ON dbo.PRM_SpecificationSignatures(SpecificationNo,SampleCategory,VersionNo,SignedAt DESC);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
