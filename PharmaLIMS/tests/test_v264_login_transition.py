import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


class V264LoginTransitionTests(unittest.TestCase):
    def test_interactive_legacy_login_must_upgrade_before_session(self):
        auth = source("Services/AuthService.cs")
        login_flow = auth.split("public async Task<User?> AuthenticateAsync", 1)[1].split(
            "public async Task<bool> ValidateCurrentUserPasswordAsync", 1
        )[0]
        self.assertIn("verification == PasswordVerificationResult.ValidLegacy", login_flow)
        self.assertIn("FinalizeVerifiedUserAsync", login_flow)
        self.assertNotIn("Production sign-in blocked", login_flow)
        self.assertLess(
            login_flow.index("FinalizeVerifiedUserAsync"),
            login_flow.index("_currentUser = user"),
        )

    def test_electronic_signature_still_fails_closed_for_unmigrated_legacy_credential(self):
        auth = source("Services/AuthService.cs")
        signature_flow = auth.split("public async Task<bool> ValidateCurrentUserPasswordAsync", 1)[1].split(
            "public User? GetCurrentUser()", 1
        )[0]
        self.assertIn("PasswordVerificationResult.ValidLegacy && AppConfig.IsProduction", signature_flow)
        self.assertIn("Electronic-signature validation blocked", signature_flow)

    def test_atomic_upgrade_replaces_legacy_secret_under_rowversion_guard(self):
        commit = source("Services/AuthenticationCommitContract.cs")
        self.assertIn("AuthenticationRowVersion=@ExpectedVersion", commit)
        self.assertIn("PasswordHashNew = CASE WHEN @Upgrade=1", commit)
        self.assertIn("PasswordSalt = CASE WHEN @Upgrade=1", commit)
        self.assertIn("PasswordHash = CASE WHEN @Upgrade=1 THEN N'[MIGRATED]'", commit)

    def test_preflight_does_not_confuse_transition_with_identity_corruption(self):
        preflight = source("Infrastructure/SystemPreflightService.cs")
        self.assertIn('"BLOCKER", "User identity"', preflight)
        self.assertIn('"WARNING", "Secure credential transition"', preflight)
        self.assertIn("pending one-time secure credential migration", preflight)


if __name__ == "__main__":
    unittest.main()
