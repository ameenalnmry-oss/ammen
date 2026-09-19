SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LIMS_SchemaVersions
    (
        VersionKey NVARCHAR(100) NOT NULL CONSTRAINT PK_LIMS_SchemaVersions PRIMARY KEY,
        Description NVARCHAR(500) NOT NULL,
        AppliedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LIMS_SchemaVersions_AppliedAt DEFAULT (SYSDATETIME()),
        AppliedBy NVARCHAR(128) NOT NULL CONSTRAINT DF_LIMS_SchemaVersions_AppliedBy DEFAULT (SUSER_SNAME())
    );
END;

IF OBJECT_ID(N'dbo.EM_AreaTemplates', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.EM_Areas', N'U') IS NOT NULL
BEGIN
    UPDATE T
    SET T.IsActive = 0
    FROM dbo.EM_AreaTemplates T
    INNER JOIN dbo.EM_Areas A ON A.Id = T.AreaId
    WHERE A.AreaCode IN (N'D29', N'D30')
      AND T.Method IN (N'Contact Plate', N'Surface Swab', N'Personnel Monitoring')
      AND T.PlateCode IN
      (
          N'D29-CP-BALANCE', N'D29-CP-WORKSURFACE',
          N'D29-SW-BALANCEPAN', N'D29-SW-CONTROLPANEL', N'D29-SW-DOORHANDLE',
          N'D29-PM-LEFTGLOVE', N'D29-PM-RIGHTGLOVE',
          N'D30-CP-BALANCE', N'D30-CP-WORKSURFACE',
          N'D30-SW-BALANCEPAN', N'D30-SW-CONTROLPANEL', N'D30-SW-DOORHANDLE',
          N'D30-PM-LEFTGLOVE', N'D30-PM-RIGHTGLOVE'
      );
END;

IF OBJECT_ID(N'dbo.LIMS_NumberSequences', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL
BEGIN
    DECLARE @ExistingLast INT = 0;
    SELECT @ExistingLast = ISNULL(MAX(TRY_CONVERT(INT,
        CASE
            WHEN CHARINDEX(N'-', REVERSE(EventNo)) > 0
            THEN RIGHT(EventNo, CHARINDEX(N'-', REVERSE(EventNo)) - 1)
            ELSE NULL
        END)), 0)
    FROM dbo.EM_Events
    WHERE EventNo LIKE N'EM-%';

    IF EXISTS (SELECT 1 FROM dbo.LIMS_NumberSequences WHERE SequenceKey = N'EM-GLOBAL')
        UPDATE dbo.LIMS_NumberSequences
        SET LastNumber = CASE WHEN LastNumber > @ExistingLast THEN LastNumber ELSE @ExistingLast END,
            UpdatedAt = GETDATE()
        WHERE SequenceKey = N'EM-GLOBAL';
    ELSE
        INSERT INTO dbo.LIMS_NumberSequences
            (NumberKey, YearNo, LastNumber, Prefix, CreatedAt, UpdatedAt, SequenceKey)
        VALUES
            (N'EM', YEAR(GETDATE()), @ExistingLast, N'EM', GETDATE(), GETDATE(), N'EM-GLOBAL');
END;

IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260718_001')
BEGIN
    INSERT INTO dbo.LIMS_SchemaVersions (VersionKey, Description)
    VALUES (N'20260718_001', N'Release hardening: global transactional EM numbering, legacy booth-template cleanup, and schema version tracking.');
END;

COMMIT TRANSACTION;

SELECT VersionKey, Description, AppliedAt, AppliedBy
FROM dbo.LIMS_SchemaVersions
WHERE VersionKey = N'20260718_001';
