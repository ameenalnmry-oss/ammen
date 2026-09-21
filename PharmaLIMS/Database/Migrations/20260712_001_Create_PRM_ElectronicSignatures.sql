SET XACT_ABORT ON;
BEGIN TRANSACTION;

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
        CONSTRAINT FK_PRM_ElectronicSignatures_PRM_Samples
            FOREIGN KEY (SampleID) REFERENCES dbo.PRM_Samples(SampleID)
    );

    CREATE INDEX IX_PRM_ElectronicSignatures_SampleID_SignedAt
        ON dbo.PRM_ElectronicSignatures(SampleID, SignedAt DESC);
END;

COMMIT TRANSACTION;
