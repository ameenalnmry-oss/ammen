-- Actual supplied site column contracts; synthetic fixture only.
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
CREATE TABLE dbo.[ElectronicSignatures](
 [SignatureID] int IDENTITY(1,1) NOT NULL,
 [SampleID] int NULL,
 [ActionType] nvarchar(50) NULL,
 [ActionReason] nvarchar(500) NULL,
 [SignedBy] nvarchar(100) NULL,
 [SignedAt] datetime NULL,
 [MeaningOfSignature] nvarchar(200) NULL,
 [UserRole] nvarchar(50) NULL,
 [CreatedUtc] datetime2(0) NOT NULL,
 [RecordHash] varbinary(32) NULL,
 [SourceWorkstation] nvarchar(128) NULL,
 [SourceApplication] nvarchar(128) NULL,
 [UtcSignedAt] datetime2(3) NOT NULL
);
CREATE TABLE dbo.[LIMS_NumberSequences](
 [Id] int IDENTITY(1,1) NOT NULL,
 [NumberKey] nvarchar(50) NOT NULL,
 [YearNo] int NOT NULL,
 [LastNumber] int NOT NULL,
 [Prefix] nvarchar(50) NOT NULL,
 [CreatedAt] datetime NOT NULL,
 [UpdatedAt] datetime NOT NULL,
 [SequenceKey] nvarchar(100) NOT NULL
);
CREATE TABLE dbo.[Samples](
 [SampleID] int IDENTITY(1,1) NOT NULL,
 [SampleNumber] nvarchar(20) NOT NULL,
 [PointID] int NULL,
 [SampleType] nvarchar(30) NULL,
 [SamplingMethod] nvarchar(20) NULL,
 [Grade] nvarchar(20) NULL,
 [ExposureTime] int NULL,
 [SamplingVolume] int NULL,
 [IncubationEndDate] datetime NULL,
 [SamplingDateTime] datetime NULL,
 [SampledBy] nvarchar(100) NULL,
 [Activity] nvarchar(100) NULL,
 [Status] nvarchar(100) NULL,
 [LabelPrinted] bit NULL,
 [CreatedDate] datetime NULL,
 [ReviewedBy] nvarchar(100) NULL,
 [ReviewDate] datetime NULL,
 [ApprovedBy] nvarchar(100) NULL,
 [ApprovalDate] datetime NULL,
 [RejectionReason] nvarchar(500) NULL,
 [IsLocked] bit NULL,
 [LockedBy] nvarchar(100) NULL,
 [LockedDate] datetime NULL,
 [ModifiedBy] nvarchar(100) NULL,
 [ModifiedDate] datetime NULL,
 [ReceivedDateTime] datetime NULL,
 [AnalysisStartedDateTime] datetime NULL,
 [AnalysisCompletedDateTime] datetime NULL,
 [ReviewedDateTime] datetime NULL,
 [ApprovedDateTime] datetime NULL,
 [SubmittedBy] nvarchar(150) NULL,
 [SubmittedDate] datetime NULL,
 [ReviewedDate] datetime NULL,
 [ApprovedDate] datetime NULL,
 [RejectedBy] nvarchar(150) NULL,
 [RejectedDate] datetime NULL,
 [Remarks] nvarchar(max) NULL,
 [ReceivedBy] nvarchar(100) NULL,
 [ContainerCount] int NULL,
 [SampleVolume] nvarchar(50) NULL,
 [ContainerCondition] nvarchar(50) NULL,
 [ReceiptTemperature] nvarchar(50) NULL,
 [ReceiptDecision] nvarchar(30) NULL,
 [ReceiptDeviationReason] nvarchar(max) NULL,
 [WaterIncubationProgram] nvarchar(200) NULL,
 [WaterIncubatorID] nvarchar(100) NULL,
 [PointCodeSnapshot] nvarchar(50) NULL,
 [PointNameSnapshot] nvarchar(200) NULL,
 [PointLocationSnapshot] nvarchar(300) NULL,
 [IncubationStartedDateTime] datetime NULL,
 [IncubationCompletedDateTime] datetime NULL
);
CREATE TABLE dbo.[SampleTests](
 [SampleTestID] int IDENTITY(1,1) NOT NULL,
 [SampleID] int NULL,
 [TestID] int NULL,
 [ResultValue] decimal(10,2) NULL,
 [IsPass] bit NULL,
 [Remarks] nvarchar(500) NULL,
 [ResultEnteredDate] datetime NULL,
 [EnteredBy] nvarchar(100) NULL,
 [ResultStatus] nvarchar(30) NULL,
 [DeviationType] nvarchar(50) NULL,
 [LimitDescription] nvarchar(500) NULL,
 [TextResult] nvarchar(250) NULL,
 [Unit] nvarchar(50) NULL,
 [TestNameSnapshot] nvarchar(200) NULL,
 [TestCategorySnapshot] nvarchar(100) NULL,
 [UnitSnapshot] nvarchar(50) NULL,
 [AlertLimitSnapshot] decimal(18,4) NULL,
 [ActionLimitSnapshot] decimal(18,4) NULL,
 [ResultRowVersion] timestamp NOT NULL
);
CREATE TABLE dbo.[WaterSamplingPoints](
 [Id] int IDENTITY(1,1) NOT NULL,
 [PointCode] nvarchar(50) NOT NULL,
 [PointName] nvarchar(150) NOT NULL,
 [WaterType] nvarchar(100) NOT NULL,
 [Location] nvarchar(150) NOT NULL,
 [Status] nvarchar(50) NOT NULL,
 [CreatedAt] datetime NOT NULL
);
CREATE TABLE dbo.[Tests](
 [TestID] int IDENTITY(1,1) NOT NULL,
 [TestCode] nvarchar(50) NULL,
 [TestName] nvarchar(200) NULL,
 [TestCategory] nvarchar(20) NULL,
 [Pharmacopoeia] nvarchar(50) NULL,
 [AlertLimit] decimal(10,2) NULL,
 [ActionLimit] decimal(10,2) NULL,
 [Unit] nvarchar(20) NULL,
 [IsWaterTest] bit NULL DEFAULT ((1)),
 [IsEnvironmentTest] bit NULL DEFAULT ((0)),
 [SortOrder] int NULL DEFAULT ((0)),
 [IsActive] bit NOT NULL DEFAULT ((1))
);
CREATE TABLE dbo.[Water_Plans](
 [WaterPlanID] int IDENTITY(1,1) NOT NULL,
 [PlanNo] nvarchar(50) NOT NULL,
 [WaterType] nvarchar(30) NOT NULL,
 [SourceType] nvarchar(20) NOT NULL,
 [Frequency] nvarchar(30) NULL,
 [DueDate] date NOT NULL,
 [RequiredDate] date NOT NULL,
 [AnalysisProfile] nvarchar(40) NOT NULL,
 [Status] nvarchar(40) NOT NULL DEFAULT (N'Planned'),
 [Notes] nvarchar(max) NULL,
 [CreatedBy] nvarchar(100) NOT NULL,
 [CreatedAt] datetime2(0) NOT NULL DEFAULT (sysdatetime()),
 [DistributedBy] nvarchar(100) NULL,
 [DistributedAt] datetime2(0) NULL,
 [CancelledBy] nvarchar(100) NULL,
 [CancelledAt] datetime2(0) NULL,
 [CancellationReason] nvarchar(max) NULL
);
CREATE TABLE dbo.[Water_PlanSampleAttempts](
 [AttemptID] int IDENTITY(1,1) NOT NULL,
 [WaterPlanSampleID] int NOT NULL,
 [SampleID] int NOT NULL,
 [SampleNumber] nvarchar(50) NOT NULL,
 [Outcome] nvarchar(40) NOT NULL,
 [LinkedBy] nvarchar(100) NOT NULL,
 [LinkedAt] datetime2(0) NOT NULL DEFAULT (sysdatetime())
);
CREATE TABLE dbo.[Water_PlanSamples](
 [WaterPlanSampleID] int IDENTITY(1,1) NOT NULL,
 [WaterPlanID] int NOT NULL,
 [PointID] int NOT NULL,
 [PointCode] nvarchar(50) NOT NULL,
 [PointName] nvarchar(200) NULL,
 [Location] nvarchar(300) NULL,
 [Status] nvarchar(40) NOT NULL DEFAULT (N'Planned'),
 [SampleID] int NULL,
 [SampleNumber] nvarchar(50) NULL,
 [RegisteredAt] datetime2(0) NULL,
 [CreatedAt] datetime2(0) NOT NULL DEFAULT (sysdatetime()),
 [AnalysisProfile] nvarchar(40) NULL
);
CREATE TABLE dbo.[Water_PlanSampleTests](
 [WaterPlanSampleID] int NOT NULL,
 [TestID] int NOT NULL,
 [CreatedAt] datetime2(0) NOT NULL DEFAULT (sysdatetime())
);
CREATE TABLE dbo.[WaterSpecifications](
 [SpecificationID] int IDENTITY(1,1) NOT NULL,
 [ProfileCode] nvarchar(20) NOT NULL,
 [TestID] int NOT NULL,
 [PointCode] nvarchar(50) NULL,
 [LowerLimit] decimal(18,6) NULL,
 [UpperLimit] decimal(18,6) NULL,
 [AlertLimit] decimal(18,6) NULL,
 [ActionLimit] decimal(18,6) NULL,
 [SpecificationText] nvarchar(500) NOT NULL,
 [EffectiveFrom] date NULL,
 [EffectiveTo] date NULL,
 [ApprovalStatus] nvarchar(30) NOT NULL DEFAULT (N'Draft'),
 [IsActive] bit NOT NULL DEFAULT ((0)),
 [ReviewedBy] nvarchar(100) NULL,
 [ReviewedAt] datetime2(0) NULL,
 [ApprovedBy] nvarchar(100) NULL,
 [ApprovedAt] datetime2(0) NULL,
 [CreatedBy] nvarchar(100) NOT NULL,
 [CreatedAt] datetime2(0) NOT NULL DEFAULT (sysutcdatetime()),
 [ProfileID] int NULL
);
CREATE TABLE dbo.[WaterTestProfiles](
 [ProfileID] int IDENTITY(1,1) NOT NULL,
 [ProfileCode] nvarchar(20) NOT NULL,
 [ProfileName] nvarchar(150) NOT NULL,
 [VersionNo] int NOT NULL DEFAULT ((1)),
 [ApprovalStatus] nvarchar(30) NOT NULL DEFAULT (N'Draft'),
 [EffectiveFrom] date NULL,
 [EffectiveTo] date NULL,
 [IsActive] bit NOT NULL DEFAULT ((0)),
 [ReviewedBy] nvarchar(100) NULL,
 [ReviewedAt] datetime2(0) NULL,
 [ApprovedBy] nvarchar(100) NULL,
 [ApprovedAt] datetime2(0) NULL,
 [CreatedBy] nvarchar(100) NOT NULL,
 [CreatedAt] datetime2(0) NOT NULL DEFAULT (sysutcdatetime()),
 [ControlledReference] nvarchar(300) NULL
);
ALTER TABLE dbo.[Samples] ADD PRIMARY KEY([SampleID]);
ALTER TABLE dbo.[Tests] ADD PRIMARY KEY([TestID]);
ALTER TABLE dbo.[WaterSamplingPoints] ADD PRIMARY KEY([Id]);
ALTER TABLE dbo.[WaterTestProfiles] ADD PRIMARY KEY([ProfileID]);
ALTER TABLE dbo.[Water_Plans] ADD PRIMARY KEY([WaterPlanID]);
ALTER TABLE dbo.[Water_PlanSamples] ADD PRIMARY KEY([WaterPlanSampleID]);
CREATE UNIQUE INDEX UX_SampleNumber ON dbo.Samples(SampleNumber);
CREATE UNIQUE INDEX UX_WaterSequence ON dbo.LIMS_NumberSequences(SequenceKey);
CREATE TABLE dbo.Users(Username nvarchar(100) PRIMARY KEY,Role nvarchar(50),IsActive bit,IsLocked bit,MustChangePassword bit,CanRegisterSamples bit,LockedUntil datetime2 NULL,AuthenticationRowVersion rowversion);
EXEC(N'CREATE TRIGGER dbo.TRG_AuditTrail_AppendOnly ON dbo.AuditTrail AFTER UPDATE,DELETE AS BEGIN THROW 56999,''Append-only fixture protection'',1; END');
EXEC(N'CREATE TRIGGER dbo.TRG_ElectronicSignatures_AppendOnly ON dbo.ElectronicSignatures AFTER UPDATE,DELETE AS BEGIN THROW 56999,''Append-only fixture protection'',1; END');
EXEC(N'CREATE   TRIGGER dbo.TRG_Water_PlanSampleTests_FreezeDistributed_20260828
    ON dbo.Water_PlanSampleTests
    AFTER INSERT, UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        IF EXISTS
        (
            SELECT 1
            FROM
            (
                SELECT WaterPlanSampleID FROM inserted
                UNION
                SELECT WaterPlanSampleID FROM deleted
            ) changed
            INNER JOIN dbo.Water_PlanSamples samplePlan ON samplePlan.WaterPlanSampleID=changed.WaterPlanSampleID
            INNER JOIN dbo.Water_Plans planRecord ON planRecord.WaterPlanID=samplePlan.WaterPlanID
            WHERE UPPER(LTRIM(RTRIM(ISNULL(planRecord.Status,N''''))))<>N''PLANNED''
        )
            THROW 53524, ''Water Plan test assignments are frozen after Distribution. Create a controlled replacement plan instead.'', 1;
    END;');
EXEC(N'CREATE   TRIGGER dbo.TRG_Water_PlanSamples_ProtectDistributed_20260828
    ON dbo.Water_PlanSamples
    AFTER UPDATE, DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        IF EXISTS
        (
            SELECT 1
            FROM deleted oldRow
            INNER JOIN dbo.Water_Plans planRecord ON planRecord.WaterPlanID=oldRow.WaterPlanID
            LEFT JOIN inserted newRow ON newRow.WaterPlanSampleID=oldRow.WaterPlanSampleID
            WHERE UPPER(LTRIM(RTRIM(ISNULL(planRecord.Status,N''''))))<>N''PLANNED''
              AND
              (
                    newRow.WaterPlanSampleID IS NULL
                 OR ISNULL(newRow.WaterPlanID,-2147483648)<>ISNULL(oldRow.WaterPlanID,-2147483648)
                 OR ISNULL(newRow.PointID,-2147483648)<>ISNULL(oldRow.PointID,-2147483648)
                 OR ISNULL(newRow.PointCode,N''<NULL>'')<>ISNULL(oldRow.PointCode,N''<NULL>'')
                 OR ISNULL(newRow.PointName,N''<NULL>'')<>ISNULL(oldRow.PointName,N''<NULL>'')
                 OR ISNULL(newRow.Location,N''<NULL>'')<>ISNULL(oldRow.Location,N''<NULL>'')
                 OR ISNULL(newRow.AnalysisProfile,N''<NULL>'')<>ISNULL(oldRow.AnalysisProfile,N''<NULL>'')
              )
        )
            THROW 53525, ''Distributed Water Plan point/instruction identity is immutable. Create a controlled replacement plan instead.'', 1;
    END;');

ALTER TABLE dbo.Users ADD CanEnterResults bit NOT NULL DEFAULT(0),CanReviewResults bit NOT NULL DEFAULT(0),CanApproveResults bit NOT NULL DEFAULT(0);
ALTER TABLE dbo.SampleTests ADD CONSTRAINT PK_AnalysisSampleTest PRIMARY KEY(SampleTestID);
ALTER TABLE dbo.SampleTests ALTER COLUMN ResultValue decimal(18,4) NULL;

-- Source: 20261008_001_Lab_Equipment_Foundation.sql
CREATE TABLE dbo.LabEquipment
    (
        EquipmentID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipment PRIMARY KEY,
        EquipmentCode NVARCHAR(50) NOT NULL,
        EquipmentName NVARCHAR(200) NOT NULL,
        EquipmentType NVARCHAR(100) NOT NULL,
        Department NVARCHAR(100) NULL,
        Location NVARCHAR(200) NULL,
        Manufacturer NVARCHAR(150) NULL,
        Model NVARCHAR(100) NULL,
        SerialNumber NVARCHAR(100) NULL,
        GmpCritical BIT NOT NULL CONSTRAINT DF_LabEquipment_GmpCritical DEFAULT(1),
        EquipmentStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_LabEquipment_Status DEFAULT(N'Active'),
        QualificationStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_LabEquipment_Qualification DEFAULT(N'Not Qualified'),
        LastQualificationDate DATE NULL,
        NextQualificationDate DATE NULL,
        CalibrationRequired BIT NOT NULL CONSTRAINT DF_LabEquipment_CalibrationRequired DEFAULT(1),
        CalibrationStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_LabEquipment_CalibrationStatus DEFAULT(N'Not Calibrated'),
        LastCalibrationDate DATE NULL,
        NextCalibrationDate DATE NULL,
        MethodReference NVARCHAR(200) NULL,
        Notes NVARCHAR(500) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_LabEquipment_IsActive DEFAULT(1),
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipment_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedBy NVARCHAR(100) NULL,
        UpdatedAt DATETIME2(0) NULL,
        RowVersion ROWVERSION NOT NULL,
        CONSTRAINT UQ_LabEquipment_EquipmentCode UNIQUE(EquipmentCode),
        CONSTRAINT CK_LabEquipment_Status CHECK(EquipmentStatus IN
            (N'Active',N'Out of Service',N'Under Maintenance',N'Quarantine',N'Retired')),
        CONSTRAINT CK_LabEquipment_QualificationStatus CHECK(QualificationStatus IN
            (N'Qualified',N'Due Soon',N'Expired',N'Not Qualified',N'Not Required')),
        CONSTRAINT CK_LabEquipment_CalibrationStatus CHECK(CalibrationStatus IN
            (N'Calibrated',N'Due Soon',N'Expired',N'Not Calibrated',N'Not Required')),
        CONSTRAINT CK_LabEquipment_QualificationDates CHECK
            (NextQualificationDate IS NULL OR LastQualificationDate IS NULL OR NextQualificationDate >= LastQualificationDate),
        CONSTRAINT CK_LabEquipment_CalibrationDates CHECK
            (NextCalibrationDate IS NULL OR LastCalibrationDate IS NULL OR NextCalibrationDate >= LastCalibrationDate)
    );

-- Source: 20261008_002_Lab_Equipment_Usage_Traceability.sql
CREATE TABLE dbo.LabEquipmentUsage
    (
        UsageID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipmentUsage PRIMARY KEY,
        Module NVARCHAR(20) NOT NULL,
        ParentRecordID INT NOT NULL,
        ResultRecordID INT NOT NULL,
        EquipmentID INT NOT NULL,
        AssignedBy NVARCHAR(100) NOT NULL,
        AssignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipmentUsage_AssignedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_LabEquipmentUsage_Equipment FOREIGN KEY(EquipmentID) REFERENCES dbo.LabEquipment(EquipmentID),
        CONSTRAINT CK_LabEquipmentUsage_Module CHECK(Module IN(N'WATER',N'EM',N'PRM')),
        CONSTRAINT UQ_LabEquipmentUsage_ModuleResult UNIQUE(Module,ResultRecordID)
    );

-- Source: 20261008_002_Lab_Equipment_Usage_Traceability.sql
CREATE TABLE dbo.LabEquipmentUsageHistory
    (
        HistoryID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipmentUsageHistory PRIMARY KEY,
        Module NVARCHAR(20) NOT NULL,
        ParentRecordID INT NOT NULL,
        ResultRecordID INT NOT NULL,
        PreviousEquipmentID INT NULL,
        EquipmentID INT NULL,
        EquipmentCodeSnapshot NVARCHAR(50) NULL,
        EquipmentNameSnapshot NVARCHAR(200) NULL,
        EquipmentTypeSnapshot NVARCHAR(100) NULL,
        EquipmentStatusSnapshot NVARCHAR(30) NULL,
        QualificationStatusSnapshot NVARCHAR(30) NULL,
        CalibrationStatusSnapshot NVARCHAR(30) NULL,
        NextQualificationDateSnapshot DATE NULL,
        NextCalibrationDateSnapshot DATE NULL,
        ChangeType NVARCHAR(20) NOT NULL,
        MeaningOfSignature NVARCHAR(250) NOT NULL,
        ActionReason NVARCHAR(500) NOT NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipmentUsageHistory_SignedAt DEFAULT SYSUTCDATETIME(),
        SourceWorkstation NVARCHAR(100) NULL,
        CONSTRAINT CK_LabEquipmentUsageHistory_Module CHECK(Module IN(N'WATER',N'EM',N'PRM')),
        CONSTRAINT CK_LabEquipmentUsageHistory_ChangeType CHECK(ChangeType IN(N'ASSIGN',N'REASSIGN',N'CLEAR'))
    );

-- Source: 20261008_003_Equipment_Test_Compatibility.sql
CREATE TABLE dbo.LabEquipmentCompatibilityRules
    (
        RuleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipmentCompatibilityRules PRIMARY KEY,
        Module NVARCHAR(20) NOT NULL,
        MatchKey NVARCHAR(220) NOT NULL,
        EquipmentType NVARCHAR(100) NOT NULL,
        RuleDescription NVARCHAR(500) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_LabEquipmentCompatibilityRules_IsActive DEFAULT(1),
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipmentCompatibilityRules_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_LabEquipmentCompatibilityRules_Module CHECK(Module IN(N'WATER',N'EM',N'PRM')),
        CONSTRAINT UQ_LabEquipmentCompatibilityRules UNIQUE(Module,MatchKey,EquipmentType)
    );

-- Source: 20261009_001_Water_LES_Execution_Evidence.sql
CREATE TABLE dbo.WaterResultExecutionEvidence
    (
        EvidenceID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WaterResultExecutionEvidence PRIMARY KEY,
        SampleID INT NOT NULL,
        SampleTestID INT NOT NULL,
        TestID INT NOT NULL,
        ProcedureReference NVARCHAR(100) NOT NULL,
        MethodGuidanceSnapshot NVARCHAR(1000) NOT NULL,
        SampleTemperatureC DECIMAL(5,2) NULL,
        VerificationReference NVARCHAR(200) NULL,
        VerificationConfirmed BIT NOT NULL,
        ExecutionRemarks NVARCHAR(1000) NULL,
        RawResultSnapshot NVARCHAR(200) NOT NULL,
        EquipmentID INT NOT NULL,
        EquipmentCodeSnapshot NVARCHAR(50) NOT NULL,
        EquipmentNameSnapshot NVARCHAR(200) NOT NULL,
        EquipmentTypeSnapshot NVARCHAR(100) NOT NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        MeaningOfSignature NVARCHAR(250) NOT NULL,
        ActionReason NVARCHAR(500) NOT NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_WaterResultExecutionEvidence_SignedAt DEFAULT SYSUTCDATETIME(),
        SourceWorkstation NVARCHAR(100) NULL,
        CONSTRAINT FK_WaterResultExecutionEvidence_Sample FOREIGN KEY(SampleID) REFERENCES dbo.Samples(SampleID),
        CONSTRAINT FK_WaterResultExecutionEvidence_SampleTest FOREIGN KEY(SampleTestID) REFERENCES dbo.SampleTests(SampleTestID),
        CONSTRAINT FK_WaterResultExecutionEvidence_Equipment FOREIGN KEY(EquipmentID) REFERENCES dbo.LabEquipment(EquipmentID),
        CONSTRAINT CK_WaterResultExecutionEvidence_Verification CHECK(VerificationConfirmed IN(0,1))
    );

-- Source: 20261009_002_Water_Resources_Traceability.sql
CREATE TABLE dbo.WaterResultResourceEvidence
 (
  EvidenceID BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
  BatchID UNIQUEIDENTIFIER NOT NULL,
  SampleID INT NOT NULL REFERENCES dbo.Samples(SampleID),
  SampleTestID INT NOT NULL REFERENCES dbo.SampleTests(SampleTestID),
  TestID INT NOT NULL,
  ResourceKind NVARCHAR(20) NOT NULL,
  EquipmentID INT NULL REFERENCES dbo.LabEquipment(EquipmentID),
  EquipmentCodeSnapshot NVARCHAR(50) NULL,
  EquipmentNameSnapshot NVARCHAR(200) NULL,
  KitCode NVARCHAR(100) NULL,
  KitLot NVARCHAR(100) NULL,
  KitExpiry DATE NULL,
  ResultSnapshot NVARCHAR(200) NOT NULL,
  SignedBy NVARCHAR(100) NOT NULL,
  SignedAt DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
  CONSTRAINT CK_WaterResourceKind CHECK(ResourceKind IN(N'INSTRUMENT',N'TEST_KIT',N'NO_INSTRUMENT')),
  CONSTRAINT CK_WaterResourceDetails CHECK(
   (ResourceKind=N'INSTRUMENT' AND EquipmentID IS NOT NULL AND KitCode IS NULL AND KitLot IS NULL AND KitExpiry IS NULL)
   OR (ResourceKind=N'TEST_KIT' AND EquipmentID IS NULL AND LEN(LTRIM(RTRIM(KitCode)))>0 AND LEN(LTRIM(RTRIM(KitLot)))>0 AND KitExpiry IS NOT NULL)
   OR (ResourceKind=N'NO_INSTRUMENT' AND EquipmentID IS NULL AND KitCode IS NULL AND KitLot IS NULL AND KitExpiry IS NULL))
 );

-- Source: 20260714_001_Harden_GMP_Workflows.sql
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

-- Source: 20260714_001_Harden_GMP_Workflows.sql
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

-- Source: 20260825_006_PRM_Quality_Event_Runtime_Compatibility_Bridge.sql
CREATE TABLE dbo.QualityEventActions
        (
            QualityEventActionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventActions_20260825_006 PRIMARY KEY,
            QualityEventID INT NULL,
            ActionType NVARCHAR(100) NULL,
            ActionDescription NVARCHAR(MAX) NULL,
            PerformedBy NVARCHAR(100) NULL,
            PerformedDate DATETIME2(0) NULL,
            ElectronicSignatureID INT NULL
        );

ALTER TABLE dbo.WaterResultResourceEvidence ADD ExecutionDate date NULL;
CREATE TABLE dbo.Certificates(CertificateID int IDENTITY PRIMARY KEY,SampleID int NOT NULL,CertificateNumber nvarchar(100) NULL,Status nvarchar(50) NULL,IsCancelled bit NOT NULL DEFAULT(0));

-- Legacy sampling-point fallback columns consumed by installed ResultsEntry header query.
CREATE TABLE dbo.SamplingPoints(PointID int NOT NULL PRIMARY KEY,PointCode nvarchar(50) NULL);
