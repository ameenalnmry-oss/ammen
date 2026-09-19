SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory', N'U') IS NULL
    THROW 53820, 'Verification failed: dbo.QualityEventInvestigationEvidenceHistory is missing.', 1;

IF COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'OldRowsJson') IS NULL
   OR COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'NewRowsJson') IS NULL
   OR COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory', N'ChangeReason') IS NULL
    THROW 53821, 'Verification failed: structured-evidence history columns are incomplete.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory')
      AND name = N'IX_QEInvestigationEvidenceHistory_Event_20260828_001'
      AND is_disabled = 0
)
    THROW 53822, 'Verification failed: structured-evidence history index is missing or disabled.', 1;

IF OBJECT_ID(N'dbo.TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001', N'TR') IS NULL
    THROW 53823, 'Verification failed: append-only structured-evidence history trigger is missing.', 1;

SELECT
    N'PASS' AS VerificationStatus,
    COUNT_BIG(*) AS HistoryRowCount
FROM dbo.QualityEventInvestigationEvidenceHistory;
