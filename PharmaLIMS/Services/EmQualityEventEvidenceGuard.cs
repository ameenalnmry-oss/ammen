using System.Data;
using System.Globalization;

namespace PharmaLIMS.Services;

internal static class EmQualityEventEvidenceGuard
{
    internal static void EnsureSaved(DataTable? loaded, DataTable locked, int eventId,
        IEnumerable<(int PlateId, int? Count, string Remarks)> visible)
    {
        ResultSnapshotGuard.EnsureMatches(loaded, locked, "Id", "EventId", eventId);
        var entries = visible.ToArray();
        ResultSnapshotGuard.EnsureVisibleKeys(entries.Select(item => item.PlateId), locked, "Id");
        var rows = locked.Rows.Cast<DataRow>().ToDictionary(row => Convert.ToInt32(row["Id"], CultureInfo.InvariantCulture));
        foreach (var entry in entries)
        {
            DataRow row = rows[entry.PlateId];
            if (entry.Count != EmResultCalculator.ReadStoredCount(row["TotalCount"]) ||
                (entry.Remarks ?? string.Empty).Trim() != (Convert.ToString(row["ColoniesObserved"], CultureInfo.InvariantCulture) ?? string.Empty).Trim())
                throw new DBConcurrencyException("Save and reload the EM results before creating a Quality Event. Unsaved counts or remarks cannot become investigation evidence.");
        }
    }
}
