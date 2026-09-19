import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


class UserManagementRegressionTests(unittest.TestCase):
    def test_release_identity_and_security_migration(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        self.assertEqual("2026.9.18.294", manifest["applicationVersion"])
        self.assertEqual(82, len(manifest["migrations"]))
        migration = next(x for x in manifest["migrations"] if x["versionKey"] == "20260915_000")
        self.assertEqual("Migrations/20260915_000_User_Administration_Security_Hardening.sql", migration["file"])
        sql = text("Database/Migrations/20260915_000_User_Administration_Security_Hardening.sql")
        self.assertIn("MustChangePassword BIT NOT NULL", sql)
        self.assertIn("PasswordChangedAt DATETIME2(0) NULL", sql)
        app = text("App.xaml.cs")
        self.assertIn("UserAdministrationSecurityMigrationKey", app)

    def test_user_management_window_and_di_are_registered(self):
        app = text("App.xaml.cs")
        xaml = text("UserManagement.xaml")
        code = text("UserManagement.xaml.cs")
        self.assertIn("services.AddSingleton<UserAdministrationService>();", app)
        self.assertIn("services.AddTransient<UserManagement>();", app)
        self.assertIn("services.AddTransient<ChangePassword>();", app)
        self.assertIn('x:Class="PharmaLIMS.UserManagement"', xaml)
        self.assertIn("UserManagement(UserAdministrationService administration)", code)

    def test_roles_are_controlled_not_free_text(self):
        xaml = text("UserManagement.xaml")
        service = text("Services/UserAdministrationService.cs")
        self.assertIn('x:Name="CboRole" IsEditable="False"', xaml)
        for role in ["Admin", "Administrator", "Technician", "Supervisor", "QA", "Quality Assurance"]:
            self.assertIn(f'"{role}"', service)
        self.assertIn("Role must be selected from the controlled PharmaLIMS role list.", service)

    def test_user_list_and_mutations_revalidate_current_permission(self):
        service = text("Services/UserAdministrationService.cs")
        self.assertIn('"view user accounts and permissions"', service)
        self.assertGreaterEqual(service.count("EnsureUserPermissionInTransaction("), 5)
        self.assertGreaterEqual(service.count('"CanManageUsers"'), 5)
        self.assertIn("AddAuditTrailAdvanced(", service)
        for event in ["User Account Created", "User Account Updated", "User Password Reset", "User Account Unlocked"]:
            self.assertIn(f'"{event}"', service)

    def test_self_privilege_escalation_and_self_admin_reset_are_blocked(self):
        service = text("Services/UserAdministrationService.cs")
        ui = text("UserManagement.xaml.cs")
        self.assertIn("AdministrativeAccountChanged(current, user)", service)
        self.assertIn("You cannot administratively modify your own PharmaLIMS account", service)
        self.assertIn("Use Change Password for your own account", service)
        self.assertIn("TxtFullName.IsEnabled = !editingSelf;", ui)
        self.assertIn("ChkActive.IsEnabled = !editingSelf;", ui)
        self.assertIn("BtnSave.IsEnabled = !busy && !editingSelf;", ui)
        self.assertIn("CboRole.IsEnabled = !editingSelf;", ui)
        self.assertIn("ChkApproveResults.IsEnabled = !editingSelf;", ui)

    def test_last_operational_user_manager_is_protected(self):
        service = text("Services/UserAdministrationService.cs")
        self.assertIn("EnsureAnotherActiveUserManagerExistsAsync", service)
        self.assertIn("ISNULL(MustChangePassword,0)=0", service)
        self.assertIn("LockedUntil IS NULL OR LockedUntil>SYSDATETIME()", service)
        self.assertIn("At least one other active account with User Management permission", service)

    def test_updates_reset_and_unlock_are_rowversion_guarded(self):
        service = text("Services/UserAdministrationService.cs")
        self.assertGreaterEqual(service.count("AuthenticationRowVersion = @ExpectedVersion"), 3)
        self.assertGreaterEqual(service.count("EnsureExpectedVersion("), 4)
        ui = text("UserManagement.xaml.cs")
        self.assertIn("target.AuthenticationRowVersion", ui)
        repository = text("Repositories/UserRepository.cs")
        self.assertIn("AuthenticationRowVersion", repository)

    def test_temporary_password_must_be_changed_before_workspace_and_esignature(self):
        service = text("Services/UserAdministrationService.cs")
        login = text("Login.xaml.cs")
        auth = text("Services/AuthService.cs")
        repository = text("Repositories/UserRepository.cs")
        self.assertIn("MustChangePassword = 1", service)
        self.assertIn("PasswordChangedAt = NULL", service)
        self.assertIn("if (user.MustChangePassword)", login)
        self.assertIn("new ChangePassword(_authService)", login)
        self.assertIn("if (freshUser.MustChangePassword)", auth)
        self.assertIn("Electronic-signature validation blocked", auth)
        self.assertIn("MustChangePassword = 0", repository)
        self.assertIn('"Password Changed"', repository)
        self.assertIn('moduleName: "Authentication"', repository)

    def test_password_policy_and_password_values_are_not_audited(self):
        security = text("Infrastructure/PasswordSecurity.cs")
        service = text("Services/UserAdministrationService.cs")
        self.assertIn("password.Length < 12", security)
        self.assertIn("char.IsUpper", security)
        self.assertIn("char.IsLower", security)
        self.assertIn("char.IsDigit", security)
        self.assertIn("Password must not contain the username", security)
        self.assertIn("PasswordSecurity.ValidateNewPassword(initialPassword, user.Username)", service)
        self.assertIn("Credential replaced securely; value not exposed", service)
        self.assertNotIn("DescribeUser(user) + initialPassword", service)

    def test_password_reset_success_log_is_after_commit(self):
        service = text("Services/UserAdministrationService.cs")
        block = service.split("public async Task ResetPasswordAsync", 1)[1].split("public async Task UnlockUserAsync", 1)[0]
        self.assertLess(block.index("CommitAsync"), block.index("User password reset completed"))

    def test_user_actions_require_electronic_signature(self):
        code = text("UserManagement.xaml.cs")
        for action in ["User Account Creation", "User Account and Permission Update", "User Password Reset", "User Account Unlock"]:
            self.assertIn(f'"{action}"', code)
        self.assertGreaterEqual(code.count("new ElectronicSignature("), 3)

    def test_usernames_are_immutable_after_creation(self):
        service = text("Services/UserAdministrationService.cs")
        code = text("UserManagement.xaml.cs")
        self.assertIn("Username is immutable after account creation", service)
        self.assertIn("TxtUsername.IsReadOnly = true;", code)

    def test_critical_schema_preflight_requires_new_user_columns(self):
        preflight = text("Infrastructure/SystemPreflightService.cs")
        self.assertIn("dbo.Users.MustChangePassword", preflight)
        self.assertIn("BIT NOT NULL", preflight)
        self.assertIn("dbo.Users.PasswordChangedAt", preflight)
        self.assertIn("DATETIME2(0) NULL", preflight)
        self.assertIn("20260915_000_User_Administration_Security_Hardening", preflight)

    def test_existing_permission_flags_are_exposed(self):
        xaml = text("UserManagement.xaml")
        for name in [
            "ChkAccessWater", "ChkAccessEM", "ChkRegisterSamples", "ChkEnterResults",
            "ChkReviewResults", "ChkApproveResults", "ChkIssueCOA", "ChkCancelCOA",
            "ChkAccessReports", "ChkManageUsers", "ChkManageSettings"
        ]:
            self.assertIn(f'x:Name="{name}"', xaml)


if __name__ == "__main__":
    unittest.main()
