import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
class ConfirmActualUseUi(unittest.TestCase):
 def test_confirmation_controls(self):
  x=(ROOT/"LabResources.xaml").read_text(encoding="utf-8")
  for v in ('x:Name="DpActualUseStart"','x:Name="DpActualUseEnd"','Click="BtnConfirmActualUse_Click"'):
   self.assertIn(v,x)
 def test_reads_database_rowversion_and_confirms_transactionally(self):
  s=(ROOT/"LabResources.xaml.cs").read_text(encoding="utf-8")
  self.assertIn("MicroEquipmentActivityService.ConfirmUse(",s)
  self.assertIn("VersionToken",s)
  self.assertIn("ActivityType=N'Use'",s)
  self.assertIn("ExecuteInTransaction(",s)
if __name__=="__main__": unittest.main()
