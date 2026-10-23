import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

class Eq001ProductionSigningGuardTests(unittest.TestCase):
    def test_equipment_screen_routes_exclusively_to_trusted_service(self):
        source = (ROOT / 'LabResources.xaml.cs').read_text(encoding='utf-8-sig')
        self.assertIn('new TrustedEquipmentSignature(', source)
        self.assertNotIn('new ElectronicSignature(', source)
        for forbidden in ('INSERT dbo.LabEquipmentSignatures', 'UPDATE dbo.LabEquipment', 'INSERT dbo.LabEquipment', 'SaveEquipment(ElectronicSignature'):
            self.assertNotIn(forbidden, source)

    def test_fresh_credential_is_submitted_only_to_authenticated_transport(self):
        source = (ROOT / 'TrustedEquipmentSignature.xaml.cs').read_text(encoding='utf-8-sig')
        self.assertIn('_client.CommitAsync(', source)
        self.assertNotIn('ValidateCurrentUserPasswordAsync', source)
        self.assertIn('TxtPassword.Clear()', source)
        self.assertIn('IsConfirmed = true', source)
        self.assertIn('Reason must contain', source)

    def test_trusted_service_is_disabled_by_default_in_config_examples(self):
        import json
        for name in ('appsettings.Development.example.json', 'appsettings.Production.example.json'):
            self.assertIs(json.loads((ROOT/name).read_text(encoding='utf-8-sig'))['TrustedSigning']['Enabled'], False)
