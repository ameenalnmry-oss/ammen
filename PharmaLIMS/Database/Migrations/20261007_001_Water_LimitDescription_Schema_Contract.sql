SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.SampleTests', N'U') IS NULL
    THROW 56301, 'Required table dbo.SampleTests is missing.', 1;

DECLARE @SystemTypeId int,
        @MaxLength smallint,
        @IsNullable bit,
        @IsComputed bit;

SELECT
    @SystemTypeId = c.system_type_id,
    @MaxLength = c.max_length,
    @IsNullable = c.is_nullable,
    @IsComputed = c.is_computed
FROM sys.columns c
WHERE c.object_id = OBJECT_ID(N'dbo.SampleTests')
  AND c.name = N'LimitDescription';

IF @SystemTypeId IS NULL
BEGIN
    ALTER TABLE dbo.SampleTests
        ADD LimitDescription NVARCHAR(500) NULL;
END
ELSE
BEGIN
    IF @SystemTypeId <> TYPE_ID(N'nvarchar') OR @IsComputed <> 0
        THROW 56302, 'dbo.SampleTests.LimitDescription has an incompatible schema type.', 1;

    -- sys.columns.max_length is stored in bytes for NVARCHAR.
    -- Preserve wider existing contracts (including NVARCHAR(MAX)); only widen legacy 200/250/300 shapes.
    IF @MaxLength <> -1 AND @MaxLength < 1000
        ALTER TABLE dbo.SampleTests ALTER COLUMN LimitDescription NVARCHAR(500) NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.SampleTests')
      AND c.name = N'LimitDescription'
      AND c.system_type_id = TYPE_ID(N'nvarchar')
      AND c.is_computed = 0
      AND c.is_nullable = 1
      AND (c.max_length = -1 OR c.max_length >= 1000)
)
    THROW 56303, 'dbo.SampleTests.LimitDescription must be NVARCHAR(500) NULL or wider.', 1;
