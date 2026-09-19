SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       v224 legacy-upgrade repair.

       Fresh installations receive these three Water controlled-master tables from
       BASELINE_20260823_001. Databases upgraded from the historical pre-baseline
       schema can legitimately have every later Water planning/result migration
       recorded while these baseline-only objects are still absent.

       This migration is additive and does not seed or guess GMP master data.
       It creates the exact baseline table contract when an object is missing and
       fails closed when a pre-existing partial object is structurally incompatible.
    */

    IF OBJECT_ID(N'dbo.Tests', N'U') IS NULL
        THROW 54500, 'Required dependency dbo.Tests is missing before 20260906_006.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Tests')
          AND name=N'TestID'
          AND system_type_id=TYPE_ID(N'int')
          AND is_nullable=0
    )
        THROW 54501, 'Required dependency dbo.Tests.TestID must be INT NOT NULL before 20260906_006.', 1;

    IF OBJECT_ID(N'dbo.WaterTestProfiles', N'U') IS NULL
    BEGIN
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
    END;

    IF OBJECT_ID(N'dbo.WaterTestProfileTests', N'U') IS NULL
    BEGIN
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
    END;

    IF OBJECT_ID(N'dbo.WaterSpecifications', N'U') IS NULL
    BEGIN
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
    END;

    /*
       Fail closed on pre-existing partial/drifted versions. The application relies
       on the full baseline contract, not merely the six columns used by the first
       preflight query.
    */
    DECLARE @RequiredColumns TABLE
    (
        TableName sysname NOT NULL,
        ColumnName sysname NOT NULL,
        SystemType sysname NOT NULL,
        MaxLength smallint NULL,
        PrecisionValue tinyint NULL,
        ScaleValue tinyint NULL,
        NullableValue bit NOT NULL
    );

    INSERT @RequiredColumns(TableName,ColumnName,SystemType,MaxLength,PrecisionValue,ScaleValue,NullableValue) VALUES
    (N'WaterTestProfiles',N'ProfileID',N'int',NULL,NULL,NULL,0),
    (N'WaterTestProfiles',N'ProfileCode',N'nvarchar',40,NULL,NULL,0),
    (N'WaterTestProfiles',N'ProfileName',N'nvarchar',300,NULL,NULL,0),
    (N'WaterTestProfiles',N'VersionNo',N'int',NULL,NULL,NULL,0),
    (N'WaterTestProfiles',N'ApprovalStatus',N'nvarchar',60,NULL,NULL,0),
    (N'WaterTestProfiles',N'EffectiveFrom',N'date',NULL,NULL,NULL,1),
    (N'WaterTestProfiles',N'EffectiveTo',N'date',NULL,NULL,NULL,1),
    (N'WaterTestProfiles',N'IsActive',N'bit',NULL,NULL,NULL,0),
    (N'WaterTestProfiles',N'ReviewedBy',N'nvarchar',200,NULL,NULL,1),
    (N'WaterTestProfiles',N'ReviewedAt',N'datetime2',NULL,NULL,0,1),
    (N'WaterTestProfiles',N'ApprovedBy',N'nvarchar',200,NULL,NULL,1),
    (N'WaterTestProfiles',N'ApprovedAt',N'datetime2',NULL,NULL,0,1),
    (N'WaterTestProfiles',N'CreatedBy',N'nvarchar',200,NULL,NULL,0),
    (N'WaterTestProfiles',N'CreatedAt',N'datetime2',NULL,NULL,0,0),

    (N'WaterTestProfileTests',N'ProfileTestID',N'int',NULL,NULL,NULL,0),
    (N'WaterTestProfileTests',N'ProfileID',N'int',NULL,NULL,NULL,0),
    (N'WaterTestProfileTests',N'TestID',N'int',NULL,NULL,NULL,0),
    (N'WaterTestProfileTests',N'SortOrder',N'int',NULL,NULL,NULL,1),
    (N'WaterTestProfileTests',N'IsActive',N'bit',NULL,NULL,NULL,0),

    (N'WaterSpecifications',N'SpecificationID',N'int',NULL,NULL,NULL,0),
    (N'WaterSpecifications',N'ProfileCode',N'nvarchar',40,NULL,NULL,0),
    (N'WaterSpecifications',N'TestID',N'int',NULL,NULL,NULL,0),
    (N'WaterSpecifications',N'PointCode',N'nvarchar',100,NULL,NULL,1),
    (N'WaterSpecifications',N'LowerLimit',N'decimal',NULL,18,6,1),
    (N'WaterSpecifications',N'UpperLimit',N'decimal',NULL,18,6,1),
    (N'WaterSpecifications',N'AlertLimit',N'decimal',NULL,18,6,1),
    (N'WaterSpecifications',N'ActionLimit',N'decimal',NULL,18,6,1),
    (N'WaterSpecifications',N'SpecificationText',N'nvarchar',1000,NULL,NULL,0),
    (N'WaterSpecifications',N'EffectiveFrom',N'date',NULL,NULL,NULL,1),
    (N'WaterSpecifications',N'EffectiveTo',N'date',NULL,NULL,NULL,1),
    (N'WaterSpecifications',N'ApprovalStatus',N'nvarchar',60,NULL,NULL,0),
    (N'WaterSpecifications',N'IsActive',N'bit',NULL,NULL,NULL,0),
    (N'WaterSpecifications',N'ReviewedBy',N'nvarchar',200,NULL,NULL,1),
    (N'WaterSpecifications',N'ReviewedAt',N'datetime2',NULL,NULL,0,1),
    (N'WaterSpecifications',N'ApprovedBy',N'nvarchar',200,NULL,NULL,1),
    (N'WaterSpecifications',N'ApprovedAt',N'datetime2',NULL,NULL,0,1),
    (N'WaterSpecifications',N'CreatedBy',N'nvarchar',200,NULL,NULL,0),
    (N'WaterSpecifications',N'CreatedAt',N'datetime2',NULL,NULL,0,0);

    IF EXISTS
    (
        SELECT 1
        FROM @RequiredColumns required
        LEFT JOIN sys.columns columnInfo
          ON columnInfo.object_id=OBJECT_ID(N'dbo.' + required.TableName)
         AND columnInfo.name=required.ColumnName
        LEFT JOIN sys.types typeInfo
          ON typeInfo.user_type_id=columnInfo.user_type_id
        WHERE columnInfo.column_id IS NULL
           OR typeInfo.name<>required.SystemType
           OR (required.MaxLength IS NOT NULL AND columnInfo.max_length<>required.MaxLength)
           OR (required.PrecisionValue IS NOT NULL AND columnInfo.precision<>required.PrecisionValue)
           OR (required.ScaleValue IS NOT NULL AND columnInfo.scale<>required.ScaleValue)
           OR columnInfo.is_nullable<>required.NullableValue
    )
        THROW 54502, 'Water controlled-master schema is present but incompatible with the v224 baseline contract. No data was changed.', 1;

    /* Identity/relationship controls are part of the controlled master contract. */
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.key_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
          AND type=N'PK'
    )
        THROW 54503, 'dbo.WaterTestProfiles requires a primary key.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.key_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfileTests')
          AND type=N'PK'
    )
        THROW 54504, 'dbo.WaterTestProfileTests requires a primary key.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.key_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.WaterSpecifications')
          AND type=N'PK'
    )
        THROW 54505, 'dbo.WaterSpecifications requires a primary key.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfileTests')
          AND referenced_object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
          AND is_disabled=0
          AND is_not_trusted=0
    )
        THROW 54506, 'dbo.WaterTestProfileTests requires an enabled foreign key to dbo.WaterTestProfiles.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfileTests')
          AND referenced_object_id=OBJECT_ID(N'dbo.Tests')
          AND is_disabled=0
          AND is_not_trusted=0
    )
        THROW 54507, 'dbo.WaterTestProfileTests requires an enabled foreign key to dbo.Tests.', 1;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id=OBJECT_ID(N'dbo.WaterSpecifications')
          AND referenced_object_id=OBJECT_ID(N'dbo.Tests')
          AND is_disabled=0
          AND is_not_trusted=0
    )
        THROW 54508, 'dbo.WaterSpecifications requires an enabled foreign key to dbo.Tests.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'20260906_006' AS MigrationVersion;
