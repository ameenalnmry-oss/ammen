import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class RuntimePermissionLockingV269Tests(unittest.TestCase):
    def test_database_helper_role_and_permission_reads_fail_closed_for_effective_lock(self):
        source = (ROOT / "DatabaseHelper.SecurityAudit.cs").read_text(encoding="utf-8-sig")
        role = source.split("public static string GetUserRole", 1)[1].split(
            "private static bool GetUserPermissionFlag", 1
        )[0]
        permission = source.split("private static bool GetUserPermissionFlag", 1)[1].split(
            "internal static string EnsureUserPermissionInTransaction", 1
        )[0]
        lock_clause = (
            "AND NOT (ISNULL(IsLocked, 0) = 1 AND "
            "(LockedUntil IS NULL OR LockedUntil > SYSDATETIME()))"
        )
        self.assertIn(lock_clause, role)
        self.assertIn(lock_clause, permission)

    def test_repository_role_and_permission_reads_fail_closed_for_effective_lock(self):
        source = (ROOT / "Repositories/UserRepository.cs").read_text(encoding="utf-8-sig")
        role = source.split("public async Task<string> GetRoleAsync", 1)[1].split(
            "public async Task<bool> GetPermissionAsync", 1
        )[0]
        permission = source.split("public async Task<bool> GetPermissionAsync", 1)[1].split(
            "private SqlParameter[] BuildUserParameters", 1
        )[0]
        lock_clause = (
            "AND NOT (ISNULL(IsLocked, 0) = 1 AND "
            "(LockedUntil IS NULL OR LockedUntil > SYSDATETIME()))"
        )
        self.assertIn(lock_clause, role)
        self.assertIn(lock_clause, permission)

    def test_production_electronic_signature_cannot_disable_password_validation(self):
        source = (ROOT / "ElectronicSignature.xaml.cs").read_text(encoding="utf-8-sig")
        configure = source.split("public void Configure", 1)[1].split("private void InitializeDefaults", 1)[0]
        self.assertIn("!requirePasswordValidation && AppConfig.IsProduction", configure)
        self.assertIn("Electronic signatures require current-user password validation in Production", configure)

    def test_em_trend_print_revalidates_report_permission_from_database(self):
        source = (ROOT / "EMTrendReport.xaml.cs").read_text(encoding="utf-8-sig")
        print_handler = source.split("private void Print_Click", 1)[1]
        self.assertIn("DatabaseHelper.CanAccessReports(Login.CurrentUser)", print_handler)
        self.assertNotIn("if (!Login.CanAccessReports)", print_handler)

    def test_prm_development_database_maintenance_revalidates_admin_and_settings_permission(self):
        source = (ROOT / "ProductionRawMaterialResults.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("DatabaseHelper.GetUserRole(GetCurrentUserDisplayName())", source)
        self.assertIn("DatabaseHelper.CanManageSettings(GetCurrentUserDisplayName())", source)
        self.assertNotIn("Login.CanManageSettings", source)
        part2 = (ROOT / "ProductionRawMaterialResults.xaml.Part2.cs").read_text(encoding="utf-8-sig")
        self.assertIn("DatabaseHelper.CanManageSettings(GetCurrentUserDisplayName())", part2)
        self.assertNotIn("Login.CanManageSettings", part2)


if __name__ == "__main__":
    unittest.main()
