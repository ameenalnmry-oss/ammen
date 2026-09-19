SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.EM_GradeLimits', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EM_GradeLimits
        (
            Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_GradeLimits PRIMARY KEY,
            Grade NVARCHAR(100) NOT NULL,
            Method NVARCHAR(100) NOT NULL,
            AlertLimitTotal DECIMAL(18,3) NULL,
            ActionLimitTotal DECIMAL(18,3) NULL,
            AirVolumeLiters INT NULL,
            IsActive BIT NOT NULL CONSTRAINT DF_EM_GradeLimits_IsActive DEFAULT (1),
            LastModifiedBy NVARCHAR(100) NULL,
            LastModifiedAt DATETIME2(0) NULL
        );
    END;

    IF COL_LENGTH(N'dbo.EM_GradeLimits', N'IsActive') IS NULL
        ALTER TABLE dbo.EM_GradeLimits ADD IsActive BIT NOT NULL CONSTRAINT DF_EM_GradeLimits_IsActive_20260819 DEFAULT (1) WITH VALUES;

    IF COL_LENGTH(N'dbo.EM_GradeLimits', N'LastModifiedBy') IS NULL
        ALTER TABLE dbo.EM_GradeLimits ADD LastModifiedBy NVARCHAR(100) NULL;

    IF COL_LENGTH(N'dbo.EM_GradeLimits', N'LastModifiedAt') IS NULL
        ALTER TABLE dbo.EM_GradeLimits ADD LastModifiedAt DATETIME2(0) NULL;

    IF EXISTS
    (
        SELECT 1
        FROM dbo.EM_GradeLimits
        WHERE (AlertLimitTotal IS NOT NULL AND AlertLimitTotal < 0)
           OR (ActionLimitTotal IS NOT NULL AND ActionLimitTotal < 0)
           OR (AlertLimitTotal IS NOT NULL AND ActionLimitTotal IS NOT NULL AND ActionLimitTotal < AlertLimitTotal)
           OR (AirVolumeLiters IS NOT NULL AND AirVolumeLiters <= 0)
    )
    BEGIN
        THROW 52019, 'Existing EM_GradeLimits rows contain invalid negative/reversed limits or non-positive air volume. Reconcile controlled master data before applying 20260819_001.', 1;
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.EM_GradeLimits')
          AND name = N'CK_EM_GradeLimits_NonNegative_20260819'
    )
    BEGIN
        ALTER TABLE dbo.EM_GradeLimits WITH CHECK
        ADD CONSTRAINT CK_EM_GradeLimits_NonNegative_20260819 CHECK
        (
            (AlertLimitTotal IS NULL OR AlertLimitTotal >= 0)
            AND (ActionLimitTotal IS NULL OR ActionLimitTotal >= 0)
            AND (AirVolumeLiters IS NULL OR AirVolumeLiters > 0)
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.EM_GradeLimits')
          AND name = N'CK_EM_GradeLimits_ActionGEAlert_20260819'
    )
    BEGIN
        ALTER TABLE dbo.EM_GradeLimits WITH CHECK
        ADD CONSTRAINT CK_EM_GradeLimits_ActionGEAlert_20260819 CHECK
        (
            AlertLimitTotal IS NULL OR ActionLimitTotal IS NULL OR ActionLimitTotal >= AlertLimitTotal
        );
    END;

    IF COL_LENGTH(N'dbo.EM_EventPlates', N'AlertLimitSnapshot') IS NULL
        ALTER TABLE dbo.EM_EventPlates ADD AlertLimitSnapshot DECIMAL(18,3) NULL;

    IF COL_LENGTH(N'dbo.EM_EventPlates', N'ActionLimitSnapshot') IS NULL
        ALTER TABLE dbo.EM_EventPlates ADD ActionLimitSnapshot DECIMAL(18,3) NULL;

    IF COL_LENGTH(N'dbo.EM_EventPlates', N'ResultUnitSnapshot') IS NULL
        ALTER TABLE dbo.EM_EventPlates ADD ResultUnitSnapshot NVARCHAR(30) NULL;

    IF COL_LENGTH(N'dbo.EM_EventPlates', N'AirVolumeLitersSnapshot') IS NULL
        ALTER TABLE dbo.EM_EventPlates ADD AirVolumeLitersSnapshot INT NULL;

    IF OBJECT_ID(N'dbo.EM_GradeLimitSignatures', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EM_GradeLimitSignatures
        (
            SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_GradeLimitSignatures PRIMARY KEY,
            GradeLimitID INT NOT NULL,
            ActionType NVARCHAR(100) NOT NULL,
            MeaningOfSignature NVARCHAR(255) NOT NULL,
            Reason NVARCHAR(MAX) NULL,
            SignedBy NVARCHAR(100) NOT NULL,
            UserRole NVARCHAR(100) NULL,
            SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_GradeLimitSignatures_SignedAt DEFAULT SYSUTCDATETIME(),
            SourceWorkstation NVARCHAR(200) NULL,
            CONSTRAINT FK_EM_GradeLimitSignatures_GradeLimit
                FOREIGN KEY (GradeLimitID) REFERENCES dbo.EM_GradeLimits(Id)
        );
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.EM_GradeLimitSignatures')
          AND name = N'IX_EM_GradeLimitSignatures_Limit_SignedAt'
    )
    BEGIN
        CREATE INDEX IX_EM_GradeLimitSignatures_Limit_SignedAt
            ON dbo.EM_GradeLimitSignatures(GradeLimitID, SignedAt DESC);
    END;


    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
