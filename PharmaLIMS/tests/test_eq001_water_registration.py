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
