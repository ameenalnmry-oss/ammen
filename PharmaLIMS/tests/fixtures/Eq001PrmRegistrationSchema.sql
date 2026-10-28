-- Disposable PRM fixture derived from repository migrations, plus supplied site Users/AuditTrail contracts. Never apply to AMEEN.
CREATE TABLE dbo.PRM_NumberSequences
        (
            SequenceID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_NumberSequences PRIMARY KEY,
            SequenceName NVARCHAR(60) NOT NULL CONSTRAINT UQ_PRM_NumberSequences_Name UNIQUE,
            Prefix NVARCHAR(20) NOT NULL,
            CurrentYear INT NOT NULL,
            LastNumber INT NOT NULL CONSTRAINT DF_PRM_NumberSequences_LastNumber DEFAULT (0),
            LastUpdated DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_NumberSequences_LastUpdated DEFAULT SYSDATETIME()
        );
CREATE TABLE dbo.PRM_Samples
        (
            SampleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_Samples PRIMARY KEY,
            SampleNumber NVARCHAR(50) NOT NULL CONSTRAINT UQ_PRM_Samples_SampleNumber UNIQUE,
            SampleCategory NVARCHAR(40) NOT NULL,
            SamplePurpose NVARCHAR(80) NULL,
            MaterialCode NVARCHAR(80) NULL,
            MaterialName NVARCHAR(200) NULL,
            MaterialType NVARCHAR(80) NULL,
            Manufacturer NVARCHAR(160) NULL,
            Supplier NVARCHAR(160) NULL,
            ManufacturerLotNo NVARCHAR(120) NULL,
            SupplierLotNo NVARCHAR(120) NULL,
            GRNNo NVARCHAR(120) NULL,
            ReceivedDate DATE NULL,
            ManufacturerExpiryDate DATE NULL,
            RetestDate DATE NULL,
            RetestReason NVARCHAR(300) NULL,
            PreviousReportNo NVARCHAR(80) NULL,
            PreviousRetestDate DATE NULL,
            ProposedRetestDate DATE NULL,
            RetestRemarks NVARCHAR(1000) NULL,
            ProductCode NVARCHAR(80) NULL,
            ProductName NVARCHAR(200) NULL,
            BatchNo NVARCHAR(120) NULL,
            BatchSize NVARCHAR(80) NULL,
            DosageForm NVARCHAR(80) NULL,
            ProductionStage NVARCHAR(80) NULL,
            SampleSource NVARCHAR(120) NULL,
            SampledFrom NVARCHAR(160) NULL,
            MachineLineNo NVARCHAR(120) NULL,
            ManufacturingDate DATE NULL,
            PackagingDate DATE NULL,
            ExpiryDate DATE NULL,
            PackSize NVARCHAR(120) NULL,
            SpecificationNo NVARCHAR(120) NULL,
            TestsRequired NVARCHAR(1000) NULL,
            SampleQuantity DECIMAL(18,3) NULL,
            Unit NVARCHAR(30) NULL,
            StorageCondition NVARCHAR(160) NULL,
            SampleDateTime DATETIME2(0) NULL,
            SampledBy NVARCHAR(120) NULL,
            Department NVARCHAR(120) NULL,
            Remarks NVARCHAR(1000) NULL,
            SampleStatus NVARCHAR(60) NOT NULL CONSTRAINT DF_PRM_Samples_SampleStatus DEFAULT N'Registered',
            ResultInterpretation NVARCHAR(60) NOT NULL CONSTRAINT DF_PRM_Samples_ResultInterpretation DEFAULT N'Not Tested',
            ReportStatus NVARCHAR(60) NOT NULL CONSTRAINT DF_PRM_Samples_ReportStatus DEFAULT N'Not Issued',
            ReviewedBy NVARCHAR(120) NULL,
            ReviewedDate DATETIME2(0) NULL,
            ApprovedBy NVARCHAR(120) NULL,
            ApprovedDate DATETIME2(0) NULL,
            CreatedBy NVARCHAR(120) NULL,
            CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_Samples_CreatedDate DEFAULT SYSDATETIME(),
            ModifiedBy NVARCHAR(120) NULL,
            ModifiedDate DATETIME2(0) NULL
        );
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
            CONSTRAINT UQ_PRM_SpecificationTests UNIQUE (SpecificationNo, SampleCategory, VersionNo, TestCode),
            CONSTRAINT CK_PRM_SpecificationTests_Approval CHECK
            (
                (ApprovalStatus = N'Approved' AND ApprovedBy IS NOT NULL AND ApprovedDate IS NOT NULL)
                OR ApprovalStatus IN (N'Draft', N'Obsolete')
            )
        );
CREATE TABLE dbo.PRM_SampleTests
        (
            SampleTestID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_SampleTests PRIMARY KEY,
            SampleID INT NOT NULL,
            TestCode NVARCHAR(40) NULL,
            TestName NVARCHAR(160) NOT NULL,
            SpecificationText NVARCHAR(500) NULL,
            Unit NVARCHAR(50) NULL,
            ResultValue NVARCHAR(200) NULL,
            ResultType NVARCHAR(60) NULL,
            SpecificationLimit DECIMAL(18,3) NULL,
            Interpretation NVARCHAR(60) NOT NULL CONSTRAINT DF_PRM_SampleTests_Interpretation DEFAULT N'Not Tested',
            Remarks NVARCHAR(500) NULL,
            RequiredTest BIT NOT NULL CONSTRAINT DF_PRM_SampleTests_Required DEFAULT (1),
            SortOrder INT NULL,
            EnteredBy NVARCHAR(120) NULL,
            EnteredDate DATETIME2(0) NULL,
            CONSTRAINT FK_PRM_SampleTests_Samples FOREIGN KEY (SampleID) REFERENCES dbo.PRM_Samples(SampleID)
        );
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
ALTER TABLE dbo.PRM_Samples ADD SpecificationVersionNo int NULL,StabilityChamberNo nvarchar(120) NULL,StabilityProtocolNo nvarchar(120) NULL,TimingReconciliationStatus nvarchar(30) NOT NULL DEFAULT N'Not Required';
ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedBy nvarchar(120) NULL,ReviewedDate datetime2(0) NULL,IsDefaultForCategory bit NOT NULL DEFAULT 0,ItemCode nvarchar(80) NULL,CompendialReference nvarchar(160) NULL,ProductionStage nvarchar(80) NULL,MinimumElapsedHours decimal(9,2) NULL;
ALTER TABLE dbo.PRM_SampleTests ADD MinimumElapsedHours decimal(9,2) NULL,SourceSpecificationTestID int NULL,SpecificationVersionNo int NULL,SpecificationItemCode nvarchar(80) NULL,SpecificationProductionStage nvarchar(80) NULL;
CREATE TABLE dbo.EQ001_PrmSupersessions(SpecificationNo nvarchar(120),SampleCategory nvarchar(40),OldVersion int,NewVersion int,OldEvidenceHash binary(32),ApprovalSignatureID bigint,RequestId uniqueidentifier,Actor nvarchar(100),WindowsCallerSid nvarchar(184),DecisionReason nvarchar(500),RecordedAtUtc datetime2(7));
CREATE TABLE dbo.EQ001_PrmSigningRequests(RequestId uniqueidentifier,Actor nvarchar(100),WindowsCallerSid nvarchar(184),SpecificationNo nvarchar(120),SampleCategory nvarchar(40),VersionNo int,ActionType nvarchar(30),HistoryID bigint);
CREATE TABLE dbo.PRM_SpecificationContentHistory(HistoryID bigint IDENTITY PRIMARY KEY,SpecificationNo nvarchar(120),SampleCategory nvarchar(40),VersionNo int,ActionType nvarchar(60),SignatureID bigint);
CREATE TABLE dbo.[AuditTrail](
 [AuditID] int IDENTITY(1,1) NOT NULL,
 [TableName] nvarchar(128) NULL,
 [RecordID] int NULL,
 [Action] nvarchar(150) NULL,
 [OldValue] nvarchar(max) NULL,
 [NewValue] nvarchar(max) NULL,
 [ChangedBy] nvarchar(100) NULL,
 [ChangeDate] datetime NULL,
 [PerformedBy] nvarchar(100) NULL,
 [Reason] nvarchar(max) NULL,
 [IPAddress] nvarchar(50) NULL,
 [CreatedUtc] datetime2(0) NOT NULL,
 [RecordHash] varbinary(32) NULL,
 [SourceWorkstation] nvarchar(128) NULL,
 [SourceApplication] nvarchar(128) NULL,
 [UtcRecordedAt] datetime2(3) NOT NULL,
 [FieldName] nvarchar(200) NULL,
 [TestName] nvarchar(300) NULL,
 [SampleNumber] nvarchar(100) NULL,
 [ModuleName] nvarchar(100) NULL,
 [ComputerName] nvarchar(200) NULL
);
CREATE TABLE dbo.Users(Username nvarchar(100) PRIMARY KEY,Role nvarchar(50),IsActive bit,IsLocked bit,MustChangePassword bit,CanRegisterSamples bit,LockedUntil datetime2 NULL,AuthenticationRowVersion rowversion);
EXEC(N'CREATE OR ALTER VIEW dbo.EQ001_UAT_PrmVersionEvidence AS
 SELECT v.SpecificationNo,v.SampleCategory,v.VersionNo,
 CONVERT(binary(32),HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),CONVERT(varchar(max),
 (SELECT JSON_QUERY((SELECT t.* FROM dbo.PRM_SpecificationTests t WHERE t.SpecificationNo=v.SpecificationNo AND t.SampleCategory=v.SampleCategory AND t.VersionNo=v.VersionNo ORDER BY SpecificationTestID FOR JSON PATH,INCLUDE_NULL_VALUES)) AS Tests,
 JSON_QUERY((SELECT s.* FROM dbo.PRM_SpecificationSignatures s WHERE s.SpecificationNo=v.SpecificationNo AND s.SampleCategory=v.SampleCategory AND s.VersionNo=v.VersionNo ORDER BY SignatureID FOR JSON PATH,INCLUDE_NULL_VALUES)) AS Signatures,
 JSON_QUERY((SELECT h.* FROM dbo.PRM_SpecificationContentHistory h WHERE h.SpecificationNo=v.SpecificationNo AND h.SampleCategory=v.SampleCategory AND h.VersionNo=v.VersionNo ORDER BY HistoryID FOR JSON PATH,INCLUDE_NULL_VALUES)) AS History
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) COLLATE Latin1_General_100_BIN2_UTF8)))) AS EvidenceHash
 FROM (SELECT DISTINCT SpecificationNo,SampleCategory,VersionNo FROM dbo.PRM_SpecificationTests) v;');
EXEC(N'CREATE OR ALTER VIEW dbo.EQ001_UAT_EffectivePrmSpecificationTests AS
 SELECT t.SpecificationTestID, t.SpecificationNo, t.SampleCategory, t.VersionNo, t.TestCode, t.TestName, t.SpecificationText, t.Unit, t.ResultType, t.SpecificationLimit, t.RequiredTest, t.SortOrder, t.ApprovalStatus, t.ApprovedBy, t.ApprovedDate, t.EffectiveDate, t.CreatedBy, t.CreatedDate, t.ReviewedBy, t.ReviewedDate, t.ItemCode, t.CompendialReference, t.IsDefaultForCategory, t.ProductionStage, t.MinimumElapsedHours,
 CONVERT(bit,CASE WHEN EXISTS(
 SELECT 1 FROM dbo.EQ001_PrmSupersessions x
 JOIN dbo.EQ001_UAT_PrmVersionEvidence old ON old.SpecificationNo=x.SpecificationNo AND old.SampleCategory=x.SampleCategory AND old.VersionNo=x.OldVersion AND old.EvidenceHash=x.OldEvidenceHash
 JOIN dbo.PRM_SpecificationSignatures sig ON sig.SignatureID=x.ApprovalSignatureID AND sig.SpecificationNo=x.SpecificationNo AND sig.SampleCategory=x.SampleCategory AND sig.VersionNo=x.NewVersion AND sig.ActionType=N''Approve Specification'' AND sig.SignedBy=x.Actor
 JOIN dbo.EQ001_PrmSigningRequests req ON req.RequestId=x.RequestId AND req.Actor=x.Actor AND req.WindowsCallerSid=x.WindowsCallerSid AND req.SpecificationNo=x.SpecificationNo AND req.SampleCategory=x.SampleCategory AND req.VersionNo=x.NewVersion AND req.ActionType=N''PrmApprove''
 JOIN dbo.PRM_SpecificationContentHistory h ON h.HistoryID=req.HistoryID AND h.SignatureID=sig.SignatureID AND h.SpecificationNo=x.SpecificationNo AND h.SampleCategory=x.SampleCategory AND h.VersionNo=x.NewVersion AND h.ActionType=N''Approve Specification''
 WHERE x.SpecificationNo=t.SpecificationNo AND x.SampleCategory=t.SampleCategory AND x.OldVersion=t.VersionNo
 ) THEN 0 ELSE t.IsActive END) AS IsActive
 FROM dbo.PRM_SpecificationTests t;');
EXEC(N'CREATE OR ALTER TRIGGER dbo.TRG_EQ001_PrmSupersessions_AppendOnly ON dbo.EQ001_PrmSupersessions AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; THROW 56809,''PRM supersession decisions are append-only.'',1; END;');
