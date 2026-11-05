import unittest
from pathlib import Path
SOURCE=Path(__file__).resolve().parents[1]/"Services"/"MicroEquipmentActivityService.cs"
class ActivityGuards(unittest.TestCase):
 def test_all_mutations_authorized(self):
  s=SOURCE.read_text(encoding="utf8")
  self.assertGreaterEqual(s.count("DatabaseHelper.EnsureUserPermissionInTransaction("),3)
 def test_draft_owned_by_authenticated_actor(self):
  s=SOURCE.read_text(encoding="utf8")
  self.assertIn("PerformedBy=@Actor",s)
  self.assertIn("AND PerformedBy=@Actor",s)
 def test_no_false_workflow_approval(self):
  s=SOURCE.read_text(encoding="utf8")
  self.assertIn("ActivityStatus=N'Submitted'",s)
  self.assertNotIn("SET ActivityStatus=N'Approved'",s)
if __name__=="__main__": unittest.main()
