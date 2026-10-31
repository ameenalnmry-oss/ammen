using System;
namespace PharmaLIMS.TrustedSigning;

/// <summary>One reviewed snapshot definition for the read-only desktop and owner-executing SQL boundary.</summary>
public static class WaterWorkflowSqlContract
{
    public const string SnapshotMarker = "/*__WATER_WORKFLOW_SNAPSHOT_SQL__*/";
    public const string SnapshotQuery = """
SELECT s.*,wp.PointCode,wp.WaterType AS PointWaterType,
 COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode,p.PointCode,N'') AS EffectivePointCode
FROM dbo.Samples s {LOCK}
LEFT JOIN dbo.WaterSamplingPoints wp {LOCK} ON wp.Id=s.PointID
LEFT JOIN dbo.SamplingPoints p {LOCK} ON p.PointID=s.PointID
WHERE s.SampleID=@sampleId;

SELECT st.SampleTestID, st.SampleID, st.TestID, st.ResultRowVersion,
       ISNULL(NULLIF(st.TestNameSnapshot,N''),t.TestName) AS TestName,
       CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.UnitSnapshot,N'') ELSE ISNULL(t.Unit,N'') END AS Unit,
       CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END AS AlertLimit,
       CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END AS ActionLimit,
       CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN 1 ELSE 0 END AS HasSpecSnapshot,
       CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.TestCategorySnapshot ELSE t.TestCategory END AS Category,
       st.ResultValue, st.Remarks, st.ResultStatus, st.DeviationType, st.LimitDescription, st.EnteredBy, st.ResultEnteredDate,
       (SELECT MAX(h.HistoryID) FROM dbo.LabEquipmentUsageHistory h {LOCK} WHERE h.Module=N'WATER' AND h.ParentRecordID=st.SampleID AND h.ResultRecordID=st.SampleTestID) AS EquipmentHistoryID,
       (SELECT u.EquipmentID FROM dbo.LabEquipmentUsage u {LOCK} WHERE u.Module=N'WATER' AND u.ParentRecordID=st.SampleID AND u.ResultRecordID=st.SampleTestID) AS EquipmentID,
       (SELECT MAX(r.EvidenceID) FROM dbo.WaterResultResourceEvidence r {LOCK} WHERE r.SampleID=st.SampleID AND r.SampleTestID=st.SampleTestID) AS ResourceEvidenceID,
       (SELECT MAX(e.EvidenceID) FROM dbo.WaterResultExecutionEvidence e {LOCK} WHERE e.SampleID=st.SampleID AND e.SampleTestID=st.SampleTestID) AS ExecutionEvidenceID
FROM dbo.SampleTests st {LOCK}
LEFT JOIN dbo.Tests t {LOCK} ON st.TestID=t.TestID
WHERE st.SampleID=@sampleId
ORDER BY st.SampleTestID;
SELECT u.*,h.HistoryID,h.PreviousEquipmentID,h.EquipmentCodeSnapshot,h.EquipmentNameSnapshot,h.EquipmentTypeSnapshot,
 h.EquipmentStatusSnapshot,h.QualificationStatusSnapshot,h.CalibrationStatusSnapshot,h.NextQualificationDateSnapshot,h.NextCalibrationDateSnapshot,
 h.ChangeType,h.MeaningOfSignature,h.ActionReason,h.SignedBy,h.UserRole,h.SignedAt,h.SourceWorkstation
FROM dbo.LabEquipmentUsage u {LOCK}
OUTER APPLY(SELECT TOP(1) h.* FROM dbo.LabEquipmentUsageHistory h {LOCK}
 WHERE h.Module=u.Module AND h.ParentRecordID=u.ParentRecordID AND h.ResultRecordID=u.ResultRecordID ORDER BY h.HistoryID DESC) h
WHERE u.Module=N'WATER' AND u.ParentRecordID=@sampleId ORDER BY u.ResultRecordID;
WITH batches AS(SELECT SampleTestID,BatchID,ROW_NUMBER() OVER(PARTITION BY SampleTestID ORDER BY MAX(EvidenceID) DESC) rn
 FROM dbo.WaterResultResourceEvidence {LOCK} WHERE SampleID=@sampleId GROUP BY SampleTestID,BatchID)
SELECT e.* FROM dbo.WaterResultResourceEvidence e {LOCK} JOIN batches b ON b.SampleTestID=e.SampleTestID AND b.BatchID=e.BatchID
WHERE e.SampleID=@sampleId AND b.rn=1 ORDER BY e.SampleTestID,e.EvidenceID;
SELECT e.* FROM dbo.WaterResultExecutionEvidence e {LOCK}
WHERE e.SampleID=@sampleId AND NOT EXISTS(SELECT 1 FROM dbo.WaterResultExecutionEvidence n {LOCK}
 WHERE n.SampleTestID=e.SampleTestID AND n.SampleID=e.SampleID AND (n.SignedAt>e.SignedAt OR (n.SignedAt=e.SignedAt AND n.EvidenceID>e.EvidenceID)))
ORDER BY e.SampleTestID,e.EvidenceID;
SELECT e.*,CONVERT(bit,CASE WHEN IsActive=1 AND EquipmentStatus=N'Active'
 AND QualificationStatus IN(N'Qualified',N'Due Soon',N'Not Required')
 AND (NextQualificationDate IS NULL OR NextQualificationDate>=CAST(SYSDATETIME() AS date))
 AND (CalibrationRequired=0 OR (CalibrationStatus IN(N'Calibrated',N'Due Soon',N'Not Required')
 AND (NextCalibrationDate IS NULL OR NextCalibrationDate>=CAST(SYSDATETIME() AS date)))) THEN 1 ELSE 0 END) AS IsAvailable
FROM dbo.LabEquipment e {LOCK} ORDER BY e.EquipmentID;
SELECT r.* FROM dbo.LabEquipmentCompatibilityRules r {LOCK} WHERE r.Module=N'WATER' ORDER BY r.RuleID;
SELECT q.* FROM dbo.QualityEvents q {LOCK} WHERE q.SampleID=@sampleId OR (q.SourceModule=N'Water' AND q.SourceRecordID=@sampleId) ORDER BY q.QualityEventID;
SELECT a.* FROM dbo.QualityEventAffectedResults a {LOCK} WHERE a.SampleTestID IN(SELECT SampleTestID FROM dbo.SampleTests {LOCK} WHERE SampleID=@sampleId)
 OR a.QualityEventID IN(SELECT QualityEventID FROM dbo.QualityEvents {LOCK} WHERE SampleID=@sampleId OR (SourceModule=N'Water' AND SourceRecordID=@sampleId)) ORDER BY a.AffectedResultID;
SELECT s.* FROM dbo.ElectronicSignatures s {LOCK} WHERE s.SampleID=@sampleId ORDER BY s.SignatureID;
SELECT c.* FROM dbo.Certificates c {LOCK} WHERE c.SampleID=@sampleId ORDER BY c.CertificateID;
SELECT ps.* FROM dbo.Water_PlanSamples ps {LOCK} WHERE ps.WaterPlanID IN(SELECT WaterPlanID FROM dbo.Water_PlanSamples {LOCK} WHERE SampleID=@sampleId) ORDER BY ps.WaterPlanSampleID;
SELECT p.* FROM dbo.Water_Plans p {LOCK} WHERE p.WaterPlanID IN(SELECT WaterPlanID FROM dbo.Water_PlanSamples {LOCK} WHERE SampleID=@sampleId) ORDER BY p.WaterPlanID;
SELECT SYSDATETIME() AS DatabaseNow,SYSUTCDATETIME() AS DatabaseUtcNow;
""";

    public static string ComposeStage(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        int index = template.IndexOf(SnapshotMarker, StringComparison.Ordinal);
        if (index < 0 || template.IndexOf(SnapshotMarker, index + SnapshotMarker.Length, StringComparison.Ordinal) >= 0)
            throw new ArgumentException("The Water workflow SQL template must contain exactly one snapshot marker.", nameof(template));
        string ownerQuery = SnapshotQuery.Replace("{LOCK}", "WITH (UPDLOCK,HOLDLOCK)", StringComparison.Ordinal);
        return template.Replace(SnapshotMarker, ownerQuery.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
