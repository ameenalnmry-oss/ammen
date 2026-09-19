namespace PharmaLIMS.Services
{
    /// <summary>
    /// Shared historical-evidence selection for entry, transactional save, trend
    /// and preflight. Callers use alias P for the plate. Never consult today's
    /// master limits. An invalid newest reconciliation does not select an older one.
    /// </summary>
    internal static class EmLimitEvidenceSql
    {
        internal const string NativeComplete = @"
P.AlertLimitSnapshot IS NOT NULL AND P.ActionLimitSnapshot IS NOT NULL
AND P.AlertLimitSnapshot>=0 AND P.ActionLimitSnapshot>=P.AlertLimitSnapshot
AND NULLIF(LTRIM(RTRIM(P.ResultUnitSnapshot)),N'') IS NOT NULL
AND (UPPER(LTRIM(RTRIM(P.Method)))<>N'ACTIVE AIR SAMPLING' OR ISNULL(P.AirVolumeLitersSnapshot,0)>0)";

        internal static string Joins(bool forUpdate = false)
        {
            string hint = forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
            return @"
OUTER APPLY
(
    SELECT TOP(1) X.ReconciliationID, X.AlertLimitSnapshot, X.ActionLimitSnapshot,
        X.ResultUnitSnapshot, X.AirVolumeLitersSnapshot, X.SignedBy, X.SignedAt,
        X.MeaningOfSignature, X.EvidenceReference, X.ReconciliationSchemaVersion
    FROM dbo.EM_LimitSnapshotReconciliations X" + hint + @"
    WHERE X.PlateID=P.Id
    ORDER BY X.ReconciliationID DESC
) R
CROSS APPLY
(
    SELECT CASE WHEN " + NativeComplete + @" THEN 1 ELSE 0 END AS NativeSnapshotComplete,
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
";
        }
    }
}
