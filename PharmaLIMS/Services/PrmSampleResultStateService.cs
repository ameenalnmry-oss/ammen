using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;

namespace PharmaLIMS.Services
{
    internal sealed class PrmAuthoritativeSampleResultState
    {
        internal PrmAuthoritativeSampleResultState(
            bool allRequiredResultsEntered,
            string overallInterpretation,
            int requiredTestCount,
            int interpretationMismatchCount)
        {
            AllRequiredResultsEntered = allRequiredResultsEntered;
            OverallInterpretation = overallInterpretation;
            RequiredTestCount = requiredTestCount;
            InterpretationMismatchCount = interpretationMismatchCount;
        }

        internal bool AllRequiredResultsEntered { get; }
        internal string OverallInterpretation { get; }
        internal int RequiredTestCount { get; }
        internal int InterpretationMismatchCount { get; }
        internal bool PersistedInterpretationsMatchEvidence => InterpretationMismatchCount == 0;
    }

    internal static class PrmSampleResultStateService
    {
        internal const string LockedResultSnapshotSql = @"
SELECT
    SampleTestID,
    SampleID,
    TestName,
    SpecificationText,
    Unit,
    ResultValue,
    ResultType,
    TestCode,
    SpecificationLimit,
    ISNULL(RequiredTest, 1) AS RequiredTest,
    MinimumElapsedHours,
    Interpretation,
    Remarks,
    EnteredBy,
    EnteredDate,
    ISNULL(SortOrder, SampleTestID) AS SortOrder
FROM dbo.PRM_SampleTests WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID
ORDER BY ISNULL(SortOrder, SampleTestID), SampleTestID;";

        private static readonly string[] SnapshotColumns =
        {
            "SampleID",
            "TestName",
            "SpecificationText",
            "Unit",
            "ResultValue",
            "ResultType",
            "TestCode",
            "SpecificationLimit",
            "RequiredTest",
            "MinimumElapsedHours",
            "Interpretation",
            "Remarks",
            "EnteredBy",
            "EnteredDate",
            "SortOrder"
        };

        internal static DataTable LockAndValidateLoadedSnapshot(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            DataTable? loadedResults)
        {
            if (loadedResults == null)
                throw new DBConcurrencyException("The PRM result snapshot is not loaded. Reload the sample before continuing.");

            DataTable persisted = LoadLockedResults(connection, transaction, sampleId);
            EnsureSnapshotMatches(loadedResults, persisted, sampleId);
            return persisted;
        }

        internal static DataTable LoadLockedResults(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId)
        {
            DataTable table = new DataTable();
            using SqlCommand command = new SqlCommand(LockedResultSnapshotSql, connection, transaction)
            {
                CommandTimeout = 60
            };
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            using SqlDataReader reader = command.ExecuteReader();
            table.Load(reader);
            return table;
        }

        internal static PrmAuthoritativeSampleResultState ReadAuthoritativeStateForUpdate(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId)
        {
            return DeriveAuthoritativeState(LoadLockedResults(connection, transaction, sampleId));
        }

        internal static PrmAuthoritativeSampleResultState DeriveAuthoritativeState(DataTable rows)
        {
            if (rows == null)
                throw new ArgumentNullException(nameof(rows));

            int requiredCount = 0;
            int mismatchCount = 0;
            bool allRequiredEntered = true;
            bool hasDnc = false;
            bool hasCheck = false;
            bool hasNotTested = false;
            bool hasConforms = false;

            foreach (DataRow row in rows.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                bool isRequired = IsRequired(row);
                string result = GetString(row, "ResultValue");

                // An optional blank row may be ignored. Once a result is entered,
                // however, it is authoritative evidence and must affect the final
                // interpretation and release decision exactly like a required test.
                if (!isRequired && string.IsNullOrWhiteSpace(result))
                    continue;

                if (isRequired)
                    requiredCount++;
                if (isRequired && string.IsNullOrWhiteSpace(result))
                    allRequiredEntered = false;

                string derived = PrmResultInterpretationEvaluator.Evaluate(
                    GetString(row, "ResultType"),
                    GetString(row, "SpecificationText"),
                    result,
                    GetString(row, "SpecificationLimit"),
                    GetString(row, "TestCode"),
                    GetString(row, "TestName"));

                string persisted = GetString(row, "Interpretation");
                if (!string.Equals(persisted, derived, StringComparison.Ordinal))
                    mismatchCount++;

                if (derived.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase)) hasDnc = true;
                else if (derived.Equals("Check Required", StringComparison.OrdinalIgnoreCase)) hasCheck = true;
                else if (derived.Equals("Not Tested", StringComparison.OrdinalIgnoreCase)) hasNotTested = true;
                else if (derived.Equals("Conforms", StringComparison.OrdinalIgnoreCase)) hasConforms = true;
                else hasCheck = true;
            }

            if (requiredCount == 0)
                allRequiredEntered = false;

            string overall = "Not Tested";
            if (hasDnc) overall = "Does Not Conform";
            else if (hasCheck) overall = "Check Required";
            else if (hasNotTested || !allRequiredEntered) overall = "In Progress";
            else if (hasConforms) overall = "Conforms";

            return new PrmAuthoritativeSampleResultState(
                allRequiredEntered,
                overall,
                requiredCount,
                mismatchCount);
        }

        internal static void EnsurePersistedInterpretationsMatchEvidence(
            PrmAuthoritativeSampleResultState state,
            string action)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            if (!state.PersistedInterpretationsMatchEvidence)
            {
                throw new DBConcurrencyException(
                    action + " is blocked because " +
                    state.InterpretationMismatchCount.ToString(CultureInfo.InvariantCulture) +
                    " persisted PRM test interpretation(s) no longer match their current result/specification evidence. " +
                    "Reload the sample and save controlled result evidence before continuing.");
            }
        }

        private static void EnsureSnapshotMatches(DataTable loaded, DataTable persisted, int sampleId)
        {
            Dictionary<int, DataRow> localById = new Dictionary<int, DataRow>();
            foreach (DataRow row in loaded.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;
                if (!row.HasVersion(DataRowVersion.Original))
                    throw new DBConcurrencyException("The PRM result grid contains rows without an original database snapshot. Reload the sample.");

                int id = Convert.ToInt32(row["SampleTestID", DataRowVersion.Original], CultureInfo.InvariantCulture);
                if (!localById.TryAdd(id, row))
                    throw new DBConcurrencyException("The loaded PRM result snapshot contains duplicate SampleTestID values. Reload the sample.");
            }

            if (localById.Count != persisted.Rows.Count)
            {
                throw new DBConcurrencyException(
                    "The PRM test set changed in the database after this window loaded it. Reload the sample before continuing.");
            }

            foreach (DataRow databaseRow in persisted.Rows)
            {
                int testId = Convert.ToInt32(databaseRow["SampleTestID"], CultureInfo.InvariantCulture);
                if (!localById.TryGetValue(testId, out DataRow? localRow))
                {
                    throw new DBConcurrencyException(
                        "PRM result " + testId.ToString(CultureInfo.InvariantCulture) +
                        " was added or removed after this window loaded the sample. Reload before continuing.");
                }

                foreach (string column in SnapshotColumns)
                {
                    if (!loaded.Columns.Contains(column) || !persisted.Columns.Contains(column))
                    {
                        throw new DBConcurrencyException(
                            "The loaded PRM result snapshot is missing controlled evidence column '" + column + "'. Reload the sample.");
                    }

                    object localValue = localRow[column, DataRowVersion.Original];
                    object databaseValue = databaseRow[column];
                    if (!Equivalent(column, localValue, databaseValue))
                    {
                        throw new DBConcurrencyException(
                            "PRM result " + testId.ToString(CultureInfo.InvariantCulture) +
                            " changed in controlled evidence field '" + column +
                            "' after this window loaded it. Reload the sample; no stale summary or workflow action was committed.");
                    }
                }
            }

            foreach (DataRow localRow in loaded.Rows)
            {
                if (localRow.RowState == DataRowState.Deleted)
                    continue;
                int localSampleId = Convert.ToInt32(localRow["SampleID", DataRowVersion.Original], CultureInfo.InvariantCulture);
                if (localSampleId != sampleId)
                    throw new DBConcurrencyException("The loaded PRM result snapshot belongs to a different sample. Reload before continuing.");
            }
        }

        private static bool Equivalent(string column, object left, object right)
        {
            bool leftNull = left == null || left == DBNull.Value;
            bool rightNull = right == null || right == DBNull.Value;
            if (leftNull || rightNull)
                return leftNull && rightNull;

            if (column.Equals("EnteredDate", StringComparison.OrdinalIgnoreCase))
                return Convert.ToDateTime(left, CultureInfo.InvariantCulture) == Convert.ToDateTime(right, CultureInfo.InvariantCulture);

            if (column.Equals("SpecificationLimit", StringComparison.OrdinalIgnoreCase) ||
                column.Equals("MinimumElapsedHours", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToDecimal(left, CultureInfo.InvariantCulture) == Convert.ToDecimal(right, CultureInfo.InvariantCulture);
            }

            if (column.Equals("RequiredTest", StringComparison.OrdinalIgnoreCase))
                return Convert.ToBoolean(left, CultureInfo.InvariantCulture) == Convert.ToBoolean(right, CultureInfo.InvariantCulture);

            if (column.Equals("SampleID", StringComparison.OrdinalIgnoreCase) ||
                column.Equals("SortOrder", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToInt32(left, CultureInfo.InvariantCulture) == Convert.ToInt32(right, CultureInfo.InvariantCulture);
            }

            return string.Equals(
                Convert.ToString(left, CultureInfo.InvariantCulture),
                Convert.ToString(right, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        private static bool IsRequired(DataRow row)
        {
            if (!row.Table.Columns.Contains("RequiredTest") || row["RequiredTest"] == DBNull.Value)
                return true;
            return Convert.ToBoolean(row["RequiredTest"], CultureInfo.InvariantCulture);
        }

        private static string GetString(DataRow row, string column)
        {
            if (!row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
                return string.Empty;
            return Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}
