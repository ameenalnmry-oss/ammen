using System.Data;
using PharmaLIMS.Services;

internal static partial class Program
{
    private static void RunWaterWorkflowFindingCases()
    {
        foreach (string status in new[] { "Under Investigation", "Released After Investigation" })
            Run("water pending-only mode " + status, () => Equal(true, WaterWorkflowEvidenceGuard.IsPendingCompletionStatus(status)));
        Run("water approved stays locked", () => Equal(false, WaterWorkflowEvidenceGuard.IsPendingCompletionStatus("Approved")));
        Run("water pending completion retains original failure evidence", () => {
            var loaded = WaterWorkflowRows(); var original = WaterDisplays();
            WaterWorkflowEvidenceGuard.EnsurePendingOnlyEdits(loaded, original, new[] { original[0], original[1] with { ResultValue = "5" } });
        });
        foreach (string field in new[] { "result", "remarks", "equipment", "resources", "les" })
            Run("water frozen completed evidence rejects " + field, () => {
                var original = WaterDisplays(); var changed = original[0];
                changed = field switch {
                    "result" => changed with { ResultValue = "1000" },
                    "remarks" => changed with { Remarks = "changed" },
                    "equipment" => changed with { EquipmentId = 99 },
                    "resources" => changed with { SupportingEquipmentCodes = "other" },
                    _ => changed with { LesVerificationReference = "other" }
                };
                Throws<DBConcurrencyException>(() => WaterWorkflowEvidenceGuard.EnsurePendingOnlyEdits(WaterWorkflowRows(), original, new[] { changed, original[1] }));
            });
        Run("water unchanged display can be submitted", () => {
            var rows = WaterWorkflowRows(); var display = WaterDisplays();
            WaterWorkflowEvidenceGuard.EnsureSaved(rows, rows.Copy(), 7, display, display);
        });
        Run("water unsaved visible numeric edit blocks signature", () => {
            var rows = WaterWorkflowRows(); var display = WaterDisplays();
            Throws<DBConcurrencyException>(() => WaterWorkflowEvidenceGuard.EnsureSaved(rows, rows.Copy(), 7, display, new[] { display[0] with { ResultValue = "2" }, display[1] }));
        });
        Run("water unsaved equipment edit blocks signature", () => {
            var rows = WaterWorkflowRows(); var display = WaterDisplays();
            Throws<DBConcurrencyException>(() => WaterWorkflowEvidenceGuard.EnsureSaved(rows, rows.Copy(), 7, display, new[] { display[0] with { EquipmentId = 99 }, display[1] }));
        });
        Run("water concurrent result blocks signature despite unchanged sample status", () => {
            var loaded = WaterWorkflowRows(); var locked = loaded.Copy(); locked.Rows[0]["ResultValue"] = 200m;
            Throws<DBConcurrencyException>(() => WaterWorkflowEvidenceGuard.EnsureSaved(loaded, locked, 7, WaterDisplays(), WaterDisplays()));
        });
        Run("water missing displayed row blocks signature", () => {
            var rows = WaterWorkflowRows(); var display = WaterDisplays();
            Throws<DBConcurrencyException>(() => WaterWorkflowEvidenceGuard.EnsureSaved(rows, rows.Copy(), 7, display, display.Take(1)));
        });
        foreach (string test in new[] { "pH", "Residual Chlorine", "Free Chlorine", "Chlorine" })
        {
            Run("water report frozen range " + test, () => Equal("0.2 - 0.5", WaterReportNumericContract.FormatRange(test, 0.2m, 0.5m)));
            Run("water report lower violation " + test, () => Equal("Non-Conform", WaterReportNumericContract.Conformity(test, 0.1m, 0.2m, 0.5m)));
            Run("water report lower equality " + test, () => Equal("Conform", WaterReportNumericContract.Conformity(test, 0.2m, 0.2m, 0.5m)));
            Run("water report upper equality " + test, () => Equal("Conform", WaterReportNumericContract.Conformity(test, 0.5m, 0.2m, 0.5m)));
            Run("water report upper violation " + test, () => Equal("Non-Conform", WaterReportNumericContract.Conformity(test, 0.6m, 0.2m, 0.5m)));
            Run("water report incomplete range " + test, () => Equal("Approved specification snapshot missing", WaterReportNumericContract.FormatRange(test, null, 0.5m)));
        }
        Run("water report keeps exact frozen range precision", () => Equal("0.0001 - 0.0005", WaterReportNumericContract.FormatRange("Chlorine", 0.0001m, 0.0005m)));
        Run("water report hardness uses frozen limit", () => Equal("Non-Conform", WaterReportNumericContract.Conformity("Hardness", 200m, null, 100m)));
    }
    private static DataTable WaterWorkflowRows()
    {
        var rows = new DataTable();
        rows.Columns.Add("SampleTestID", typeof(int)); rows.Columns.Add("SampleID", typeof(int));
        rows.Columns.Add("ResultValue", typeof(decimal)); rows.Columns.Add("EquipmentID", typeof(int));
        rows.Rows.Add(1, 7, 101m, 3); rows.Rows.Add(2, 7, DBNull.Value, DBNull.Value);
        return rows;
    }
    private static WaterResultDisplayEvidence[] WaterDisplays() => new[] {
        new WaterResultDisplayEvidence(1, "101", "failure investigated", 3),
        new WaterResultDisplayEvidence(2, "", "", null)
    };
}
