import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class EmAuthorizationRefreshV269Tests(unittest.TestCase):
    def test_em_limit_save_revalidates_settings_permission_inside_transaction(self):
        source = (ROOT / "EMLimitsManagement.xaml.cs").read_text(encoding="utf-8-sig")
        save = source.split("DatabaseHelper.ExecuteInTransaction((connection, transaction) =>", 1)[1]
        self.assertIn("EnsureUserPermissionInTransaction", save)
        self.assertIn('"CanManageSettings"', save)
        self.assertIn("signature.SignedBy", save)
        self.assertIn("signerRole", save)
        self.assertNotIn("Login.CurrentUserRole", save.split("DatabaseHelper.AddAuditTrailAdvanced", 1)[0])

    def test_external_trend_development_admin_override_uses_fresh_database_role(self):
        source = (ROOT / "ExternalTrendImportDialog.xaml.cs").read_text(encoding="utf-8-sig")
        helper = source.split("private static bool IsAdministrativeRole()", 1)[1].split(
            "private static bool CanStageImport", 1
        )[0]
        self.assertIn("DatabaseHelper.GetUserRole(CurrentUser())", helper)
        self.assertNotIn("Login.CurrentUserRole", helper)
        self.assertIn("AppConfig.DevelopmentAdminFullPermissions", helper)


if __name__ == "__main__":
    unittest.main()
