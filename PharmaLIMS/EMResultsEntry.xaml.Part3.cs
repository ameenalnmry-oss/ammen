#nullable disable
using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace PharmaLIMS
{
    public partial class EMResultsEntry
    {
        private static string PlateSnapshotSql(bool forUpdate)
        {
            string hint = forUpdate ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
            return @"
SELECT P.Id, P.EventId, P.Method, P.PlateCode, P.SequenceNo, P.TotalCount,
       P.ColoniesObserved, P.ResultCFU, P.Status, P.FungalCount, P.CorrectedCount,
       P.ResultRowVersion, P.ResultCalculationVersion,
       E.ResultRowVersion AS EventRowVersion, E.WorkflowStatus, E.FinalResult,
       EVID.AlertLimitSnapshot, EVID.ActionLimitSnapshot,
       EVID.ResultUnitSnapshot, EVID.AirVolumeLitersSnapshot,
       EVID.NativeSnapshotComplete, EVID.ReconciliationID, EVID.EvidenceSource,
       EVID.EvidenceComplete, A.Grade
FROM dbo.EM_EventPlates P" + hint + @"
INNER JOIN dbo.EM_Events E" + hint + @" ON P.EventId=E.Id
INNER JOIN dbo.EM_Areas A ON E.AreaId=A.Id
" + EmLimitEvidenceSql.Joins(forUpdate) + @"
WHERE P.EventId=@eventId
ORDER BY P.Id;";
        }

        private static DataTable ReadEmRows(
            SqlConnection connection, SqlTransaction transaction, string sql, params SqlParameter[] parameters)
        {
            using SqlCommand command = new SqlCommand(sql, connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.AddRange(parameters);
            using SqlDataReader reader = command.ExecuteReader();
            DataTable result = new DataTable();
            result.Load(reader);
            return result;
        }

        private static EmCalculatedResult CalculateLockedPlate(DataRow row, int? enteredCount)
        {
            // The SQL count column is decimal. Never silently round historical
            // fractional counts into the integer-count method used by this screen.
            EmResultCalculator.ReadStoredCount(row["TotalCount"]);
            if (Convert.ToInt32(row["EvidenceComplete"], CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("A complete approved EM snapshot is required. Reconcile historical evidence before saving.");
            string method = Convert.ToString(row["Method"], CultureInfo.InvariantCulture) ?? string.Empty;
            int? volume = row["AirVolumeLitersSnapshot"] == DBNull.Value ? null
                : Convert.ToInt32(row["AirVolumeLitersSnapshot"], CultureInfo.InvariantCulture);
            decimal? alert = row["AlertLimitSnapshot"] == DBNull.Value ? null
                : Convert.ToDecimal(row["AlertLimitSnapshot"], CultureInfo.InvariantCulture);
            decimal? action = row["ActionLimitSnapshot"] == DBNull.Value ? null
                : Convert.ToDecimal(row["ActionLimitSnapshot"], CultureInfo.InvariantCulture);
            if (!EmResultCalculator.IsActiveAirSampling(method) && !EmResultCalculator.IsDirectCountMethod(method))
                throw new InvalidOperationException("The EM counting method is not supported by the controlled calculation.");
            EmCalculatedResult result = EmResultCalculator.Calculate(enteredCount, method, volume, alert, action);
            if (enteredCount.HasValue && result.Status == "Not Assessed")
                throw new InvalidOperationException("EM result evidence is not assessable. No changes were committed.");
            return result;
        }

        private string PersistEmResultsInTransaction(
            SqlConnection connection, SqlTransaction transaction, ElectronicSignature signature)
        {
            // Lock order: current signer -> parent event -> complete plate set -> evidence.
            string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                connection, transaction, signature.SignedBy, "CanEnterResults", "save EM results");
            DatabaseHelper.EnsureUserPermissionInTransaction(
                connection, transaction, signature.SignedBy, "CanAccessEM", "access EM results");

            DataTable parent = ReadEmRows(connection, transaction, @"
SELECT Id, WorkflowStatus, FinalResult, ResultRowVersion
FROM dbo.EM_Events WITH (UPDLOCK,HOLDLOCK) WHERE Id=@eventId;",
                new SqlParameter("@eventId", currentEventId));
            if (parent.Rows.Count != 1)
                throw new DBConcurrencyException("The EM event is missing. Reload before saving.");
            string lockedStatus = Convert.ToString(parent.Rows[0]["WorkflowStatus"], CultureInfo.InvariantCulture) ?? "";
            if (new[] { "Under Review", "Reviewed", "Approved", "Completed", "Cancelled", "Closed" }
                .Contains(lockedStatus.Trim(), StringComparer.OrdinalIgnoreCase))
                throw new DBConcurrencyException("The EM event is locked for result entry. No changes were committed.");

            DataTable locked = ReadEmRows(connection, transaction, PlateSnapshotSql(true),
                new SqlParameter("@eventId", currentEventId));
            ResultSnapshotGuard.EnsureMatches(_loadedPlateSnapshot, locked, "Id", "EventId", currentEventId);
            ResultSnapshotGuard.EnsureVisibleKeys(plateItems.Select(item => item.PlateId), locked, "Id");
            Dictionary<int, DataRow> rows = locked.Rows.Cast<DataRow>().ToDictionary(row => Convert.ToInt32(row["Id"]));

            foreach (EMPlateResultItem item in plateItems.OrderBy(item => item.PlateId))
            {
                DataRow source = rows[item.PlateId];
                // Editable fields are count and remarks ONLY. The result and decision
                // are derived from the freshly locked evidence, never the UI fields.
                EmCalculatedResult calculated = CalculateLockedPlate(source, item.TotalCount);
                if (calculated.Status == "OOS" && string.IsNullOrWhiteSpace(item.Remarks))
                    throw new InvalidOperationException("Remarks are required for OOS plate " + source["PlateCode"] + ".");
                object newCount = (object)item.TotalCount ?? DBNull.Value;
                object newValue = (object)calculated.Value ?? DBNull.Value;
                object newRemarks = string.IsNullOrWhiteSpace(item.Remarks) ? DBNull.Value : item.Remarks.Trim();

                bool unchanged = EmResultCalculator.ReadStoredCount(source["TotalCount"]) == item.TotalCount &&
                    ResultSnapshotGuard.Equivalent(source["ResultCFU"], newValue) &&
                    ResultSnapshotGuard.Equivalent(source["ColoniesObserved"], newRemarks) &&
                    string.Equals(Convert.ToString(source["Status"]), calculated.Status, StringComparison.Ordinal) &&
                    source["ResultCalculationVersion"] != DBNull.Value &&
                    Convert.ToInt16(source["ResultCalculationVersion"]) == EmResultCalculator.CalculationVersion;
                if (unchanged) continue;

                // OUTPUT INTO supports tables with enabled triggers; the deleted
                // values are the rows actually replaced, not pre-transaction reads.
                DataTable delta = ReadEmRows(connection, transaction, @"
DECLARE @Changes TABLE(OldValue nvarchar(max), NewValue nvarchar(max));
UPDATE dbo.EM_EventPlates
SET TotalCount=@totalCount, ColoniesObserved=@remarks, ResultCFU=@resultCFU,
    Status=@status, ResultCalculationVersion=@calculationVersion
OUTPUT CONCAT(N'TotalCount=',deleted.TotalCount,N'; Remarks=',deleted.ColoniesObserved,
              N'; ResultCFU=',deleted.ResultCFU,N'; Status=',deleted.Status,
              N'; CalculationVersion=',deleted.ResultCalculationVersion),
       CONCAT(N'TotalCount=',inserted.TotalCount,N'; Remarks=',inserted.ColoniesObserved,
              N'; ResultCFU=',inserted.ResultCFU,N'; Status=',inserted.Status,
              N'; CalculationVersion=',inserted.ResultCalculationVersion)
INTO @Changes
WHERE Id=@plateId AND EventId=@eventId AND ResultRowVersion=@expectedVersion;
SELECT OldValue,NewValue FROM @Changes;",
                    new SqlParameter("@totalCount", SqlDbType.Int) { Value = newCount },
                    new SqlParameter("@remarks", SqlDbType.NVarChar, -1) { Value = newRemarks },
                    new SqlParameter("@resultCFU", SqlDbType.Decimal)
                    { Precision = EmResultCalculator.Precision, Scale = EmResultCalculator.Scale, Value = newValue },
                    new SqlParameter("@status", SqlDbType.NVarChar, 50) { Value = calculated.Status },
                    new SqlParameter("@calculationVersion", SqlDbType.SmallInt) { Value = EmResultCalculator.CalculationVersion },
                    new SqlParameter("@plateId", item.PlateId),
                    new SqlParameter("@eventId", currentEventId),
                    new SqlParameter("@expectedVersion", SqlDbType.Binary, 8) { Value = source["ResultRowVersion"] });
                if (delta.Rows.Count != 1)
                    throw new DBConcurrencyException("An EM plate changed or disappeared. No changes were committed.");

                string evidence = "; Method=" + source["Method"] + "; PlateCode=" + source["PlateCode"] +
                    "; Unit=" + source["ResultUnitSnapshot"] +
                    "; AirVolumeLiters=" + source["AirVolumeLitersSnapshot"] +
                    "; AlertLimitSnapshot=" + source["AlertLimitSnapshot"] +
                    "; ActionLimitSnapshot=" + source["ActionLimitSnapshot"] +
                    "; EvidenceSource=" + source["EvidenceSource"];
                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction, "EM_EventPlates", item.PlateId, "EM Result",
                    Convert.ToString(delta.Rows[0]["OldValue"]) + evidence,
                    Convert.ToString(delta.Rows[0]["NewValue"]) + evidence,
                    signature.Reason, signature.SignedBy, "ResultCFU", null, currentEventNo, "EM");
            }

            // Derive the event summary from the authoritative complete post-write set.
            DataTable saved = ReadEmRows(connection, transaction, PlateSnapshotSql(true),
                new SqlParameter("@eventId", currentEventId));
            var savedStatuses = new List<string>();
            foreach (DataRow row in saved.Rows)
            {
                EmCalculatedResult expected = CalculateLockedPlate(
                    row, EmResultCalculator.ReadStoredCount(row["TotalCount"]));
                if (!EmResultCalculator.StoredEvidenceMatches(
                    expected, row["ResultCFU"], row["Status"], row["ResultCalculationVersion"]))
                    throw new InvalidOperationException("Saved EM value or decision differs from frozen-source evidence. " +
                        "No results were committed; review the ResultCFU storage contract.");
                savedStatuses.Add(expected.Status);
            }
            string finalResult = EmResultCalculator.Aggregate(savedStatuses);

            DataTable eventDelta = ReadEmRows(connection, transaction, @"
DECLARE @Changes TABLE(OldValue nvarchar(max), NewValue nvarchar(max));
UPDATE dbo.EM_Events
SET FinalResult=@finalResult, WorkflowStatus=N'Results Entered',
    ResultsEnteredBy=@signedBy, ResultsEnteredDate=SYSDATETIME()
OUTPUT CONCAT(N'FinalResult=',deleted.FinalResult,N'; WorkflowStatus=',deleted.WorkflowStatus),
       CONCAT(N'FinalResult=',inserted.FinalResult,N'; WorkflowStatus=',inserted.WorkflowStatus)
INTO @Changes
WHERE Id=@eventId AND ResultRowVersion=@expectedVersion;
SELECT OldValue,NewValue FROM @Changes;",
                new SqlParameter("@finalResult", finalResult),
                new SqlParameter("@signedBy", signature.SignedBy),
                new SqlParameter("@eventId", currentEventId),
                new SqlParameter("@expectedVersion", SqlDbType.Binary, 8) { Value = parent.Rows[0]["ResultRowVersion"] });
            if (eventDelta.Rows.Count != 1)
                throw new DBConcurrencyException("The EM event changed before result completion. No changes were committed.");

            DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT INTO dbo.EM_EventSignatures
(EventID,EventNo,ActionType,ActionReason,SignedBy,UserRole,MeaningOfSignature,SignedAt)
VALUES(@eventId,@eventNo,N'EM Result Entry',@reason,@signedBy,@userRole,@meaning,SYSDATETIME());",
                new[] {
                    new SqlParameter("@eventId",currentEventId), new SqlParameter("@eventNo",currentEventNo),
                    new SqlParameter("@reason",signature.Reason), new SqlParameter("@signedBy",signature.SignedBy),
                    new SqlParameter("@userRole",signerRole), new SqlParameter("@meaning",signature.Meaning)
                }, connection, transaction);
            DatabaseHelper.AddAuditTrailAdvanced(
                connection, transaction, "EM_Events", currentEventId, "EM Result",
                Convert.ToString(eventDelta.Rows[0]["OldValue"]), Convert.ToString(eventDelta.Rows[0]["NewValue"]),
                signature.Reason, signature.SignedBy, "FinalResult", null, currentEventNo, "EM");
            return finalResult;
        }
    }
}
