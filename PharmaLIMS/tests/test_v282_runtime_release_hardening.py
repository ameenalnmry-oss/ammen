import json
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]

def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

class V282RuntimeReleaseHardeningTests(unittest.TestCase):
    def test_production_verification_checks_structural_postconditions(self):
        verifier = text("Infrastructure/StartupDatabaseMigrator.Verification.cs")
        self.assertIn("RecordedMigrationPostconditionsSatisfiedAsync", verifier)
        self.assertIn("AuthenticationLoginCompatibilityMigrationKey", verifier)
        self.assertIn("UserAdministrationSecurityMigrationKey", verifier)
        self.assertIn("database schema does not satisfy the controlled structural contract", verifier)
        self.assertIn("transaction: null", verifier)

    def test_user_administration_migration_has_physical_contract(self):
        migrator = text("Infrastructure/StartupDatabaseMigrator.cs")
        verifier = text("Infrastructure/StartupDatabaseMigrator.Verification.cs")
        self.assertIn('"20260915_000" => UserAdministrationSecurityPostconditionSql', migrator)
        self.assertIn("MustChangePassword", verifier)
        self.assertIn("TYPE_ID(N'bit')", verifier)
        self.assertIn("PasswordChangedAt", verifier)
        self.assertIn("TYPE_ID(N'datetime2')", verifier)
        self.assertIn("scale=0", verifier)

    def test_runtime_smoke_is_real_project_and_solution_member(self):
        self.assertTrue((ROOT / "tests/PharmaLIMS.RuntimeSmoke/PharmaLIMS.RuntimeSmoke.csproj").is_file())
        program = text("tests/PharmaLIMS.RuntimeSmoke/Program.cs")
        self.assertIn("StartupDatabaseMigrator", program)
        self.assertIn("ApplyRequiredUpdatesAsync", program)
        self.assertIn('ExpectedLoginTitle = "PharmaLIMS - Login"', program)
        self.assertIn("CREATE DATABASE", program)
        self.assertIn("DROP DATABASE", program)
        solution = text("pharmaLIMS.slnx")
        self.assertIn("tests/PharmaLIMS.RuntimeSmoke/PharmaLIMS.RuntimeSmoke.csproj", solution)

    def test_authoritative_runner_uses_unittest_sql_and_runtime_smoke(self):
        runner = text("scripts/Invoke-ReleaseValidation.ps1")
        self.assertIn('unittest discover -s tests -p "test_*.py"', runner)
        self.assertIn("RunDatabaseIntegration", runner)
        self.assertIn("RunRuntimeSmoke", runner)
        self.assertIn("PharmaLIMS.RuntimeSmoke", runner)
        self.assertNotIn("-m pytest", runner)
        wrapper = text("scripts/run_release_validation.ps1")
        self.assertIn("Invoke-ReleaseValidation.ps1", wrapper)
        self.assertNotIn("-m pytest", wrapper)

    def test_ci_calls_same_authoritative_runner(self):
        ci = text(".github/workflows/ci.yml")
        self.assertIn("./scripts/Invoke-ReleaseValidation.ps1 -RunDatabaseIntegration -RunRuntimeSmoke", ci)
        self.assertNotIn("python -B -m unittest discover", ci)
        self.assertNotIn("dotnet run --project tests/PharmaLIMS.DatabaseIntegration", ci)

    def test_current_release_identity_advances_beyond_v282(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        self.assertEqual("2026.9.18.294", manifest["applicationVersion"])
        project = text("PharmaLIMS.csproj")
        self.assertIn("<Version>2026.9.18.294</Version>", project)
        self.assertTrue((ROOT / "RELEASE_NOTES_2026.9.18.294.md").is_file())
        self.assertTrue((ROOT / "SBOM_2026.9.18.294.cdx.json").is_file())

if __name__ == '__main__':
    unittest.main()
