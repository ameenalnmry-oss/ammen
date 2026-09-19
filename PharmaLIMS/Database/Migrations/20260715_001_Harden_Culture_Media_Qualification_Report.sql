SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NULL
        THROW 51000, 'Required table dbo.MediaQualifications does not exist. The base Culture Media database schema must be installed first.', 1;

    /* Main qualification workflow columns */
    IF COL_LENGTH('dbo.MediaQualifications', 'ReleasedBy') IS NULL
        ALTER TABLE dbo.MediaQualifications ADD ReleasedBy NVARCHAR(100) NULL;

    IF COL_LENGTH('dbo.MediaQualifications', 'ReviewDate') IS NULL
        ALTER TABLE dbo.MediaQualifications ADD ReviewDate DATETIME2(0) NULL;

    IF COL_LENGTH('dbo.MediaQualifications', 'ReleaseDate') IS NULL
        ALTER TABLE dbo.MediaQualifications ADD ReleaseDate DATETIME2(0) NULL;

    IF COL_LENGTH('dbo.MediaQualifications', 'QualificationStatus') IS NULL
    BEGIN
        ALTER TABLE dbo.MediaQualifications ADD QualificationStatus NVARCHAR(30) NULL;

        UPDATE dbo.MediaQualifications
        SET QualificationStatus =
            CASE
                WHEN UPPER(LTRIM(RTRIM(ISNULL(OverallResult, '')))) IN ('FAIL', 'FAILED', 'REJECTED') THEN 'Failed'
                WHEN UPPER(LTRIM(RTRIM(ISNULL(OverallResult, '')))) = 'PASS'
                     AND NULLIF(LTRIM(RTRIM(ISNULL(ReviewedBy, ''))), '') IS NOT NULL THEN 'Qualified'
                ELSE 'Pending Review'
            END
        WHERE QualificationStatus IS NULL;

        ALTER TABLE dbo.MediaQualifications ALTER COLUMN QualificationStatus NVARCHAR(30) NOT NULL;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c
            ON c.object_id = dc.parent_object_id
           AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'dbo.MediaQualifications')
          AND c.name = N'QualificationStatus'
    )
    BEGIN
        ALTER TABLE dbo.MediaQualifications
        ADD CONSTRAINT DF_MediaQualifications_QualificationStatus
            DEFAULT (N'Pending Review') FOR QualificationStatus;
    END;

    /* The test-detail table may not exist in older databases. */
    IF OBJECT_ID(N'dbo.MediaQualificationTests', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.MediaQualificationTests
        (
            MediaQualificationTestID INT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_MediaQualificationTests PRIMARY KEY,
            MediaQualificationID INT NOT NULL,
            TestName NVARCHAR(100) NULL,
            OrganismName NVARCHAR(200) NULL,
            ATCCNumber NVARCHAR(80) NULL,
            InoculumLevel NVARCHAR(80) NULL,
            ExpectedResult NVARCHAR(200) NULL,
            ActualResult NVARCHAR(200) NULL,
            ControlCount DECIMAL(18,2) NULL,
            TestCount DECIMAL(18,2) NULL,
            RecoveryPercent DECIMAL(9,2) NULL,
            IncubationConditions NVARCHAR(200) NULL,
            TestResult NVARCHAR(30) NULL,
            Remarks NVARCHAR(MAX) NULL,
            CreatedDate DATETIME2(0) NOT NULL
                CONSTRAINT DF_MediaQualificationTests_CreatedDate DEFAULT (SYSDATETIME()),
            CONSTRAINT FK_MediaQualificationTests_MediaQualifications
                FOREIGN KEY (MediaQualificationID)
                REFERENCES dbo.MediaQualifications(MediaQualificationID)
                ON DELETE CASCADE
        );

        CREATE INDEX IX_MediaQualificationTests_MediaQualificationID
            ON dbo.MediaQualificationTests(MediaQualificationID, MediaQualificationTestID);
    END
    ELSE
    BEGIN
        IF COL_LENGTH('dbo.MediaQualificationTests', 'TestName') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD TestName NVARCHAR(100) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'OrganismName') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD OrganismName NVARCHAR(200) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'ATCCNumber') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD ATCCNumber NVARCHAR(80) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'InoculumLevel') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD InoculumLevel NVARCHAR(80) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'ExpectedResult') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD ExpectedResult NVARCHAR(200) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'ActualResult') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD ActualResult NVARCHAR(200) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'ControlCount') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD ControlCount DECIMAL(18,2) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'TestCount') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD TestCount DECIMAL(18,2) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'RecoveryPercent') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD RecoveryPercent DECIMAL(9,2) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'IncubationConditions') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD IncubationConditions NVARCHAR(200) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'TestResult') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD TestResult NVARCHAR(30) NULL;
        IF COL_LENGTH('dbo.MediaQualificationTests', 'Remarks') IS NULL
            ALTER TABLE dbo.MediaQualificationTests ADD Remarks NVARCHAR(MAX) NULL;
    END;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.MediaQualificationTests')
          AND name = N'CK_MediaQualificationTests_Counts'
    )
    BEGIN
        ALTER TABLE dbo.MediaQualificationTests WITH NOCHECK
        ADD CONSTRAINT CK_MediaQualificationTests_Counts CHECK
        (
            (ControlCount IS NULL OR ControlCount >= 0)
            AND (TestCount IS NULL OR TestCount >= 0)
            AND (RecoveryPercent IS NULL OR RecoveryPercent >= 0)
        );
    END;

    COMMIT TRANSACTION;

    SELECT
        N'Culture Media database update completed successfully.' AS Result,
        OBJECT_ID(N'dbo.MediaQualifications', N'U') AS MediaQualificationsObjectID,
        OBJECT_ID(N'dbo.MediaQualificationTests', N'U') AS MediaQualificationTestsObjectID,
        COL_LENGTH('dbo.MediaQualifications', 'ReleasedBy') AS ReleasedByColumn,
        COL_LENGTH('dbo.MediaQualifications', 'ReviewDate') AS ReviewDateColumn,
        COL_LENGTH('dbo.MediaQualifications', 'ReleaseDate') AS ReleaseDateColumn,
        COL_LENGTH('dbo.MediaQualifications', 'QualificationStatus') AS QualificationStatusColumn,
        COL_LENGTH('dbo.MediaQualificationTests', 'ControlCount') AS ControlCountColumn,
        COL_LENGTH('dbo.MediaQualificationTests', 'TestCount') AS TestCountColumn,
        COL_LENGTH('dbo.MediaQualificationTests', 'RecoveryPercent') AS RecoveryPercentColumn,
        COL_LENGTH('dbo.MediaQualificationTests', 'IncubationConditions') AS IncubationConditionsColumn;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
