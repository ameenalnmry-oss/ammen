-- PharmaLIMS controlled EM history inventory. READ-ONLY.
-- Review every proposed calculation against actual approved frozen evidence
-- before any QA-managed correction. NEVER use this as an UPDATE script.
-- Historical approved records, signatures, and audit trails remain unchanged.
SET NOCOUNT ON;
IF DB_NAME() <> N'PharmaLIMS_UAT_20261001'
    THROW 56021, 'Inventory restricted to the designated isolated UAT database.', 1;

WITH ApprovedPlateInventory AS
(
    SELECT
        e.Id AS EventID,
        e.EventNo,
        e.WorkflowStatus,
        p.Id AS PlateID,
        p.PlateCode,
        p.Method,
        p.TotalCount,
        p.ResultCFU AS StoredResultCFU,
        p.Status AS StoredInterpretation,
        p.ResultCalculationVersion AS StoredCalculationVersion,
        p.AlertLimitSnapshot AS NativeAlert,
        p.ActionLimitSnapshot AS NativeAction,
        p.ResultUnitSnapshot AS NativeUnit,
        p.AirVolumeLitersSnapshot AS NativeAirVolumeLiters,
        r.ReconciliationID,
        r.SignedBy AS ReconciliationSignedBy,
        r.SignedAt AS ReconciliationSignedAt,
        r.EvidenceReference,
        r.AlertLimitSnapshot AS ReconciledAlert,
        r.ActionLimitSnapshot AS ReconciledAction,
        r.ResultUnitSnapshot AS ReconciledUnit,
        r.AirVolumeLitersSnapshot AS ReconciledAirVolumeLiters,
        CASE WHEN
           p.AlertLimitSnapshot IS NOT NULL AND
           p.ActionLimitSnapshot IS NOT NULL AND
           p.ActionLimitSnapshot >= p.AlertLimitSnapshot AND
           p.AlertLimitSnapshot >= 0 AND
           NULLIF(LTRIM(RTRIM(p.ResultUnitSnapshot)),N'') IS NOT NULL AND
           (p.Method<>N'Active Air Sampling' OR p.AirVolumeLitersSnapshot > 0)
           THEN 1 ELSE 0 END AS NativeValid,
        CASE WHEN
           r.ReconciliationSchemaVersion=1 AND
           r.SignedBy IS NOT NULL AND r.SignedAt IS NOT NULL AND
           NULLIF(LTRIM(RTRIM(r.MeaningOfSignature)),N'') IS NOT NULL AND
           NULLIF(LTRIM(RTRIM(r.EvidenceReference)),N'') IS NOT NULL AND
           r.AlertLimitSnapshot >= 0 AND
           r.ActionLimitSnapshot >= r.AlertLimitSnapshot AND
           NULLIF(LTRIM(RTRIM(r.ResultUnitSnapshot)),N'') IS NOT NULL AND
           (p.Method<>N'Active Air Sampling' OR r.AirVolumeLitersSnapshot > 0)
           THEN 1 ELSE 0 END AS ReconciliationValid
    FROM dbo.EM_Events e
    JOIN dbo.EM_EventPlates p ON p.EventId=e.Id
    OUTER APPLY
    (
        SELECT TOP (1) q.ReconciliationID,q.SignedBy,q.SignedAt,
               q.EvidenceReference,q.MeaningOfSignature,
               q.ReconciliationSchemaVersion,q.AlertLimitSnapshot,
               q.ActionLimitSnapshot,q.ResultUnitSnapshot,q.AirVolumeLitersSnapshot
        FROM dbo.EM_LimitSnapshotReconciliations q
        WHERE q.PlateID=p.Id
        ORDER BY q.ReconciliationID DESC
    ) r
    WHERE e.WorkflowStatus=N'Approved' AND p.TotalCount IS NOT NULL
),
Frozen AS
(
    SELECT *,
       CASE WHEN NativeValid=1 THEN NativeAlert
            WHEN ReconciliationValid=1 THEN ReconciledAlert END AS FrozenAlert,
       CASE WHEN NativeValid=1 THEN NativeAction
            WHEN ReconciliationValid=1 THEN ReconciledAction END AS FrozenAction,
       CASE WHEN NativeValid=1 THEN NativeUnit
            WHEN ReconciliationValid=1 THEN ReconciledUnit END AS FrozenUnit,
       CASE WHEN NativeValid=1 THEN NativeAirVolumeLiters
            WHEN ReconciliationValid=1 THEN ReconciledAirVolumeLiters END AS FrozenAirVolumeLiters
    FROM ApprovedPlateInventory
),
Calculated AS
(
    SELECT *,
       CASE
          WHEN Method=N'Active Air Sampling' AND FrozenAirVolumeLiters>0
          THEN ROUND(CONVERT(decimal(28,12),TotalCount)*1000/
               CONVERT(decimal(28,12),FrozenAirVolumeLiters),12)
          WHEN Method IN (N'Settle Plate',N'Contact Plate',N'Surface Swab',N'Personnel Monitoring')
          THEN CONVERT(decimal(28,12),TotalCount)
          ELSE NULL END AS IndependentlyCalculatedValue
    FROM Frozen
),
Evaluated AS
(
    SELECT *,
       CASE
          WHEN FrozenAction IS NULL OR FrozenAlert IS NULL
            OR IndependentlyCalculatedValue IS NULL THEN NULL
          WHEN Method=N'Active Air Sampling'
            AND TotalCount*1000 > FrozenAction*FrozenAirVolumeLiters THEN N'OOS'
          WHEN Method=N'Active Air Sampling'
            AND TotalCount*1000 > FrozenAlert*FrozenAirVolumeLiters THEN N'Alert'
          WHEN Method IN (N'Settle Plate',N'Contact Plate',N'Surface Swab',N'Personnel Monitoring')
            AND TotalCount > FrozenAction THEN N'OOS'
          WHEN Method IN (N'Settle Plate',N'Contact Plate',N'Surface Swab',N'Personnel Monitoring')
            AND TotalCount > FrozenAlert THEN N'Alert'
          ELSE N'PASS' END AS IndependentlyCalculatedInterpretation
    FROM Calculated
)
SELECT EventNo,EventID,PlateID,PlateCode,Method,WorkflowStatus,
    TotalCount,StoredResultCFU,StoredInterpretation,StoredCalculationVersion,
    FrozenAlert,FrozenAction,FrozenUnit,FrozenAirVolumeLiters,
    CASE WHEN TRY_CONVERT(decimal(28,12),FrozenUnit) IS NOT NULL
         THEN N'YES' ELSE N'' END AS NumericFrozenUnitRequiresQA,
    IndependentlyCalculatedValue,IndependentlyCalculatedInterpretation,
    CASE WHEN StoredResultCFU IS NOT NULL
          AND IndependentlyCalculatedValue IS NOT NULL
          AND StoredResultCFU<>IndependentlyCalculatedValue
         THEN N'YES' ELSE N'' END AS StoredValueMismatch,
    CASE WHEN IndependentlyCalculatedInterpretation IS NOT NULL
          AND StoredInterpretation<>IndependentlyCalculatedInterpretation
         THEN N'YES' ELSE N'' END AS StoredInterpretationMismatch,
    ReconciliationID,
    ReconciliationSignedBy,ReconciliationSignedAt,EvidenceReference,
    CASE
      WHEN FrozenAction IS NULL OR IndependentlyCalculatedValue IS NULL
           THEN N'FROZEN_EVIDENCE_REQUIRES_QA_REVIEW'
      WHEN StoredResultCFU IS NULL
           THEN N'MISSING_STORED_RESULT'
      WHEN StoredResultCFU <> IndependentlyCalculatedValue
           THEN N'STORED_CALCULATION_MISMATCH'
      WHEN StoredInterpretation<>IndependentlyCalculatedInterpretation
           THEN N'STORED_INTERPRETATION_MISMATCH'
      WHEN TRY_CONVERT(decimal(28,12),FrozenUnit) IS NOT NULL
           THEN N'NUMERIC_UNIT_REQUIRES_QA_REVIEW'
      WHEN StoredCalculationVersion IS NULL
           THEN N'MISSING_CALCULATION_VERSION'
      WHEN StoredCalculationVersion<>1
           THEN N'CALCULATION_VERSION_MISMATCH'
      ELSE N'REVIEW_REQUIRED' END AS QAReviewReason
FROM Evaluated
WHERE StoredResultCFU IS NULL
   OR StoredCalculationVersion IS NULL
   OR StoredResultCFU <> IndependentlyCalculatedValue
   OR StoredCalculationVersion <> 1
   OR FrozenAction IS NULL
   OR IndependentlyCalculatedValue IS NULL
   OR StoredInterpretation<>IndependentlyCalculatedInterpretation
   OR TRY_CONVERT(decimal(28,12),FrozenUnit) IS NOT NULL
ORDER BY EventNo,PlateID;
