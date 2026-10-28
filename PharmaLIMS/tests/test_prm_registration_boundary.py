from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
class PrmRegistrationBoundary(unittest.TestCase):
 def test_restricted_registration_does_not_reach_desktop_numbering(self):
  for f in ['ProductionRawMaterialSamples.xaml.cs','scripts/site-overlays/AMEEN-ProductionRawMaterialSamples.cs.txt']:
   s=(ROOT/f).read_text(encoding='utf-8-sig')
   self.assertIn('await SaveTrustedPrmSampleAsync()',s)
 def test_recovered_outcome_cannot_bind_unrelated_current_form_for_edit(self):
  s=(ROOT/'ProductionRawMaterialSamples.TrustedRegistration.cs').read_text()
  self.assertNotIn('_selectedSampleId=result.SampleId',s,'Recovery must not bind the current unrelated form to its saved sample')
  self.assertIn('ClearForm();',s)
 def test_new_request_validation_and_wall_clock_mapping_are_inside_capture(self):
  s=(ROOT/'ProductionRawMaterialSamples.TrustedRegistration.cs').read_text()
  capture=s.split('private PrmRegistrationRequest CaptureTrustedPrmRegistration()',1)[1]
  self.assertIn('ValidateForm();',capture)
  self.assertIn('PrmRegistrationFormMapping.Capture(fields)',capture)
  before=s.split('private PrmRegistrationRequest CaptureTrustedPrmRegistration()',1)[0]
  self.assertIn('pending?.Payload ?? CaptureTrustedPrmRegistration()',before)
  self.assertNotIn('ValidateForm();',before)
 def test_sql_boundary_and_atomic_receipts(self):
  p=ROOT/'scripts/EQ001_UAT_Prm_Registration.sql'
  self.assertTrue(p.exists(),'restricted PRM registration procedure is missing')
  s=p.read_text()
  for text in ['WITH EXECUTE AS OWNER','AuthenticationRowVersion','sp_getapplock','EQ001_PrmRegistrationReceipts','PRM_SampleTests','dbo.AuditTrail','@VerifiedIsolatedUat bit = 0']:
   self.assertIn(text,s)
  self.assertNotIn('GRANT UPDATE',s)
if __name__=='__main__': unittest.main()
