import hashlib
import json
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class V273PrmStandardProfileReadinessHistoricalTests(unittest.TestCase):
    def test_retired_v273_migration_is_checksum_preserved_and_superseded(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.18.294", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))

        old = next(item for item in manifest["migrations"] if item["versionKey"] == "20260913_000")
        self.assertEqual("20260913_001", old.get("supersededBy"))
        old_file = ROOT / "Database" / old["file"]
        self.assertTrue(old_file.exists())
        self.assertEqual("69c57cbb007ce13ff720f49be5f639161991802c19ad99bd738f34d7449c4f04", hashlib.sha256(old_file.read_bytes()).hexdigest())
        self.assertEqual(hashlib.sha256(old_file.read_bytes()).hexdigest(), old["sha256"])

        current = next(item for item in manifest["migrations"] if item["versionKey"] == "20260913_001")
        current_file = ROOT / "Database" / current["file"]
        self.assertTrue(current_file.exists())
        self.assertEqual(hashlib.sha256(current_file.read_bytes()).hexdigest(), current["sha256"])

    def test_current_governance_migration_never_auto_approves_or_retires_site_approved_profile(self):
        sql = (ROOT / "Database/Migrations/20260913_001_PRM_Standard_Profile_Governance_Hardening.sql").read_text(encoding="utf-8-sig")

        self.assertIn("Existing active Approved canonical profiles are preserved exactly as-is", sql)
        self.assertIn("normal Specification Master Review -> Approve workflow", sql)
        self.assertIn("never inserts PRM_SpecificationSignatures", sql)
        self.assertIn("DRAFT TEMPLATE CONTENT ONLY", sql)
        self.assertIn("N'Draft',NULL,NULL,NULL,NULL,NULL", sql)
        self.assertIn("Controlled PRM Draft Template 20260913_001", sql)
        self.assertIn("ApprovalStatus=N'Obsolete'", sql)
        self.assertIn("CreatedBy=N'Controlled PRM Standard Profile Readiness 20260913'", sql)
        self.assertNotIn("N'Controlled PRM Profile Review 20260913',SYSDATETIME()", sql)
        self.assertNotIn("N'Controlled PRM Profile Approval 20260913',SYSDATETIME()", sql)
        self.assertNotRegex(sql, r"(?i)INSERT\s+(?:INTO\s+)?dbo\.PRM_SpecificationSignatures")
        self.assertNotRegex(sql, r"(?i)UPDATE\s+(?:dbo\.)?PRM_Samples\b")
        self.assertNotRegex(sql, r"(?i)UPDATE\s+(?:dbo\.)?PRM_SampleTests\b")
        self.assertNotRegex(sql, r"(?i)INSERT\s+(?:INTO\s+)?(?:dbo\.)?PRM_SampleTests\b")

    def test_registration_fails_closed_and_requires_normal_qa_master_workflow(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        message = registration.split("private static string BuildNoApprovedProfileMessage", 1)[1].split(
            "private string GetSelectedItemCode", 1
        )[0]

        self.assertIn("The controlled standard profile is unavailable", message)
        self.assertIn("Database Maintenance/deployment", message)
        self.assertIn("Specification Master Review -> Approve", message)
        self.assertIn("QA must verify the approved site/product specification", message)
        self.assertIn("scientifically justified and formally approved", message)

        validation = registration.split("private void ValidateForm()", 1)[1].split("private void InsertSample()", 1)[0]
        self.assertIn("configured.ApprovalStatus=N'Approved'", validation)
        self.assertIn("configured.IsActive=1", validation)
        self.assertIn("throw new InvalidOperationException", validation)
        self.assertNotIn("AppConfig.IsDevelopment", validation)
        self.assertNotIn("AllowLegacyPrmSpecificationFallback", validation)

    def test_profile_governance_migration_is_not_auto_applied_during_normal_login_startup(self):
        app = (ROOT / "App.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("AuthenticationLoginCompatibilityMigrationKey", app)
        self.assertNotIn("20260913_001", app)
        self.assertIn("Production startup is verify-only; broader migrations remain controlled deployment/maintenance actions.", app)


if __name__ == "__main__":
    unittest.main()
