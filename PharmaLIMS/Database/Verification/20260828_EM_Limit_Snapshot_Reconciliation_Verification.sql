SET NOCOUNT ON;

DECLARE @Missing TABLE(Item nvarchar(300) NOT NULL);

IF OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations',N'U') IS NULL
    INSERT @Missing VALUES(N'dbo.EM_LimitSnapshotReconciliations');
IF NOT EXISTS(SELECT 1 FROM sys.triggers WHERE object_id=OBJECT_ID(N'dbo.TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828') AND is_disabled=0)
    INSERT @Missing VALUES(N'TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828 enabled');
IF NOT EXISTS(SELECT 1 FROM sys.triggers WHERE object_id=OBJECT_ID(N'dbo.TRG_EM_EventPlates_FreezeLimits_20260828') AND is_disabled=0)
    INSERT @Missing VALUES(N'TRG_EM_EventPlates_FreezeLimits_20260828 enabled');
IF NOT EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260828_003')
    INSERT @Missing VALUES(N'LIMS_SchemaVersions 20260828_003');

IF OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations',N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'PlateID') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.PlateID');
    IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'SupersedesReconciliationID') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.SupersedesReconciliationID');
    IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'AlertLimitSnapshot') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.AlertLimitSnapshot');
    IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'ActionLimitSnapshot') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.ActionLimitSnapshot');
    IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'ResultUnitSnapshot') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.ResultUnitSnapshot');
    IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'EvidenceReference') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.EvidenceReference');
    IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'Reason') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.Reason');
    IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'SignedBy') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.SignedBy');
    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations') AND name=N'IX_EM_LimitSnapshotReconciliations_Plate') INSERT @Missing VALUES(N'IX_EM_LimitSnapshotReconciliations_Plate');
END;

SELECT Item AS MissingOrInvalid FROM @Missing ORDER BY Item;

;WITH EffectiveEvidence AS
(
    SELECT P.Id,E.EventNo,P.PlateCode,P.Method,A.Grade,
           CASE WHEN P.AlertLimitSnapshot IS NOT NULL AND P.ActionLimitSnapshot IS NOT NULL
                      AND NULLIF(LTRIM(RTRIM(ISNULL(P.ResultUnitSnapshot,N''))),N'') IS NOT NULL
                      AND (UPPER(LTRIM(RTRIM(P.Method)))<>N'ACTIVE AIR SAMPLING' OR ISNULL(P.AirVolumeLitersSnapshot,0)>0)
                THEN N'Native'
                WHEN R.ReconciliationID IS NOT NULL THEN N'Reconciled'
                ELSE N'Unresolved' END AS EvidenceState
    FROM dbo.EM_EventPlates P
    INNER JOIN dbo.EM_Events E ON E.Id=P.EventId
    INNER JOIN dbo.EM_Areas A ON A.Id=E.AreaId
    OUTER APPLY
    (
        SELECT TOP(1) X.ReconciliationID
        FROM dbo.EM_LimitSnapshotReconciliations X
        WHERE X.PlateID=P.Id
        ORDER BY X.ReconciliationID DESC
    ) R
)
SELECT EvidenceState,COUNT(*) PlateCount
FROM EffectiveEvidence
GROUP BY EvidenceState
ORDER BY EvidenceState;
