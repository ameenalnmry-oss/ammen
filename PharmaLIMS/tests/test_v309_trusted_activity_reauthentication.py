from __future__ import annotations

import hashlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(rel: str) -> str:
    return (ROOT / rel).read_text(encoding="utf-8")


def test_v309_identity_and_migration_manifest() -> None:
    assert "<Version>2026.10.19.309</Version>" in text("PharmaLIMS.csproj")
    assert 'AssemblyInformationalVersion("2026.10.19.309")' in text("AssemblyInfo.cs")

    manifest = json.loads(text("Database/MigrationManifest.json"))
    assert manifest["applicationVersion"] == "2026.10.19.309"
    entry = next(x for x in manifest["migrations"] if x["versionKey"] == "20261030_001")
    migration = ROOT / "Database" / entry["file"]
    assert hashlib.sha256(migration.read_bytes()).hexdigest() == entry["sha256"]


def test_activity_authenticator_uses_primary_eq001_identity_authority() -> None:
    source = text("tools/PharmaLIMS.TrustedSigning.Host/TrustedActivityAuthenticator.cs")
    program = text("tools/PharmaLIMS.TrustedSigning.Host/Program.cs")
    assert "UserRepository" in source
    assert "DatabaseConnection" in source
    assert "PasswordChangedAt is null" in source
    assert "PasswordSecurity.Verify" in source
    assert "ResetAuthenticationFailuresAsync" in source
    assert "MicroEquipment_ReadActivitySignerCredential" not in source
    assert "IntegratedSecurity" in source
    assert "TrustServerCertificate" in source
    assert "new TrustedActivityAuthenticator(settings.ConnectionString)" in program
    assert "new SqlTrustedActivitySignatureStore(activityConnection)" in program


def test_migration_keeps_activity_authentication_least_privileged() -> None:
    sql = text("Database/Migrations/20261030_001_Trusted_Activity_Authentication_Bridge.sql")
    for proc in (
        "MicroEquipment_ReadActivitySignerCredential",
        "MicroEquipment_RecordActivityAuthFailure",
        "MicroEquipment_CommitActivityAuthSuccess",
    ):
        assert f"CREATE OR ALTER PROCEDURE dbo.{proc}" in sql
        assert f"GRANT EXECUTE ON dbo.{proc} TO PharmaLIMS_TrustedEquipmentSigner" in sql

    assert sql.count("WITH EXECUTE AS OWNER") >= 3
    assert "p.sid=SUSER_SID(ORIGINAL_LOGIN())" in sql
    assert "IS_SRVROLEMEMBER(N''sysadmin'',ORIGINAL_LOGIN())" in sql
    assert "DENY SELECT ON OBJECT::dbo.Users (PasswordHash,PasswordHashNew,PasswordSalt)" in sql
    assert "DENY UPDATE ON OBJECT::dbo.Users TO PharmaLIMS_TrustedEquipmentSigner" in sql


def test_configure_script_requires_bridge_and_does_not_restore_broad_user_grants() -> None:
    script = text("scripts/Configure-UatActivitySigning.ps1")
    assert "MicroEquipment_ReadActivitySignerCredential" in script
    assert "MicroEquipment_RecordActivityAuthFailure" in script
    assert "MicroEquipment_CommitActivityAuthSuccess" in script
    assert "GRANT SELECT ON OBJECT::dbo.Users TO" not in script
    assert "GRANT UPDATE (" not in script
    assert "DENY SELECT ON OBJECT::dbo.Users (PasswordHash,PasswordHashNew,PasswordSalt)" in script
    assert "DENY UPDATE ON OBJECT::dbo.Users TO [PharmaLIMS_TrustedEquipmentSigner]" in script


def test_activity_auth_success_does_not_modify_last_login() -> None:
    sql = text("Database/Migrations/20261030_001_Trusted_Activity_Authentication_Bridge.sql")
    success = sql.split("CREATE OR ALTER PROCEDURE dbo.MicroEquipment_CommitActivityAuthSuccess", 1)[1]
    success = success.split("END;');", 1)[0]
    assert "LastLogin" not in success
    assert "FailedLoginAttempts=0" in success
    assert "AuthenticationRowVersion=@ExpectedVersion" in success


def test_primary_identity_hotfix_migration_keeps_isolated_authorization_gate() -> None:
    manifest = json.loads(text("Database/MigrationManifest.json"))
    entry = next(x for x in manifest["migrations"] if x["versionKey"] == "20261031_001")
    assert entry["databaseScope"] == "ActivitySigning"
    migration = ROOT / "Database" / entry["file"]
    sql = migration.read_text(encoding="utf-8")
    assert hashlib.sha256(migration.read_bytes()).hexdigest() == entry["sha256"]
    assert "ORIGINAL_LOGIN()" in sql
    assert "PharmaLIMS_TrustedEquipmentSigner" in sql
    assert "CanReviewResults=1" in sql
    assert "CanApproveResults=1" in sql
    signing = sql.split("CREATE OR ALTER PROCEDURE dbo.MicroEquipment_SignActivityStage", 1)[1]
    assert "CredentialInitialized=1" not in signing
    assert "MustChangePassword" not in signing
    assert "LockedUntil" not in signing


def test_uat_signer_provisioning_uses_bridge_not_direct_user_credentials() -> None:
    sql = text("tests/eq001_provision_trusted_signer_uat.sql")
    assert "MicroEquipment_ReadActivitySignerCredential" in sql
    assert "MicroEquipment_RecordActivityAuthFailure" in sql
    assert "MicroEquipment_CommitActivityAuthSuccess" in sql
    assert "DENY SELECT ON OBJECT::dbo.Users (PasswordHash,PasswordHashNew,PasswordSalt)" in sql
    assert "DENY UPDATE ON OBJECT::dbo.Users" in sql


def test_incomplete_primary_credential_is_finalized_before_workspace_or_activity_signing() -> None:
    login = text("Login.xaml.cs")
    auth = text("Services/AuthService.cs")
    assert "user.MustChangePassword || user.PasswordChangedAt is null" in login
    method = auth.split("public async Task<bool> ChangeCurrentUserPasswordAsync", 1)[1]
    validation = method.index("PasswordSecurity.ValidateNewPassword(newPassword, _currentUser.Username);")
    trusted = method.index("if (_useTrustedTransport && AppConfig.TrustedSigningEnabled)")
    assert validation < trusted


def test_activity_runtime_uses_canonical_trusted_application_reauthentication() -> None:
    server = text("tools/PharmaLIMS.TrustedSigning/WindowsTrustedPipeServer.cs")
    endpoint = text("tools/PharmaLIMS.TrustedSigning.Host/TrustedApplicationEndpoint.cs")
    auth = text("Services/AuthService.cs")
    coordinator = text("tools/PharmaLIMS.TrustedSigning/TrustedActivitySigning.cs")

    assert '"ActivityReauthenticate"' in endpoint
    assert "ReauthenticateForTrustedActionAsync" in endpoint
    assert '"ActivityReauthenticate"' in server
    assert "CommitVerifiedAsync" in server
    assert "ReauthenticateActivityAsync" in server
    assert "public async Task<User?> ReauthenticateForTrustedActionAsync" in auth

    method = auth.split("public async Task<User?> ReauthenticateForTrustedActionAsync", 1)[1]
    method = method.split("public async Task<bool> ChangeCurrentUserPasswordAsync", 1)[0]
    assert "VerifyPassword(password, user)" in method
    assert "RegisterAuthenticationFailureAsync" in method
    assert "FinalizeVerifiedUserAsync" in method
    assert "isLogin: false" in method
    assert "PasswordChangedAt is null" in method

    assert "public async Task CommitVerifiedAsync" in coordinator
