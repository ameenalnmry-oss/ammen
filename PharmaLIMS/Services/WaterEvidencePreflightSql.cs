namespace PharmaLIMS.Services;
internal static class WaterEvidencePreflightSql
{
    internal const string EquipmentUsage = @"
DECLARE @Cutover datetime2(0)=
(
    SELECT TOP(1) AppliedAt
    FROM dbo.LIMS_SchemaVersions
    WHERE VersionKey=N'20261008_002'
    ORDER BY AppliedAt DESC
);

DECLARE @ResourceCutover datetime2(0)=(SELECT TOP(1) AppliedAt FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20261009_002' ORDER BY AppliedAt DESC);

DECLARE @Findings TABLE(Details nvarchar(1000) NOT NULL);

IF @Cutover IS NOT NULL
BEGIN
    INSERT @Findings(Details)
    SELECT N'Water SampleTestID ' + CONVERT(nvarchar(20),st.SampleTestID) + N' lacks valid current signed resource evidence.'
    FROM dbo.SampleTests st
    LEFT JOIN dbo.Tests t ON t.TestID=st.TestID
    CROSS APPLY(SELECT UPPER(LTRIM(RTRIM(COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName)))) AS TestName) name
    OUTER APPLY(SELECT TOP(1) BatchID FROM dbo.WaterResultResourceEvidence WHERE SampleTestID=st.SampleTestID AND SampleID=st.SampleID ORDER BY EvidenceID DESC) b
    OUTER APPLY(SELECT TOP(1) * FROM dbo.WaterResultResourceEvidence WHERE SampleTestID=st.SampleTestID AND SampleID=st.SampleID AND BatchID=b.BatchID ORDER BY EvidenceID) primaryResource
    WHERE st.ResultEnteredDate>=@Cutover AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200),st.ResultValue))),N'') IS NOT NULL
    AND ((st.ResultEnteredDate<@ResourceCutover AND NOT EXISTS(SELECT 1 FROM dbo.LabEquipmentUsage u WHERE u.Module=N'WATER' AND u.ParentRecordID=st.SampleID AND u.ResultRecordID=st.SampleTestID))
    OR (st.ResultEnteredDate>=@ResourceCutover AND
      (primaryResource.EvidenceID IS NULL OR NOT
       ((primaryResource.ResourceKind=N'NO_INSTRUMENT' AND name.TestName=N'TURBIDITY'
         AND OBJECT_ID(N'dbo.WaterVisualMethodEvidence',N'U') IS NOT NULL
         AND EXISTS(SELECT 1 FROM dbo.WaterVisualMethodEvidence v WHERE v.SampleID=st.SampleID AND v.SampleTestID=st.SampleTestID
         AND v.ResourceEvidenceID=primaryResource.EvidenceID AND v.IsVoid=0
         AND NULLIF(LTRIM(RTRIM(v.VisualMethodReference)),N'') IS NOT NULL
         AND NULLIF(LTRIM(RTRIM(v.ObservationDescription)),N'') IS NOT NULL
         AND NULLIF(LTRIM(RTRIM(v.SignedBy)),N'') IS NOT NULL))
       OR (primaryResource.ResourceKind=N'NO_INSTRUMENT' AND UPPER(LTRIM(RTRIM(COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName)))) IN
         (N'APPEARANCE',N'APPEARANCE (COLOR & CLARITY)',N'COLOR & CLARITY',N'COLOUR & CLARITY',N'DESCRIPTION',N'ODOR',N'ODOUR',N'TASTE'))
       OR (primaryResource.ResourceKind=N'TEST_KIT' AND (name.TestName LIKE N'%CHLOR%' OR name.TestName LIKE N'%HARDNESS%' OR name.TestName LIKE N'%CALCIUM%' OR name.TestName LIKE N'%MAGNESIUM%' OR name.TestName LIKE N'%NITRATE%' OR name.TestName LIKE N'%SULPHATE%' OR name.TestName LIKE N'%SULFATE%' OR name.TestName LIKE N'%AMMON%' OR name.TestName IN(N'ACIDITY',N'ALKALINITY',N'OXIDISABLE SUBSTANCES',N'OXIDIZABLE SUBSTANCES',N'HEAVY METALS (AS PB)')) AND NULLIF(LTRIM(RTRIM(primaryResource.KitCode)),N'') IS NOT NULL AND NULLIF(LTRIM(RTRIM(primaryResource.KitLot)),N'') IS NOT NULL AND primaryResource.KitExpiry>=COALESCE(primaryResource.ExecutionDate,CAST(primaryResource.SignedAt AS date)))
       OR (primaryResource.ResourceKind=N'INSTRUMENT' AND name.TestName NOT IN(N'RESIDUAL CHLORINE',N'FREE CHLORINE',N'FREE RESIDUAL CHLORINE',N'CHLORINE',N'APPEARANCE',N'APPEARANCE (COLOR & CLARITY)',N'COLOR & CLARITY',N'COLOUR & CLARITY',N'DESCRIPTION',N'ODOR',N'ODOUR',N'TASTE') AND EXISTS(SELECT 1 FROM dbo.LabEquipmentUsage u WHERE u.Module=N'WATER' AND u.ParentRecordID=st.SampleID AND u.ResultRecordID=st.SampleTestID AND u.EquipmentID=primaryResource.EquipmentID)))
       OR EXISTS(SELECT 1 FROM dbo.WaterResultResourceEvidence r WHERE r.SampleTestID=st.SampleTestID AND r.SampleID=st.SampleID AND r.BatchID=b.BatchID
         AND (NULLIF(LTRIM(RTRIM(r.SignedBy)),N'') IS NULL OR r.TestID<>st.TestID OR TRY_CONVERT(decimal(18,4),r.ResultSnapshot) IS NULL OR TRY_CONVERT(decimal(18,4),st.ResultValue) IS NULL OR TRY_CONVERT(decimal(18,4),r.ResultSnapshot)<>TRY_CONVERT(decimal(18,4),st.ResultValue)
              OR (r.ResourceKind=N'TEST_KIT' AND (r.KitExpiry IS NULL OR r.KitExpiry<COALESCE(r.ExecutionDate,CAST(r.SignedAt AS date)) OR NULLIF(LTRIM(RTRIM(r.KitCode)),N'') IS NULL OR NULLIF(LTRIM(RTRIM(r.KitLot)),N'') IS NULL)))))));

    INSERT @Findings(Details)
    SELECT N'EM plate ' + CONVERT(nvarchar(20),p.Id) +
           N' has a post-cutover entered result without equipment usage evidence.'
    FROM dbo.EM_EventPlates p
    JOIN dbo.EM_Events e ON e.Id=p.EventId
    WHERE e.ResultsEnteredDate>=@Cutover
      AND p.TotalCount IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.LabEquipmentUsage u
          WHERE u.Module=N'EM' AND u.ResultRecordID=p.Id
      );

    INSERT @Findings(Details)
    SELECT N'PRM SampleTestID ' + CONVERT(nvarchar(20),st.SampleTestID) +
           N' has a post-cutover entered result without equipment usage evidence.'
    FROM dbo.PRM_SampleTests st
    WHERE st.EnteredDate>=@Cutover
      AND NULLIF(LTRIM(RTRIM(ISNULL(st.ResultValue,N''))),N'') IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.LabEquipmentUsage u
          WHERE u.Module=N'PRM' AND u.ResultRecordID=st.SampleTestID
      );
END;

INSERT @Findings(Details)
SELECT N'Equipment usage ' + u.Module + N'/' + CONVERT(nvarchar(20),u.ResultRecordID) +
       N' has no matching immutable signed usage-history evidence.'
FROM dbo.LabEquipmentUsage u
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.LabEquipmentUsageHistory h
    WHERE h.Module=u.Module
      AND h.ParentRecordID=u.ParentRecordID
      AND h.ResultRecordID=u.ResultRecordID
      AND h.EquipmentID=u.EquipmentID
      AND h.ChangeType IN(N'ASSIGN',N'REASSIGN')
      AND NULLIF(LTRIM(RTRIM(h.SignedBy)),N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(h.MeaningOfSignature)),N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(h.ActionReason)),N'') IS NOT NULL
);

INSERT @Findings(Details)
SELECT N'Water SampleTestID '+CONVERT(nvarchar(20),st.SampleTestID)+N' has signed result evidence but lacks entry attribution/date.'
FROM dbo.SampleTests st WHERE NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200),st.ResultValue))),N'') IS NOT NULL
AND (st.ResultEnteredDate IS NULL OR NULLIF(LTRIM(RTRIM(st.EnteredBy)),N'') IS NULL)
AND (EXISTS(SELECT 1 FROM dbo.WaterResultResourceEvidence r WHERE r.SampleTestID=st.SampleTestID AND r.SampleID=st.SampleID)
 OR EXISTS(SELECT 1 FROM dbo.WaterResultExecutionEvidence e WHERE e.SampleTestID=st.SampleTestID AND e.SampleID=st.SampleID));

SELECT Details FROM @Findings ORDER BY Details;";
    internal const string Les = @"DECLARE @Cutover datetime2(0)=(SELECT TOP(1) AppliedAt FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20261009_001' ORDER BY AppliedAt DESC);
DECLARE @Findings TABLE(Details nvarchar(1000) NOT NULL);
INSERT @Findings
SELECT N'Water SampleTestID '+CONVERT(nvarchar(20),st.SampleTestID)+N' lacks valid latest signed LES evidence.'
FROM dbo.SampleTests st OUTER APPLY(SELECT TOP(1) * FROM dbo.WaterResultExecutionEvidence e WHERE e.SampleTestID=st.SampleTestID AND e.SampleID=st.SampleID ORDER BY EvidenceID DESC) e
WHERE st.TestID IN(2,3) AND st.ResultEnteredDate>=@Cutover AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200),st.ResultValue))),N'') IS NOT NULL
AND (e.EvidenceID IS NULL OR e.TestID<>st.TestID OR NULLIF(LTRIM(RTRIM(e.ProcedureReference)),N'') IS NULL OR NULLIF(LTRIM(RTRIM(e.VerificationReference)),N'') IS NULL OR e.VerificationConfirmed<>1
 OR NULLIF(LTRIM(RTRIM(e.SignedBy)),N'') IS NULL OR NULLIF(LTRIM(RTRIM(e.MeaningOfSignature)),N'') IS NULL OR NULLIF(LTRIM(RTRIM(e.ActionReason)),N'') IS NULL
 OR TRY_CONVERT(decimal(18,4),e.RawResultSnapshot) IS NULL OR TRY_CONVERT(decimal(18,4),st.ResultValue) IS NULL OR TRY_CONVERT(decimal(18,4),e.RawResultSnapshot)<>TRY_CONVERT(decimal(18,4),st.ResultValue));
INSERT @Findings SELECT N'Conductivity LES evidence '+CONVERT(nvarchar(20),EvidenceID)+N' lacks valid 24–26 C temperature.' FROM dbo.WaterResultExecutionEvidence WHERE TestID=3 AND (SampleTemperatureC IS NULL OR SampleTemperatureC<24 OR SampleTemperatureC>26);
INSERT @Findings
SELECT N'Latest Water LES evidence '+CONVERT(nvarchar(20),e.EvidenceID)+N' disagrees with current instrument assignment.'
FROM dbo.WaterResultExecutionEvidence e WHERE NOT EXISTS(SELECT 1 FROM dbo.WaterResultExecutionEvidence n WHERE n.SampleID=e.SampleID AND n.SampleTestID=e.SampleTestID AND n.EvidenceID>e.EvidenceID)
AND NOT EXISTS(SELECT 1 FROM dbo.LabEquipmentUsage u WHERE u.Module=N'WATER' AND u.ParentRecordID=e.SampleID AND u.ResultRecordID=e.SampleTestID AND u.EquipmentID=e.EquipmentID);
INSERT @Findings
SELECT N'Water LES evidence '+CONVERT(nvarchar(20),e.EvidenceID)+N' lacks matching historical signed instrument use.'
FROM dbo.WaterResultExecutionEvidence e WHERE NOT EXISTS(SELECT 1 FROM dbo.LabEquipmentUsageHistory h WHERE h.Module=N'WATER' AND h.ParentRecordID=e.SampleID AND h.ResultRecordID=e.SampleTestID AND h.EquipmentID=e.EquipmentID AND h.ChangeType IN(N'ASSIGN',N'REASSIGN') AND h.SignedAt<=e.SignedAt AND NULLIF(LTRIM(RTRIM(h.SignedBy)),N'') IS NOT NULL);
SELECT Details FROM @Findings ORDER BY Details;";
}
