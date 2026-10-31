#nullable disable
using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PharmaLIMS
{
    public static partial class DatabaseHelper
    {
        internal static void EnsureEMQualityEventEvidenceCurrentInTransaction(SqlConnection connection, SqlTransaction transaction, int qualityEventId)
        {
            using SqlCommand context = new SqlCommand(@"
SELECT SourceRecordID,SampleNumber FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK)
WHERE QualityEventID=@qualityEventId AND
(UPPER(LTRIM(RTRIM(ISNULL(SourceModule,N'')))) IN(N'EM',N'ENVIRONMENTAL MONITORING')
 OR UPPER(LTRIM(RTRIM(ISNULL(DetectionSource,N''))))=N'ENVIRONMENTAL MONITORING');", connection, transaction);
            context.Parameters.Add("@qualityEventId", SqlDbType.Int).Value = qualityEventId;
            object sourceId; string eventNo;
            using (SqlDataReader reader = context.ExecuteReader())
            {
                if (!reader.Read()) return;
                sourceId = reader[0]; eventNo = Convert.ToString(reader[1], CultureInfo.InvariantCulture) ?? "";
            }
            using SqlCommand resolve = new SqlCommand(@"
SELECT Id FROM dbo.EM_Events WHERE Id=@sourceId OR EventNo=@eventNo;", connection, transaction);
            resolve.Parameters.Add("@sourceId", SqlDbType.Int).Value = sourceId;
            resolve.Parameters.Add("@eventNo", SqlDbType.NVarChar, 100).Value = eventNo;
            int eventId;
            using (SqlDataReader reader = resolve.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("The EM investigation source event is missing.");
                eventId = reader.GetInt32(0);
                if (reader.Read()) throw new InvalidOperationException("The EM investigation source identity is ambiguous.");
            }
            EnsureEMParentPlanActiveInTransaction(connection, transaction, eventId);
            if (!HasCurrentEMInvestigationEvidenceInTransaction(connection, transaction, eventId, qualityEventId))
                throw new InvalidOperationException("The EM investigation does not cover the current results. A new investigation is required.");
        }

        // Parent is locked before event/plates, matching plan cancellation/release.
        internal static void EnsureEMParentPlanActiveInTransaction(SqlConnection connection, SqlTransaction transaction, int eventId)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT E.PlanID, P.Status
FROM dbo.EM_Events E
LEFT JOIN dbo.EM_Plans P WITH(UPDLOCK,HOLDLOCK) ON P.PlanID=E.PlanID
WHERE E.Id=@eventId;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("The EM event no longer exists.");
            EmControlledLifecycleGuard.EnsurePlanActive(reader.IsDBNull(0) ? null : reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
            reader.Close();
            using SqlCommand identity = new SqlCommand(@"
IF EXISTS(SELECT 1 FROM dbo.EM_EventPlates P WITH(UPDLOCK,HOLDLOCK)
 JOIN dbo.EM_Events E WITH(UPDLOCK,HOLDLOCK) ON E.Id=P.EventId
 LEFT JOIN dbo.EM_PlanSamples S WITH(UPDLOCK,HOLDLOCK) ON S.PlanSampleID=P.PlanSampleID
 WHERE E.Id=@eventId AND (P.Method LIKE N'%personnel%' OR P.Method LIKE N'%glove%')
 AND (NULLIF(LTRIM(RTRIM(E.EmployeeId)),N'') IS NULL OR NULLIF(LTRIM(RTRIM(E.EmployeeName)),N'') IS NULL
 OR (E.PlanID IS NOT NULL AND (S.PlanSampleID IS NULL OR S.PlanID<>E.PlanID OR ISNULL(S.EventID,0)<>E.Id
 OR ISNULL(S.EmployeeID,N'') COLLATE Latin1_General_100_BIN2<>ISNULL(E.EmployeeId,N'') COLLATE Latin1_General_100_BIN2
 OR ISNULL(S.EmployeeName,N'') COLLATE Latin1_General_100_BIN2<>ISNULL(E.EmployeeName,N'') COLLATE Latin1_General_100_BIN2))))
 THROW 53440, 'Personnel monitoring identity is missing or differs from the controlled plan sample. Processing is blocked.', 1;", connection, transaction);
            identity.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            identity.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            identity.ExecuteNonQuery();
        }

        internal static string GetEMInvestigationFingerprintInTransaction(SqlConnection connection, SqlTransaction transaction, int eventId)
        {
            // Exclude workflow/signature timestamps: review itself must not change scientific evidence.
            using SqlCommand command = new SqlCommand(@"
SELECT E.Id,E.EventNo,E.AreaId,E.AreaCodeSnapshot,E.AreaNameSnapshot,E.GradeSnapshot,
       E.EmployeeId,E.EmployeeName,E.EmployeeDepartment,E.EmployeeShift,
       JSON_QUERY((SELECT P.Id,P.Method,P.PlateCode,P.SequenceNo,P.SampleSite,P.PlanSampleID,
              P.TotalCount,P.FungalCount,P.CorrectedCount,P.ColoniesObserved,P.ResultCFU,P.Status,
              P.ResultCalculationVersion,EVID.AlertLimitSnapshot,EVID.ActionLimitSnapshot,
              EVID.ResultUnitSnapshot,EVID.AirVolumeLitersSnapshot,EVID.EvidenceSource,EVID.ReconciliationID,
              JSON_QUERY((SELECT U.UsageID,U.Module,U.ParentRecordID,U.ResultRecordID,U.EquipmentID,U.AssignedBy,U.AssignedAt
                  FROM dbo.LabEquipmentUsage U WITH(UPDLOCK,HOLDLOCK)
                  WHERE U.Module=N'EM' AND U.ResultRecordID=P.Id
                  ORDER BY U.UsageID FOR JSON PATH,INCLUDE_NULL_VALUES)) AS EquipmentUsage,
              JSON_QUERY((SELECT H.HistoryID,H.Module,H.ParentRecordID,H.ResultRecordID,H.PreviousEquipmentID,H.EquipmentID,
                      H.EquipmentCodeSnapshot,H.EquipmentNameSnapshot,H.EquipmentTypeSnapshot,H.EquipmentStatusSnapshot,
                      H.QualificationStatusSnapshot,H.CalibrationStatusSnapshot,H.NextQualificationDateSnapshot,H.NextCalibrationDateSnapshot,
                      H.ChangeType,H.MeaningOfSignature,H.ActionReason,H.SignedBy,H.UserRole,H.SignedAt,H.SourceWorkstation
                  FROM dbo.LabEquipmentUsageHistory H WITH(UPDLOCK,HOLDLOCK)
                  WHERE H.Module=N'EM' AND H.ResultRecordID=P.Id
                  ORDER BY H.HistoryID FOR JSON PATH,INCLUDE_NULL_VALUES)) AS EquipmentHistory
          FROM dbo.EM_EventPlates P WITH(UPDLOCK,HOLDLOCK)
          " + EmLimitEvidenceSql.Joins(true) + @"
          WHERE P.EventId=E.Id ORDER BY P.Id FOR JSON PATH,INCLUDE_NULL_VALUES)) AS Plates
FROM dbo.EM_Events E WITH(UPDLOCK,HOLDLOCK)
WHERE E.Id=@eventId FOR JSON PATH,INCLUDE_NULL_VALUES;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            StringBuilder json = new StringBuilder();
            using (SqlDataReader reader = command.ExecuteReader())
                while (reader.Read()) json.Append(reader.GetString(0));
            if (json.Length == 0 || json.ToString() == "[]") throw new InvalidOperationException("The EM event evidence is missing.");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString())));
        }

        internal static bool HasCurrentEMInvestigationEvidenceInTransaction(SqlConnection connection, SqlTransaction transaction, int eventId, int qualityEventId)
        {
            string current = GetEMInvestigationFingerprintInTransaction(connection, transaction, eventId);
            using SqlCommand command = new SqlCommand(@"
SELECT (SELECT TOP(1) ActionDescription FROM dbo.QualityEventActions WITH(UPDLOCK,HOLDLOCK)
        WHERE QualityEventID=@qualityEventId AND ActionType=N'EM Evidence Snapshot' ORDER BY QualityEventActionID),
       CASE WHEN EXISTS(SELECT 1 FROM dbo.QualityEventActions WITH(UPDLOCK,HOLDLOCK)
        WHERE QualityEventID=@qualityEventId AND ActionType=N'EM Evidence Invalidated') THEN 1 ELSE 0 END;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@qualityEventId", SqlDbType.Int).Value = qualityEventId;
            using SqlDataReader reader = command.ExecuteReader();
            reader.Read();
            string captured = reader.IsDBNull(0) ? null : reader.GetString(0);
            try { EmControlledLifecycleGuard.EnsureInvestigationEvidence(captured, current, reader.GetInt32(1) == 1); return true; }
            catch (InvalidOperationException) { return false; }
        }

        internal static void CaptureEMInvestigationEvidenceInTransaction(SqlConnection connection, SqlTransaction transaction, int eventId, int qualityEventId, string signedBy)
        {
            string fingerprint = GetEMInvestigationFingerprintInTransaction(connection, transaction, eventId);
            ExecuteNonQueryWithTransaction(@"
INSERT dbo.QualityEventActions(QualityEventID,ActionType,ActionDescription,PerformedBy,PerformedDate)
VALUES(@qualityEventId,N'EM Evidence Snapshot',@fingerprint,@signedBy,SYSDATETIME());",
                new[] { new SqlParameter("@qualityEventId",qualityEventId),new SqlParameter("@fingerprint",fingerprint), new SqlParameter("@signedBy",signedBy) },connection,transaction);
        }

        internal static void InvalidateEMInvestigationEvidenceInTransaction(SqlConnection connection, SqlTransaction transaction, int eventId, string eventNo, string signedBy)
        {
            // Append once; neither closed decisions nor their affected-result snapshots are rewritten.
            ExecuteNonQueryWithTransaction(@"
INSERT dbo.QualityEventActions(QualityEventID,ActionType,ActionDescription,PerformedBy,PerformedDate)
SELECT Q.QualityEventID,N'EM Evidence Invalidated',N'Saved EM results changed after this investigation evidence was captured. A new investigation is required.',@signedBy,SYSDATETIME()
FROM dbo.QualityEvents Q WITH(UPDLOCK,HOLDLOCK)
WHERE (Q.SourceRecordID=@eventId OR Q.SampleNumber=@eventNo)
  AND (UPPER(LTRIM(RTRIM(ISNULL(Q.SourceModule,N'')))) IN(N'EM',N'ENVIRONMENTAL MONITORING')
       OR UPPER(LTRIM(RTRIM(ISNULL(Q.DetectionSource,N''))))=N'ENVIRONMENTAL MONITORING')
  AND NOT EXISTS(SELECT 1 FROM dbo.QualityEventActions A WITH(UPDLOCK,HOLDLOCK)
                 WHERE A.QualityEventID=Q.QualityEventID AND A.ActionType=N'EM Evidence Invalidated');",
                new[] { new SqlParameter("@eventId",eventId),new SqlParameter("@eventNo",eventNo),new SqlParameter("@signedBy",signedBy) },connection,transaction);
        }
    }
}
