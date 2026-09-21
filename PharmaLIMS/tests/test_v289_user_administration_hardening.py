import hashlib
import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


class V289UserAdministrationHardeningTests(unittest.TestCase):
    def test_release_identity_and_new_signature_migration(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        self.assertEqual("2026.9.18.294", manifest["applicationVersion"])
        self.assertEqual(84, len(manifest["migrations"]))
        entry = next(x for x in manifest["migrations"] if x["versionKey"] == "20260917_001")
        path = ROOT / "Database" / entry["file"]
        self.assertTrue(path.is_file())
        self.assertEqual(entry["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())

    def test_user_admin_signature_evidence_is_transactional_and_append_only(self):
        migration = text("Database/Migrations/20260917_001_User_Administration_Signature_Evidence.sql")
        service = text("Services/UserAdministrationService.cs")
        contract = text("Infrastructure/ComplianceRecordProtectionContract.cs")
        self.assertIn("CREATE TABLE dbo.UserAdministrationSignatures", migration)
        self.assertIn("MeaningOfSignature NVARCHAR(255) NOT NULL", migration)
        self.assertIn("CK_UserAdministrationSignatures_ActionReason_Specific", migration)
        self.assertIn("LEN(LTRIM(RTRIM(ActionReason))) >= 10", migration)
        self.assertIn("CREATE OR ALTER TRIGGER dbo.TRG_UserAdministrationSignatures_AppendOnly", migration)
        self.assertIn("user administration signature evidence is append-only and cannot be updated or deleted", migration)
        self.assertIn("INSERT dbo.UserAdministrationSignatures", service)
        self.assertEqual(4, service.count("await AddUserAdministrationSignatureAsync("))
        self.assertIn("UserAdministrationSignatures", contract)
        self.assertIn("TRG_UserAdministrationSignatures_AppendOnly", contract)

    def test_signature_actor_reason_and_meaning_are_required(self):
        service = text("Services/UserAdministrationService.cs")
        electronic = text("ElectronicSignature.xaml.cs")
        self.assertIn("RequireSignatureMeaning", service)
        self.assertIn("normalized.Length < 10", service)
        self.assertIn("RequireSignedActor", service)
        self.assertIn("Electronic signature must be completed by the currently authenticated User Manager", service)
        self.assertIn("generic preset reason is not sufficient", service)
        self.assertIn("cboReason.IsEditable = true;", electronic)
        self.assertIn("Type a specific justification for this exact user-account change", electronic)
        self.assertIn("cboReason.SelectedIndex >= 0", electronic)

    def test_new_user_has_no_privileged_default_role(self):
        code = text("UserManagement.xaml.cs")
        self.assertIn("CboRole.SelectedIndex = -1;", code)
        self.assertNotIn("CboRole.SelectedIndex = 1;", code)

    def test_busy_state_freezes_target_selection_and_new_user_action(self):
        xaml = text("UserManagement.xaml")
        code = text("UserManagement.xaml.cs")
        self.assertIn('x:Name="BtnNew"', xaml)
        self.assertIn("GridUsers.IsEnabled = !busy;", code)
        self.assertIn("BtnNew.IsEnabled = !busy;", code)
        self.assertIn("User target = _selectedUser;", code)
        self.assertGreaterEqual(code.count("User target = _selectedUser;"), 2)

    def test_responsive_infrastructure_preserves_explicit_scrollbar_policy(self):
        xaml = text("UserManagement.xaml")
        usability = text("Infrastructure/WindowUsability.cs")
        self.assertIn('HorizontalScrollBarVisibility="Disabled"', xaml)
        self.assertIn("ReadLocalValue(ScrollViewer.HorizontalScrollBarVisibilityProperty)", usability)
        self.assertIn("DependencyProperty.UnsetValue", usability)

    def test_unlock_noop_is_rejected(self):
        service = text("Services/UserAdministrationService.cs")
        self.assertIn("current.FailedLoginAttempts <= 0", service)
        self.assertIn("is not locked and has no failed-login counter to clear", service)

    def test_startup_and_preflight_require_signature_evidence_schema(self):
        app = text("App.xaml.cs")
        verify = text("Infrastructure/StartupDatabaseMigrator.Verification.cs")
        preflight = text("Infrastructure/SystemPreflightService.cs")
        self.assertGreaterEqual(app.count("UserAdministrationSignatureEvidenceMigrationKey"), 2)
        self.assertIn('UserAdministrationSignatureEvidenceMigrationKey = "20260917_001"', verify)
        self.assertIn("UserAdministrationSignatureEvidencePostconditionSql", verify)
        self.assertIn("dbo.UserAdministrationSignatures", preflight)
        self.assertIn("20260917_001_User_Administration_Signature_Evidence", preflight)

    def test_historical_user_admin_migrations_remain_unchanged(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        by_key = {m["versionKey"]: m for m in manifest["migrations"]}
        expected = {
            "20260911_000": "832ffb73859f2db064043bfe72e04744242ea5a645eec9c07ead919d558859b8",
            "20260915_000": "b4e2c69d6846957b72fd6641c37ec7f3adeb3d14b14710f8fb3999a5b3375e26",
            "20260917_000": "32ed032df23d1b14d152a14e720a5efbdf05fc24613c56ef4e94c39409309465",
        }
        for key, digest in expected.items():
            path = ROOT / "Database" / by_key[key]["file"]
            self.assertEqual(digest, hashlib.sha256(path.read_bytes()).hexdigest())
            self.assertEqual(digest, by_key[key]["sha256"])


if __name__ == "__main__":
    unittest.main()
