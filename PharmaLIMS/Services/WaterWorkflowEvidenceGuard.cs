using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace PharmaLIMS.Services;

// Values as shown at load, including qualitative/numeric formatting. Comparing this
// immutable copy avoids reparsing or rounding an unchanged historic measurement.
internal sealed record WaterResultDisplayEvidence(
    int ResultId, string ResultValue, string Remarks, int? EquipmentId,
    string SupportingEquipmentCodes = "", string TestKitCode = "", string TestKitLot = "",
    DateTime? TestKitExpiry = null, string LesSampleTemperatureC = "",
    string LesVerificationReference = "", bool LesVerificationConfirmed = false,
    string LesExecutionRemarks = "");

internal static class WaterWorkflowEvidenceGuard
{
    internal static bool IsPendingCompletionStatus(string status) =>
        string.Equals(status, "Under Investigation", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Released After Investigation", StringComparison.OrdinalIgnoreCase);

    internal static void EnsureSaved(DataTable? loaded, DataTable locked, int sampleId,
        IEnumerable<WaterResultDisplayEvidence> original, IEnumerable<WaterResultDisplayEvidence> visible)
    {
        ResultSnapshotGuard.EnsureMatches(loaded, locked, "SampleTestID", "SampleID", sampleId);
        var before = Index(original);
        var current = Index(visible);
        EnsureSameKeys(before, current);
        var rows = locked.Rows.Cast<DataRow>().ToDictionary(row => Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture));
        foreach (var item in current.Values)
        {
            if (!rows.TryGetValue(item.ResultId, out DataRow? row) || item != before[item.ResultId])
                throw Unsaved();
            int? persistedEquipment = row["EquipmentID"] == DBNull.Value ? null : Convert.ToInt32(row["EquipmentID"], CultureInfo.InvariantCulture);
            if (item.EquipmentId != persistedEquipment) throw Unsaved();
        }
    }

    internal static void EnsurePendingOnlyEdits(DataTable locked,
        IEnumerable<WaterResultDisplayEvidence> original, IEnumerable<WaterResultDisplayEvidence> visible)
    {
        var before = Index(original);
        var current = Index(visible);
        EnsureSameKeys(before, current);
        foreach (DataRow row in locked.Rows)
        {
            int id = Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture);
            if (row["ResultValue"] != DBNull.Value && current.TryGetValue(id, out var item) && item != before[id])
                throw new DBConcurrencyException("Only pending water tests may be completed during or after investigation. Existing results, remarks, equipment and execution evidence are frozen; reload before continuing.");
        }
    }

    private static Dictionary<int, WaterResultDisplayEvidence> Index(IEnumerable<WaterResultDisplayEvidence> entries)
    {
        var result = new Dictionary<int, WaterResultDisplayEvidence>();
        foreach (var entry in entries)
            if (!result.TryAdd(entry.ResultId, entry)) throw Unsaved();
        return result;
    }
    private static void EnsureSameKeys(Dictionary<int, WaterResultDisplayEvidence> before, Dictionary<int, WaterResultDisplayEvidence> current)
    {
        if (before.Count != current.Count || before.Keys.Any(id => !current.ContainsKey(id))) throw Unsaved();
    }
    private static DBConcurrencyException Unsaved() => new DBConcurrencyException(
        "Save and reload the water results before this controlled operation. Unsaved results, remarks, equipment or execution evidence cannot be signed.");
}
