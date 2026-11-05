"""Security wiring checks for the legacy isolated Windows-UAT compatibility surface.

The v309 trusted-signing path no longer authenticates through this compatibility
helper. These tests prove the helper remains bounded and cannot become a credential
replacement/general write bypass if it is retained for preflight compatibility.
"""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


class WindowsSigningRuntimeTests(unittest.TestCase):
    def test_uat_role_has_no_credential_replacement_or_general_write_authority(self):
        path = ROOT / "tests/eq001_windows_runtime_uat.sql"
        self.assertTrue(path.exists(), "Windows-only restricted runtime support is missing")
        text = path.read_text(encoding="utf-8-sig").replace("''", "'")
        self.assertIn("ORIGINAL_LOGIN()", text)
        self.assertGreaterEqual(text.count("PasswordChangedAt IS NOT NULL"), 3)
        self.assertIn("SUSER_SID(ORIGINAL_LOGIN())", text)
        self.assertIn("p.type=N'U'", text)
        self.assertIn("CHARINDEX(N'\\',@Actor)=0", text)
        self.assertIn("DENY INSERT, UPDATE, DELETE ON SCHEMA::dbo", text)
        self.assertIn(
            "DENY SELECT ON OBJECT::dbo.Users (PasswordHash,PasswordHashNew,PasswordSalt)",
            text,
        )
        for forbidden in [
            "SET PasswordHash",
            "SET PasswordSalt",
            "SET MustChangePassword",
            "INSERT dbo.AuditTrail",
            "ALTER LOGIN",
            "SESSION_CONTEXT",
        ]:
            self.assertNotIn(forbidden, text)

    def test_compatibility_runtime_is_preflight_only_not_a_user_repository_auth_bypass(self):
        runtime = (ROOT / "Infrastructure/WindowsSigningUatRuntime.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/UserRepository.cs").read_text(encoding="utf-8-sig")

        self.assertIn("internal static class WindowsSigningUatRuntime", runtime)
        self.assertIn("IsEnabledAsync", runtime)
        self.assertIn("IS_ROLEMEMBER", runtime)
        self.assertIn("AppConfig.IsDevelopment", runtime)
        self.assertNotIn("UPDATE dbo.Users", runtime)
        self.assertNotIn("PasswordHashNew", runtime)
        self.assertNotIn("PasswordSalt", runtime)

        self.assertIn("!AppConfig.TrustedSigningEnabled", preflight)
        self.assertIn("WindowsSigningUatRuntime.IsEnabledAsync", preflight)
        self.assertNotIn("WindowsSigningUatRuntime", repository)
        self.assertNotIn("EQ001_WindowsUat_", repository)

    def test_preflight_uses_readiness_flags_not_password_verifiers(self):
        text = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        section = text.split("string legacyCredentialQuery", 1)[1].split(
            "DataTable legacyCredentialFindings", 1
        )[0]

        self.assertIn("EQ001_CredentialReadiness", section)
        self.assertIn("CredentialInitialized=0", section)
        self.assertIn("MicroEquipmentSignerEligibility", section)
        # The trusted-signing and restricted-UAT branches consume readiness metadata,
        # not password hashes/salts. Direct Users access is only the non-trusted fallback.
        trusted_branch = section.split(': windowsRuntime ?', 1)[0]
        self.assertNotIn("PasswordHashNew", trusted_branch)
        self.assertNotIn("PasswordSalt", trusted_branch)


if __name__ == "__main__":
    unittest.main()
