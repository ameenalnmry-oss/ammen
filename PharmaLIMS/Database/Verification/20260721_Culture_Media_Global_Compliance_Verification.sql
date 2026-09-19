SET NOCOUNT ON;

SELECT
    OBJECT_ID(N'dbo.CultureMediaStockTransactions', N'U') AS CultureMediaStockTransactions,
    OBJECT_ID(N'dbo.CultureMediaStockReconciliations', N'U') AS CultureMediaStockReconciliations,
    OBJECT_ID(N'dbo.CultureMediaPreparationDispositions', N'U') AS CultureMediaPreparationDispositions,
    OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') AS CultureMediaQualificationRequirements,
    OBJECT_ID(N'dbo.CultureMediaPrintHistory', N'U') AS CultureMediaPrintHistory,
    COL_LENGTH(N'dbo.CultureMediaStockTransactions', N'BalanceBeforeG') AS BalanceBeforeG,
    COL_LENGTH(N'dbo.MediaPreparations', N'VisualCheckedBy') AS VisualCheckedBy,
    COL_LENGTH(N'dbo.MediaPreparations', N'SterilityReviewedBy') AS SterilityReviewedBy,
    COL_LENGTH(N'dbo.MediaPreparations', N'ReleasedBy') AS ReleasedBy,
    COL_LENGTH(N'dbo.MediaPreparations', N'RejectedBy') AS RejectedBy,
    COL_LENGTH(N'dbo.EM_Events', N'MediaPreparationID') AS EMEventMediaPreparationID;

SELECT
    l.MediaLotID,
    l.LotNumber,
    l.InitialStockG,
    l.CurrentStockG,
    l.StockStatus,
    ISNULL(ledger.LedgerNetChange, 0) AS LedgerNetChange,
    latest.BalanceAfterG AS LastRecordedBalance,
    latest.TransactionType AS LastTransactionType,
    latest.PerformedAt AS LastTransactionAt,
    reconciliation.PhysicalCountG AS LastPhysicalCountG,
    reconciliation.DifferenceG AS LastReconciliationDifferenceG,
    reconciliation.ReconciledAt AS LastReconciledAt
FROM dbo.CultureMediaLots l
OUTER APPLY
(
    SELECT SUM(st.QuantityChangeG) AS LedgerNetChange
    FROM dbo.CultureMediaStockTransactions st
    WHERE st.MediaLotID = l.MediaLotID
) ledger
OUTER APPLY
(
    SELECT TOP (1) st.BalanceAfterG, st.TransactionType, st.PerformedAt
    FROM dbo.CultureMediaStockTransactions st
    WHERE st.MediaLotID = l.MediaLotID
    ORDER BY st.PerformedAt DESC, st.StockTransactionID DESC
) latest
OUTER APPLY
(
    SELECT TOP (1) r.PhysicalCountG, r.DifferenceG, r.ReconciledAt
    FROM dbo.CultureMediaStockReconciliations r
    WHERE r.MediaLotID = l.MediaLotID
    ORDER BY r.ReconciledAt DESC, r.ReconciliationID DESC
) reconciliation
ORDER BY l.MediaLotID DESC;

SELECT TOP (100)
    p.MediaPreparationID,
    p.MediaPreparationNo,
    p.PreparedBy,
    p.VisualCheckedBy,
    p.VisualCheckedAt,
    p.SterilityReview,
    p.SterilityReviewedBy,
    p.SterilityReviewedAt,
    p.ReleasedBy,
    p.ReleasedAt,
    p.RejectedBy,
    p.RejectedAt,
    p.ReleaseStatus
FROM dbo.MediaPreparations p
ORDER BY p.MediaPreparationID DESC;

SELECT *
FROM dbo.CultureMediaQualificationRequirements
ORDER BY MediaTypePattern, TestName, EffectiveDate DESC;

SELECT TOP (100) *
FROM dbo.CultureMediaStockReconciliations
ORDER BY ReconciliationID DESC;

SELECT TOP (100) *
FROM dbo.CultureMediaPrintHistory
ORDER BY PrintHistoryID DESC;


SELECT TOP (100)
    e.Id AS EMEventID,
    e.EventNo,
    e.MediaUsed,
    e.MediaLotNo AS RecordedMediaPreparationNo,
    e.MediaPreparationID,
    p.MediaPreparationNo,
    p.ReleaseStatus,
    p.SterilityReview,
    p.ExpiryDate
FROM dbo.EM_Events e
LEFT JOIN dbo.MediaPreparations p ON p.MediaPreparationID = e.MediaPreparationID
ORDER BY e.Id DESC;
