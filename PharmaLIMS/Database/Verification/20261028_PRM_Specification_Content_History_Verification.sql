SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.PRM_SpecificationContentHistory', N'U') IS NULL
    THROW 56457, 'PRM specification content history is missing.', 1;
IF NOT EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.PRM_SpecificationContentHistory')
    AND name=N'TRG_PRM_SpecificationContentHistory_AppendOnly' AND is_disabled=0 AND is_instead_of_trigger=1)
    THROW 56457, 'PRM specification content history append-only protection is missing or disabled.', 1;
IF EXISTS(SELECT name FROM (VALUES(N'HistoryID'),(N'SpecificationNo'),(N'SampleCategory'),(N'VersionNo'),
    (N'ActionType'),(N'OldRowsJson'),(N'NewRowsJson'),(N'ChangedBy'),(N'ChangedAt'),(N'ChangeReason'),(N'SignatureID'),(N'ContentHash')) required(name)
    WHERE COL_LENGTH(N'dbo.PRM_SpecificationContentHistory',name) IS NULL)
    THROW 56457, 'PRM specification content history schema is incomplete.', 1;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationContentHistory')
    AND name=N'IX_PRM_SpecificationContentHistory_Version' AND is_disabled=0)
    THROW 56457, 'PRM specification content history version index is missing.', 1;
SELECT N'PASS: PRM specification content history and append-only protection are ready.' AS Verification;
