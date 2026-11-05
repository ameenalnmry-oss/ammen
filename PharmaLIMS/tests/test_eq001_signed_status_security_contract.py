from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
MIGRATIONS = ROOT / "Database/Migrations"
LAB = ROOT / "LabResources.xaml.cs"
DIALOG = ROOT / "TrustedEquipmentSignature.xaml.cs"
TRUSTED = ROOT / "tools/PharmaLIMS.TrustedSigning"
HOST = ROOT / "tools/PharmaLIMS.TrustedSigning.Host"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


class Eq001SecurityBoundary(unittest.TestCase):
    """Security-contract checks for the current v309 trusted activity-signing design.

    These tests intentionally follow behavior/control ownership rather than obsolete
    pre-refactor filenames. They must fail if the trusted Windows boundary, database
    isolation, canonical credential verification, SoD/concurrency inputs, or
    fail-closed client path are removed.
    """

    def test_signing_uses_controlled_database_operation(self):
        ui = read(LAB)
        store = read(TRUSTED / "SqlTrustedActivitySignatureStore.cs")
        signing = ui.split("private async Task SignActivityStageAsync(string stage)", 1)[1].split(
            "private void BtnNew_Click", 1
        )[0]

        self.assertIn("ReadActivitySnapshotAsync", signing)
        self.assertIn("new ActivitySigningRequest", signing)
        self.assertIn("snapshot.VersionToken", signing)
        self.assertIn("snapshot.DatabaseName", signing)
        self.assertNotIn("MicroEquipment_SignActivityStage", signing)
        self.assertNotIn("UPDATE dbo.MicroEquipmentActivities", signing)
        self.assertNotIn("INSERT dbo.MicroEquipmentActivitySignatures", signing)

        self.assertIn('new SqlCommand("dbo.MicroEquipment_SignActivityStage"', store)
        self.assertIn("CommandType = CommandType.StoredProcedure", store)
        self.assertNotIn("UPDATE dbo.MicroEquipmentActivities", store)
        self.assertNotIn("INSERT dbo.MicroEquipmentActivitySignatures", store)

    def test_direct_app_signature_inserts_are_forbidden(self):
        self.assertTrue(
            any(
                "DENY INSERT ON dbo.MicroEquipmentActivitySignatures TO "
                in read(path)
                for path in MIGRATIONS.glob("*.sql")
            ),
            "EQ-001 OPEN: migration does not restrict direct application signature insertion",
        )

    def test_shared_sql_caller_cannot_execute_elevated_signing(self):
        latest = read(MIGRATIONS / "20261031_001_Activity_Signing_Primary_Identity_Authority.sql")
        self.assertIn("ORIGINAL_LOGIN()", latest)
        self.assertIn("SUSER_SID(ORIGINAL_LOGIN())", latest)
        self.assertIn("PharmaLIMS_TrustedEquipmentSigner", latest)
        self.assertIn("IS_SRVROLEMEMBER", latest)
        self.assertIn("THROW 56750", latest)

        cutover = read(ROOT / "tests/eq001_permission_cutover_after_uat.sql").upper()
        self.assertIn(
            "DENY EXECUTE ON OBJECT::DBO.MICROEQUIPMENT_SIGNACTIVITYSTAGE TO PHARMALIMS_EQUIPMENTAPP;",
            cutover,
        )
        self.assertIn(
            "GRANT EXECUTE ON OBJECT::DBO.MICROEQUIPMENT_SIGNACTIVITYSTAGE TO PHARMALIMS_TRUSTEDEQUIPMENTSIGNER;",
            cutover,
        )

    def test_uat_signing_request_cannot_cross_databases(self):
        ui = read(LAB)
        coordinator = read(TRUSTED / "TrustedActivitySigning.cs")
        store = read(TRUSTED / "SqlTrustedActivitySignatureStore.cs")

        self.assertIn("snapshot.DatabaseName", ui)
        self.assertIn("ActivitySigningDatabasePolicy.IsAllowedUatDatabase", coordinator)
        self.assertIn("ActivitySigningDatabasePolicy.IsAllowedUatDatabase(settings.InitialCatalog)", store)
        self.assertIn("!request.DatabaseName.Equals(_databaseName, StringComparison.Ordinal)", store)
        self.assertIn("IntegratedSecurity", store)
        self.assertIn("settings.TrustServerCertificate", store)

    def test_trusted_service_uses_canonical_primary_credential_and_local_stage_gate(self):
        server = read(TRUSTED / "WindowsTrustedPipeServer.cs")
        endpoint = read(HOST / "TrustedApplicationEndpoint.cs")
        auth = read(ROOT / "Services/AuthService.cs")
        coordinator = read(TRUSTED / "TrustedActivitySigning.cs")
        migration = read(MIGRATIONS / "20261031_001_Activity_Signing_Primary_Identity_Authority.sql")

        self.assertIn('"ActivityReauthenticate"', server)
        self.assertIn("ReauthenticateActivityAsync", server)
        self.assertIn('"ActivityReauthenticate"', endpoint)
        self.assertIn("ReauthenticateForTrustedActionAsync", endpoint)
        self.assertIn("VerifyPassword(password, user)", auth)
        self.assertIn("user.MustChangePassword || user.PasswordChangedAt is null", auth)
        self.assertIn("!signer.IsActive || signer.IsLocked || signer.MustChangePassword", coordinator)
        self.assertIn("CanReviewResults", coordinator)
        self.assertIn("CanApproveResults", coordinator)

        signing_proc = migration.split("CREATE OR ALTER PROCEDURE dbo.MicroEquipment_SignActivityStage", 1)[1]
        self.assertIn("MicroEquipmentSignerEligibility", signing_proc)
        self.assertIn("CanReviewResults=1", signing_proc)
        self.assertIn("CanApproveResults=1", signing_proc)
        # Credential state belongs to the primary EQ001 authority after v309 split-DB correction.
        self.assertNotIn("CredentialInitialized=1", signing_proc)
        self.assertNotIn("PasswordHashNew", signing_proc)
        self.assertNotIn("PasswordSalt", signing_proc)

    def test_ui_requires_active_authenticated_signer_and_never_falls_back_to_local_sql(self):
        source = read(LAB)
        signing = source.split("private async Task SignActivityStageAsync(string stage)", 1)[1].split(
            "private void BtnNew_Click", 1
        )[0]

        self.assertIn("_authService?.GetCurrentUser()", signing)
        self.assertIn("!user.IsActive", signing)
        self.assertIn("user.IsLocked", signing)
        self.assertIn("user.MustChangePassword", signing)
        self.assertIn("!user.CanReviewResults", signing)
        self.assertIn("!user.CanApproveResults", signing)
        self.assertIn("TrustedEquipmentSigningClient.CreateActivityFromConfiguration()", signing)
        self.assertNotIn("_database.Execute", signing)
        self.assertNotIn("MicroEquipmentActivityService.SignActivityStage", signing)

    def test_signing_binds_dialog_identity_to_windows_caller_and_canonical_reauthentication(self):
        dialog = read(DIALOG)
        client = read(TRUSTED / "WindowsTrustedPipeClient.cs")
        server = read(TRUSTED / "WindowsTrustedPipeServer.cs")
        callers = read(TRUSTED / "TrustedCallerMap.cs")

        self.assertIn("CommitActivityAsync(request, _username, password", dialog)
        self.assertIn("expectedUsername", client)
        self.assertIn("Trusted activity signer differs from the active account.", client)
        self.assertIn("_callers.Resolve(callerSid, serviceSid, envelope.ExpectedUsername)", server)
        self.assertIn("!string.Equals(authentication.SignedBy, username", server)
        self.assertIn("!verifiedUsername.Equals(username", server)
        self.assertIn("!string.Equals(username, expectedUsername, StringComparison.OrdinalIgnoreCase)", callers)

    def test_service_rejects_unmapped_or_mismatched_windows_actor(self):
        callers = read(TRUSTED / "TrustedCallerMap.cs")
        server = read(TRUSTED / "WindowsTrustedPipeServer.cs")

        self.assertIn("callerSid, serviceSid, envelope.ExpectedUsername", server)
        self.assertIn("string.Equals(callerSid, serviceSid", callers)
        self.assertIn("!_accounts.TryGetValue(callerSid", callers)
        self.assertIn("!string.Equals(username, expectedUsername", callers)
        self.assertIn("throw new UnauthorizedAccessException", callers)

    def test_signing_rejects_control_characters_in_reason(self):
        coordinator = read(TRUSTED / "TrustedActivitySigning.cs")
        self.assertIn("request.Reason.Any(char.IsControl)", coordinator)
        self.assertIn("request.Reason.Trim().Length < 10", coordinator)
        self.assertIn("request.Reason.Length > 500", coordinator)

    def test_signing_rejects_empty_version_stale_request_and_invalid_actor(self):
        coordinator = read(TRUSTED / "TrustedActivitySigning.cs")
        self.assertIn("request.ExpectedVersion is not { Length: 8 }", coordinator)
        self.assertIn("!request.ExpectedVersion.Any(value => value != 0)", coordinator)
        self.assertIn("nowUtc - request.IssuedAtUtc > TimeSpan.FromMinutes(2)", coordinator)
        self.assertIn("string.IsNullOrWhiteSpace(signer.Username)", coordinator)
        self.assertIn("signer.Username.Length > 100", coordinator)

    def test_database_enforces_signed_stage_transition_and_sod(self):
        migrations = [read(path) for path in MIGRATIONS.glob("*.sql")]
        self.assertTrue(
            any("TRG_MicroEquipmentActivities_SignedStatus" in sql for sql in migrations),
            "EQ-001 OPEN: no signed status transition guard",
        )
        latest = read(MIGRATIONS / "20261031_001_Activity_Signing_Primary_Identity_Authority.sql")
        self.assertIn("VersionToken=@Version", latest)
        self.assertIn("PerformedBy<>@Actor", latest)
        self.assertIn("SignatureMeaning=N''Reviewed'' AND SignedBy<>@Actor", latest)
        self.assertIn("MicroEquipment_ValidateActivitySignatures", latest)


if __name__ == "__main__":
    unittest.main()
