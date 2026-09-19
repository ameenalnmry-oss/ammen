SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') IS NULL
    THROW 53830, 'ExternalTrendImportBatches is missing.', 1;
IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NULL
    THROW 53831, 'ExternalTrendImportRows is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportBatches')
      AND name = N'IX_ExternalTrendImportBatches_ApprovedEM_20260829'
)
    THROW 53832, 'Approved External EM batch read index is missing.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.ExternalTrendImportRows')
      AND name = N'IX_ExternalTrendImportRows_MethodParameterDate_20260829'
)
    THROW 53833, 'External Trend method/parameter/date read index is missing.', 1;

SELECT
    N'PASS' AS VerificationStatus,
    i.name AS IndexName,
    i.is_disabled AS IsDisabled
FROM sys.indexes i
WHERE i.object_id IN
(
    OBJECT_ID(N'dbo.ExternalTrendImportBatches'),
    OBJECT_ID(N'dbo.ExternalTrendImportRows')
)
AND i.name IN
(
    N'IX_ExternalTrendImportBatches_ApprovedEM_20260829',
    N'IX_ExternalTrendImportRows_MethodParameterDate_20260829'
)
ORDER BY i.name;
