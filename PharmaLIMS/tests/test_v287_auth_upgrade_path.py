import hashlib
import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]

def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

class V287AuthenticationUpgradePathTests(unittest.TestCase):
    def test_debug_startup_reconciles_auth_prerequisites_without_enabling_production_ddl(self):
        app = text("App.xaml.cs")
        branch = app.split("if (AppConfig.IsDevelopment)", 1)[1].split("loginWindow.SetStartupBusy(false)", 1)[0]
        self.assertIn("ApplyControlledMigrationAsync", branch)
        self.assertIn("AuthenticationLoginCompatibilityMigrationKey", branch)
        self.assertIn("UserAdministrationSecurityMigrationKey", branch)
        self.assertIn("else", branch)
        self.assertIn("VerifyControlledMigrationAsync", branch)
        self.assertNotIn("ApplyRequiredUpdatesAsync", app)

    def test_auth_repair_preserves_explicit_values_and_only_maps_null_security_bits_to_zero(self):
        helper = text("Infrastructure/StartupDatabaseMigrator.Part2.cs").split(
            "private static async Task PrepareAuthenticationLoginCompatibilityAsync", 1
        )[1].split("private static async Task PrepareUserAdministrationSecurityCompatibilityAsync", 1)[0]
        self.assertIn("SET IsActive=0", helper)
        self.assertIn("WHERE IsActive IS NULL", helper)
        self.assertIn("PermissionColumns CURSOR", helper)
        self.assertIn("BIT NOT NULL", helper)
        self.assertIn("SET FailedLoginAttempts=0", helper)
        self.assertIn("SET IsLocked=0", helper)
        for forbidden in ("SET IsActive=1", "CanAccessWater=1", "CanManageSettings=1", "PasswordHashNew=", "PasswordSalt="):
            self.assertNotIn(forbidden, helper)

    def test_20260915_nullable_password_change_flag_is_repaired_fail_safe(self):
        helper = text("Infrastructure/StartupDatabaseMigrator.Part2.cs").split(
            "private static async Task PrepareUserAdministrationSecurityCompatibilityAsync", 1
        )[1]
        self.assertIn("SET MustChangePassword=0", helper)
        self.assertIn("WHERE MustChangePassword IS NULL", helper)
        self.assertIn("ALTER TABLE dbo.Users ALTER COLUMN MustChangePassword BIT NOT NULL", helper)
        self.assertIn("automatic conversion could lose audit timestamp precision and is blocked", helper)

    def test_historical_auth_migration_bytes_remain_unchanged(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        by_key = {m["versionKey"]: m for m in manifest["migrations"]}
        expected = {
            "20260911_000": "832ffb73859f2db064043bfe72e04744242ea5a645eec9c07ead919d558859b8",
            "20260915_000": "b4e2c69d6846957b72fd6641c37ec7f3adeb3d14b14710f8fb3999a5b3375e26",
        }
        for key, digest in expected.items():
            path = ROOT / "Database" / by_key[key]["file"]
            self.assertEqual(digest, hashlib.sha256(path.read_bytes()).hexdigest())
            self.assertEqual(digest, by_key[key]["sha256"])

    def test_release_identity_is_v287(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertIn("<Version>2026.9.23.295</Version>", text("PharmaLIMS.csproj"))

if __name__ == "__main__":
    unittest.main()
