"""Source/deployment contracts only; SQL behavior runs in DatabaseIntegration/UAT."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
MIGRATION = ROOT / 'Database/Migrations/20261017_001_Equipment_Signing_Integrity.sql'

def sql():
    return MIGRATION.read_text(encoding='utf-8-sig') if MIGRATION.exists() else ''

class EquipmentSigningRemediation(unittest.TestCase):
    def test_actual_uat_target_is_consistent_across_trusted_deployment_boundaries(self):
        service = ROOT / 'tools/PharmaLIMS.TrustedSigning'
        policy = (service/'TrustedActivitySigning.cs').read_text(encoding='utf-8-sig')
        server = (service/'WindowsTrustedPipeServer.cs').read_text(encoding='utf-8-sig')
        store = (service/'SqlTrustedActivitySignatureStore.cs').read_text(encoding='utf-8-sig')
        self.assertIn('ActivitySigningDatabasePolicy.IsAllowedUatDatabase', store)
        self.assertIn('ActivitySigningDatabasePolicy.IsAllowedUatDatabase(request.DatabaseName)', policy)
        self.assertIn('PharmaLIMS_UAT_', policy)
        self.assertIn('_callers.Resolve(callerSid, serviceSid, envelope.ExpectedUsername)', server)
        actual = 'PharmaLIMS_UAT_20261001'
        for name in ('eq001_provision_trusted_signer_uat.sql',
                     'eq001_record_uat_acceptance.sql',
                     'eq001_permission_cutover_after_uat.sql',
                     'eq001_run_signing_host_uat.ps1'):
            self.assertIn(actual, (ROOT/'tests'/name).read_text(encoding='utf-8-sig'), name)
        launcher = (ROOT/'tests/eq001_admin_launch_signing_uat.ps1').read_text(encoding='utf-8-sig')
        self.assertIn('param([switch]$Rehearsal)', launcher)
        self.assertIn("$selfArgs+=' -Rehearsal'", launcher)
        self.assertIn("$hostArgs+=' -Rehearsal'", launcher)
        self.assertNotIn('approver group unexpectedly contains members', launcher)

    def test_existing_admin_allowance_is_preserved_at_both_service_checks(self):
        coordinator = (ROOT/'tools/PharmaLIMS.TrustedSigning/TrustedActivitySigning.cs').read_text(encoding='utf-8-sig')
        migration = (ROOT/'Database/Migrations/20261031_001_Activity_Signing_Primary_Identity_Authority.sql').read_text(encoding='utf-8-sig')
        auth = (ROOT/'Services/AuthService.cs').read_text(encoding='utf-8-sig')
        self.assertIn('request.Stage == "Reviewed" ? !signer.CanReviewResults : !signer.CanApproveResults', coordinator)
        self.assertIn("Role IN(N''Admin'',N''Administrator'')", migration)
        # Credential readiness moved to the primary identity authority; Admin does not bypass it.
        self.assertIn('user.MustChangePassword || user.PasswordChangedAt is null', auth)
        self.assertIn('VerifyPassword(password, user)', auth)

    def test_sql_stage_check_preserves_same_admin_policy(self):
        source = sql()
        self.assertTrue('SELECT Username,Role,IsActive' in source,
                        'Readiness view must expose application role without credentials.')
        self.assertTrue(source.count("Role IN(N''Admin'',N''Administrator'')") == 2,
                        'SQL review and approval must preserve the existing Admin allowance.')
        self.assertTrue('ORIGINAL_LOGIN()' in source and 'CredentialInitialized=1' in source,
                        'Application Admin must not bypass trusted origin or account readiness.')

    def test_signing_authorizes_original_login_and_actor_inside_atomic_operation(self):
        s = sql()
        self.assertIn('ORIGINAL_LOGIN()', s)
        self.assertIn('MicroEquipmentSignerEligibility', s)
        self.assertIn('CredentialInitialized=1', s)
        self.assertIn('BEGIN TRANSACTION', s)
        self.assertIn('ROLLBACK TRANSACTION', s)

    def test_signed_content_and_insert_delete_routes_are_guarded(self):
        s = sql()
        self.assertIn('TRG_MicroEquipmentActivities_ImmutableSigned', s)
        self.assertIn('AFTER INSERT, UPDATE, DELETE', s)
        self.assertIn('CONVERT(varbinary(max)', s)
        self.assertIn('TRG_MicroEquipmentActivityLinks_SignedGuard', s)
        self.assertIn('SELECT ActivityID FROM deleted', s)
        self.assertIn('SELECT ActivityID FROM inserted', s)

    def test_print_requires_matching_stage_hashes_before_atomic_read(self):
        source = '\n'.join(p.read_text(encoding='utf-8-sig') for p in ROOT.glob('LabResources*.cs'))
        self.assertIn('LoadActivityPrintEvidence', source)
        self.assertIn('MicroEquipment_ValidateActivitySignatures', source)
        self.assertIn('MicroEquipment_GetSignedContent', sql())
        self.assertIn("N''Reviewed''", sql())
        self.assertIn("N''Approved''", sql())

    def test_service_uses_primary_identity_without_raw_credentials_in_activity_store(self):
        store = (ROOT/'tools/PharmaLIMS.TrustedSigning/SqlTrustedActivitySignatureStore.cs').read_text(encoding='utf-8-sig')
        endpoint = (ROOT/'tools/PharmaLIMS.TrustedSigning.Host/TrustedApplicationEndpoint.cs').read_text(encoding='utf-8-sig')
        auth = (ROOT/'Services/AuthService.cs').read_text(encoding='utf-8-sig')
        self.assertNotIn('PasswordHashNew', store)
        self.assertNotIn('PasswordSalt', store)
        self.assertIn('"ActivityReauthenticate"', endpoint)
        self.assertIn('ReauthenticateForTrustedActionAsync', endpoint)
        self.assertIn('VerifyPassword(password, user)', auth)
        provision = (ROOT/'tests/eq001_provision_trusted_signer_uat.sql').read_text(encoding='utf-8-sig')
        self.assertIn('DENY SELECT ON OBJECT::dbo.Users (PasswordHash,PasswordHashNew,PasswordSalt)', provision)
        self.assertIn('SUSER_SID(@serviceLogin)', provision)

    def test_rehearsal_has_explicit_isolated_boundary_without_permission_cutover(self):
        launcher = (ROOT/'tests/eq001_run_signing_host_uat.ps1').read_text(encoding='utf-8-sig')
        migration = (ROOT/'Database/Migrations/20261031_001_Activity_Signing_Primary_Identity_Authority.sql').read_text(encoding='utf-8-sig')
        policy = (ROOT/'tools/PharmaLIMS.TrustedSigning/TrustedActivitySigning.cs').read_text(encoding='utf-8-sig')
        self.assertIn('PHARMALIMS_SIGNING_UAT_REHEARSAL', launcher)
        self.assertIn('EXEC dbo.MicroEquipment_AssertTrustedBoundary', migration)
        self.assertIn('TRG_MicroEquipmentActivitySignatures_TrustedOrigin', sql())
        self.assertIn("PharmaLIMS_Integration_", policy)
        self.assertNotIn('DENY EXECUTE ON OBJECT::dbo.MicroEquipment_SignActivityStage', launcher)

    def test_cutover_requires_recorded_uat_acceptance_and_preserves_hardening(self):
        source = (ROOT/'tests/eq001_permission_cutover_after_uat.sql').read_text(encoding='utf-8-sig')
        self.assertIn('EQ001_UAT_Acceptance', source)
        self.assertIn('TrustedBoundary', source)
        self.assertIn('DENY EXECUTE', source)

    def test_sql_rejection_cases_are_in_database_integration_gate(self):
        source = (ROOT/'tests/PharmaLIMS.DatabaseIntegration/Program.cs').read_text(encoding='utf-8-sig')
        self.assertTrue('EquipmentSigningIntegrityIntegration.VerifyAsync' in source)

if __name__ == '__main__':
    unittest.main()
