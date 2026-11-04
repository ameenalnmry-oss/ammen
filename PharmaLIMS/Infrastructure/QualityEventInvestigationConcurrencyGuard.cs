using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PharmaLIMS.Infrastructure
{
    // A window's token describes the persisted content it actually loaded, never its edited UI.
    // Compare under retained update/range locks before any content, status, audit or signature write.
    internal static class QualityEventInvestigationConcurrencyGuard
    {
        private static readonly string[] EventTables =
        {
            "QualityEventChecklistAnswers", "QualityEventAffectedResults", "QualityEventActions",
            "QualityEventRootCauseWhys", "QualityEventImpactAssessments", "QualityEventCAPAItems",
            "QualityEventRetesting", "QualityEventDistribution", "QualityEventInvestigationEvidenceHistory",
            "QualityEventSignatures"
        };

        public static string CaptureLoadedVersion(string connectionString, int eventId, bool prm)
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            string token = Capture(connection, transaction, eventId, prm);
            transaction.Commit();
            return token;
        }

        public static string Capture(SqlConnection connection, SqlTransaction transaction, int eventId, bool prm)
        {
            var tables = new List<DataTable>();
            DataTable header = Read(connection, transaction,
                "SELECT * FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK) WHERE QualityEventID=@Id", eventId);
            if (header.Rows.Count != 1)
                throw new DBConcurrencyException("The investigation no longer exists. Reload before continuing.");
            if (prm && !Convert.ToString(header.Rows[0]["SourceModule"], CultureInfo.InvariantCulture)!.Trim().Equals("PRM", StringComparison.OrdinalIgnoreCase))
                throw new DBConcurrencyException("The investigation source changed. Reload before continuing.");
            tables.Add(header);
            foreach (string table in EventTables)
                tables.Add(ReadOptional(connection, transaction, table, "QualityEventID=@Id", eventId));
            // Definition changes can change the checklist the investigator saw, even with unchanged answers.
            tables.Add(ReadOptional(connection, transaction, "QualityEventChecklistQuestions", "1=1", eventId));
            if (prm)
            {
                const string sample = "SampleID IN (SELECT SampleID FROM dbo.QualityEvents WHERE QualityEventID=@Id UNION SELECT SourceRecordID FROM dbo.QualityEvents WHERE QualityEventID=@Id)";
                tables.Add(ReadOptional(connection, transaction, "PRM_Samples", sample, eventId));
                tables.Add(ReadOptional(connection, transaction, "PRM_SampleTests", sample, eventId));
                tables.Add(ReadOptional(connection, transaction, "PRM_ElectronicSignatures", sample, eventId));
                tables.Add(ReadOptional(connection, transaction, "PRM_QualityEventEvidenceReconciliations",
                    "ReplacementQualityEventID=@Id OR LegacyQualityEventID=@Id", eventId));
            }
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                foreach (DataTable table in tables)
                {
                    writer.Write(table.Columns.Count);
                    foreach (DataColumn column in table.Columns) { writer.Write(column.ColumnName); writer.Write(column.DataType.FullName ?? ""); }
                    var rows = new List<string>();
                    foreach (DataRow row in table.Rows)
                    {
                        using var rowStream = new MemoryStream();
                        using (var rowWriter = new BinaryWriter(rowStream, Encoding.UTF8, true))
                            foreach (object? value in row.ItemArray)
                            {
                                rowWriter.Write(value != null && value != DBNull.Value);
                                if (value == null || value == DBNull.Value) continue;
                                rowWriter.Write(value is byte[] bytes ? Convert.ToBase64String(bytes) :
                                    value is DateTime date ? date.ToString("O", CultureInfo.InvariantCulture) :
                                    value is DateTimeOffset offset ? offset.ToString("O", CultureInfo.InvariantCulture) :
                                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                            }
                        rows.Add(Convert.ToBase64String(rowStream.ToArray()));
                    }
                    rows.Sort(StringComparer.Ordinal);
                    writer.Write(rows.Count);
                    foreach (string row in rows) writer.Write(row);
                }
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }

        public static void EnsureUnchanged(SqlConnection connection, SqlTransaction transaction, int eventId, bool prm, string? loadedVersion)
        {
            string currentVersion = Capture(connection, transaction, eventId, prm);
            if (string.IsNullOrEmpty(loadedVersion) || !string.Equals(loadedVersion, currentVersion, StringComparison.Ordinal))
                throw new DBConcurrencyException("The investigation content or evidence changed. Reload before saving, submitting or closing; no changes were committed.");
            if (!prm && ReadOptional(connection, transaction, "QualityEventSignatures", "QualityEventID=@Id", eventId).Rows.Count != 0)
                throw new InvalidOperationException("The investigation carries a closure signature and cannot be modified.");
            DataTable header = Read(connection, transaction,
                "SELECT CurrentStatus,ClosedBy,ClosedDate FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK) WHERE QualityEventID=@Id", eventId);
            string status = Convert.ToString(header.Rows[0]["CurrentStatus"], CultureInfo.InvariantCulture) ?? "";
            if (new[] { "Closed", "QA Closed", "Cancelled", "Rejected Closed" }.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase)
                || header.Rows[0]["ClosedDate"] != DBNull.Value || !string.IsNullOrWhiteSpace(Convert.ToString(header.Rows[0]["ClosedBy"], CultureInfo.InvariantCulture)))
                throw new InvalidOperationException("The investigation is closed or carries closure evidence and cannot be modified.");
        }

        private static DataTable ReadOptional(SqlConnection connection, SqlTransaction transaction, string table, string predicate, int id)
        {
            // Table identifiers are exclusively the fixed names above; absent optional legacy tables remain distinguishable.
            return Read(connection, transaction,
                $"IF OBJECT_ID(N'dbo.{table}',N'U') IS NOT NULL SELECT * FROM dbo.[{table}] WITH(UPDLOCK,HOLDLOCK) WHERE {predicate};", id);
        }

        private static DataTable Read(SqlConnection connection, SqlTransaction transaction, string sql, int id)
        {
            using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
            using var reader = command.ExecuteReader();
            var table = new DataTable();
            table.Load(reader);
            return table;
        }
    }
}
