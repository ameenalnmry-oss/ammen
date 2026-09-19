using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace PharmaLIMS.Services
{
    /// <summary>
    /// Compares an immutable load-time copy with the complete, locked database set.
    /// Call before ANY write, including signatures or audit rows. The caller owns
    /// the transaction and must keep the parent/set locks until commit.
    /// </summary>
    internal static class ResultSnapshotGuard
    {
        internal static void EnsureMatches(
            DataTable? loaded, DataTable current, string keyColumn, string ownerColumn, int ownerId)
        {
            if (loaded == null)
                throw Conflict("No original result snapshot is available");
            if (loaded.Columns.Count != current.Columns.Count ||
                loaded.Columns.Cast<DataColumn>().Any(c => !current.Columns.Contains(c.ColumnName)))
                throw Conflict("The result snapshot schema changed");
            if (loaded.Rows.Count != current.Rows.Count)
                throw Conflict("The complete test/plate set changed");

            Dictionary<int, DataRow> originals = Index(loaded, keyColumn, ownerColumn, ownerId);
            Dictionary<int, DataRow> persisted = Index(current, keyColumn, ownerColumn, ownerId);
            foreach (KeyValuePair<int, DataRow> entry in persisted)
            {
                if (!originals.TryGetValue(entry.Key, out DataRow? original))
                    throw Conflict("A test/plate was added, removed, or replaced");

                foreach (DataColumn column in loaded.Columns)
                {
                    if (!Equivalent(original[column.ColumnName], entry.Value[column.ColumnName]))
                        throw Conflict("Result " + entry.Key.ToString(CultureInfo.InvariantCulture) +
                            " changed in " + column.ColumnName);
                }
            }
        }

        internal static void EnsureVisibleKeys(
            IEnumerable<int> visibleKeys, DataTable snapshot, string keyColumn)
        {
            int[] local = visibleKeys.ToArray();
            int[] expected = snapshot.Rows.Cast<DataRow>()
                .Select(row => Convert.ToInt32(row[keyColumn], CultureInfo.InvariantCulture)).ToArray();
            if (local.Distinct().Count() != local.Length ||
                local.Length != expected.Length || !local.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)))
                throw Conflict("The result grid no longer represents the complete loaded set");
        }

        internal static bool Equivalent(object? left, object? right)
        {
            bool leftNull = left == null || left == DBNull.Value;
            bool rightNull = right == null || right == DBNull.Value;
            if (leftNull || rightNull) return leftNull && rightNull;
            if (left is byte[] a && right is byte[] b) return a.SequenceEqual(b);
            return Equals(left, right);
        }

        private static Dictionary<int, DataRow> Index(
            DataTable rows, string key, string owner, int ownerId)
        {
            if (!rows.Columns.Contains(key) || !rows.Columns.Contains(owner))
                throw Conflict("Identity evidence is missing");
            Dictionary<int, DataRow> result = new Dictionary<int, DataRow>();
            foreach (DataRow row in rows.Rows)
            {
                if (row.RowState == DataRowState.Deleted ||
                    row[key] == DBNull.Value || row[owner] == DBNull.Value ||
                    Convert.ToInt32(row[owner], CultureInfo.InvariantCulture) != ownerId ||
                    !result.TryAdd(Convert.ToInt32(row[key], CultureInfo.InvariantCulture), row))
                    throw Conflict("Invalid, duplicate, or cross-record identity in the snapshot");
            }
            return result;
        }

        private static DBConcurrencyException Conflict(string detail) =>
            new DBConcurrencyException(detail +
                ". Reload the record before saving. No result, signature, or audit changes were committed.");
    }
}
