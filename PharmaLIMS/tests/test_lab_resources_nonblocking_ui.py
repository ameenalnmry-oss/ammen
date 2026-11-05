"""Regression guard for EQ-001 adjacent Laboratory Resources UI responsiveness.
Static contracts complement, but do not replace, interactive Windows UI checks.
"""
import pathlib
import re
import unittest

SOURCE = (pathlib.Path(__file__).resolve().parents[1] / "LabResources.xaml.cs").read_text(encoding="utf-8-sig")


class LabResourcesNonBlockingUi(unittest.TestCase):
    def test_initial_open_awaits_nonblocking_equipment_load(self):
        self.assertIn("private async void LabResources_Loaded", SOURCE)
        self.assertIn("await LoadEquipmentAsync();", SOURCE)
        master = SOURCE.split("private async Task LoadEquipmentAsync()", 1)[1].split("private void UpdateSummary()", 1)[0]
        self.assertIn("await _database.ExecuteQueryAsync(", master)
        self.assertNotIn("_database.ExecuteQuery(", master)
        self.assertIn("request != _equipmentLoadRequest", master)

    def test_selected_history_never_waits_synchronously_on_ui(self):
        section = SOURCE.split("private async Task LoadEquipmentActivityHistoryAsync(", 1)[1].split(
            "private void BtnPrintSelectedActivity_Click", 1
        )[0]
        self.assertGreaterEqual(section.count("await _database.ExecuteQueryAsync("), 2)
        self.assertNotIn("_database.ExecuteQuery(", section)
        self.assertIn("request != _activityHistoryRequest", section)
        self.assertIn("_selectedEquipmentId != equipmentId", section)

    def test_refresh_reload_save_wait_for_equipment_reload(self):
        for method in ("BtnRefresh_Click", "BtnReload_Click", "BtnSave_Click"):
            self.assertIn(f"private async void {method}(", SOURCE)
        self.assertGreaterEqual(SOURCE.count("await LoadEquipmentAsync();"), 4)


if __name__ == "__main__":
    unittest.main()
