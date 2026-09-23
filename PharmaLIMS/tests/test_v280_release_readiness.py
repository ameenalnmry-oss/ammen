import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]

class ReleaseReadinessV280Tests(unittest.TestCase):
    def test_release_critical_contracts_are_explicit_and_fail_closed(self):
        text = (ROOT / 'Infrastructure' / 'SystemPreflightService.cs').read_text(encoding='utf-8-sig')
        self.assertIn('CheckReleaseCriticalSchemaContractsAsync', text)
        self.assertIn('Release-critical database readiness', text)
        for token in [
            'dbo.Users.AuthenticationRowVersion',
            'dbo.Users.MustChangePassword',
            'dbo.Users.PasswordChangedAt',
            'dbo.EM_Events.ResultRowVersion',
            'dbo.EM_EventPlates.ResultRowVersion',
            'dbo.SampleTests.ResultRowVersion',
            'Migration Required:',
            '20260910_000_Review_Result_And_Authentication_Concurrency',
            '20260911_000_Authentication_Login_Compatibility_Backfill',
            '20260915_000_User_Administration_Security_Hardening',
        ]:
            self.assertIn(token, text)
        self.assertGreaterEqual(text.count('"Database Schema", "BLOCKER"'), 2)

    def test_database_readiness_runs_before_generic_schema_checks(self):
        text = (ROOT / 'Infrastructure' / 'SystemPreflightService.cs').read_text(encoding='utf-8-sig')
        run = text[text.index('public async Task<SystemPreflightReport> RunAsync()'):]
        self.assertLess(run.index('CheckReleaseCriticalSchemaContractsAsync(checks)'),
                        run.index('CheckCriticalSchemaColumnsAsync(checks)'))
        self.assertIn('return BuildReport(checks, connectedServer, connectedDatabase);', run)

    def test_release_checklist_is_manual_and_not_auto_passed(self):
        xaml = (ROOT / 'SystemPreflight.xaml').read_text(encoding='utf-8-sig')
        code = (ROOT / 'SystemPreflight.xaml.cs').read_text(encoding='utf-8-sig')
        self.assertIn('Copy Release Checklist', xaml)
        self.assertIn('BtnCopyReleaseChecklist_Click', xaml)
        self.assertIn('BuildReleaseAcceptanceChecklist', code)
        for token in [
            'Clean Release build succeeds',
            'DatabaseIntegration / ReviewRegression',
            'User Management create/update/permissions/reset/unlock/concurrency',
            'Water registration/results/review/approval/Quality Event/certificate',
            'EM planning/collection/results/review/approval/reporting',
            'PRM registration/results/review/approval/Quality Event/certificate/reissue',
            'Audit Trail evidence',
            'QA reviewed the executed evidence and approved release/deployment',
            'copying the checklist does not mark any item as passed',
        ]:
            self.assertIn(token, code)

    def test_version_identity_is_v281(self):
        project = (ROOT / 'PharmaLIMS.csproj').read_text(encoding='utf-8-sig')
        assembly = (ROOT / 'AssemblyInfo.cs').read_text(encoding='utf-8-sig')
        self.assertIn('<Version>2026.9.23.295</Version>', project)
        self.assertIn('AssemblyVersion("2026.9.23.295")', assembly)
        self.assertTrue((ROOT / 'SBOM_2026.9.23.295.cdx.json').exists())
        self.assertTrue((ROOT / 'RELEASE_NOTES_2026.9.23.295.md').exists())

if __name__ == '__main__':
    unittest.main()
