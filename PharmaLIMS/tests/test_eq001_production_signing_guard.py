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

class Eq001ProtectedAuthenticationBoundaryTests(unittest.TestCase):
    def test_uat_desktop_principal_has_no_raw_password_or_lockout_writes(self):
        source = (ROOT/'tests/PharmaLIMS.TrustedSigning.WindowsUat/Program.cs').read_text()
        self.assertNotIn('GRANT UPDATE(LastLogin,FailedLoginAttempts,IsLocked,LockedUntil,PasswordHashNew,PasswordSalt,PasswordHash) ON dbo.Users TO [{_clientLogin}]', source)
        self.assertIn('WPF principal cannot replace signer credentials', source)
        self.assertIn('WPF principal cannot clear lockout', source)
