from pathlib import Path
import json
import hashlib
import unittest

ROOT = Path(__file__).resolve().parents[1]

class V307EquipmentUsageReconciliationTests(unittest.TestCase):
    def test_preflight_exposes_controlled_equipment_reconciliation(self):
        xaml = (ROOT / "SystemPreflight.xaml").read_text(encoding="utf-8")
        code = (ROOT / "SystemPreflight.xaml.cs").read_text(encoding="utf-8")
        self.assertIn('x:Name="BtnEquipmentReconcile"', xaml)
        self.assertIn('Click="BtnEquipmentReconcile_Click"', xaml)
        self.assertIn("Equipment usage traceability", code)
        self.assertIn("new EquipmentUsageReconciliation", code)
        self.assertIn("DatabaseHelper.CanApproveResults(Login.CurrentUser)", code)

    def test_reconciliation_is_append_only_and_does_not_rewrite_results(self):
        migration = (ROOT / "Database/Migrations/20261029_001_Historical_Equipment_Usage_Reconciliation.sql").read_text(encoding="utf-8")
        service = (ROOT / "Services/EquipmentUsageReconciliationService.cs").read_text(encoding="utf-8")
        self.assertIn("TRG_LabEquipmentUsageReconciliations_AppendOnly_20261029", migration)
        contract = (ROOT / "Infrastructure/ComplianceRecordProtectionContract.cs").read_text(encoding="utf-8")
        self.assertIn("LabEquipmentUsageReconciliations", contract)
        self.assertIn("TRG_LabEquipmentUsageReconciliations_AppendOnly_20261029", contract)
        self.assertIn("AFTER UPDATE, DELETE", migration)
        self.assertIn("THROW 56831", migration)
        self.assertNotIn("UPDATE dbo.SampleTests", service)
        self.assertNotIn("UPDATE dbo.PRM_SampleTests", service)
        self.assertNotIn("UPDATE dbo.EM_EventPlates", service)
        self.assertNotIn("INSERT dbo.LabEquipmentUsage\n", service)
        self.assertIn("INSERT dbo.LabEquipmentUsageReconciliations", service)
        self.assertIn("SYSUTCDATETIME()", service)

    def test_missing_assignment_is_narrowly_historical_water_only(self):
        service = (ROOT / "Services/EquipmentUsageReconciliationService.cs").read_text(encoding="utf-8")
        preflight = (ROOT / "Services/WaterEvidencePreflightSql.cs").read_text(encoding="utf-8")
        self.assertIn("20261008_002", service)
        self.assertIn("20261009_002", service)
        self.assertIn("st.ResultEnteredDate>=@EquipmentCutover", service)
        self.assertIn("st.ResultEnteredDate<@ResourceCutover", service)
        self.assertIn('candidate.Module.Equals("WATER"', service)
        self.assertIn("MISSING_HISTORICAL_ASSIGNMENT", preflight)
        self.assertIn("LabEquipmentUsageReconciliations", preflight)

    def test_existing_assignment_reconciliation_cannot_change_equipment(self):
        service = (ROOT / "Services/EquipmentUsageReconciliationService.cs").read_text(encoding="utf-8")
        xaml_cs = (ROOT / "EquipmentUsageReconciliation.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("currentEquipment != selectedEquipmentId", service)
        self.assertIn("cboEquipment.IsEnabled = !candidate.ExistingEquipmentID.HasValue", xaml_cs)

    def test_signature_permission_and_evidence_are_required(self):
        service = (ROOT / "Services/EquipmentUsageReconciliationService.cs").read_text(encoding="utf-8")
        ui = (ROOT / "EquipmentUsageReconciliation.xaml.cs").read_text(encoding="utf-8")
        self.assertIn('"CanApproveResults"', service)
        self.assertIn("ElectronicSignature", ui)
        self.assertIn("Review and Reconcile Historical Equipment Usage", ui)
        self.assertIn("Placeholder reconciliation evidence is not permitted", service)
        self.assertIn("MeaningOfSignature", service)
        self.assertIn("ActionReason", service)


    def test_review_gap_fixture_maps_reconciliation_table_explicitly(self):
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/ReviewGapClosureIntegration.cs").read_text(encoding="utf-8")
        self.assertIn('"LabEquipmentUsageReconciliations", "LabEquipmentUsageHistory", "LabEquipmentUsage"', integration)
        self.assertIn("CREATE TABLE #Review_LabEquipmentUsageReconciliations", integration)
        self.assertIn("SupersedesReconciliationID bigint", integration)

    def test_manifest_contains_v307_migration_with_matching_hash(self):
        manifest_path = ROOT / "Database/MigrationManifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.assertEqual("2026.10.19.309", manifest["applicationVersion"])
        entry = next(x for x in manifest["migrations"] if x["versionKey"] == "20261029_001")
        migration_path = ROOT / "Database" / entry["file"]
        self.assertEqual(hashlib.sha256(migration_path.read_bytes()).hexdigest(), entry["sha256"])

if __name__ == "__main__":
    unittest.main()
