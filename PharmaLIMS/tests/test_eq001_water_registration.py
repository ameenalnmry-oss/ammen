import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
class WaterBoundaryContract(unittest.TestCase):
    def test_atomic_registration_boundary_exists(self):
        p=ROOT/'scripts/EQ001_UAT_Water_Registration.sql'
        self.assertTrue(p.exists(), 'Water registration has no trusted SQL boundary')
        sql=p.read_text()
        for name in ['WaterRegistrationReceipts','sp_getapplock','@ActorVersion','@CallerSid','Water_PlanSampleTests','ElectronicSignatures','Water Sample Registered']:
            self.assertIn(name,sql)
    def test_no_trusted_registration_local_fallback(self):
        source=(ROOT/'NewSampleDialog.xaml.cs').read_text(encoding='utf-8-sig')
        water=source[source.index('private async Task SaveWaterSample()'):]
        self.assertIn('SaveTrustedWaterSampleAsync', water)
        self.assertLess(water.index('SaveTrustedWaterSampleAsync'),water.index('string finalSampleNumber = GetNextWaterSampleNumber();'))
    def test_independent_water_host_gate(self):
        source=(ROOT/'tools/PharmaLIMS.TrustedSigning/TrustedHostSettings.cs').read_text()
        self.assertIn('EnableWaterRegistrationUat',source)

    def test_source_delivery_errors_are_visible_in_ci_annotations(self):
        import os
        import subprocess
        import sys
        import uuid
        probe=ROOT/('water-validation-probe-'+uuid.uuid4().hex+'.tmp')
        try:
            probe.write_text('Synthetic source validation diagnostic probe',encoding='utf-8')
            result=subprocess.run([sys.executable,'-B',str(ROOT/'scripts/validate_project.py'),'--delivery-package'],
                env={**os.environ,'GITHUB_ACTIONS':'true','PYTHONDONTWRITEBYTECODE':'1'},text=True,capture_output=True)
            self.assertNotEqual(0,result.returncode)
            self.assertIn('::error title=Controlled source validation::',result.stdout)
            self.assertIn(probe.name,result.stdout)
        finally:
            probe.unlink(missing_ok=True)

    def test_delivery_directory_names_do_not_collide_on_windows(self):
        directories={}
        manifest=(ROOT/'SOURCE_MANIFEST_SHA256.txt').read_text(encoding='utf-8')
        for line in manifest.splitlines():
            if line.startswith('#') or '  ' not in line: continue
            path=line.split('  ',1)[1].split('/')
            for i in range(1,len(path)):
                directory='/'.join(path[:i])
                directories.setdefault(directory.casefold(),set()).add(directory)
        collisions=[sorted(names) for names in directories.values() if len(names)>1]
        self.assertEqual([],collisions,'Directory names must remain distinct on case-insensitive Windows filesystems')
