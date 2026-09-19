SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    PharmaLIMS controlled fresh-install core baseline.

    Scope:
      - Creates only the empty core schema required before the controlled
        migrations in Database/MigrationManifest.json are executed.
      - Does not create users, passwords, water points, tests, limits, media,
        specifications, or other GMP master data.
      - Must run only against an empty PharmaLIMS database. The application
        migrator owns the surrounding transaction and records this exact file
        checksum only after successful execution.
*/

IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.Samples', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.Tests', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.CultureMediaLots', N'U') IS NOT NULL
BEGIN
    THROW 53000, 'Fresh-install baseline refused: operational PharmaLIMS tables already exist.', 1;
END;

CREATE TABLE dbo.LIMS_SchemaVersions
(
    VersionKey NVARCHAR(100) NOT NULL CONSTRAINT PK_LIMS_SchemaVersions PRIMARY KEY,
    Description NVARCHAR(500) NOT NULL,
    MigrationChecksum NVARCHAR(128) NULL,
    ApplicationVersion NVARCHAR(50) NULL,
    AppliedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LIMS_SchemaVersions_AppliedAt DEFAULT SYSUTCDATETIME(),
    AppliedBy NVARCHAR(128) NOT NULL CONSTRAINT DF_LIMS_SchemaVersions_AppliedBy DEFAULT SUSER_SNAME()
);

CREATE TABLE dbo.Users
(
    UserID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Username NVARCHAR(100) NOT NULL,
    PasswordHash NVARCHAR(512) NULL,
    PasswordHashNew NVARCHAR(512) NULL,
    PasswordSalt NVARCHAR(256) NULL,
    FullName NVARCHAR(200) NOT NULL,
    Role NVARCHAR(100) NOT NULL,
    Department NVARCHAR(150) NULL,
    Section NVARCHAR(150) NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
    LastLogin DATETIME2(0) NULL,
    FailedLoginAttempts INT NOT NULL CONSTRAINT DF_Users_FailedLoginAttempts DEFAULT (0),
    IsLocked BIT NOT NULL CONSTRAINT DF_Users_IsLocked DEFAULT (0),
    LockedUntil DATETIME2(0) NULL,
    CanAccessWater BIT NOT NULL CONSTRAINT DF_Users_CanAccessWater DEFAULT (0),
    CanAccessEM BIT NOT NULL CONSTRAINT DF_Users_CanAccessEM DEFAULT (0),
    CanRegisterSamples BIT NOT NULL CONSTRAINT DF_Users_CanRegisterSamples DEFAULT (0),
    CanEnterResults BIT NOT NULL CONSTRAINT DF_Users_CanEnterResults DEFAULT (0),
    CanReviewResults BIT NOT NULL CONSTRAINT DF_Users_CanReviewResults DEFAULT (0),
    CanApproveResults BIT NOT NULL CONSTRAINT DF_Users_CanApproveResults DEFAULT (0),
    CanIssueCOA BIT NOT NULL CONSTRAINT DF_Users_CanIssueCOA DEFAULT (0),
    CanCancelCOA BIT NOT NULL CONSTRAINT DF_Users_CanCancelCOA DEFAULT (0),
    CanAccessReports BIT NOT NULL CONSTRAINT DF_Users_CanAccessReports DEFAULT (0),
    CanManageUsers BIT NOT NULL CONSTRAINT DF_Users_CanManageUsers DEFAULT (0),
    CanManageSettings BIT NOT NULL CONSTRAINT DF_Users_CanManageSettings DEFAULT (0),
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2(0) NULL
);

CREATE UNIQUE INDEX UX_Users_Username_20260821
    ON dbo.Users(Username);

CREATE TABLE dbo.Tests
(
    TestID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tests PRIMARY KEY,
    TestCode NVARCHAR(50) NULL,
    TestName NVARCHAR(200) NOT NULL,
    TestCategory NVARCHAR(100) NOT NULL,
    Unit NVARCHAR(80) NULL,
    AlertLimit DECIMAL(18,6) NULL,
    ActionLimit DECIMAL(18,6) NULL,
    SortOrder INT NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_Tests_IsActive DEFAULT (1),
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Tests_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Tests_Limits_Baseline CHECK
    (
        AlertLimit IS NULL OR ActionLimit IS NULL OR ActionLimit >= AlertLimit
    )
);

CREATE TABLE dbo.WaterSamplingPoints
(
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WaterSamplingPoints PRIMARY KEY,
    PointCode NVARCHAR(50) NOT NULL,
    PointName NVARCHAR(200) NOT NULL,
    Location NVARCHAR(300) NULL,
    WaterType NVARCHAR(40) NOT NULL,
    Status NVARCHAR(30) NOT NULL CONSTRAINT DF_WaterSamplingPoints_Status DEFAULT N'Active',
    SortOrder INT NULL,
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_WaterSamplingPoints_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_WaterSamplingPoints_PointCode UNIQUE(PointCode)
);

/* Compatibility-only legacy lookup. New Water records use WaterSamplingPoints
   and controlled point snapshots; no data is seeded here. */
CREATE TABLE dbo.SamplingPoints
(
    PointID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SamplingPoints PRIMARY KEY,
    PointCode NVARCHAR(50) NOT NULL,
    PointName NVARCHAR(200) NULL,
    Location NVARCHAR(300) NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_SamplingPoints_Active DEFAULT (0),
    CONSTRAINT UQ_SamplingPoints_PointCode UNIQUE(PointCode)
);

CREATE TABLE dbo.WaterTestProfiles
(
    ProfileID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WaterTestProfiles PRIMARY KEY,
    ProfileCode NVARCHAR(20) NOT NULL,
    ProfileName NVARCHAR(150) NOT NULL,
    VersionNo INT NOT NULL CONSTRAINT DF_WaterTestProfiles_Version DEFAULT (1),
    ApprovalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_WaterTestProfiles_Status DEFAULT N'Draft',
    EffectiveFrom DATE NULL,
    EffectiveTo DATE NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_WaterTestProfiles_Active DEFAULT (0),
    ReviewedBy NVARCHAR(100) NULL,
    ReviewedAt DATETIME2(0) NULL,
    ApprovedBy NVARCHAR(100) NULL,
    ApprovedAt DATETIME2(0) NULL,
    CreatedBy NVARCHAR(100) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_WaterTestProfiles_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_WaterTestProfiles_Version UNIQUE(ProfileCode, VersionNo),
    CONSTRAINT CK_WaterTestProfiles_Approval CHECK
    (
        (ApprovalStatus=N'Draft' AND IsActive=0)
        OR (ApprovalStatus=N'Reviewed' AND ReviewedBy IS NOT NULL AND ReviewedAt IS NOT NULL AND IsActive=0)
        OR (ApprovalStatus=N'Approved' AND ApprovedBy IS NOT NULL AND ApprovedAt IS NOT NULL)
        OR (ApprovalStatus=N'Obsolete' AND IsActive=0)
    )
);

CREATE TABLE dbo.WaterTestProfileTests
(
    ProfileTestID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WaterTestProfileTests PRIMARY KEY,
    ProfileID INT NOT NULL,
    TestID INT NOT NULL,
    SortOrder INT NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_WaterTestProfileTests_Active DEFAULT (1),
    CONSTRAINT FK_WaterTestProfileTests_Profile FOREIGN KEY(ProfileID) REFERENCES dbo.WaterTestProfiles(ProfileID),
    CONSTRAINT FK_WaterTestProfileTests_Test FOREIGN KEY(TestID) REFERENCES dbo.Tests(TestID),
    CONSTRAINT UQ_WaterTestProfileTests UNIQUE(ProfileID, TestID)
);

CREATE TABLE dbo.WaterSpecifications
(
    SpecificationID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WaterSpecifications PRIMARY KEY,
    ProfileCode NVARCHAR(20) NOT NULL,
    TestID INT NOT NULL,
    PointCode NVARCHAR(50) NULL,
    LowerLimit DECIMAL(18,6) NULL,
    UpperLimit DECIMAL(18,6) NULL,
    AlertLimit DECIMAL(18,6) NULL,
    ActionLimit DECIMAL(18,6) NULL,
    SpecificationText NVARCHAR(500) NOT NULL,
    EffectiveFrom DATE NULL,
    EffectiveTo DATE NULL,
    ApprovalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_WaterSpecifications_Status DEFAULT N'Draft',
    IsActive BIT NOT NULL CONSTRAINT DF_WaterSpecifications_Active DEFAULT (0),
    ReviewedBy NVARCHAR(100) NULL,
    ReviewedAt DATETIME2(0) NULL,
    ApprovedBy NVARCHAR(100) NULL,
    ApprovedAt DATETIME2(0) NULL,
    CreatedBy NVARCHAR(100) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_WaterSpecifications_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_WaterSpecifications_Test FOREIGN KEY(TestID) REFERENCES dbo.Tests(TestID),
    CONSTRAINT CK_WaterSpecifications_Active CHECK
    (
        IsActive=0 OR (ApprovalStatus=N'Approved' AND ApprovedBy IS NOT NULL AND ApprovedAt IS NOT NULL)
    )
);

CREATE TABLE dbo.Samples
(
    SampleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Samples PRIMARY KEY,
    SampleNumber NVARCHAR(100) NOT NULL,
    PointID INT NULL,
    PointCodeSnapshot NVARCHAR(50) NULL,
    PointNameSnapshot NVARCHAR(200) NULL,
    PointLocationSnapshot NVARCHAR(300) NULL,
    SampleType NVARCHAR(100) NOT NULL,
    SamplingMethod NVARCHAR(100) NULL,
    Grade NVARCHAR(50) NULL,
    ExposureTime INT NULL,
    SamplingVolume INT NULL,
    Activity NVARCHAR(200) NULL,
    SamplingDateTime DATETIME2(0) NOT NULL,
    ReceivedDateTime DATETIME2(0) NULL,
    IncubationStartedDateTime DATETIME2(0) NULL,
    IncubationCompletedDateTime DATETIME2(0) NULL,
    IncubationEndDate DATETIME2(0) NULL,
    AnalysisStartedDateTime DATETIME2(0) NULL,
    AnalysisCompletedDateTime DATETIME2(0) NULL,
    ReviewedDateTime DATETIME2(0) NULL,
    ApprovedDateTime DATETIME2(0) NULL,
    SampledBy NVARCHAR(100) NOT NULL,
    ReceivedBy NVARCHAR(100) NULL,
    ContainerCount INT NULL,
    SampleVolume NVARCHAR(100) NULL,
    ContainerCondition NVARCHAR(200) NULL,
    ReceiptTemperature NVARCHAR(100) NULL,
    ReceiptDecision NVARCHAR(50) NULL,
    ReceiptDeviationReason NVARCHAR(MAX) NULL,
    WaterIncubationProgram NVARCHAR(200) NULL,
    WaterIncubatorID NVARCHAR(100) NULL,
    Status NVARCHAR(60) NOT NULL CONSTRAINT DF_Samples_Status DEFAULT N'Registered',
    LabelPrinted BIT NOT NULL CONSTRAINT DF_Samples_LabelPrinted DEFAULT (0),
    CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_Samples_CreatedDate DEFAULT SYSUTCDATETIME(),
    ModifiedDate DATETIME2(0) NULL,
    CONSTRAINT UQ_Samples_SampleNumber UNIQUE(SampleNumber),
    CONSTRAINT FK_Samples_WaterPoint FOREIGN KEY(PointID) REFERENCES dbo.WaterSamplingPoints(Id)
);

CREATE TABLE dbo.SampleTests
(
    SampleTestID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SampleTests PRIMARY KEY,
    SampleID INT NOT NULL,
    TestID INT NOT NULL,
    TestNameSnapshot NVARCHAR(200) NULL,
    TestCategorySnapshot NVARCHAR(100) NULL,
    UnitSnapshot NVARCHAR(80) NULL,
    AlertLimitSnapshot DECIMAL(18,6) NULL,
    ActionLimitSnapshot DECIMAL(18,6) NULL,
    ResultValue NVARCHAR(200) NULL,
    ResultStatus NVARCHAR(30) NULL,
    DeviationType NVARCHAR(100) NULL,
    LimitDescription NVARCHAR(500) NULL,
    Remarks NVARCHAR(MAX) NULL,
    EnteredBy NVARCHAR(100) NULL,
    EnteredDate DATETIME2(0) NULL,
    ResultEnteredBy NVARCHAR(100) NULL,
    ResultEnteredDate DATETIME2(0) NULL,
    CONSTRAINT FK_SampleTests_Sample FOREIGN KEY(SampleID) REFERENCES dbo.Samples(SampleID),
    CONSTRAINT FK_SampleTests_Test FOREIGN KEY(TestID) REFERENCES dbo.Tests(TestID),
    CONSTRAINT UQ_SampleTests_Sample_Test UNIQUE(SampleID, TestID)
);

CREATE TABLE dbo.SampleStatusHistory
(
    HistoryID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SampleStatusHistory PRIMARY KEY,
    SampleID INT NOT NULL,
    Status NVARCHAR(60) NULL,
    NewStatus NVARCHAR(60) NULL,
    ChangedBy NVARCHAR(100) NULL,
    PerformedBy NVARCHAR(100) NULL,
    ChangedDate DATETIME2(0) NULL,
    PerformedAt DATETIME2(0) NULL,
    CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_SampleStatusHistory_Created DEFAULT SYSUTCDATETIME(),
    Remarks NVARCHAR(MAX) NULL,
    Reason NVARCHAR(MAX) NULL,
    CONSTRAINT FK_SampleStatusHistory_Sample FOREIGN KEY(SampleID) REFERENCES dbo.Samples(SampleID)
);

CREATE TABLE dbo.LIMS_NumberSequences
(
    SequenceID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LIMS_NumberSequences PRIMARY KEY,
    SequenceKey NVARCHAR(100) NOT NULL,
    NumberKey NVARCHAR(50) NOT NULL,
    YearNo INT NOT NULL,
    LastNumber INT NOT NULL CONSTRAINT DF_LIMS_NumberSequences_Last DEFAULT (0),
    Prefix NVARCHAR(50) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LIMS_NumberSequences_Created DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LIMS_NumberSequences_Updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_LIMS_NumberSequences_SequenceKey UNIQUE(SequenceKey),
    CONSTRAINT UQ_LIMS_NumberSequences_NumberYear UNIQUE(NumberKey, YearNo),
    CONSTRAINT CK_LIMS_NumberSequences_Last CHECK(LastNumber >= 0)
);

CREATE TABLE dbo.Certificates
(
    CertificateID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Certificates PRIMARY KEY,
    CertificateNumber NVARCHAR(50) NOT NULL,
    SampleID INT NOT NULL,
    SampleNumber NVARCHAR(100) NULL,
    IssueDate DATETIME2(0) NOT NULL,
    IssuedBy NVARCHAR(100) NOT NULL,
    Status NVARCHAR(40) NOT NULL CONSTRAINT DF_Certificates_Status DEFAULT N'Active',
    RevisionNo INT NOT NULL CONSTRAINT DF_Certificates_Revision DEFAULT (0),
    IsCancelled BIT NOT NULL CONSTRAINT DF_Certificates_Cancelled DEFAULT (0),
    VerificationCode NVARCHAR(100) NULL,
    ReportHash NVARCHAR(128) NULL,
    CertificateStatus NVARCHAR(40) NULL,
    ReissuedFromCertificateID INT NULL,
    CancelledBy NVARCHAR(100) NULL,
    CancelledDate DATETIME2(0) NULL,
    CancellationReason NVARCHAR(MAX) NULL,
    CONSTRAINT UQ_Certificates_Number UNIQUE(CertificateNumber),
    CONSTRAINT FK_Certificates_Sample FOREIGN KEY(SampleID) REFERENCES dbo.Samples(SampleID),
    CONSTRAINT FK_Certificates_Reissue FOREIGN KEY(ReissuedFromCertificateID) REFERENCES dbo.Certificates(CertificateID)
);

CREATE TABLE dbo.CertificatePrintHistory
(
    PrintHistoryID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CertificatePrintHistory PRIMARY KEY,
    CertificateID INT NOT NULL,
    SampleID INT NOT NULL,
    CertificateNumber NVARCHAR(50) NOT NULL,
    PrintedBy NVARCHAR(100) NOT NULL,
    PrintedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CertificatePrintHistory_PrintedAt DEFAULT SYSUTCDATETIME(),
    PrintReason NVARCHAR(500) NULL,
    CONSTRAINT FK_CertificatePrintHistory_Certificate FOREIGN KEY(CertificateID) REFERENCES dbo.Certificates(CertificateID),
    CONSTRAINT FK_CertificatePrintHistory_Sample FOREIGN KEY(SampleID) REFERENCES dbo.Samples(SampleID)
);

CREATE TABLE dbo.AuditTrail
(
    AuditID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditTrail PRIMARY KEY,
    TableName NVARCHAR(128) NOT NULL,
    RecordID INT NOT NULL,
    Action NVARCHAR(200) NOT NULL,
    FieldName NVARCHAR(200) NULL,
    TestName NVARCHAR(200) NULL,
    SampleNumber NVARCHAR(100) NULL,
    ModuleName NVARCHAR(100) NULL,
    OldValue NVARCHAR(MAX) NULL,
    NewValue NVARCHAR(MAX) NULL,
    Reason NVARCHAR(MAX) NULL,
    ChangedBy NVARCHAR(100) NOT NULL,
    PerformedBy NVARCHAR(100) NOT NULL,
    ChangeDate DATETIME2(0) NOT NULL CONSTRAINT DF_AuditTrail_ChangeDate DEFAULT SYSUTCDATETIME(),
    CreatedUtc DATETIME2(0) NOT NULL CONSTRAINT DF_AuditTrail_CreatedUtc DEFAULT SYSUTCDATETIME(),
    UtcRecordedAt DATETIME2(0) NOT NULL CONSTRAINT DF_AuditTrail_UtcRecordedAt DEFAULT SYSUTCDATETIME(),
    IPAddress NVARCHAR(100) NULL,
    SourceWorkstation NVARCHAR(200) NULL,
    ComputerName NVARCHAR(200) NULL,
    SourceApplication NVARCHAR(100) NOT NULL CONSTRAINT DF_AuditTrail_SourceApp DEFAULT N'PharmaLIMS'
);

CREATE TABLE dbo.ElectronicSignatures
(
    SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ElectronicSignatures PRIMARY KEY,
    SampleID INT NOT NULL,
    ActionType NVARCHAR(100) NOT NULL,
    ActionReason NVARCHAR(MAX) NULL,
    SignedBy NVARCHAR(100) NOT NULL,
    MeaningOfSignature NVARCHAR(255) NOT NULL,
    UserRole NVARCHAR(100) NULL,
    SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_ElectronicSignatures_SignedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_ElectronicSignatures_Sample FOREIGN KEY(SampleID) REFERENCES dbo.Samples(SampleID)
);

CREATE TABLE dbo.EM_Areas
(
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_Areas PRIMARY KEY,
    AreaCode NVARCHAR(50) NOT NULL,
    AreaName NVARCHAR(200) NOT NULL,
    AreaGroup NVARCHAR(100) NULL,
    Grade NVARCHAR(50) NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_EM_Areas_Active DEFAULT (1),
    CONSTRAINT UQ_EM_Areas_Code UNIQUE(AreaCode)
);

CREATE TABLE dbo.EM_AreaTemplates
(
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_AreaTemplates PRIMARY KEY,
    AreaId INT NOT NULL,
    Method NVARCHAR(100) NOT NULL,
    PlateCode NVARCHAR(100) NOT NULL,
    SequenceNo INT NOT NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_EM_AreaTemplates_Active DEFAULT (1),
    CONSTRAINT FK_EM_AreaTemplates_Area FOREIGN KEY(AreaId) REFERENCES dbo.EM_Areas(Id),
    CONSTRAINT UQ_EM_AreaTemplates UNIQUE(AreaId, Method, PlateCode)
);

CREATE TABLE dbo.EM_Events
(
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_Events PRIMARY KEY,
    EventNo NVARCHAR(100) NOT NULL,
    AreaId INT NOT NULL,
    EventDate DATETIME2(0) NOT NULL,
    ARNo NVARCHAR(100) NULL,
    SanitizationDetails NVARCHAR(MAX) NULL,
    SanitizationTime NVARCHAR(50) NULL,
    DisinfectantUsed NVARCHAR(200) NULL,
    MediaUsed NVARCHAR(200) NULL,
    MediaLotNo NVARCHAR(100) NULL,
    SamplingTimeFrom NVARCHAR(50) NULL,
    SamplingTimeTo NVARCHAR(50) NULL,
    ActivityNoOfPersons NVARCHAR(200) NULL,
    AirSamplerNo NVARCHAR(100) NULL,
    AirSamplingTime NVARCHAR(50) NULL,
    IncubationTemperature NVARCHAR(200) NULL,
    IncubatorNo1 NVARCHAR(100) NULL,
    IncubatorNo2 NVARCHAR(100) NULL,
    IncubationStart NVARCHAR(50) NULL,
    IncubationEnd NVARCHAR(50) NULL,
    WorkflowStatus NVARCHAR(50) NULL,
    FinalResult NVARCHAR(50) NULL,
    Remarks NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_Events_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_EM_Events_EventNo UNIQUE(EventNo),
    CONSTRAINT FK_EM_Events_Area FOREIGN KEY(AreaId) REFERENCES dbo.EM_Areas(Id)
);

CREATE TABLE dbo.EM_EventPlates
(
    Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_EventPlates PRIMARY KEY,
    EventId INT NOT NULL,
    Method NVARCHAR(100) NOT NULL,
    PlateCode NVARCHAR(100) NOT NULL,
    SequenceNo INT NOT NULL,
    TotalCount DECIMAL(18,3) NULL,
    FungalCount DECIMAL(18,3) NULL,
    ColoniesObserved NVARCHAR(500) NULL,
    CorrectedCount DECIMAL(18,3) NULL,
    ResultCFU DECIMAL(18,3) NULL,
    Status NVARCHAR(50) NULL,
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_EventPlates_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_EM_EventPlates_Event FOREIGN KEY(EventId) REFERENCES dbo.EM_Events(Id),
    CONSTRAINT UQ_EM_EventPlates_Event_Plate UNIQUE(EventId, PlateCode)
);

CREATE TABLE dbo.CultureMedia
(
    MediaID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMedia PRIMARY KEY,
    MediaCode NVARCHAR(50) NOT NULL,
    MediaName NVARCHAR(200) NOT NULL,
    MediaType NVARCHAR(100) NULL,
    Manufacturer NVARCHAR(200) NULL,
    StorageCondition NVARCHAR(200) NULL,
    DefaultExpiryDays INT NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_CultureMedia_Active DEFAULT (1),
    CONSTRAINT UQ_CultureMedia_Code UNIQUE(MediaCode)
);

CREATE TABLE dbo.MediaNumberSequences
(
    SequenceID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MediaNumberSequences PRIMARY KEY,
    SequenceName NVARCHAR(100) NOT NULL,
    SequenceYear INT NOT NULL,
    LastNumber INT NOT NULL CONSTRAINT DF_MediaNumberSequences_Last DEFAULT (0),
    Prefix NVARCHAR(30) NOT NULL,
    ModifiedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MediaNumberSequences_Modified DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_MediaNumberSequences UNIQUE(SequenceName, SequenceYear),
    CONSTRAINT CK_MediaNumberSequences_Last CHECK(LastNumber >= 0)
);

CREATE TABLE dbo.CultureMediaLots
(
    MediaLotID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaLots PRIMARY KEY,
    MediaID INT NOT NULL,
    LotNumber NVARCHAR(120) NOT NULL,
    Supplier NVARCHAR(200) NULL,
    ManufacturerLot NVARCHAR(120) NULL,
    ReceivedDate DATE NULL,
    ExpiryDate DATE NULL,
    QuantityReceived DECIMAL(18,3) NULL,
    InitialStockG DECIMAL(18,3) NULL,
    CurrentStockG DECIMAL(18,3) NULL,
    StockStatus NVARCHAR(30) NULL,
    COANumber NVARCHAR(120) NULL,
    ReceivedBy NVARCHAR(100) NULL,
    ReceiptStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_CultureMediaLots_Status DEFAULT N'Quarantine',
    Remarks NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaLots_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_CultureMediaLots_Media FOREIGN KEY(MediaID) REFERENCES dbo.CultureMedia(MediaID),
    CONSTRAINT UQ_CultureMediaLots_Media_Lot UNIQUE(MediaID, LotNumber)
);

CREATE TABLE dbo.MediaPreparations
(
    MediaPreparationID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MediaPreparations PRIMARY KEY,
    MediaPreparationNo NVARCHAR(100) NOT NULL,
    MediaID INT NOT NULL,
    MediaLotID INT NOT NULL,
    PreparationDate DATE NULL,
    ExpiryDate DATE NULL,
    QuantityPrepared DECIMAL(18,3) NULL,
    PowderQuantityG DECIMAL(18,3) NULL,
    PreparedBy NVARCHAR(100) NOT NULL,
    BatchSize NVARCHAR(100) NULL,
    FinalPH NVARCHAR(100) NULL,
    Appearance NVARCHAR(300) NULL,
    AutoclaveCycleNo NVARCHAR(100) NULL,
    AutoclaveTemperature NVARCHAR(100) NULL,
    AutoclaveHoldingTime NVARCHAR(100) NULL,
    SterilityReview NVARCHAR(50) NULL,
    ReleaseStatus NVARCHAR(50) NOT NULL CONSTRAINT DF_MediaPreparations_Status DEFAULT N'Under Release',
    Remarks NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MediaPreparations_Created DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2(0) NULL,
    CONSTRAINT UQ_MediaPreparations_Number UNIQUE(MediaPreparationNo),
    CONSTRAINT FK_MediaPreparations_Media FOREIGN KEY(MediaID) REFERENCES dbo.CultureMedia(MediaID),
    CONSTRAINT FK_MediaPreparations_Lot FOREIGN KEY(MediaLotID) REFERENCES dbo.CultureMediaLots(MediaLotID)
);

CREATE TABLE dbo.MediaQualifications
(
    MediaQualificationID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MediaQualifications PRIMARY KEY,
    QualificationNo NVARCHAR(50) NOT NULL,
    MediaLotID INT NOT NULL,
    MediaPreparationID INT NULL,
    QualificationType NVARCHAR(100) NOT NULL,
    QualificationDate DATE NULL,
    PerformedBy NVARCHAR(100) NOT NULL,
    ReviewedBy NVARCHAR(100) NULL,
    ReleasedBy NVARCHAR(100) NULL,
    ReviewDate DATETIME2(0) NULL,
    ReleaseDate DATETIME2(0) NULL,
    QualificationStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_MediaQualifications_Status DEFAULT N'Pending Review',
    OverallResult NVARCHAR(30) NULL,
    Remarks NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_MediaQualifications_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_MediaQualifications_Number UNIQUE(QualificationNo),
    CONSTRAINT FK_MediaQualifications_Lot FOREIGN KEY(MediaLotID) REFERENCES dbo.CultureMediaLots(MediaLotID),
    CONSTRAINT FK_MediaQualifications_Preparation FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID)
);
