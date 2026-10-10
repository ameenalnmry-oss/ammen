import unittest
from pathlib import Path
BASE=Path(__file__).resolve().parents[1]
class ResourceActivityHistoryContract(unittest.TestCase):
 def test_reuse_existing_window(self):
  x=(BASE/"LabResources.xaml").read_text(encoding="utf8")
  self.assertIn('x:Name="GridActivityHistory"',x)
  self.assertIn('x:Name="LblActivityHistory"',x)
 def test_history_is_read_only_and_schema_safe(self):
  x=(BASE/"LabResources.xaml").read_text(encoding="utf8")
  c=(BASE/"LabResources.xaml.cs").read_text(encoding="utf8")
  self.assertIn('IsReadOnly="True"',x)
  self.assertIn('OBJECT_ID(N\'dbo.MicroEquipmentActivities\'',c)
  self.assertIn('LoadEquipmentActivityHistory(',c)
  self.assertIn('ActivityStatus',c)
if __name__=="__main__": unittest.main()
