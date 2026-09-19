import pathlib, re, unittest
ROOT=pathlib.Path(__file__).resolve().parents[1]
def text(p): return (ROOT/p).read_text(encoding='utf-8-sig')
class V286Closure(unittest.TestCase):
    def test_all_protected_behavioral_gate(self):
        p=text('tests/PharmaLIMS.DatabaseIntegration/Program.cs')
        self.assertIn('VerifyAllProtectedTablesRejectDmlAsync(connectionString, baseline)', p)
        self.assertIn('WHERE 1=0', p)
        self.assertIn('protectionContract.Rows', p)
    def test_lock_policy_enabled(self):
        self.assertIn('<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>', text('PharmaLIMS.csproj'))
        ci=text('.github/workflows/ci.yml')
        self.assertIn('dotnet restore ./PharmaLIMS.csproj --locked-mode', ci)
        self.assertIn('packages.lock.json is required', ci)
        self.assertIn('--force-evaluate', text('scripts/Generate-NuGetLocks.ps1'))
    def test_provenance_covered(self):
        new=text('scripts/New-PublishManifest.ps1')
        self.assertLess(new.index("Set-Content -LiteralPath $provenance"), new.index('$lines = Get-ChildItem'))
        verify=text('scripts/Test-PublishManifest.ps1')
        self.assertIn('not covered by the hash manifest', verify)
    def test_secret_provisioning(self):
        p=text('tools/PharmaLIMS.SecretProvisioning/Program.cs')
        self.assertIn('ProtectCurrentUserBase64', p)
        self.assertIn('Console.ReadKey(intercept: true)', p)
        self.assertIn('same Windows account', p)
    def test_lineage(self):
        self.assertIn('2026.9.17.288', text('REPLACEMENT_README_AR.md'))
if __name__=='__main__': unittest.main()
