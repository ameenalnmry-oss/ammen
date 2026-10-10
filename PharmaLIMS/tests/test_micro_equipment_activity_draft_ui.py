import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
class ActivityDraftUiTests(unittest.TestCase):
 def test_draft_capture_is_inside_existing_lab_resources(self):
  x=(ROOT/"LabResources.xaml").read_text(encoding="utf8")
  self.assertIn('x:Name="CboActivityType"',x)
  self.assertIn('x:Name="TxtActivityMethod"',x)
  self.assertIn('Click="BtnCreateActivityDraft_Click"',x)
 def test_draft_is_a_real_atomic_write_not_fake_use(self):
  s=(ROOT/"LabResources.xaml.cs").read_text(encoding="utf8")
  self.assertIn('MicroEquipmentActivityService.CreateDraft(',s)
  self.assertIn('ExecuteInTransaction(',s)
  self.assertIn('Activity draft',s)
  self.assertNotIn('MicroEquipmentActivityService.ConfirmUse(',s)
if __name__=="__main__": unittest.main()
