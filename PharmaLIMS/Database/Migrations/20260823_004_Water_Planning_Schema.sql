SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Samples', N'U') IS NULL
       OR OBJECT_ID(N'dbo.SampleTests', N'U') IS NULL
       OR OBJECT_ID(N'dbo.Tests', N'U') IS NULL
       OR OBJECT_ID(N'dbo.WaterSamplingPoints', N'U') IS NULL
        THROW 53400, 'Required Water planning dependency is missing.', 1;

    IF OBJECT_ID(N'dbo.Water_Plans', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Water_Plans
        (
            WaterPlanID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Water_Plans PRIMARY KEY,
            PlanNo NVARCHAR(50) NOT NULL,
            WaterType NVARCHAR(30) NOT NULL,
            SourceType NVARCHAR(20) NOT NULL,
            Frequency NVARCHAR(30) NULL,
            DueDate DATE NOT NULL,
            RequiredDate DATE NOT NULL,
            AnalysisProfile NVARCHAR(40) NOT NULL,
            Status NVARCHAR(40) NOT NULL CONSTRAINT DF_Water_Plans_Status DEFAULT N'Planned',
            Notes NVARCHAR(MAX) NULL,
            CreatedBy NVARCHAR(100) NOT NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_Plans_CreatedAt DEFAULT SYSDATETIME(),
            DistributedBy NVARCHAR(100) NULL,
            DistributedAt DATETIME2(0) NULL,
            CancelledBy NVARCHAR(100) NULL,
            CancelledAt DATETIME2(0) NULL,
            CancellationReason NVARCHAR(MAX) NULL
        );
        CREATE UNIQUE INDEX UX_Water_Plans_PlanNo ON dbo.Water_Plans(PlanNo);
    END;

    IF OBJECT_ID(N'dbo.Water_PlanSamples', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Water_PlanSamples
        (
            WaterPlanSampleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Water_PlanSamples PRIMARY KEY,
            WaterPlanID INT NOT NULL,
            PointID INT NOT NULL,
            PointCode NVARCHAR(50) NOT NULL,
            PointName NVARCHAR(200) NULL,
            Location NVARCHAR(300) NULL,
            AnalysisProfile NVARCHAR(40) NULL,
            Status NVARCHAR(40) NOT NULL CONSTRAINT DF_Water_PlanSamples_Status DEFAULT N'Planned',
            SampleID INT NULL,
            SampleNumber NVARCHAR(50) NULL,
            RegisteredAt DATETIME2(0) NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_PlanSamples_CreatedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_Water_PlanSamples_Plans FOREIGN KEY(WaterPlanID) REFERENCES dbo.Water_Plans(WaterPlanID),
            CONSTRAINT UQ_Water_PlanSamples_PlanPoint UNIQUE(WaterPlanID, PointID)
        );
        CREATE INDEX IX_Water_PlanSamples_Status ON dbo.Water_PlanSamples(WaterPlanID,Status);
    END;

    IF COL_LENGTH(N'dbo.Water_PlanSamples', N'AnalysisProfile') IS NULL
        ALTER TABLE dbo.Water_PlanSamples ADD AnalysisProfile NVARCHAR(40) NULL;

    IF OBJECT_ID(N'dbo.Water_PlanSampleTests', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Water_PlanSampleTests
        (
            WaterPlanSampleID INT NOT NULL,
            TestID INT NOT NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_PlanSampleTests_CreatedAt DEFAULT SYSDATETIME(),
            CONSTRAINT PK_Water_PlanSampleTests PRIMARY KEY(WaterPlanSampleID,TestID),
            CONSTRAINT FK_Water_PlanSampleTests_Samples FOREIGN KEY(WaterPlanSampleID) REFERENCES dbo.Water_PlanSamples(WaterPlanSampleID) ON DELETE CASCADE
        );
    END;

    IF OBJECT_ID(N'dbo.Water_PlanSampleAttempts', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Water_PlanSampleAttempts
        (
            AttemptID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Water_PlanSampleAttempts PRIMARY KEY,
            WaterPlanSampleID INT NOT NULL,
            SampleID INT NOT NULL,
            SampleNumber NVARCHAR(50) NOT NULL,
            Outcome NVARCHAR(40) NOT NULL,
            LinkedBy NVARCHAR(100) NOT NULL,
            LinkedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_PlanSampleAttempts_LinkedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_Water_PlanSampleAttempts_PlanSample FOREIGN KEY(WaterPlanSampleID) REFERENCES dbo.Water_PlanSamples(WaterPlanSampleID)
        );
    END;

    IF OBJECT_ID(N'dbo.Water_PlanSignatures', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Water_PlanSignatures
        (
            SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Water_PlanSignatures PRIMARY KEY,
            WaterPlanID INT NOT NULL,
            ActionType NVARCHAR(100) NOT NULL,
            ActionReason NVARCHAR(MAX) NULL,
            SignedBy NVARCHAR(100) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            MeaningOfSignature NVARCHAR(255) NULL,
            SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_PlanSignatures_SignedAt DEFAULT SYSDATETIME(),
            CONSTRAINT FK_Water_PlanSignatures_Plans FOREIGN KEY(WaterPlanID) REFERENCES dbo.Water_Plans(WaterPlanID)
        );
    END;

    IF COL_LENGTH(N'dbo.Samples',N'PointCodeSnapshot') IS NULL
        ALTER TABLE dbo.Samples ADD PointCodeSnapshot NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.Samples',N'PointNameSnapshot') IS NULL
        ALTER TABLE dbo.Samples ADD PointNameSnapshot NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.Samples',N'PointLocationSnapshot') IS NULL
        ALTER TABLE dbo.Samples ADD PointLocationSnapshot NVARCHAR(300) NULL;

    IF COL_LENGTH(N'dbo.SampleTests',N'TestNameSnapshot') IS NULL
        ALTER TABLE dbo.SampleTests ADD TestNameSnapshot NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.SampleTests',N'TestCategorySnapshot') IS NULL
        ALTER TABLE dbo.SampleTests ADD TestCategorySnapshot NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.SampleTests',N'UnitSnapshot') IS NULL
        ALTER TABLE dbo.SampleTests ADD UnitSnapshot NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.SampleTests',N'AlertLimitSnapshot') IS NULL
        ALTER TABLE dbo.SampleTests ADD AlertLimitSnapshot DECIMAL(18,4) NULL;
    IF COL_LENGTH(N'dbo.SampleTests',N'ActionLimitSnapshot') IS NULL
        ALTER TABLE dbo.SampleTests ADD ActionLimitSnapshot DECIMAL(18,4) NULL;
    IF COL_LENGTH(N'dbo.SampleTests',N'ResultStatus') IS NULL
        ALTER TABLE dbo.SampleTests ADD ResultStatus NVARCHAR(30) NULL;
    IF COL_LENGTH(N'dbo.SampleTests',N'DeviationType') IS NULL
        ALTER TABLE dbo.SampleTests ADD DeviationType NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.SampleTests',N'LimitDescription') IS NULL
        ALTER TABLE dbo.SampleTests ADD LimitDescription NVARCHAR(300) NULL;

    /* Compile the backfill after any additive columns above are materialized. */
    EXEC sys.sp_executesql N'
        UPDATE st
        SET TestNameSnapshot=COALESCE(st.TestNameSnapshot,t.TestName),
            TestCategorySnapshot=COALESCE(st.TestCategorySnapshot,t.TestCategory),
            UnitSnapshot=COALESCE(st.UnitSnapshot,t.Unit),
            AlertLimitSnapshot=COALESCE(st.AlertLimitSnapshot,t.AlertLimit),
            ActionLimitSnapshot=COALESCE(st.ActionLimitSnapshot,t.ActionLimit)
        FROM dbo.SampleTests st
        INNER JOIN dbo.Tests t ON t.TestID=st.TestID
        WHERE st.TestNameSnapshot IS NULL
           OR st.TestCategorySnapshot IS NULL
           OR st.UnitSnapshot IS NULL;';

    EXEC sys.sp_executesql N'
        UPDATE s
        SET PointCodeSnapshot=COALESCE(s.PointCodeSnapshot,w.PointCode),
            PointNameSnapshot=COALESCE(s.PointNameSnapshot,w.PointName),
            PointLocationSnapshot=COALESCE(s.PointLocationSnapshot,w.Location)
        FROM dbo.Samples s
        INNER JOIN dbo.WaterSamplingPoints w ON w.Id=s.PointID
        WHERE s.PointCodeSnapshot IS NULL
           OR s.PointNameSnapshot IS NULL
           OR s.PointLocationSnapshot IS NULL;';

    EXEC sys.sp_executesql N'
        CREATE OR ALTER TRIGGER dbo.TRG_Water_PlanSignatures_AppendOnly
        ON dbo.Water_PlanSignatures
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
