SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.LIMS_SchemaVersions',N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.LIMS_SchemaVersions
        (
            VersionKey NVARCHAR(100) NOT NULL CONSTRAINT PK_LIMS_SchemaVersions PRIMARY KEY,
            Description NVARCHAR(500) NOT NULL,
            MigrationChecksum NVARCHAR(128) NULL,
            ApplicationVersion NVARCHAR(50) NULL,
            AppliedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LIMS_SchemaVersions_AppliedAt DEFAULT SYSDATETIME(),
            AppliedBy NVARCHAR(128) NOT NULL CONSTRAINT DF_LIMS_SchemaVersions_AppliedBy DEFAULT SUSER_SNAME()
        );
    END;

    IF COL_LENGTH(N'dbo.LIMS_SchemaVersions',N'MigrationChecksum') IS NULL
        ALTER TABLE dbo.LIMS_SchemaVersions ADD MigrationChecksum NVARCHAR(128) NULL;
    IF COL_LENGTH(N'dbo.LIMS_SchemaVersions',N'ApplicationVersion') IS NULL
        ALTER TABLE dbo.LIMS_SchemaVersions ADD ApplicationVersion NVARCHAR(50) NULL;

    IF OBJECT_ID(N'dbo.EM_Schedules',N'U') IS NULL
        THROW 51300, 'Required table dbo.EM_Schedules does not exist.', 1;

    IF COL_LENGTH(N'dbo.EM_Schedules',N'ReviewedBy') IS NULL ALTER TABLE dbo.EM_Schedules ADD ReviewedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.EM_Schedules',N'ReviewedAt') IS NULL ALTER TABLE dbo.EM_Schedules ADD ReviewedAt DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovedBy') IS NULL ALTER TABLE dbo.EM_Schedules ADD ApprovedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovedAt') IS NULL ALTER TABLE dbo.EM_Schedules ADD ApprovedAt DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_Schedules',N'MediaPreparationID') IS NULL ALTER TABLE dbo.EM_Schedules ADD MediaPreparationID INT NULL;
    IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovedPointCount') IS NULL ALTER TABLE dbo.EM_Schedules ADD ApprovedPointCount INT NULL;
    IF COL_LENGTH(N'dbo.EM_Schedules',N'ApprovalStatus') IS NULL
        ALTER TABLE dbo.EM_Schedules ADD ApprovalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_EM_Schedules_ApprovalStatus_20260722 DEFAULT N'Draft';
    IF COL_LENGTH(N'dbo.EM_Schedules',N'VersionNo') IS NULL
        ALTER TABLE dbo.EM_Schedules ADD VersionNo INT NOT NULL CONSTRAINT DF_EM_Schedules_VersionNo_20260722 DEFAULT(1);
    IF COL_LENGTH(N'dbo.EM_Schedules',N'EffectiveFrom') IS NULL ALTER TABLE dbo.EM_Schedules ADD EffectiveFrom DATE NULL;

    IF OBJECT_ID(N'dbo.EM_ScheduleSignatures',N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EM_ScheduleSignatures
        (
            SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_ScheduleSignatures PRIMARY KEY,
            ScheduleID INT NOT NULL,
            ActionType NVARCHAR(100) NOT NULL,
            ActionReason NVARCHAR(MAX) NULL,
            SignedBy NVARCHAR(100) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_ScheduleSignatures_SignedAt_20260722 DEFAULT SYSDATETIME(),
            CONSTRAINT FK_EM_ScheduleSignatures_Schedules_20260722 FOREIGN KEY(ScheduleID) REFERENCES dbo.EM_Schedules(ScheduleID)
        );
        CREATE INDEX IX_EM_ScheduleSignatures_Schedule_20260722 ON dbo.EM_ScheduleSignatures(ScheduleID,SignedAt DESC);
    END;

    IF OBJECT_ID(N'dbo.EM_PlanSamples',N'U') IS NULL
        THROW 51301, 'Required table dbo.EM_PlanSamples does not exist.', 1;

    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'MediaPreparationID') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD MediaPreparationID INT NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'PlannedIncubationEnd') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD PlannedIncubationEnd DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1Start') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1Start DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1TargetEnd') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1TargetEnd DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1End') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1End DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1ActualTempC') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1ActualTempC DECIMAL(5,2) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1CompletedBy') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1CompletedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase1CompletedAt') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1CompletedAt DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2Start') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2Start DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2TargetEnd') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2TargetEnd DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2End') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2End DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2ActualTempC') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2ActualTempC DECIMAL(5,2) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2CompletedBy') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2CompletedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'IncubationPhase2CompletedAt') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2CompletedAt DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'ControlReadBy') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD ControlReadBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.EM_PlanSamples',N'ControlReadAt') IS NULL ALTER TABLE dbo.EM_PlanSamples ADD ControlReadAt DATETIME2(0) NULL;

    IF OBJECT_ID(N'dbo.Samples',N'U') IS NULL
        THROW 51302, 'Required table dbo.Samples does not exist.', 1;
    IF COL_LENGTH(N'dbo.Samples',N'IncubationStartedDateTime') IS NULL ALTER TABLE dbo.Samples ADD IncubationStartedDateTime DATETIME NULL;
    IF COL_LENGTH(N'dbo.Samples',N'IncubationCompletedDateTime') IS NULL ALTER TABLE dbo.Samples ADD IncubationCompletedDateTime DATETIME NULL;

    EXEC sys.sp_executesql N'
        UPDATE dbo.Samples
        SET IncubationStartedDateTime = COALESCE(IncubationStartedDateTime, AnalysisStartedDateTime)
        WHERE IncubationStartedDateTime IS NULL
          AND AnalysisStartedDateTime IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(ISNULL(WaterIncubationProgram,N''''))),N'''') IS NOT NULL;';

    IF OBJECT_ID(N'dbo.Certificates',N'U') IS NULL
        THROW 51303, 'Required table dbo.Certificates does not exist.', 1;
    IF COL_LENGTH(N'dbo.Certificates',N'RevisionNo') IS NULL ALTER TABLE dbo.Certificates ADD RevisionNo INT NOT NULL CONSTRAINT DF_Certificates_RevisionNo_20260722 DEFAULT(0);
    IF COL_LENGTH(N'dbo.Certificates',N'IsCancelled') IS NULL ALTER TABLE dbo.Certificates ADD IsCancelled BIT NOT NULL CONSTRAINT DF_Certificates_IsCancelled_20260722 DEFAULT(0);
    IF COL_LENGTH(N'dbo.Certificates',N'VerificationCode') IS NULL ALTER TABLE dbo.Certificates ADD VerificationCode NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.Certificates',N'ReportHash') IS NULL ALTER TABLE dbo.Certificates ADD ReportHash NVARCHAR(128) NULL;
    IF COL_LENGTH(N'dbo.Certificates',N'CertificateStatus') IS NULL ALTER TABLE dbo.Certificates ADD CertificateStatus NVARCHAR(40) NULL;
    IF COL_LENGTH(N'dbo.Certificates',N'ReissuedFromCertificateID') IS NULL ALTER TABLE dbo.Certificates ADD ReissuedFromCertificateID INT NULL;

    EXEC sys.sp_executesql N'
        UPDATE dbo.Certificates
        SET CertificateStatus = COALESCE(NULLIF(LTRIM(RTRIM(CertificateStatus)),N''''), CASE WHEN ISNULL(IsCancelled,0)=1 THEN N''Cancelled'' ELSE N''Active'' END),
            VerificationCode = COALESCE(NULLIF(LTRIM(RTRIM(VerificationCode)),N''''), CertificateNumber)
        WHERE CertificateStatus IS NULL OR LTRIM(RTRIM(CertificateStatus))=N''''
           OR VerificationCode IS NULL OR LTRIM(RTRIM(VerificationCode))=N'''';';

    IF OBJECT_ID(N'dbo.CertificateDocumentSnapshots',N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CertificateDocumentSnapshots
        (
            SnapshotID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CertificateDocumentSnapshots PRIMARY KEY,
            CertificateID INT NOT NULL,
            SampleID INT NOT NULL,
            CertificateNumber NVARCHAR(50) NOT NULL,
            SnapshotContent NVARCHAR(MAX) NOT NULL,
            SnapshotHash NVARCHAR(128) NOT NULL,
            CreatedBy NVARCHAR(100) NOT NULL,
            CreatedAt DATETIME2(0) NOT NULL,
            CONSTRAINT FK_CertificateDocumentSnapshots_Certificate_20260722 FOREIGN KEY(CertificateID) REFERENCES dbo.Certificates(CertificateID),
            CONSTRAINT UQ_CertificateDocumentSnapshots_Certificate_20260722 UNIQUE(CertificateID)
        );
        CREATE INDEX IX_CertificateDocumentSnapshots_Sample_20260722 ON dbo.CertificateDocumentSnapshots(SampleID,CreatedAt DESC);
    END;

    IF OBJECT_ID(N'dbo.CertificateLifecycleAudit',N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CertificateLifecycleAudit
        (
            AuditID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CertificateLifecycleAudit PRIMARY KEY,
            CertificateID INT NOT NULL,
            SampleID INT NULL,
            CertificateNumber NVARCHAR(50) NOT NULL,
            ActionType NVARCHAR(100) NOT NULL,
            ActionName NVARCHAR(100) NULL,
            OldStatus NVARCHAR(40) NULL,
            NewStatus NVARCHAR(40) NULL,
            Reason NVARCHAR(MAX) NULL,
            PerformedBy NVARCHAR(100) NOT NULL,
            PerformedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CertificateLifecycleAudit_PerformedAt_20260722 DEFAULT SYSDATETIME(),
            CONSTRAINT FK_CertificateLifecycleAudit_Certificate_20260722 FOREIGN KEY(CertificateID) REFERENCES dbo.Certificates(CertificateID)
        );
    END
    ELSE
    BEGIN
        IF COL_LENGTH(N'dbo.CertificateLifecycleAudit',N'SampleID') IS NULL ALTER TABLE dbo.CertificateLifecycleAudit ADD SampleID INT NULL;
        IF COL_LENGTH(N'dbo.CertificateLifecycleAudit',N'ActionType') IS NULL ALTER TABLE dbo.CertificateLifecycleAudit ADD ActionType NVARCHAR(100) NULL;
        IF COL_LENGTH(N'dbo.CertificateLifecycleAudit',N'ActionName') IS NULL ALTER TABLE dbo.CertificateLifecycleAudit ADD ActionName NVARCHAR(100) NULL;
        IF COL_LENGTH(N'dbo.CertificateLifecycleAudit',N'PerformedAt') IS NULL ALTER TABLE dbo.CertificateLifecycleAudit ADD PerformedAt DATETIME2(0) NULL;
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.Certificates') AND name=N'UX_Certificates_ActiveSample'
    )
    AND NOT EXISTS
    (
        SELECT SampleID
        FROM dbo.Certificates
        WHERE SampleID IS NOT NULL AND IsCancelled=0 AND CertificateStatus=N'Active'
        GROUP BY SampleID
        HAVING COUNT(*)>1
    )
        EXEC sys.sp_executesql N'CREATE UNIQUE INDEX UX_Certificates_ActiveSample ON dbo.Certificates(SampleID) WHERE IsCancelled=0 AND CertificateStatus=N''Active'';';

    IF OBJECT_ID(N'dbo.PRM_Certificates',N'U') IS NULL
        THROW 51304, 'Required table dbo.PRM_Certificates does not exist.', 1;

    IF OBJECT_ID(N'dbo.PRM_CertificateSnapshots',N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PRM_CertificateSnapshots
        (
            SnapshotID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_CertificateSnapshots PRIMARY KEY,
            CertificateID INT NOT NULL,
            SampleID INT NOT NULL,
            CertificateNumber NVARCHAR(60) NOT NULL,
            HtmlContent NVARCHAR(MAX) NOT NULL,
            SnapshotHash NVARCHAR(128) NOT NULL,
            CreatedBy NVARCHAR(120) NOT NULL,
            CreatedAt DATETIME2(0) NOT NULL,
            CONSTRAINT FK_PRM_CertificateSnapshots_Certificate_20260722 FOREIGN KEY(CertificateID) REFERENCES dbo.PRM_Certificates(CertificateID),
            CONSTRAINT UQ_PRM_CertificateSnapshots_Certificate_20260722 UNIQUE(CertificateID)
        );
        CREATE INDEX IX_PRM_CertificateSnapshots_Sample_20260722 ON dbo.PRM_CertificateSnapshots(SampleID,CreatedAt DESC);
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260722_003')
        INSERT dbo.LIMS_SchemaVersions(VersionKey,Description,ApplicationVersion)
        VALUES(N'20260722_003',N'Project complexity resolution: controlled EM schedules and incubation, atomic certificate snapshots, water timestamps, and workflow hardening.',N'2026.7.22.41');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
