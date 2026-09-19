SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
        THROW 51100, 'Required table dbo.PRM_Samples does not exist.', 1;
    IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
        THROW 51101, 'Required table dbo.PRM_SampleTests does not exist.', 1;
    IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NULL
        THROW 51102, 'Required table dbo.PRM_Certificates does not exist.', 1;
    IF OBJECT_ID(N'dbo.PRM_CertificateHistory', N'U') IS NULL
        THROW 51103, 'Required table dbo.PRM_CertificateHistory does not exist.', 1;
    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
        THROW 51104, 'Required table dbo.QualityEvents does not exist.', 1;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_SpecificationTests
        (
            SpecificationTestID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_SpecificationTests PRIMARY KEY,
            SpecificationNo NVARCHAR(120) NOT NULL,
            SampleCategory NVARCHAR(40) NOT NULL,
            VersionNo INT NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Version DEFAULT (1),
            TestCode NVARCHAR(40) NOT NULL,
            TestName NVARCHAR(160) NOT NULL,
            SpecificationText NVARCHAR(500) NOT NULL,
            Unit NVARCHAR(50) NULL,
            ResultType NVARCHAR(60) NOT NULL,
            SpecificationLimit DECIMAL(18,3) NULL,
            RequiredTest BIT NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Required DEFAULT (1),
            SortOrder INT NULL,
            ApprovalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Approval DEFAULT N'Draft',
            ApprovedBy NVARCHAR(120) NULL,
            ApprovedDate DATETIME2(0) NULL,
            EffectiveDate DATE NULL,
            IsActive BIT NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Active DEFAULT (0),
            CreatedBy NVARCHAR(120) NULL,
            CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Created DEFAULT SYSDATETIME(),
            CONSTRAINT UQ_PRM_SpecificationTests UNIQUE (SpecificationNo, SampleCategory, VersionNo, TestCode)
        );
    END;

    IF OBJECT_ID(N'dbo.PRM_ElectronicSignatures', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_ElectronicSignatures
        (
            SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_ElectronicSignatures PRIMARY KEY,
            SampleID INT NOT NULL,
            ActionType NVARCHAR(80) NOT NULL,
            SignedBy NVARCHAR(120) NOT NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            ActionReason NVARCHAR(MAX) NOT NULL,
            UserRole NVARCHAR(80) NULL,
            SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_ElectronicSignatures_SignedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_PRM_ElectronicSignatures_Samples FOREIGN KEY (SampleID) REFERENCES dbo.PRM_Samples(SampleID)
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.PRM_ElectronicSignatures')
          AND name = N'IX_PRM_ElectronicSignatures_Sample_Action'
    )
    BEGIN
        CREATE INDEX IX_PRM_ElectronicSignatures_Sample_Action
            ON dbo.PRM_ElectronicSignatures(SampleID, ActionType, SignedAt DESC);
    END;

    IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventAffectedResults
        (
            AffectedResultID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventAffectedResults PRIMARY KEY,
            QualityEventID INT NOT NULL,
            SampleTestID INT NULL,
            TestID INT NULL,
            TestName NVARCHAR(200) NULL,
            ResultValue NVARCHAR(200) NULL,
            SpecificationLimit NVARCHAR(500) NULL,
            Unit NVARCHAR(50) NULL,
            FailureType NVARCHAR(120) NULL,
            CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventAffectedResults_Created DEFAULT SYSDATETIME()
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
