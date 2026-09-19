import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]

def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

class V291PreflightDatetime2FixTests(unittest.TestCase):
    def test_signedat_datetime2_zero_uses_sql_server_metadata_length_six_everywhere(self):
        required = "name=N'SignedAt' AND system_type_id=TYPE_ID(N'datetime2') AND max_length=6 AND scale=0 AND is_nullable=0"
        forbidden = "name=N'SignedAt' AND system_type_id=TYPE_ID(N'datetime2') AND max_length=8 AND scale=0 AND is_nullable=0"
        paths = [
            "Infrastructure/StartupDatabaseMigrator.Verification.cs",
            "Infrastructure/SystemPreflightService.cs",
            "tests/PharmaLIMS.DatabaseIntegration/Program.cs",
        ]
        for path in paths:
            source = text(path)
            self.assertIn(required, source, path)
            self.assertNotIn(forbidden, source, path)

    def test_historical_user_administration_migration_still_declares_datetime2_zero(self):
        migration = text("Database/Migrations/20260917_001_User_Administration_Signature_Evidence.sql")
        self.assertIn("SignedAt DATETIME2(0) NOT NULL", migration)

    def test_preflight_blocker_still_fails_closed_for_real_signature_schema_mismatch(self):
        preflight = text("Infrastructure/SystemPreflightService.cs")
        self.assertIn("User administration signature evidence contract", preflight)
        self.assertIn("Migration Required:", preflight)
        self.assertIn("20260917_001_User_Administration_Signature_Evidence", preflight)

if __name__ == "__main__":
    unittest.main()
