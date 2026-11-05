import unittest
from pathlib import Path
BASE=Path(__file__).resolve().parents[1]
class ResourceActivityHistoryContract(unittest.TestCase):
 def test_reuse_existing_window(self):
  x=(BASE/"LabResources.xaml").read_text(encoding="utf8")
  self.assertIn('x:Name="GridActivityHistory"',x)
  self.assertIn('x:Name="LblActivityHistory"',x)
 def test_printed_history_uses_medica_logo_when_available(self):
  c=(BASE/"LabResources.xaml.cs").read_text(encoding="utf8")
  self.assertIn('string historyLogoPath',c)
  self.assertIn('document.Blocks.Add(new System.Windows.Documents.BlockUIContainer(historyLogo)',c)
  self.assertIn('Line("MEDICA | LABORATORY EQUIPMENT HISTORY", true)',c)
 def test_printed_history_discloses_partial_record_set(self):
  c=(BASE/"LabResources.xaml.cs").read_text(encoding="utf8")
  self.assertIn("SELECT COUNT_BIG(*) AS TotalActivities",c)
  self.assertIn("PARTIAL HISTORY COPY",c)
  self.assertIn("totalActivities > history.Rows.Count",c)
 def test_printed_approved_activity_discloses_missing_signatures(self):
  c=(BASE/"LabResources.xaml.cs").read_text(encoding="utf8")
  self.assertIn("SIGNATURE EVIDENCE MISSING",c)
  self.assertIn('signatures.Rows.Count == 0',c)
 def test_approved_print_requires_review_and_approval_stages(self):
  c=(BASE/"LabResources.xaml.cs").read_text(encoding="utf8")
  self.assertIn("INCOMPLETE SIGNATURE CHAIN",c)
  self.assertIn('Field(s, "SignatureMeaning"), "Reviewed"',c)
  self.assertIn('Field(s, "SignatureMeaning"), "Approved"',c)
 def test_history_is_read_only_and_schema_safe(self):
  x=(BASE/"LabResources.xaml").read_text(encoding="utf8")
  c=(BASE/"LabResources.xaml.cs").read_text(encoding="utf8")
  self.assertIn('IsReadOnly="True"',x)
  self.assertIn('OBJECT_ID(N\'dbo.MicroEquipmentActivities\'',c)
  self.assertIn('LoadEquipmentActivityHistory(',c)
  self.assertIn('ActivityStatus',c)
if __name__=="__main__": unittest.main()
