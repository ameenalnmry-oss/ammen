import hashlib
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


class V265AuthenticationReadinessTests(unittest.TestCase):
    def test_manifest_controls_authentication_compatibility_migration(self):
        manifest = json.loads(source("Database/MigrationManifest.json"))
        matches = [m for m in manifest["migrations"] if m["versionKey"] == "20260911_000"]
        self.assertEqual(1, len(matches))
        entry = matches[0]
        migration = ROOT / "Database" / entry["file"]
        self.assertTrue(migration.is_file())
        self.assertEqual(entry["sha256"], hashlib.sha256(migration.read_bytes()).hexdigest())

    def test_auth_compatibility_is_narrow_and_does_not_reset_authorization_or_passwords(self):
        sql = source("Database/Migrations/20260911_000_Authentication_Login_Compatibility_Backfill.sql")
        for column in (
            "PasswordHashNew", "PasswordSalt", "FailedLoginAttempts", "IsLocked",
            "LockedUntil", "AuthenticationRowVersion", "CanAccessWater", "CanManageSettings",
        ):
            self.assertIn(column, sql)
        lowered = sql.lower()
        self.assertNotIn("update dbo.users set password", lowered)
        self.assertNotIn("insert into dbo.users", lowered)
        self.assertNotIn("set isactive = 1", lowered)
        self.assertNotIn("set islocked = 0", lowered)
        self.assertNotIn("default (1) with values", lowered)
        self.assertNotIn("admin123", lowered)

    def test_startup_runs_only_authentication_compatibility_before_enabling_login(self):
        app = source("App.xaml.cs")
        self.assertIn("AuthenticationLoginCompatibilityMigrationKey", app)
        self.assertIn("ApplyControlledMigrationAsync", app)
        self.assertIn("SetStartupBusy(true", app)
        self.assertIn("SetStartupBusy(false", app)
        self.assertLess(app.index("SetStartupBusy(true"), app.index("ApplyControlledMigrationAsync"))
        self.assertLess(app.index("ApplyControlledMigrationAsync"), app.index("SetStartupBusy(false"))
        startup = app.split("protected override async void OnStartup", 1)[1].split("private static string GetStartupErrorMessage", 1)[0]
        self.assertNotIn("ApplyRequiredUpdatesAsync", startup)

    def test_valid_secure_credential_never_downgrades_on_password_mismatch(self):
        auth = source("Services/AuthService.cs")
        verify = auth.split("private static PasswordVerificationResult VerifyPassword", 1)[1].split(
            "private enum PasswordVerificationResult", 1
        )[0]
        self.assertIn("IsCredentialEncodingStructurallyValid", verify)
        self.assertIn("if (!valid)", verify)
        self.assertIn("return PasswordVerificationResult.Invalid;", verify)
        self.assertIn("HasVerifiedLegacyCredential", verify)
        self.assertLess(verify.index("if (!valid)"), verify.index("HasVerifiedLegacyCredential(password, user)"))

    def test_password_encoding_structure_is_size_checked(self):
        security = source("Infrastructure/PasswordSecurity.cs")
        self.assertIn("IsCredentialEncodingStructurallyValid", security)
        self.assertIn("SaltSizeBytes", security)
        self.assertIn("HashSizeBytes", security)

    def test_post_authentication_workspace_failure_is_not_reported_as_bad_password(self):
        login = source("Login.xaml.cs")
        self.assertIn("authenticationCompleted = true", login)
        self.assertIn("Password was accepted, but the PharmaLIMS workspace could not open", login)
        self.assertIn("_authService?.ClearCurrentUser()", login)
        self.assertIn("this is not a password rejection", login)

    def test_water_core_sources_are_not_part_of_v265_authentication_fix(self):
        changeset = source("CHANGESET_v265.txt")
        self.assertIn("Water workflow source was not modified", changeset)


if __name__ == "__main__":
    unittest.main()
