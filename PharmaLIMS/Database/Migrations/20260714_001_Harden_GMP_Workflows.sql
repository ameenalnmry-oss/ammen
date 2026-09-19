SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /* Water workflow columns */
    IF OBJECT_ID(N'dbo.Samples', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.Samples', N'ReceivedDateTime') IS NULL
            ALTER TABLE dbo.Samples ADD ReceivedDateTime DATETIME NULL;
        IF COL_LENGTH(N'dbo.Samples', N'AnalysisStartedDateTime') IS NULL
            ALTER TABLE dbo.Samples ADD AnalysisStartedDateTime DATETIME NULL;
        IF COL_LENGTH(N'dbo.Samples', N'AnalysisCompletedDateTime') IS NULL
            ALTER TABLE dbo.Samples ADD AnalysisCompletedDateTime DATETIME NULL;
        IF COL_LENGTH(N'dbo.Samples', N'ReviewedDateTime') IS NULL
            ALTER TABLE dbo.Samples ADD ReviewedDateTime DATETIME NULL;
        IF COL_LENGTH(N'dbo.Samples', N'ApprovedDateTime') IS NULL
            ALTER TABLE dbo.Samples ADD ApprovedDateTime DATETIME NULL;
    END;

    IF OBJECT_ID(N'dbo.SampleTests', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.SampleTests', N'ResultStatus') IS NULL
            ALTER TABLE dbo.SampleTests ADD ResultStatus NVARCHAR(30) NULL;
        IF COL_LENGTH(N'dbo.SampleTests', N'DeviationType') IS NULL
            ALTER TABLE dbo.SampleTests ADD DeviationType NVARCHAR(100) NULL;
        IF COL_LENGTH(N'dbo.SampleTests', N'LimitDescription') IS NULL
            ALTER TABLE dbo.SampleTests ADD LimitDescription NVARCHAR(250) NULL;
    END;

    /* EM workflow columns and signatures */
    IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.EM_Events', N'WorkflowStatus') IS NULL
            ALTER TABLE dbo.EM_Events ADD WorkflowStatus NVARCHAR(50) NULL;
        IF COL_LENGTH(N'dbo.EM_Events', N'ResultsEnteredBy') IS NULL
            ALTER TABLE dbo.EM_Events ADD ResultsEnteredBy NVARCHAR(100) NULL;
        IF COL_LENGTH(N'dbo.EM_Events', N'ResultsEnteredDate') IS NULL
            ALTER TABLE dbo.EM_Events ADD ResultsEnteredDate DATETIME NULL;
        IF COL_LENGTH(N'dbo.EM_Events', N'SubmittedBy') IS NULL
            ALTER TABLE dbo.EM_Events ADD SubmittedBy NVARCHAR(100) NULL;
        IF COL_LENGTH(N'dbo.EM_Events', N'SubmittedDate') IS NULL
            ALTER TABLE dbo.EM_Events ADD SubmittedDate DATETIME NULL;
        IF COL_LENGTH(N'dbo.EM_Events', N'ReviewedBy') IS NULL
            ALTER TABLE dbo.EM_Events ADD ReviewedBy NVARCHAR(100) NULL;
        IF COL_LENGTH(N'dbo.EM_Events', N'ReviewedDate') IS NULL
            ALTER TABLE dbo.EM_Events ADD ReviewedDate DATETIME NULL;
        IF COL_LENGTH(N'dbo.EM_Events', N'ApprovedBy') IS NULL
            ALTER TABLE dbo.EM_Events ADD ApprovedBy NVARCHAR(100) NULL;
        IF COL_LENGTH(N'dbo.EM_Events', N'ApprovedDate') IS NULL
            ALTER TABLE dbo.EM_Events ADD ApprovedDate DATETIME NULL;

        IF COL_LENGTH(N'dbo.EM_Events', N'FinalResult') IS NULL
            ALTER TABLE dbo.EM_Events ADD FinalResult NVARCHAR(50) NULL;
        ELSE
            ALTER TABLE dbo.EM_Events ALTER COLUMN FinalResult NVARCHAR(50) NULL;
        ALTER TABLE dbo.EM_Events ALTER COLUMN WorkflowStatus NVARCHAR(50) NULL;
    END;

    IF OBJECT_ID(N'dbo.EM_EventPlates', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.EM_EventPlates', N'Status') IS NULL
            ALTER TABLE dbo.EM_EventPlates ADD Status NVARCHAR(50) NULL;
        ELSE
            ALTER TABLE dbo.EM_EventPlates ALTER COLUMN Status NVARCHAR(50) NULL;

        IF COL_LENGTH(N'dbo.EM_EventPlates', N'ColoniesObserved') IS NULL
            ALTER TABLE dbo.EM_EventPlates ADD ColoniesObserved NVARCHAR(500) NULL;
        ELSE
            ALTER TABLE dbo.EM_EventPlates ALTER COLUMN ColoniesObserved NVARCHAR(500) NULL;
    END;

    IF OBJECT_ID(N'dbo.EM_EventSignatures', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EM_EventSignatures
        (
            SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_EventSignatures PRIMARY KEY,
            EventID INT NOT NULL,
            EventNo NVARCHAR(50) NULL,
            ActionType NVARCHAR(100) NOT NULL,
            ActionReason NVARCHAR(MAX) NULL,
            SignedBy NVARCHAR(100) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_EventSignatures_SignedAt DEFAULT SYSDATETIME()
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.EM_EventSignatures')
          AND name = N'IX_EM_EventSignatures_EventID_Action'
    )
        CREATE INDEX IX_EM_EventSignatures_EventID_Action
            ON dbo.EM_EventSignatures(EventID, ActionType, SignedAt DESC);

    /* PRM registration and controlled specification master */
    IF OBJECT_ID(N'dbo.PRM_NumberSequences', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_NumberSequences
        (
            SequenceID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_NumberSequences PRIMARY KEY,
            SequenceName NVARCHAR(60) NOT NULL CONSTRAINT UQ_PRM_NumberSequences_Name UNIQUE,
            Prefix NVARCHAR(20) NOT NULL,
            CurrentYear INT NOT NULL,
            LastNumber INT NOT NULL CONSTRAINT DF_PRM_NumberSequences_LastNumber DEFAULT (0),
            LastUpdated DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_NumberSequences_LastUpdated DEFAULT SYSDATETIME()
        );
    END;

    IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
    BEGIN
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
    END;

    IF COL_LENGTH(N'dbo.PRM_Samples', N'ResultInterpretation') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD ResultInterpretation NVARCHAR(60) NOT NULL CONSTRAINT DF_PRM_Samples_ResultInterpretation_20260714 DEFAULT N'Not Tested';
    IF COL_LENGTH(N'dbo.PRM_Samples', N'ReportStatus') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD ReportStatus NVARCHAR(60) NOT NULL CONSTRAINT DF_PRM_Samples_ReportStatus_20260714 DEFAULT N'Not Issued';
    IF COL_LENGTH(N'dbo.PRM_Samples', N'ReviewedBy') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD ReviewedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.PRM_Samples', N'ReviewedDate') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD ReviewedDate DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.PRM_Samples', N'ApprovedBy') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD ApprovedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.PRM_Samples', N'ApprovedDate') IS NULL
        ALTER TABLE dbo.PRM_Samples ADD ApprovedDate DATETIME2(0) NULL;

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
            CONSTRAINT UQ_PRM_SpecificationTests UNIQUE (SpecificationNo, SampleCategory, VersionNo, TestCode),
            CONSTRAINT CK_PRM_SpecificationTests_Approval CHECK
            (
                (ApprovalStatus = N'Approved' AND ApprovedBy IS NOT NULL AND ApprovedDate IS NOT NULL)
                OR ApprovalStatus IN (N'Draft', N'Obsolete')
            )
        );
    END;

    IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
    BEGIN
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
    END;

    IF COL_LENGTH(N'dbo.PRM_SampleTests', N'TestCode') IS NULL
        ALTER TABLE dbo.PRM_SampleTests ADD TestCode NVARCHAR(40) NULL;
    IF COL_LENGTH(N'dbo.PRM_SampleTests', N'SpecificationLimit') IS NULL
        ALTER TABLE dbo.PRM_SampleTests ADD SpecificationLimit DECIMAL(18,3) NULL;
    IF COL_LENGTH(N'dbo.PRM_SampleTests', N'RequiredTest') IS NULL
        ALTER TABLE dbo.PRM_SampleTests ADD RequiredTest BIT NOT NULL CONSTRAINT DF_PRM_SampleTests_Required_20260714 DEFAULT (1);
    IF COL_LENGTH(N'dbo.PRM_SampleTests', N'SortOrder') IS NULL
        ALTER TABLE dbo.PRM_SampleTests ADD SortOrder INT NULL;

    /* Preserve legacy PRM test definitions as inactive drafts for controlled QA review.
       They are intentionally not activated or approved by this migration. */
    ;WITH LegacySpecificationTests AS
    (
        SELECT
            s.SpecificationNo,
            s.SampleCategory,
            COALESCE(NULLIF(LTRIM(RTRIM(t.TestCode)), N''), N'LEGACY-' + CONVERT(NVARCHAR(20), t.SampleTestID)) AS TestCode,
            COALESCE(NULLIF(LTRIM(RTRIM(t.TestName)), N''), N'Legacy test') AS TestName,
            COALESCE(NULLIF(LTRIM(RTRIM(t.SpecificationText)), N''), N'Legacy specification - QA verification required') AS SpecificationText,
            t.Unit,
            COALESCE(NULLIF(LTRIM(RTRIM(t.ResultType)), N''), N'Text') AS ResultType,
            t.SpecificationLimit,
            ISNULL(t.RequiredTest, 1) AS RequiredTest,
            t.SortOrder,
            ROW_NUMBER() OVER
            (
                PARTITION BY s.SpecificationNo, s.SampleCategory,
                    COALESCE(NULLIF(LTRIM(RTRIM(t.TestCode)), N''), N'LEGACY-' + CONVERT(NVARCHAR(20), t.SampleTestID))
                ORDER BY t.SampleTestID
            ) AS RowRank
        FROM dbo.PRM_Samples s
        INNER JOIN dbo.PRM_SampleTests t ON t.SampleID = s.SampleID
        WHERE NULLIF(LTRIM(RTRIM(s.SpecificationNo)), N'') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(s.SampleCategory)), N'') IS NOT NULL
    )
    INSERT INTO dbo.PRM_SpecificationTests
    (
        SpecificationNo, SampleCategory, VersionNo, TestCode, TestName,
        SpecificationText, Unit, ResultType, SpecificationLimit, RequiredTest,
        SortOrder, ApprovalStatus, IsActive, CreatedBy, CreatedDate
    )
    SELECT
        legacy.SpecificationNo, legacy.SampleCategory, 1, legacy.TestCode, legacy.TestName,
        legacy.SpecificationText, legacy.Unit, legacy.ResultType, legacy.SpecificationLimit, legacy.RequiredTest,
        legacy.SortOrder, N'Draft', 0, N'Migration 20260714', SYSDATETIME()
    FROM LegacySpecificationTests legacy
    WHERE legacy.RowRank = 1
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.PRM_SpecificationTests configured
          WHERE configured.SpecificationNo = legacy.SpecificationNo
            AND configured.SampleCategory = legacy.SampleCategory
            AND configured.VersionNo = 1
            AND configured.TestCode = legacy.TestCode
      );

    IF OBJECT_ID(N'dbo.PRM_Reports', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_Reports
        (
            ReportID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_Reports PRIMARY KEY,
            ReportNumber NVARCHAR(60) NOT NULL CONSTRAINT UQ_PRM_Reports_ReportNumber UNIQUE,
            SampleID INT NOT NULL,
            ReportType NVARCHAR(120) NOT NULL,
            ReportStatus NVARCHAR(60) NOT NULL CONSTRAINT DF_PRM_Reports_Status DEFAULT N'Draft',
            Conclusion NVARCHAR(500) NULL,
            VerificationCode NVARCHAR(120) NULL,
            ReportHash NVARCHAR(128) NULL,
            IssuedBy NVARCHAR(120) NULL,
            IssueDate DATETIME2(0) NULL,
            CreatedBy NVARCHAR(120) NULL,
            CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_Reports_Created DEFAULT SYSDATETIME(),
            CONSTRAINT FK_PRM_Reports_Samples FOREIGN KEY (SampleID) REFERENCES dbo.PRM_Samples(SampleID)
        );
    END;

    IF OBJECT_ID(N'dbo.PRM_ReportHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_ReportHistory
        (
            HistoryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_ReportHistory PRIMARY KEY,
            ReportID INT NOT NULL,
            ActionType NVARCHAR(80) NOT NULL,
            ActionBy NVARCHAR(120) NULL,
            ActionDate DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_ReportHistory_Date DEFAULT SYSDATETIME(),
            Comments NVARCHAR(1000) NULL,
            CONSTRAINT FK_PRM_ReportHistory_Reports FOREIGN KEY (ReportID) REFERENCES dbo.PRM_Reports(ReportID)
        );
    END;

    /* PRM electronic signatures and certificate lifecycle */
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

    IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_Certificates
        (
            CertificateID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_Certificates PRIMARY KEY,
            CertificateNumber NVARCHAR(60) NOT NULL CONSTRAINT UQ_PRM_Certificates_Number UNIQUE,
            SampleID INT NOT NULL,
            CertificateType NVARCHAR(80) NULL,
            ReportTitle NVARCHAR(200) NULL,
            IssueDate DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_Certificates_IssueDate DEFAULT SYSDATETIME(),
            IssuedBy NVARCHAR(120) NOT NULL,
            CertificateStatus NVARCHAR(60) NOT NULL CONSTRAINT DF_PRM_Certificates_Status DEFAULT N'Active',
            RevisionNo INT NOT NULL CONSTRAINT DF_PRM_Certificates_Revision DEFAULT (1),
            IsCancelled BIT NOT NULL CONSTRAINT DF_PRM_Certificates_Cancelled DEFAULT (0),
            CancelledBy NVARCHAR(120) NULL,
            CancelledDate DATETIME2(0) NULL,
            CancellationReason NVARCHAR(500) NULL,
            ReissuedFromCertificateID INT NULL,
            VerificationCode NVARCHAR(80) NOT NULL,
            ReportHash NVARCHAR(120) NOT NULL,
            CreatedBy NVARCHAR(120) NOT NULL,
            CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_Certificates_Created DEFAULT SYSDATETIME(),
            CONSTRAINT FK_PRM_Certificates_Samples FOREIGN KEY (SampleID) REFERENCES dbo.PRM_Samples(SampleID)
        );
    END;

    IF OBJECT_ID(N'dbo.PRM_CertificateHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_CertificateHistory
        (
            HistoryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_CertificateHistory PRIMARY KEY,
            CertificateID INT NOT NULL,
            ActionName NVARCHAR(80) NOT NULL,
            ActionBy NVARCHAR(120) NOT NULL,
            ActionDate DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_CertificateHistory_Date DEFAULT SYSDATETIME(),
            Reason NVARCHAR(500) NULL,
            CONSTRAINT FK_PRM_CertificateHistory_Certificates FOREIGN KEY (CertificateID) REFERENCES dbo.PRM_Certificates(CertificateID)
        );
    END;

    IF EXISTS
    (
        SELECT SampleID
        FROM dbo.PRM_Certificates
        WHERE CertificateStatus = N'Active'
        GROUP BY SampleID
        HAVING COUNT(*) > 1
    )
        THROW 51001, 'Duplicate active PRM certificates exist. Resolve them before applying the unique active-certificate index.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.PRM_Certificates')
          AND name = N'UX_PRM_Certificates_OneActivePerSample'
    )
        CREATE UNIQUE INDEX UX_PRM_Certificates_OneActivePerSample
            ON dbo.PRM_Certificates(SampleID)
            WHERE CertificateStatus = N'Active';

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.PRM_ElectronicSignatures')
          AND name = N'IX_PRM_ElectronicSignatures_Sample_Action'
    )
        CREATE INDEX IX_PRM_ElectronicSignatures_Sample_Action
            ON dbo.PRM_ElectronicSignatures(SampleID, ActionType, SignedAt DESC);

    /* Central Quality Events used by PRM */
    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEvents
        (
            QualityEventID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEvents PRIMARY KEY,
            EventNumber NVARCHAR(60) NULL,
            EventType NVARCHAR(120) NULL,
            Severity NVARCHAR(60) NULL,
            SampleID INT NULL,
            SampleNumber NVARCHAR(80) NULL,
            SourceModule NVARCHAR(80) NULL,
            SourceRecordID INT NULL,
            CurrentStatus NVARCHAR(60) NULL CONSTRAINT DF_QualityEvents_Status DEFAULT N'Open',
            DetectedBy NVARCHAR(120) NULL,
            DetectedDate DATETIME2(0) NULL,
            DetectionSource NVARCHAR(160) NULL,
            InitialDescription NVARCHAR(MAX) NULL,
            ImmediateAction NVARCHAR(MAX) NULL,
            RootCauseCategory NVARCHAR(120) NULL,
            RootCauseDetails NVARCHAR(MAX) NULL,
            ImpactAssessment NVARCHAR(MAX) NULL,
            CAPARequired BIT NULL CONSTRAINT DF_QualityEvents_CAPA DEFAULT (0),
            QAConclusion NVARCHAR(MAX) NULL,
            FinalDisposition NVARCHAR(120) NULL,
            ClosedBy NVARCHAR(120) NULL,
            ClosedDate DATETIME2(0) NULL,
            CreatedBy NVARCHAR(120) NULL,
            CreatedDate DATETIME2(0) NULL CONSTRAINT DF_QualityEvents_Created DEFAULT SYSDATETIME(),
            ModifiedBy NVARCHAR(120) NULL,
            ModifiedDate DATETIME2(0) NULL
        );
    END;

    IF COL_LENGTH(N'dbo.QualityEvents', N'SourceModule') IS NULL
        ALTER TABLE dbo.QualityEvents ADD SourceModule NVARCHAR(80) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'SourceRecordID') IS NULL
        ALTER TABLE dbo.QualityEvents ADD SourceRecordID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'EventNumber') IS NULL
        ALTER TABLE dbo.QualityEvents ADD EventNumber NVARCHAR(60) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'EventType') IS NULL
        ALTER TABLE dbo.QualityEvents ADD EventType NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'Severity') IS NULL
        ALTER TABLE dbo.QualityEvents ADD Severity NVARCHAR(60) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'SampleID') IS NULL
        ALTER TABLE dbo.QualityEvents ADD SampleID INT NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'SampleNumber') IS NULL
        ALTER TABLE dbo.QualityEvents ADD SampleNumber NVARCHAR(80) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'CurrentStatus') IS NULL
        ALTER TABLE dbo.QualityEvents ADD CurrentStatus NVARCHAR(60) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'DetectedBy') IS NULL
        ALTER TABLE dbo.QualityEvents ADD DetectedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'DetectedDate') IS NULL
        ALTER TABLE dbo.QualityEvents ADD DetectedDate DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'DetectionSource') IS NULL
        ALTER TABLE dbo.QualityEvents ADD DetectionSource NVARCHAR(160) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'InitialDescription') IS NULL
        ALTER TABLE dbo.QualityEvents ADD InitialDescription NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ImmediateAction') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ImmediateAction NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'RootCauseCategory') IS NULL
        ALTER TABLE dbo.QualityEvents ADD RootCauseCategory NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'RootCauseDetails') IS NULL
        ALTER TABLE dbo.QualityEvents ADD RootCauseDetails NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ImpactAssessment') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ImpactAssessment NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'CAPARequired') IS NULL
        ALTER TABLE dbo.QualityEvents ADD CAPARequired BIT NULL CONSTRAINT DF_QualityEvents_CAPA_20260714 DEFAULT (0);
    IF COL_LENGTH(N'dbo.QualityEvents', N'QAConclusion') IS NULL
        ALTER TABLE dbo.QualityEvents ADD QAConclusion NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'FinalDisposition') IS NULL
        ALTER TABLE dbo.QualityEvents ADD FinalDisposition NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ClosedBy') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ClosedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ClosedDate') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ClosedDate DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'CreatedBy') IS NULL
        ALTER TABLE dbo.QualityEvents ADD CreatedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'CreatedDate') IS NULL
        ALTER TABLE dbo.QualityEvents ADD CreatedDate DATETIME2(0) NULL CONSTRAINT DF_QualityEvents_Created_20260714 DEFAULT SYSDATETIME();
    IF COL_LENGTH(N'dbo.QualityEvents', N'ModifiedBy') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ModifiedBy NVARCHAR(120) NULL;
    IF COL_LENGTH(N'dbo.QualityEvents', N'ModifiedDate') IS NULL
        ALTER TABLE dbo.QualityEvents ADD ModifiedDate DATETIME2(0) NULL;

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

    IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions', N'ExpectedAnswer') IS NULL
            ALTER TABLE dbo.QualityEventChecklistQuestions ADD ExpectedAnswer NVARCHAR(20) NULL CONSTRAINT DF_QualityEventChecklistQuestions_ExpectedAnswer DEFAULT N'Yes';
        IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions', N'QuestionLogic') IS NULL
            ALTER TABLE dbo.QualityEventChecklistQuestions ADD QuestionLogic NVARCHAR(50) NULL CONSTRAINT DF_QualityEventChecklistQuestions_QuestionLogic DEFAULT N'PositiveCheck';
    END;

    /* Culture Media compliance tables */
    IF OBJECT_ID(N'dbo.CultureMediaSopFields', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CultureMediaSopFields
        (
            CultureMediaSopFieldID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaSopFields PRIMARY KEY,
            EntityType NVARCHAR(50) NOT NULL,
            EntityID INT NOT NULL,
            AnnexureCode NVARCHAR(50) NOT NULL,
            FieldName NVARCHAR(120) NOT NULL,
            FieldValue NVARCHAR(MAX) NULL,
            CreatedBy NVARCHAR(100) NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaSopFields_Created DEFAULT SYSDATETIME(),
            UpdatedBy NVARCHAR(100) NULL,
            UpdatedAt DATETIME2(0) NULL,
            CONSTRAINT UQ_CultureMediaSopFields UNIQUE (EntityType, EntityID, AnnexureCode, FieldName)
        );
    END;

    IF OBJECT_ID(N'dbo.CultureMediaVisualChecks', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CultureMediaVisualChecks
        (
            VisualCheckID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaVisualChecks PRIMARY KEY,
            EntityType NVARCHAR(50) NOT NULL,
            EntityID INT NOT NULL,
            CheckStage NVARCHAR(80) NOT NULL,
            CheckDate DATE NOT NULL,
            EquipmentCode NVARCHAR(80) NULL,
            CrackedContainers NVARCHAR(30) NULL,
            UnequalFilling NVARCHAR(30) NULL,
            FlatSurface NVARCHAR(30) NULL,
            Dehydration NVARCHAR(30) NULL,
            Haemolysis NVARCHAR(30) NULL,
            ExcessiveDarkening NVARCHAR(30) NULL,
            CrystalFormation NVARCHAR(30) NULL,
            ExcessiveBubbles NVARCHAR(30) NULL,
            SterilityContamination NVARCHAR(30) NULL,
            RedoxIndicator NVARCHAR(30) NULL,
            ExcessiveMoisture NVARCHAR(30) NULL,
            Clarity NVARCHAR(30) NULL,
            PackIntegrity NVARCHAR(30) NULL,
            OtherObservations NVARCHAR(MAX) NULL,
            Conclusion NVARCHAR(200) NOT NULL,
            DoneBy NVARCHAR(100) NULL,
            CheckedBy NVARCHAR(100) NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaVisualChecks_Created DEFAULT SYSDATETIME()
        );
    END;

    IF OBJECT_ID(N'dbo.CultureMediaSignatures', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CultureMediaSignatures
        (
            CultureMediaSignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaSignatures PRIMARY KEY,
            EntityType NVARCHAR(50) NOT NULL,
            EntityID INT NOT NULL,
            RecordNumber NVARCHAR(100) NULL,
            ActionType NVARCHAR(100) NOT NULL,
            ActionReason NVARCHAR(MAX) NULL,
            SignedBy NVARCHAR(100) NOT NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaSignatures_SignedAt DEFAULT SYSDATETIME()
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.PRM_NumberSequences WHERE SequenceName = N'RM_SAMPLE')
        INSERT INTO dbo.PRM_NumberSequences (SequenceName, Prefix, CurrentYear, LastNumber) VALUES (N'RM_SAMPLE', N'RM', YEAR(GETDATE()), 0);
    IF NOT EXISTS (SELECT 1 FROM dbo.PRM_NumberSequences WHERE SequenceName = N'IP_SAMPLE')
        INSERT INTO dbo.PRM_NumberSequences (SequenceName, Prefix, CurrentYear, LastNumber) VALUES (N'IP_SAMPLE', N'IP', YEAR(GETDATE()), 0);
    IF NOT EXISTS (SELECT 1 FROM dbo.PRM_NumberSequences WHERE SequenceName = N'FP_SAMPLE')
        INSERT INTO dbo.PRM_NumberSequences (SequenceName, Prefix, CurrentYear, LastNumber) VALUES (N'FP_SAMPLE', N'FP', YEAR(GETDATE()), 0);
    IF NOT EXISTS (SELECT 1 FROM dbo.PRM_NumberSequences WHERE SequenceName = N'ST_SAMPLE')
        INSERT INTO dbo.PRM_NumberSequences (SequenceName, Prefix, CurrentYear, LastNumber) VALUES (N'ST_SAMPLE', N'ST', YEAR(GETDATE()), 0);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
