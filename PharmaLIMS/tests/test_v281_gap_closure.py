import hashlib
import json
import pathlib
import re
import subprocess
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]

class V281GapClosureTests(unittest.TestCase):
    def test_production_startup_is_verify_only(self):
        app = (ROOT / "App.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("VerifyControlledMigrationAsync", app)
        self.assertIn("if (AppConfig.IsDevelopment)", app)
        prod_branch = app.split("else", 1)[1]
        self.assertIn("VerifyControlledMigrationAsync", prod_branch)

    def test_verify_controlled_migration_checks_package_bytes_and_ledger_without_ddl(self):
        text = (ROOT / "Infrastructure/StartupDatabaseMigrator.Verification.cs").read_text(encoding="utf-8-sig")
        self.assertIn("SHA256.HashData", text)
        self.assertIn("LIMS_SchemaVersions", text)
        self.assertIn("Migration Required:", text)
        for forbidden in ("ALTER TABLE", "CREATE TABLE", "DROP TABLE", "UPDATE dbo.", "INSERT dbo."):
            self.assertNotIn(forbidden, text)

    def test_preflight_hashes_installed_sql_bytes(self):
        text = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("SHA256.HashData(fileBytes)", text)
        self.assertIn("packageChecksumMismatches", text)
        self.assertIn("missingPackageFiles", text)
        self.assertIn("package, manifest, and database ledger", text)

    def test_interactive_maintenance_rechecks_live_permission(self):
        migrator = (ROOT / "Infrastructure/StartupDatabaseMigrator.cs").read_text(encoding="utf-8-sig")
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        users = (ROOT / "Services/UserAdministrationService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("ApplyRequiredUpdatesAsUserAsync(Login.CurrentUser)", main)
        self.assertIn('"CanManageSettings"', migrator)
        self.assertIn("AcquireMigrationSessionApplicationLockAsync(maintenanceLease)", migrator)
        self.assertIn("PharmaLIMS.SchemaMigration", users)
        self.assertIn("@LockMode=N'Shared'", users)

    def test_direct_user_repository_mutation_is_compile_time_blocked(self):
        repo = (ROOT / "Repositories/UserRepository.cs").read_text(encoding="utf-8-sig")
        self.assertGreaterEqual(repo.count("[Obsolete("), 3)
        self.assertIn("error: true", repo)
        self.assertIn("Direct user creation through UserRepository is disabled", repo)

    def test_sqlite_is_not_used_and_sql_server_driver_is_explicit(self):
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        self.assertIn("Microsoft.Data.SqlClient", project)
        for path in ROOT.rglob("*"):
            if not path.is_file() or path.suffix.lower() not in {".cs", ".csproj", ".json", ".config"}:
                continue
            text = path.read_text(encoding="utf-8-sig", errors="ignore")
            self.assertNotIn("System.Data.SQLite", text)
            self.assertNotIn("Microsoft.Data.Sqlite", text)

    def test_session_timeout_and_di_are_present(self):
        app = (ROOT / "App.xaml.cs").read_text(encoding="utf-8-sig")
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        self.assertIn("ServiceCollection", app)
        self.assertIn("Microsoft.Extensions.DependencyInjection", project)
        self.assertIn("StartSessionTimeoutMonitor", app)
        self.assertIn("InputManager.Current.PreProcessInput", app)
        self.assertIn("SessionTimeoutMinutes", app)

    def test_dpapi_secret_support_and_plaintext_production_rejection(self):
        cfg = (ROOT / "AppConfig.cs").read_text(encoding="utf-8-sig")
        store = (ROOT / "Infrastructure/ProtectedSecretStore.cs").read_text(encoding="utf-8-sig")
        self.assertIn("DpapiProtectedPassword", cfg)
        self.assertIn("Plaintext SQL passwords are prohibited", cfg)
        self.assertIn("ProtectedData.Unprotect", store)
        self.assertIn("DataProtectionScope.CurrentUser", store)
        self.assertTrue((ROOT / "scripts/protect_db_password.ps1").exists())

    def test_partial_class_families_have_valid_base(self):
        proc = subprocess.run(
            [sys.executable, "-B", str(ROOT / "scripts/check_partial_classes.py")],
            cwd=ROOT, capture_output=True, text=True
        )
        self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)

    def test_unified_release_validation_runner_exists(self):
        text = (ROOT / "scripts/run_release_validation.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("Invoke-ReleaseValidation.ps1", text)
        self.assertNotIn("python -B -m pytest -q", text)
        authoritative = (ROOT / "scripts/Invoke-ReleaseValidation.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("dotnet build PharmaLIMS.csproj --configuration Release", authoritative)
        self.assertIn("PharmaLIMS.DatabaseIntegration", authoritative)

if __name__ == "__main__":
    unittest.main()
