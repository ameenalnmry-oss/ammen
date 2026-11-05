"""Wiring checks complement the executable SQL transaction regression.

Regression: a verified temporary-password user has no Login.CurrentUser yet.
Routing its audit through the workspace signer resolver must fail this test.
"""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def source(name):
    return (ROOT / name).read_text(encoding="utf-8-sig")


class PasswordChangeAuditTests(unittest.TestCase):
    def test_first_login_password_change_audits_without_workspace_identity(self):
        block = source("Repositories/UserRepository.cs").split(
            "public async Task<bool> ChangeVerifiedPasswordAsync", 1)[1].split(
            "public async Task<string> GetRoleAsync", 1)[0]
        self.assertNotIn("DatabaseHelper.AddAuditTrailAdvanced(", block,
                         "First-login audit must not require a workspace session that is still blocked.")
        self.assertIn("AuthenticationCommitContract.PasswordChangeSql", block)
        self.assertIn("AuthenticationCommitContract.PasswordChangeAuditSql", block)
        self.assertIn('"@CommittedVersion"', block)
        self.assertLess(block.index("PasswordChangeAuditSql"), block.index("CommitAsync"))
        self.assertLess(block.index("CommitAsync"), block.index("verifiedUser.MustChangePassword = false"))
        self.assertIn("RollbackAsync(CancellationToken.None)", block)

    def test_workspace_and_electronic_signatures_stay_blocked_until_change_commits(self):
        login = source("Login.xaml.cs")
        self.assertLess(login.index("passwordChange.ShowDialog()"), login.index("CurrentUser = user.Username"))
        auth = source("Services/AuthService.cs")
        block = auth.split("public async Task<bool> ChangeCurrentUserPasswordAsync", 1)[1].split(
            "public User? GetCurrentUser()", 1)[0]
        self.assertLess(block.index("VerifyPassword(currentPassword, freshUser)"), block.index("ChangeVerifiedPasswordAsync"))
        self.assertNotIn("Login.CurrentUser =", block)
        self.assertIn("if (freshUser.MustChangePassword)", auth)
        audit = source("DatabaseHelper.SecurityAudit.cs").split(
            "private static string ResolveAuthenticatedSigner", 1)[1]
        self.assertIn("An authenticated user account is required", audit)

    def test_password_audit_binds_exact_updated_account_and_never_accepts_free_actor(self):
        contract = source("Services/AuthenticationCommitContract.cs")
        self.assertIn('internal const string PasswordChangeAuditSql = @"', contract)
        audit = contract.split('internal const string PasswordChangeAuditSql = @"', 1)[1].split('";', 1)[0]
        for token in ["INSERT dbo.AuditTrail", "account.UserID = @UserID", "account.Username = @Username",
                      "account.AuthenticationRowVersion = @CommittedVersion", "account.MustChangePassword = 0",
                      "account.PasswordChangedAt IS NOT NULL", "@@ROWCOUNT <> 1", "THROW 55221",
                      "account.Username, account.Username", "SYSUTCDATETIME()"]:
            self.assertIn(token, audit)
        for token in ["@PerformedBy", "@SignedBy", "@PasswordHashNew", "@PasswordSalt", "Login.CurrentUser"]:
            self.assertNotIn(token, audit)


if __name__ == "__main__":
    unittest.main()
