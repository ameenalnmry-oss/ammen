using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace PharmaLIMS.Services
{
    public sealed class EquipmentUsageReconciliationCandidate
    {
        public string Module { get; init; } = string.Empty;
        public int ParentRecordID { get; init; }
        public int ResultRecordID { get; init; }
        public int? ExistingEquipmentID { get; init; }
        public string RecordReference { get; init; } = string.Empty;
        public string TestName { get; init; } = string.Empty;
        public DateTime? ResultEnteredAt { get; init; }
        public string GapType { get; init; } = string.Empty;
        public string GapDescription { get; init; } = string.Empty;
        public string Display => $"{Module} / {RecordReference} / Result {ResultRecordID}";
        public string ResultEnteredText => ResultEnteredAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Unknown";
    }

    public sealed class HistoricalEquipmentChoice
    {
        public int EquipmentID { get; init; }
        public string EquipmentCode { get; init; } = string.Empty;
        public string EquipmentName { get; init; } = string.Empty;
        public string EquipmentType { get; init; } = string.Empty;
        public bool IsCurrentlyReady { get; init; }
        public string CurrentReadiness { get; init; } = string.Empty;
        public string DisplayName => EquipmentCode + " - " + EquipmentName +
            (IsCurrentlyReady ? string.Empty : " [current: " + CurrentReadiness + "]");
    }

    /// <summary>
    /// Append-only QA reconciliation for historical equipment-use evidence gaps.
    /// This service never rewrites the original result, never backdates a signature,
    /// and never creates a current LabEquipmentUsage assignment for a historical gap.
    /// </summary>
    public static class EquipmentUsageReconciliationService
    {
        private const string MissingAssignment = "MISSING_HISTORICAL_ASSIGNMENT";
        private const string MissingHistory = "MISSING_SIGNED_HISTORY";

        public static IReadOnlyList<EquipmentUsageReconciliationCandidate> LoadCandidates()
        {
            DataTable table = DatabaseHelper.ExecuteQuery(@"
DECLARE @EquipmentCutover datetime2(0)=
(
    SELECT TOP(1) AppliedAt
    FROM dbo.LIMS_SchemaVersions
    WHERE VersionKey=N'20261008_002'
    ORDER BY AppliedAt
);
DECLARE @ResourceCutover datetime2(0)=
(
    SELECT TOP(1) AppliedAt
    FROM dbo.LIMS_SchemaVersions
    WHERE VersionKey=N'20261009_002'
    ORDER BY AppliedAt
);

DECLARE @Candidates TABLE
(
    Module nvarchar(20) NOT NULL,
    ParentRecordID int NOT NULL,
    ResultRecordID int NOT NULL,
    ExistingEquipmentID int NULL,
    RecordReference nvarchar(200) NOT NULL,
    TestName nvarchar(250) NOT NULL,
    ResultEnteredAt datetime2(0) NULL,
    GapType nvarchar(60) NOT NULL,
    GapDescription nvarchar(500) NOT NULL
);

-- Only the narrow historical Water interval can be reconciled when the original
-- result predates controlled resource evidence but postdates equipment traceability.
IF @EquipmentCutover IS NOT NULL AND @ResourceCutover IS NOT NULL
BEGIN
    INSERT @Candidates
    SELECT N'WATER',
           st.SampleID,
           st.SampleTestID,
           NULL,
           COALESCE(NULLIF(LTRIM(RTRIM(s.SampleCode)),N''),N'SampleID '+CONVERT(nvarchar(20),st.SampleID)),
           COALESCE(NULLIF(LTRIM(RTRIM(st.TestNameSnapshot)),N''),NULLIF(LTRIM(RTRIM(t.TestName)),N''),N'TestID '+CONVERT(nvarchar(20),st.TestID)),
           st.ResultEnteredDate,
           N'MISSING_HISTORICAL_ASSIGNMENT',
           N'Historical Water result predates controlled resource-evidence cutover and has no equipment-use evidence.'
    FROM dbo.SampleTests st
    LEFT JOIN dbo.Samples s ON s.SampleID=st.SampleID
    LEFT JOIN dbo.Tests t ON t.TestID=st.TestID
    WHERE st.ResultEnteredDate>=@EquipmentCutover
      AND st.ResultEnteredDate<@ResourceCutover
      AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200),st.ResultValue))),N'') IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.LabEquipmentUsage u
          WHERE u.Module=N'WATER'
            AND u.ParentRecordID=st.SampleID
            AND u.ResultRecordID=st.SampleTestID
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.LabEquipmentUsageReconciliations r
          WHERE r.Module=N'WATER'
            AND r.ParentRecordID=st.SampleID
            AND r.ResultRecordID=st.SampleTestID
            AND NULLIF(LTRIM(RTRIM(r.SignedBy)),N'') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(r.EvidenceReference)),N'') IS NOT NULL
            AND NOT EXISTS
            (
                SELECT 1 FROM dbo.LabEquipmentUsageReconciliations newer
                WHERE newer.SupersedesReconciliationID=r.ReconciliationID
            )
      );
END;

-- A current assignment without immutable signed history can be documented through
-- a QA reconciliation. The selected equipment is locked to the current assignment.
INSERT @Candidates
SELECT u.Module,
       u.ParentRecordID,
       u.ResultRecordID,
       u.EquipmentID,
       CASE
           WHEN u.Module=N'WATER' THEN N'Water SampleID '+CONVERT(nvarchar(20),u.ParentRecordID)
           WHEN u.Module=N'PRM' THEN N'PRM SampleID '+CONVERT(nvarchar(20),u.ParentRecordID)
           WHEN u.Module=N'EM' THEN N'EM EventID '+CONVERT(nvarchar(20),u.ParentRecordID)
           ELSE u.Module+N' '+CONVERT(nvarchar(20),u.ParentRecordID)
       END,
       CASE
           WHEN u.Module=N'WATER' THEN COALESCE(NULLIF(LTRIM(RTRIM(wst.TestNameSnapshot)),N''),NULLIF(LTRIM(RTRIM(wt.TestName)),N''),N'Result '+CONVERT(nvarchar(20),u.ResultRecordID))
           WHEN u.Module=N'PRM' THEN COALESCE(NULLIF(LTRIM(RTRIM(pst.TestName)),N''),N'Result '+CONVERT(nvarchar(20),u.ResultRecordID))
           WHEN u.Module=N'EM' THEN N'EM equipment activity'
           ELSE N'Result '+CONVERT(nvarchar(20),u.ResultRecordID)
       END,
       CASE
           WHEN u.Module=N'WATER' THEN wst.ResultEnteredDate
           ELSE u.AssignedAt
       END,
       N'MISSING_SIGNED_HISTORY',
       N'Current equipment assignment exists but no matching immutable signed usage-history evidence is present.'
FROM dbo.LabEquipmentUsage u
LEFT JOIN dbo.SampleTests wst ON u.Module=N'WATER' AND wst.SampleTestID=u.ResultRecordID
LEFT JOIN dbo.Tests wt ON wt.TestID=wst.TestID
LEFT JOIN dbo.PRM_SampleTests pst ON u.Module=N'PRM' AND pst.SampleTestID=u.ResultRecordID
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
)
AND NOT EXISTS
(
    SELECT 1
    FROM dbo.LabEquipmentUsageReconciliations r
    WHERE r.Module=u.Module
      AND r.ParentRecordID=u.ParentRecordID
      AND r.ResultRecordID=u.ResultRecordID
      AND r.EquipmentID=u.EquipmentID
      AND NULLIF(LTRIM(RTRIM(r.SignedBy)),N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(r.EvidenceReference)),N'') IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.LabEquipmentUsageReconciliations newer
          WHERE newer.SupersedesReconciliationID=r.ReconciliationID
      )
);

SELECT Module,ParentRecordID,ResultRecordID,ExistingEquipmentID,
       RecordReference,TestName,ResultEnteredAt,GapType,GapDescription
FROM @Candidates
ORDER BY Module,RecordReference,ResultRecordID;");

            return table.Rows.Cast<DataRow>().Select(row => new EquipmentUsageReconciliationCandidate
            {
                Module = Convert.ToString(row["Module"], CultureInfo.InvariantCulture) ?? string.Empty,
                ParentRecordID = Convert.ToInt32(row["ParentRecordID"], CultureInfo.InvariantCulture),
                ResultRecordID = Convert.ToInt32(row["ResultRecordID"], CultureInfo.InvariantCulture),
                ExistingEquipmentID = row["ExistingEquipmentID"] == DBNull.Value
                    ? null
                    : Convert.ToInt32(row["ExistingEquipmentID"], CultureInfo.InvariantCulture),
                RecordReference = Convert.ToString(row["RecordReference"], CultureInfo.InvariantCulture) ?? string.Empty,
                TestName = Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? string.Empty,
                ResultEnteredAt = row["ResultEnteredAt"] == DBNull.Value
                    ? null
                    : Convert.ToDateTime(row["ResultEnteredAt"], CultureInfo.InvariantCulture),
                GapType = Convert.ToString(row["GapType"], CultureInfo.InvariantCulture) ?? string.Empty,
                GapDescription = Convert.ToString(row["GapDescription"], CultureInfo.InvariantCulture) ?? string.Empty
            }).ToList();
        }

        public static IReadOnlyList<HistoricalEquipmentChoice> LoadEquipment()
        {
            DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT EquipmentID,EquipmentCode,EquipmentName,EquipmentType,
       CASE
         WHEN IsActive=1
          AND EquipmentStatus=N'Active'
          AND QualificationStatus IN(N'Qualified',N'Due Soon',N'Not Required')
          AND (NextQualificationDate IS NULL OR NextQualificationDate>=CAST(SYSDATETIME() AS date))
          AND
          (
            CalibrationRequired=0 OR
            (CalibrationStatus IN(N'Calibrated',N'Due Soon',N'Not Required')
             AND (NextCalibrationDate IS NULL OR NextCalibrationDate>=CAST(SYSDATETIME() AS date)))
          )
         THEN CAST(1 AS bit) ELSE CAST(0 AS bit)
       END AS IsCurrentlyReady,
       CASE
         WHEN IsActive=0 THEN N'Inactive'
         WHEN EquipmentStatus<>N'Active' THEN EquipmentStatus
         WHEN QualificationStatus NOT IN(N'Qualified',N'Due Soon',N'Not Required') THEN QualificationStatus
         WHEN NextQualificationDate IS NOT NULL AND NextQualificationDate<CAST(SYSDATETIME() AS date) THEN N'Qualification Expired'
         WHEN CalibrationRequired=1 AND CalibrationStatus NOT IN(N'Calibrated',N'Due Soon',N'Not Required') THEN CalibrationStatus
         WHEN CalibrationRequired=1 AND NextCalibrationDate IS NOT NULL AND NextCalibrationDate<CAST(SYSDATETIME() AS date) THEN N'Calibration Expired'
         ELSE N'Currently Ready'
       END AS CurrentReadiness
FROM dbo.LabEquipment
ORDER BY EquipmentCode;");

            return table.Rows.Cast<DataRow>().Select(row => new HistoricalEquipmentChoice
            {
                EquipmentID = Convert.ToInt32(row["EquipmentID"], CultureInfo.InvariantCulture),
                EquipmentCode = Convert.ToString(row["EquipmentCode"], CultureInfo.InvariantCulture) ?? string.Empty,
                EquipmentName = Convert.ToString(row["EquipmentName"], CultureInfo.InvariantCulture) ?? string.Empty,
                EquipmentType = Convert.ToString(row["EquipmentType"], CultureInfo.InvariantCulture) ?? string.Empty,
                IsCurrentlyReady = Convert.ToBoolean(row["IsCurrentlyReady"], CultureInfo.InvariantCulture),
                CurrentReadiness = Convert.ToString(row["CurrentReadiness"], CultureInfo.InvariantCulture) ?? string.Empty
            }).ToList();
        }

        public static void Save(
            EquipmentUsageReconciliationCandidate candidate,
            int equipmentId,
            string evidenceReference,
            string evidenceSummary,
            string signedBy,
            string meaningOfSignature,
            string actionReason)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));
            if (equipmentId <= 0)
                throw new InvalidOperationException("Select the historically used equipment.");
            if (string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Trim().Length < 4)
                throw new InvalidOperationException("A controlled historical evidence reference is required.");
            if (string.IsNullOrWhiteSpace(evidenceSummary) || evidenceSummary.Trim().Length < 12)
                throw new InvalidOperationException("Describe the controlled evidence reviewed before reconciliation.");
            if (string.IsNullOrWhiteSpace(actionReason) || actionReason.Trim().Length < 12)
                throw new InvalidOperationException("A specific reconciliation reason is required.");
            if (IsPlaceholder(evidenceReference) || IsPlaceholder(evidenceSummary) || IsPlaceholder(actionReason))
                throw new InvalidOperationException("Placeholder reconciliation evidence is not permitted.");

            string evidenceRef = evidenceReference.Trim();
            string evidenceText = evidenceSummary.Trim();
            string reason = actionReason.Trim();
            string signer = signedBy?.Trim() ?? string.Empty;
            string meaning = meaningOfSignature?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(signer) || string.IsNullOrWhiteSpace(meaning))
                throw new InvalidOperationException("A confirmed electronic signature is required.");

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                string role = DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection,
                    transaction,
                    signer,
                    "CanApproveResults",
                    "reconcile historical laboratory equipment-use evidence");

                LockAndRevalidateCandidate(connection, transaction, candidate, equipmentId);

                string equipmentCode;
                string equipmentName;
                string equipmentType;
                using (var equipment = new SqlCommand(@"
SELECT EquipmentCode,EquipmentName,EquipmentType
FROM dbo.LabEquipment WITH(UPDLOCK,HOLDLOCK)
WHERE EquipmentID=@EquipmentID;", connection, transaction))
                {
                    equipment.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = equipmentId;
                    using SqlDataReader reader = equipment.ExecuteReader();
                    if (!reader.Read())
                        throw new DBConcurrencyException("The selected equipment no longer exists.");

                    equipmentCode = reader.GetString(0);
                    equipmentName = reader.GetString(1);
                    equipmentType = reader.GetString(2);
                }

                using (var duplicate = new SqlCommand(@"
SELECT TOP(1) ReconciliationID
FROM dbo.LabEquipmentUsageReconciliations WITH(UPDLOCK,HOLDLOCK)
WHERE Module=@Module
  AND ParentRecordID=@ParentRecordID
  AND ResultRecordID=@ResultRecordID
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.LabEquipmentUsageReconciliations newer
      WHERE newer.SupersedesReconciliationID=LabEquipmentUsageReconciliations.ReconciliationID
  );", connection, transaction))
                {
                    duplicate.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = candidate.Module;
                    duplicate.Parameters.Add("@ParentRecordID", SqlDbType.Int).Value = candidate.ParentRecordID;
                    duplicate.Parameters.Add("@ResultRecordID", SqlDbType.Int).Value = candidate.ResultRecordID;
                    if (duplicate.ExecuteScalar() != null)
                        throw new DBConcurrencyException("This evidence gap was already reconciled. Refresh System Preflight.");
                }

                using var insert = new SqlCommand(@"
INSERT dbo.LabEquipmentUsageReconciliations
(
 Module,ParentRecordID,ResultRecordID,EquipmentID,
 EquipmentCodeSnapshot,EquipmentNameSnapshot,EquipmentTypeSnapshot,
 ResultEnteredAtSnapshot,GapType,EvidenceReference,EvidenceSummary,
 MeaningOfSignature,ActionReason,SignedBy,UserRole,SignedAt,SourceWorkstation
)
OUTPUT INSERTED.ReconciliationID
VALUES
(
 @Module,@ParentRecordID,@ResultRecordID,@EquipmentID,
 @EquipmentCode,@EquipmentName,@EquipmentType,
 @ResultEnteredAt,@GapType,@EvidenceReference,@EvidenceSummary,
 @Meaning,@Reason,@SignedBy,@UserRole,SYSUTCDATETIME(),@Workstation
);", connection, transaction);

                insert.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = candidate.Module;
                insert.Parameters.Add("@ParentRecordID", SqlDbType.Int).Value = candidate.ParentRecordID;
                insert.Parameters.Add("@ResultRecordID", SqlDbType.Int).Value = candidate.ResultRecordID;
                insert.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = equipmentId;
                insert.Parameters.Add("@EquipmentCode", SqlDbType.NVarChar, 50).Value = equipmentCode;
                insert.Parameters.Add("@EquipmentName", SqlDbType.NVarChar, 200).Value = equipmentName;
                insert.Parameters.Add("@EquipmentType", SqlDbType.NVarChar, 100).Value = equipmentType;
                insert.Parameters.Add("@ResultEnteredAt", SqlDbType.DateTime2).Value =
                    candidate.ResultEnteredAt.HasValue ? candidate.ResultEnteredAt.Value : DBNull.Value;
                insert.Parameters.Add("@GapType", SqlDbType.NVarChar, 60).Value = candidate.GapType;
                insert.Parameters.Add("@EvidenceReference", SqlDbType.NVarChar, 250).Value = evidenceRef;
                insert.Parameters.Add("@EvidenceSummary", SqlDbType.NVarChar, 1500).Value = evidenceText;
                insert.Parameters.Add("@Meaning", SqlDbType.NVarChar, 250).Value = meaning;
                insert.Parameters.Add("@Reason", SqlDbType.NVarChar, 700).Value = reason;
                insert.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = signer;
                insert.Parameters.Add("@UserRole", SqlDbType.NVarChar, 100).Value = role;
                insert.Parameters.Add("@Workstation", SqlDbType.NVarChar, 100).Value = Environment.MachineName;
                long reconciliationId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "LabEquipmentUsageReconciliations",
                    candidate.ResultRecordID,
                    "Historical Equipment Usage Reconciliation",
                    candidate.GapDescription,
                    $"Equipment={equipmentCode}; Evidence={evidenceRef}; ReconciliationID={reconciliationId}",
                    reason,
                    signer,
                    "EquipmentUsageEvidence",
                    null,
                    candidate.RecordReference,
                    candidate.Module);
            });
        }

        private static void LockAndRevalidateCandidate(
            SqlConnection connection,
            SqlTransaction transaction,
            EquipmentUsageReconciliationCandidate candidate,
            int selectedEquipmentId)
        {
            if (candidate.GapType == MissingHistory)
            {
                using var current = new SqlCommand(@"
SELECT EquipmentID
FROM dbo.LabEquipmentUsage WITH(UPDLOCK,HOLDLOCK)
WHERE Module=@Module AND ParentRecordID=@ParentRecordID AND ResultRecordID=@ResultRecordID;", connection, transaction);
                current.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = candidate.Module;
                current.Parameters.Add("@ParentRecordID", SqlDbType.Int).Value = candidate.ParentRecordID;
                current.Parameters.Add("@ResultRecordID", SqlDbType.Int).Value = candidate.ResultRecordID;
                object? value = current.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    throw new DBConcurrencyException("The equipment assignment changed. Refresh the reconciliation list.");

                int currentEquipment = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if (currentEquipment != selectedEquipmentId ||
                    (candidate.ExistingEquipmentID.HasValue && candidate.ExistingEquipmentID.Value != currentEquipment))
                    throw new DBConcurrencyException("The selected equipment no longer matches the current assignment.");

                using var history = new SqlCommand(@"
SELECT TOP(1) HistoryID
FROM dbo.LabEquipmentUsageHistory WITH(UPDLOCK,HOLDLOCK)
WHERE Module=@Module
  AND ParentRecordID=@ParentRecordID
  AND ResultRecordID=@ResultRecordID
  AND EquipmentID=@EquipmentID
  AND ChangeType IN(N'ASSIGN',N'REASSIGN')
  AND NULLIF(LTRIM(RTRIM(SignedBy)),N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(MeaningOfSignature)),N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(ActionReason)),N'') IS NOT NULL;", connection, transaction);
                history.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = candidate.Module;
                history.Parameters.Add("@ParentRecordID", SqlDbType.Int).Value = candidate.ParentRecordID;
                history.Parameters.Add("@ResultRecordID", SqlDbType.Int).Value = candidate.ResultRecordID;
                history.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = selectedEquipmentId;
                if (history.ExecuteScalar() != null)
                    throw new DBConcurrencyException("Signed equipment-use history now exists. Refresh System Preflight.");

                return;
            }

            if (candidate.GapType != MissingAssignment ||
                !candidate.Module.Equals("WATER", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("This finding is not eligible for controlled equipment-use reconciliation.");
            }

            using var result = new SqlCommand(@"
DECLARE @EquipmentCutover datetime2(0)=
(
 SELECT TOP(1) AppliedAt FROM dbo.LIMS_SchemaVersions
 WHERE VersionKey=N'20261008_002' ORDER BY AppliedAt
);
DECLARE @ResourceCutover datetime2(0)=
(
 SELECT TOP(1) AppliedAt FROM dbo.LIMS_SchemaVersions
 WHERE VersionKey=N'20261009_002' ORDER BY AppliedAt
);
SELECT st.ResultEnteredDate
FROM dbo.SampleTests st WITH(UPDLOCK,HOLDLOCK)
WHERE st.SampleID=@ParentRecordID
  AND st.SampleTestID=@ResultRecordID
  AND st.ResultEnteredDate>=@EquipmentCutover
  AND st.ResultEnteredDate<@ResourceCutover
  AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200),st.ResultValue))),N'') IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.LabEquipmentUsage u WITH(UPDLOCK,HOLDLOCK)
      WHERE u.Module=N'WATER' AND u.ParentRecordID=st.SampleID AND u.ResultRecordID=st.SampleTestID
  );", connection, transaction);
            result.Parameters.Add("@ParentRecordID", SqlDbType.Int).Value = candidate.ParentRecordID;
            result.Parameters.Add("@ResultRecordID", SqlDbType.Int).Value = candidate.ResultRecordID;
            object? entered = result.ExecuteScalar();
            if (entered == null || entered == DBNull.Value)
                throw new DBConcurrencyException("The historical result is no longer eligible for reconciliation. Refresh System Preflight.");
        }

        private static bool IsPlaceholder(string text)
        {
            string normalized = (text ?? string.Empty).Trim().ToUpperInvariant();
            return normalized is "N/A" or "NA" or "NONE" or "UNKNOWN" or "NOT FOUND" or "NOT AVAILABLE" or "-";
        }
    }
}
