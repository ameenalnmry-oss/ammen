import hashlib
import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]

def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

class V288UserAdministrationWriteCompatibilityTests(unittest.TestCase):
    def test_release_identity_and_controlled_migration(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        self.assertEqual("2026.9.18.294", manifest["applicationVersion"])
        self.assertEqual(83, len(manifest["migrations"]))
        entry = next(x for x in manifest["migrations"] if x["versionKey"] == "20260917_000")
        path = ROOT / "Database" / entry["file"]
        self.assertTrue(path.is_file())
        self.assertEqual(entry["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())

    def test_migration_repairs_columns_without_fabricating_legacy_creation_time(self):
        sql = text("Database/Migrations/20260917_000_User_Administration_Write_Compatibility.sql")
        self.assertIn("ADD CreatedAt DATETIME2(0) NULL", sql)
        self.assertIn("DEFAULT SYSUTCDATETIME()", sql)
        self.assertNotIn("WITH VALUES", sql)
        self.assertNotIn("UPDATE dbo.Users\n    SET CreatedAt", sql)
        self.assertIn("ADD UpdatedAt DATETIME2(0) NULL", sql)
        self.assertIn("No role, permission, activation state, password, lock state", sql)

    def test_write_paths_that_failed_are_covered_by_schema_contract(self):
        service = text("Services/UserAdministrationService.cs")
        repository = text("Repositories/UserRepository.cs")
        self.assertIn("CreatedAt, UpdatedAt", service)
        self.assertGreaterEqual(service.count("UpdatedAt = SYSUTCDATETIME()"), 3)
        self.assertIn("UpdatedAt = SYSUTCDATETIME()", repository)
        verify = text("Infrastructure/StartupDatabaseMigrator.Verification.cs")
        self.assertIn("UserAdministrationWriteCompatibilityPostconditionSql", verify)
        self.assertIn("name=N'CreatedAt'", verify)
        self.assertIn("name=N'UpdatedAt'", verify)

    def test_development_applies_and_production_only_verifies_new_bounded_migration(self):
        app = text("App.xaml.cs")
        branch = app.split("if (AppConfig.IsDevelopment)", 1)[1].split("loginWindow.SetStartupBusy(false)", 1)[0]
        self.assertIn("ApplyControlledMigrationAsync", branch)
        self.assertIn("VerifyControlledMigrationAsync", branch)
        self.assertGreaterEqual(branch.count("UserAdministrationWriteCompatibilityMigrationKey"), 2)
        self.assertNotIn("ApplyRequiredUpdatesAsync", app)

    def test_preflight_requires_write_compatibility_columns(self):
        preflight = text("Infrastructure/SystemPreflightService.cs")
        self.assertIn("dbo.Users.CreatedAt", preflight)
        self.assertIn("dbo.Users.UpdatedAt", preflight)
        self.assertIn("20260917_000_User_Administration_Write_Compatibility", preflight)

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

if __name__ == "__main__":
    unittest.main()
