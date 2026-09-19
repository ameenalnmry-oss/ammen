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
    public partial class ResultsEntry
    {
        private DataTable _loadedWaterSnapshot;
        private readonly Dictionary<int, string> _loadedWaterDisplayValues = new Dictionary<int, string>();

        private static DataTable ReadWaterRows(
            SqlConnection connection, SqlTransaction transaction, string sql, params SqlParameter[] parameters)
        {
            using SqlCommand command = new SqlCommand(sql, connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.AddRange(parameters);
            using SqlDataReader reader = command.ExecuteReader();
            DataTable rows = new DataTable();
            rows.Load(reader);
            return rows;
        }

        private ResultItem WaterItemFromEvidence(DataRow row)
        {
            ResultItem item = new ResultItem
            {
                SampleTestID = Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture),
                TestID = row["TestID"] == DBNull.Value ? 0 : Convert.ToInt32(row["TestID"], CultureInfo.InvariantCulture),
                TestName = Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? "",
                Unit = Convert.ToString(row["Unit"], CultureInfo.InvariantCulture) ?? "",
                AlertLimit = row["AlertLimit"] == DBNull.Value ? null : Convert.ToDecimal(row["AlertLimit"], CultureInfo.InvariantCulture),
                ActionLimit = row["ActionLimit"] == DBNull.Value ? null : Convert.ToDecimal(row["ActionLimit"], CultureInfo.InvariantCulture),
                ResultValue = Convert.ToString(row["ResultValue"], CultureInfo.InvariantCulture) ?? "",
                Remarks = Convert.ToString(row["Remarks"], CultureInfo.InvariantCulture) ?? ""
            };
            // Preserve the established legacy water-profile policy, but use only
            // the locked row's test identity and specifications, never grid metadata.
            if (Convert.ToInt32(row["HasSpecSnapshot"], CultureInfo.InvariantCulture) == 0)
                ApplyEffectiveSpecification(item);
            return item;
        }

        private bool PersistWaterEdits(
            SqlConnection connection, SqlTransaction transaction, ElectronicSignature signature)
        {
            DataTable locked = ReadWaterRows(connection, transaction, WaterResultSnapshotSql.Build(true),
                new SqlParameter("@sampleId", currentSampleId));
            ResultSnapshotGuard.EnsureMatches(_loadedWaterSnapshot, locked, "SampleTestID", "SampleID", currentSampleId);

            DataTable visible = locked.Clone();
            foreach (DataRow row in locked.Rows)
                if (!IsRemovedTest(Convert.ToString(row["TestName"]))) visible.ImportRow(row);
            ResultSnapshotGuard.EnsureVisibleKeys(resultItems.Select(item => item.SampleTestID), visible, "SampleTestID");

            Dictionary<int, DataRow> byId = locked.Rows.Cast<DataRow>()
                .ToDictionary(row => Convert.ToInt32(row["SampleTestID"]));
            bool anyResultChanged = false;
            var expectedWrittenValues = new Dictionary<int, decimal>();
            foreach (ResultItem edited in resultItems.OrderBy(item => item.SampleTestID))
            {
                DataRow source = byId[edited.SampleTestID];
                ResultItem authoritative = WaterItemFromEvidence(source);
                if (!_loadedWaterDisplayValues.TryGetValue(edited.SampleTestID, out string originalDisplay))
                    throw new DBConcurrencyException("The original water result display is missing. Reload the sample.");

                bool inputEdited = !string.Equals(edited.ResultValue ?? "", originalDisplay, StringComparison.Ordinal);
                object resultValue = source["ResultValue"];
                if (inputEdited)
                {
                    if (string.IsNullOrWhiteSpace(edited.ResultValue))
                    {
                        if (source["ResultValue"] != DBNull.Value)
                            throw new InvalidOperationException("An existing result cannot be silently cleared. Use the controlled result correction/invalidation workflow.");
                    }
                    else
                    {
                        authoritative.ResultValue = edited.ResultValue;
                        if (!TryNormalizeResultForSave(authoritative, out decimal normalized))
                            throw new InvalidOperationException("Invalid result value for test: " + authoritative.TestName);
                        resultValue = GetPersistedResultValue(authoritative, normalized);
                    }
                }

                // Notes-only saves retain the exact database numeric value and all
                // result-status/specification evidence, including its original precision.
                bool resultChanged = !ResultSnapshotGuard.Equivalent(resultValue, source["ResultValue"]);
                authoritative.ResultValue = Convert.ToString(resultValue, CultureInfo.InvariantCulture) ?? "";
                authoritative.Remarks = (edited.Remarks ?? "").Trim();
                string status = CalculatePassFail(authoritative);
                if ((status == "OOS" || status == "ALERT") && authoritative.Remarks.Length == 0)
                    throw new InvalidOperationException("Remarks are required for " + status + " test " + authoritative.TestName + ".");
                object remarks = authoritative.Remarks.Length == 0 ? DBNull.Value : authoritative.Remarks;
                bool notesChanged = !ResultSnapshotGuard.Equivalent(remarks, source["Remarks"]);
                if (!resultChanged && !notesChanged) continue;

                string setClause = resultChanged
                    ? @"ResultValue=@resultValue, Remarks=@remarks, ResultStatus=@resultStatus,
                        DeviationType=@deviationType, LimitDescription=@limitDescription"
                    : "Remarks=@remarks";
                DataTable delta = ReadWaterRows(connection, transaction, @"
DECLARE @Changes TABLE(OldValue nvarchar(max), NewValue nvarchar(max));
UPDATE dbo.SampleTests SET " + setClause + @"
OUTPUT CONCAT(N'ResultValue=',deleted.ResultValue,N'; Remarks=',deleted.Remarks,
              N'; ResultStatus=',deleted.ResultStatus,N'; DeviationType=',deleted.DeviationType,
              N'; LimitDescription=',deleted.LimitDescription),
       CONCAT(N'ResultValue=',inserted.ResultValue,N'; Remarks=',inserted.Remarks,
              N'; ResultStatus=',inserted.ResultStatus,N'; DeviationType=',inserted.DeviationType,
              N'; LimitDescription=',inserted.LimitDescription)
INTO @Changes
WHERE SampleTestID=@sampleTestId AND SampleID=@sampleId AND ResultRowVersion=@expectedVersion;
SELECT OldValue,NewValue FROM @Changes;",
                    new SqlParameter("@resultValue", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = resultChanged ? resultValue : DBNull.Value },
                    new SqlParameter("@remarks", SqlDbType.NVarChar, -1) { Value = remarks },
                    new SqlParameter("@resultStatus", SqlDbType.NVarChar, 50) { Value = status },
                    new SqlParameter("@deviationType", SqlDbType.NVarChar, 100) { Value = (object)BuildDeviationType(authoritative, status) ?? DBNull.Value },
                    new SqlParameter("@limitDescription", SqlDbType.NVarChar, 1000) { Value = (object)BuildLimitDescription(authoritative) ?? DBNull.Value },
                    new SqlParameter("@sampleTestId", edited.SampleTestID),
                    new SqlParameter("@sampleId", currentSampleId),
                    new SqlParameter("@expectedVersion", SqlDbType.Binary, 8) { Value = source["ResultRowVersion"] });
                if (delta.Rows.Count != 1)
                    throw new DBConcurrencyException("The water result changed or is missing. No changes were committed.");
                if (resultChanged)
                {
                    decimal expectedValue = Convert.ToDecimal(resultValue, CultureInfo.InvariantCulture);
                    expectedWrittenValues.Add(edited.SampleTestID, expectedValue);
                }
                anyResultChanged |= resultChanged;
                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction, "SampleTests", edited.SampleTestID,
                    resultChanged ? "Water Result Entry" : "Water Result Remarks",
                    Convert.ToString(delta.Rows[0]["OldValue"]), Convert.ToString(delta.Rows[0]["NewValue"]),
                    signature.Reason, signature.SignedBy, resultChanged ? "ResultValue" : "Remarks",
                    authoritative.TestName, GetSignatureRecordNumber(), "Water");
            }
            if (expectedWrittenValues.Count > 0)
            {
                // Re-read after all updates/triggers, under the same parent/set locks.
                // A mismatch aborts this transaction, including its signatures/audit.
                DataTable saved = ReadWaterRows(connection, transaction, WaterResultSnapshotSql.Build(true),
                    new SqlParameter("@sampleId", currentSampleId));
                var persisted = saved.Rows.Cast<DataRow>()
                    .ToDictionary(row => Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture));
                foreach (KeyValuePair<int, decimal> expected in expectedWrittenValues)
                {
                    if (!persisted.TryGetValue(expected.Key, out DataRow row) ||
                        !WaterResultValueContract.StoredValueMatches(expected.Value, row["ResultValue"]))
                        throw new InvalidOperationException("Water result precision changed during persistence. " +
                            "No results were committed; review the ResultValue storage contract.");
                }
            }
            return anyResultChanged;
        }

        private (int Completed, int Passed, int Alerts, int Oos, int Pending) ReadWaterSummary(
            SqlConnection connection, SqlTransaction transaction)
        {
            DataTable rows = ReadWaterRows(connection, transaction, WaterResultSnapshotSql.Build(true),
                new SqlParameter("@sampleId", currentSampleId));
            int completed = 0, passed = 0, alerts = 0, oos = 0, pending = 0;
            foreach (DataRow row in rows.Rows)
            {
                if (row["ResultValue"] == DBNull.Value) { pending++; continue; }
                if (IsRemovedTest(Convert.ToString(row["TestName"], CultureInfo.InvariantCulture))) continue;
                completed++;
                string status = CalculatePassFail(WaterItemFromEvidence(row));
                if (status == "PASS") passed++;
                else if (status == "ALERT") alerts++;
                else if (status == "OOS") oos++;
                else throw new InvalidOperationException("A persisted water result cannot be assessed. No workflow change was committed.");
            }
            return (completed, passed, alerts, oos, pending);
        }
    }
}
