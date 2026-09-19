-- READ ONLY. Run after the controlled v260 migration on the intended test/validation database.
-- No UPDATE, repair, signature insertion, or evidence backfill is performed.
SET NOCOUNT ON;

SELECT DB_NAME() AS DatabaseName, SYSDATETIME() AS CheckedAtDatabaseTime;

SELECT expected.TableName, expected.ColumnName,
       TYPE_NAME(c.system_type_id) AS ActualType, c.max_length, c.precision, c.scale, c.is_nullable,
       CASE WHEN c.system_type_id=189 AND c.is_nullable=0 THEN N'PASS' ELSE N'BLOCK' END AS RowversionContract
FROM (VALUES
    (N'dbo.Users',N'AuthenticationRowVersion'),
    (N'dbo.EM_Events',N'ResultRowVersion'),
    (N'dbo.EM_EventPlates',N'ResultRowVersion'),
    (N'dbo.SampleTests',N'ResultRowVersion')
) expected(TableName,ColumnName)
LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(expected.TableName) AND c.name=expected.ColumnName;

SELECT name,TYPE_NAME(system_type_id) AS ActualType,precision,scale,is_nullable,
       CASE WHEN name=N'ResultCFU' AND system_type_id IN(106,108) AND precision=28 AND scale=12 AND is_nullable=1 THEN N'PASS'
            WHEN name=N'ResultCalculationVersion' AND system_type_id=52 AND is_nullable=1 THEN N'PASS'
            ELSE N'BLOCK' END AS ResultSchemaContract
FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
AND name IN(N'ResultCFU',N'ResultCalculationVersion');

-- Selection below is generated from Services/EmLimitEvidenceSql.cs (read-only mode).
-- It exposes original records; the application EmTrendAssessmentService performs
-- versioned calculation/value/decision consistency checks. A NULL version is a
-- review queue indicator, NOT proof that the historical record is incorrect.
SELECT P.Id AS PlateID, P.EventId, P.Method, P.PlateCode, P.TotalCount,
       P.ResultCFU AS StoredResult, P.Status AS StoredStatus, P.ResultCalculationVersion,
       EVID.AlertLimitSnapshot,EVID.ActionLimitSnapshot,EVID.ResultUnitSnapshot,
       EVID.AirVolumeLitersSnapshot,EVID.EvidenceComplete,EVID.ReconciliationID,EVID.EvidenceSource,
       CASE WHEN EVID.EvidenceComplete=0 THEN N'Missing/invalid frozen evidence'
            WHEN P.ResultCFU IS NOT NULL AND P.ResultCalculationVersion IS NULL THEN N'Legacy value: verify in controlled trend'
            ELSE N'Use controlled trend for value/decision verification' END AS ReviewContext
FROM dbo.EM_EventPlates P

OUTER APPLY
(
    SELECT TOP(1) X.ReconciliationID, X.AlertLimitSnapshot, X.ActionLimitSnapshot,
        X.ResultUnitSnapshot, X.AirVolumeLitersSnapshot, X.SignedBy, X.SignedAt,
        X.MeaningOfSignature, X.EvidenceReference, X.ReconciliationSchemaVersion
    FROM dbo.EM_LimitSnapshotReconciliations X
    WHERE X.PlateID=P.Id
    ORDER BY X.ReconciliationID DESC
) R
CROSS APPLY
(
    SELECT CASE WHEN 
P.AlertLimitSnapshot IS NOT NULL AND P.ActionLimitSnapshot IS NOT NULL
AND P.AlertLimitSnapshot>=0 AND P.ActionLimitSnapshot>=P.AlertLimitSnapshot
AND NULLIF(LTRIM(RTRIM(P.ResultUnitSnapshot)),N'') IS NOT NULL
AND (UPPER(LTRIM(RTRIM(P.Method)))<>N'ACTIVE AIR SAMPLING' OR ISNULL(P.AirVolumeLitersSnapshot,0)>0) THEN 1 ELSE 0 END AS NativeSnapshotComplete,
        CASE WHEN R.ReconciliationID IS NOT NULL AND R.ReconciliationSchemaVersion=1
            AND R.AlertLimitSnapshot>=0 AND R.ActionLimitSnapshot>=R.AlertLimitSnapshot
            AND NULLIF(LTRIM(RTRIM(R.ResultUnitSnapshot)),N'') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(R.SignedBy)),N'') IS NOT NULL AND R.SignedAt IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(R.MeaningOfSignature)),N'') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(R.EvidenceReference)),N'') IS NOT NULL
            AND (UPPER(LTRIM(RTRIM(P.Method)))<>N'ACTIVE AIR SAMPLING' OR ISNULL(R.AirVolumeLitersSnapshot,0)>0)
            THEN 1 ELSE 0 END AS ReconciliationComplete
) N
CROSS APPLY
(
    SELECT
        CASE WHEN N.NativeSnapshotComplete=1 THEN P.AlertLimitSnapshot
             WHEN N.ReconciliationComplete=1 THEN R.AlertLimitSnapshot END AS AlertLimitSnapshot,
        CASE WHEN N.NativeSnapshotComplete=1 THEN P.ActionLimitSnapshot
             WHEN N.ReconciliationComplete=1 THEN R.ActionLimitSnapshot END AS ActionLimitSnapshot,
        CASE WHEN N.NativeSnapshotComplete=1 THEN P.ResultUnitSnapshot
             WHEN N.ReconciliationComplete=1 THEN R.ResultUnitSnapshot END AS ResultUnitSnapshot,
        CASE WHEN N.NativeSnapshotComplete=1 THEN P.AirVolumeLitersSnapshot
             WHEN N.ReconciliationComplete=1 THEN R.AirVolumeLitersSnapshot END AS AirVolumeLitersSnapshot,
        CASE WHEN N.NativeSnapshotComplete=0 AND N.ReconciliationComplete=1 THEN R.ReconciliationID END AS ReconciliationID,
        N.NativeSnapshotComplete,
        CASE WHEN N.NativeSnapshotComplete=1 OR N.ReconciliationComplete=1 THEN 1 ELSE 0 END AS EvidenceComplete,
        CASE WHEN N.NativeSnapshotComplete=1 THEN N'Native Frozen Snapshot'
             WHEN N.ReconciliationComplete=1 THEN N'Signed Historical Reconciliation #'+CONVERT(nvarchar(20),R.ReconciliationID)
             ELSE N'Missing or invalid frozen evidence' END AS EvidenceSource
) EVID

ORDER BY P.EventId,P.Id;
