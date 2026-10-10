import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
class ActivityLinkUiTests(unittest.TestCase):
 def test_link_controls_in_existing_window(self):
  x=(ROOT/"LabResources.xaml").read_text(encoding="utf8")
  for token in ['x:Name="TxtActivityId"','x:Name="CboSourceModule"','x:Name="TxtSourceParentId"','x:Name="TxtSourceResultId"','Click="BtnLinkActivity_Click"']:
   self.assertIn(token,x)
 def test_link_uses_existing_validated_service_and_no_fake_confirmation(self):
  s=(ROOT/"LabResources.xaml.cs").read_text(encoding="utf8")
  self.assertIn("MicroEquipmentActivityService.AddLink(",s)
  self.assertIn("BtnLinkActivity_Click(",s)
  self.assertNotIn("MicroEquipmentActivityService.ConfirmUse(",s)
if __name__=="__main__": unittest.main()
