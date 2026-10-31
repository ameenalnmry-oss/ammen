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
