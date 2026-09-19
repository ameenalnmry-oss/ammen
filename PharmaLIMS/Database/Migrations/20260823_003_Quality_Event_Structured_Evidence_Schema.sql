SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
        THROW 53300, 'Required table dbo.QualityEvents is missing.', 1;

    IF OBJECT_ID(N'dbo.ElectronicSignatures', N'U') IS NULL
        THROW 53301, 'Required table dbo.ElectronicSignatures is missing.', 1;

    IF OBJECT_ID(N'dbo.QualityEventSignatures', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventSignatures
        (
            QualityEventSignatureID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventSignatures PRIMARY KEY,
            QualityEventID INT NOT NULL,
            SignatureID INT NOT NULL,
            SignedBy NVARCHAR(100) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventSignatures_SignedAt DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_QualityEventSignatures_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT FK_QualityEventSignatures_Signature FOREIGN KEY(SignatureID) REFERENCES dbo.ElectronicSignatures(SignatureID),
            CONSTRAINT UQ_QualityEventSignatures_Signature UNIQUE(SignatureID)
        );
        CREATE INDEX IX_QualityEventSignatures_Event
            ON dbo.QualityEventSignatures(QualityEventID, SignedAt, QualityEventSignatureID);
    END;

    IF OBJECT_ID(N'dbo.QualityEventPrintHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventPrintHistory
        (
            QualityEventPrintID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventPrintHistory PRIMARY KEY,
            QualityEventID INT NOT NULL,
            PrintedBy NVARCHAR(100) NOT NULL,
            PrintedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventPrintHistory_Date DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_QualityEventPrintHistory_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
        CREATE INDEX IX_QualityEventPrintHistory_Event
            ON dbo.QualityEventPrintHistory(QualityEventID, PrintedDate, QualityEventPrintID);
    END;

    IF OBJECT_ID(N'dbo.QualityEventActions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventActions
        (
            QualityEventActionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventActions PRIMARY KEY,
            QualityEventID INT NOT NULL,
            ActionType NVARCHAR(100) NOT NULL,
            ActionDescription NVARCHAR(MAX) NOT NULL,
            PerformedBy NVARCHAR(100) NOT NULL,
            PerformedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventActions_Date DEFAULT SYSUTCDATETIME(),
            ElectronicSignatureID INT NULL,
            CONSTRAINT FK_QualityEventActions_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT FK_QualityEventActions_Signature FOREIGN KEY(ElectronicSignatureID) REFERENCES dbo.ElectronicSignatures(SignatureID)
        );
        CREATE INDEX IX_QualityEventActions_Event ON dbo.QualityEventActions(QualityEventID, PerformedDate, QualityEventActionID);
    END;

    IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventChecklistQuestions
        (
            QuestionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventChecklistQuestions PRIMARY KEY,
            SectionName NVARCHAR(200) NOT NULL,
            QuestionText NVARCHAR(MAX) NOT NULL,
            AppliesToEventType NVARCHAR(100) NULL,
            AppliesToSampleType NVARCHAR(100) NULL,
            AppliesToTestCategory NVARCHAR(150) NULL,
            AppliesToTestNameKeyword NVARCHAR(200) NULL,
            AnswerType NVARCHAR(50) NOT NULL CONSTRAINT DF_QualityEventChecklistQuestions_AnswerType_20260823 DEFAULT N'YesNoNA',
            IsRequired BIT NOT NULL CONSTRAINT DF_QualityEventChecklistQuestions_Required_20260823 DEFAULT (1),
            ExpectedAnswer NVARCHAR(20) NULL CONSTRAINT DF_QualityEventChecklistQuestions_Expected_20260823 DEFAULT N'Yes',
            QuestionLogic NVARCHAR(50) NULL CONSTRAINT DF_QualityEventChecklistQuestions_Logic_20260823 DEFAULT N'PositiveCheck',
            SortOrder INT NOT NULL,
            IsActive BIT NOT NULL CONSTRAINT DF_QualityEventChecklistQuestions_Active_20260823 DEFAULT (1),
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventChecklistQuestions_Created_20260823 DEFAULT SYSUTCDATETIME()
        );
        CREATE INDEX IX_QualityEventChecklistQuestions_Profile
            ON dbo.QualityEventChecklistQuestions(IsActive, AppliesToSampleType, AppliesToTestCategory, SortOrder);
    END;

    IF OBJECT_ID(N'dbo.QualityEventChecklistAnswers', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventChecklistAnswers
        (
            AnswerID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventChecklistAnswers PRIMARY KEY,
            QualityEventID INT NOT NULL,
            QuestionID INT NOT NULL,
            AnswerValue NVARCHAR(250) NULL,
            Comments NVARCHAR(MAX) NULL,
            AnsweredBy NVARCHAR(100) NOT NULL,
            AnsweredDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventChecklistAnswers_Date DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_QualityEventChecklistAnswers_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT FK_QualityEventChecklistAnswers_Question FOREIGN KEY(QuestionID) REFERENCES dbo.QualityEventChecklistQuestions(QuestionID),
            CONSTRAINT UQ_QualityEventChecklistAnswers UNIQUE(QualityEventID, QuestionID)
        );
    END;

    IF OBJECT_ID(N'dbo.QualityEventRootCauseWhys', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventRootCauseWhys
        (
            WhyID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventRootCauseWhys PRIMARY KEY,
            QualityEventID INT NOT NULL,
            WhyLevel INT NOT NULL,
            Question NVARCHAR(500) NULL,
            Answer NVARCHAR(MAX) NULL,
            CreatedBy NVARCHAR(100) NOT NULL,
            ModifiedBy NVARCHAR(100) NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventRootCauseWhys_Created DEFAULT SYSUTCDATETIME(),
            ModifiedAt DATETIME2(0) NULL,
            CONSTRAINT FK_QualityEventRootCauseWhys_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
            CONSTRAINT UQ_QualityEventRootCauseWhys_Level UNIQUE(QualityEventID, WhyLevel)
        );
    END;

    IF OBJECT_ID(N'dbo.QualityEventImpactAssessments', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventImpactAssessments
        (
            ImpactID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventImpactAssessments PRIMARY KEY,
            QualityEventID INT NOT NULL,
            AssessmentArea NVARCHAR(200) NOT NULL,
            Finding NVARCHAR(MAX) NULL,
            ImpactStatus NVARCHAR(100) NULL,
            CreatedBy NVARCHAR(100) NOT NULL,
            ModifiedBy NVARCHAR(100) NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventImpactAssessments_Created DEFAULT SYSUTCDATETIME(),
            ModifiedAt DATETIME2(0) NULL,
            CONSTRAINT FK_QualityEventImpactAssessments_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
    END;

    IF OBJECT_ID(N'dbo.QualityEventCAPAItems', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventCAPAItems
        (
            CAPAItemID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventCAPAItems PRIMARY KEY,
            QualityEventID INT NOT NULL,
            ActionDescription NVARCHAR(MAX) NOT NULL,
            ActionType NVARCHAR(100) NULL,
            Responsible NVARCHAR(200) NULL,
            DueDate DATE NULL,
            EffectivenessCheck NVARCHAR(MAX) NULL,
            CAPAStatus NVARCHAR(100) NULL,
            CreatedBy NVARCHAR(100) NOT NULL,
            ModifiedBy NVARCHAR(100) NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventCAPAItems_Created DEFAULT SYSUTCDATETIME(),
            ModifiedAt DATETIME2(0) NULL,
            CONSTRAINT FK_QualityEventCAPAItems_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
    END;

    IF OBJECT_ID(N'dbo.QualityEventRetesting', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventRetesting
        (
            RetestingID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventRetesting PRIMARY KEY,
            QualityEventID INT NOT NULL,
            RetestPerformed NVARCHAR(100) NULL,
            SampleAliquot NVARCHAR(200) NULL,
            Analyst NVARCHAR(200) NULL,
            RetestResult NVARCHAR(MAX) NULL,
            Justification NVARCHAR(MAX) NULL,
            ScientificBasis NVARCHAR(MAX) NULL,
            DispositionUse NVARCHAR(MAX) NULL,
            CreatedBy NVARCHAR(100) NOT NULL,
            ModifiedBy NVARCHAR(100) NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventRetesting_Created DEFAULT SYSUTCDATETIME(),
            ModifiedAt DATETIME2(0) NULL,
            CONSTRAINT FK_QualityEventRetesting_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
    END;

    IF OBJECT_ID(N'dbo.QualityEventDistribution', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventDistribution
        (
            DistributionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventDistribution PRIMARY KEY,
            QualityEventID INT NOT NULL,
            Department NVARCHAR(200) NOT NULL,
            Recipient NVARCHAR(200) NULL,
            DateReceived DATE NULL,
            CreatedBy NVARCHAR(100) NOT NULL,
            ModifiedBy NVARCHAR(100) NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventDistribution_Created DEFAULT SYSUTCDATETIME(),
            ModifiedAt DATETIME2(0) NULL,
            CONSTRAINT FK_QualityEventDistribution_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
    END;

    IF OBJECT_ID(N'dbo.QualityEventRelatedItems', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.QualityEventRelatedItems
        (
            RelatedItemID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventRelatedItems PRIMARY KEY,
            QualityEventID INT NOT NULL,
            RelatedModule NVARCHAR(100) NOT NULL,
            RelatedRecordID INT NULL,
            RelatedRecordNo NVARCHAR(120) NULL,
            RelationshipType NVARCHAR(100) NULL,
            CreatedBy NVARCHAR(100) NOT NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventRelatedItems_Created DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_QualityEventRelatedItems_Event FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)
        );
        CREATE INDEX IX_QualityEventRelatedItems_Record
            ON dbo.QualityEventRelatedItems(RelatedModule, RelatedRecordID, RelatedRecordNo);
    END;

    /* These records are compliance evidence and must remain append-only. */
    EXEC sys.sp_executesql N'
        CREATE OR ALTER TRIGGER dbo.TRG_QualityEventSignatures_AppendOnly
        ON dbo.QualityEventSignatures
        AFTER UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 51050, ''Controlled PharmaLIMS compliance records are append-only and cannot be updated or deleted.'', 1;
        END;';

    EXEC sys.sp_executesql N'
        CREATE OR ALTER TRIGGER dbo.TRG_QualityEventPrintHistory_AppendOnly
        ON dbo.QualityEventPrintHistory
        AFTER UPDATE, DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 51050, ''Controlled PharmaLIMS compliance records are append-only and cannot be updated or deleted.'', 1;
        END;';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
