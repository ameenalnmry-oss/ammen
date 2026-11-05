"""Guards for live equipment readiness refresh and trigger-compatible activity creation.
Source checks do not replace integrated runtime signoff.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class EquipmentResourceRefreshContract(unittest.TestCase):
    def test_water_resources_refresh_before_validation_and_signature(self):
        source = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        start = source.index("private async void BtnSaveResults_Click(")
        end = source.index("ElectronicSignature signatureWindow =", start)
        body = source[start:end]
        self.assertIn("_isRefreshingEquipmentAtSave", body)
        self.assertIn("await RefreshWaterEquipmentBeforeSaveAsync()", body)
        self.assertIn("WaterResourceSelectionValid(item)", body)
        self.assertLess(body.index("RefreshWaterEquipmentBeforeSaveAsync"), body.index("WaterResourceSelectionValid(item)"))
        partial = (ROOT / "ResultsEntry.xaml.Part5.cs").read_text(encoding="utf-8-sig")
        self.assertIn("await System.Threading.Tasks.Task.Run(", partial)
        self.assertIn("LabEquipmentUsageService.LoadAvailableEquipment(_equipmentDatabase)", partial)
        self.assertIn("currentSampleId != sampleIdAtRefresh", partial)
        self.assertIn("loaded.EquipmentChoices = WaterTestResourcePolicy.FilterEquipment", partial)

    def test_activity_create_supports_enabled_sql_triggers(self):
        source = (ROOT / "Services/MicroEquipmentActivityService.cs").read_text(encoding="utf-8-sig")
        method = source.split("internal static long CreateDraft(", 1)[1].split("internal static void AddLink(", 1)[0]
        self.assertIn("DECLARE @CreatedActivity TABLE", method)
        self.assertIn("OUTPUT INSERTED.ActivityID INTO @CreatedActivity(ActivityID)", method)
        self.assertIn("SELECT ActivityID FROM @CreatedActivity;", method)
        self.assertIn("WriteAudit(connection,transaction", method)


if __name__ == "__main__":
    unittest.main()
