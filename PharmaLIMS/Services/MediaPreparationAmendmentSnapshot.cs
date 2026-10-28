using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace PharmaLIMS.Services
{
    /// <summary>Persisted preparation evidence, captured and compared while the parent and child ranges are locked.</summary>
    internal sealed class MediaPreparationAmendmentSnapshot
    {
        private readonly DataSet _evidence;
        public DataRow Preparation => _evidence.Tables[0].Rows[0];
        public string AuditValue { get; }

        private MediaPreparationAmendmentSnapshot(DataSet evidence)
        {
            if (evidence.Tables.Count != 3 || evidence.Tables[0].Rows.Count != 1)
                throw new DBConcurrencyException("The preparation no longer exists. Reload before continuing.");
            _evidence = evidence;
            AuditValue = JsonSerializer.Serialize(new
            {
                Preparation = AuditRows(evidence.Tables[0]),
                SopFields = AuditRows(evidence.Tables[1]),
                Signatures = AuditRows(evidence.Tables[2])
            });
        }

        private static object[] AuditRows(DataTable table) => table.Rows.Cast<DataRow>()
            .Select(row => table.Columns.Cast<DataColumn>().OrderBy(column => column.ColumnName, StringComparer.Ordinal)
                .ToDictionary(column => column.ColumnName, column => row.IsNull(column) ? null : row[column]))
            .OrderBy(row => JsonSerializer.Serialize(row), StringComparer.Ordinal).Cast<object>().ToArray();

        public static MediaPreparationAmendmentSnapshot Read(SqlConnection connection, SqlTransaction transaction, int preparationId, int commandTimeout)
        {
            using var command = new SqlCommand(@"
SELECT * FROM dbo.MediaPreparations WITH (UPDLOCK,HOLDLOCK)
WHERE MediaPreparationID=@MediaPreparationID;
SELECT * FROM dbo.CultureMediaSopFields WITH (UPDLOCK,HOLDLOCK)
WHERE EntityType=N'MediaPreparation' AND EntityID=@MediaPreparationID;
SELECT * FROM dbo.CultureMediaSignatures WITH (UPDLOCK,HOLDLOCK)
WHERE EntityType=N'MediaPreparation' AND EntityID=@MediaPreparationID;", connection, transaction)
            { CommandTimeout = commandTimeout };
            command.Parameters.Add("@MediaPreparationID", SqlDbType.Int).Value = preparationId;
            using var adapter = new SqlDataAdapter(command);
            var evidence = new DataSet();
            adapter.Fill(evidence);
            return new MediaPreparationAmendmentSnapshot(evidence);
        }

        public string SopField(string annexureCode, string fieldName)
        {
            DataRow? field = _evidence.Tables[1].Rows.Cast<DataRow>().SingleOrDefault(row =>
                Convert.ToString(row["AnnexureCode"], CultureInfo.InvariantCulture) == annexureCode &&
                Convert.ToString(row["FieldName"], CultureInfo.InvariantCulture) == fieldName);
            return field == null || field.IsNull("FieldValue") ? string.Empty : Convert.ToString(field["FieldValue"], CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public static void EnsureUnchanged(MediaPreparationAmendmentSnapshot? loaded, MediaPreparationAmendmentSnapshot current)
        {
            if (loaded == null || !string.Equals(loaded.AuditValue, current.AuditValue, StringComparison.Ordinal))
                throw new DBConcurrencyException("The preparation, its scientific fields or signatures changed after loading. Reload and review the current record before signing again.");
        }

        public static void EnsureCanAmend(MediaPreparationAmendmentSnapshot? loaded, MediaPreparationAmendmentSnapshot current)
        {
            DataRow row = current.Preparation;
            if (!string.Equals(Convert.ToString(row["ReleaseStatus"], CultureInfo.InvariantCulture), "Under Release", StringComparison.OrdinalIgnoreCase) ||
                new[] { "VisualCheckedBy", "VisualCheckedAt", "SterilityReviewedBy", "SterilityReviewedAt", "ReleasedBy", "ReleasedAt" }.Any(column => !row.IsNull(column)) ||
                current._evidence.Tables[2].Rows.Cast<DataRow>().Any(signature =>
                {
                    string action = Convert.ToString(signature["ActionType"], CultureInfo.InvariantCulture) ?? string.Empty;
                    return action.Contains("Review", StringComparison.OrdinalIgnoreCase) ||
                           action.Contains("Visual Check", StringComparison.OrdinalIgnoreCase) ||
                           action.Contains("Release Prepared", StringComparison.OrdinalIgnoreCase);
                }))
                throw new DBConcurrencyException("The preparation is signed or no longer amendable. Reload before continuing.");
            EnsureUnchanged(loaded, current);
        }
    }
}
