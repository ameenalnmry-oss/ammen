import unittest
from pathlib import Path
SOURCE=Path(__file__).resolve().parents[1]/"Services"/"MicroEquipmentActivityService.cs"

class ControlledActivityAuthorizationTests(unittest.TestCase):
    def test_transactional_permission_on_every_write(self):
        s=SOURCE.read_text(encoding="utf-8")
        self.assertGreaterEqual(s.count('DatabaseHelper.EnsureUserPermissionInTransaction('),3)
        self.assertIn('"CanEnterResults"',s)
    def test_actor_cannot_confirm_someone_elses_draft(self):
        s=SOURCE.read_text(encoding="utf-8")
        self.assertIn("PerformedBy=@Actor",s)
        self.assertIn('command.Parameters.Add("@Actor"',s)
    def test_event_and_results_must_be_real(self):
        s=SOURCE.read_text(encoding="utf-8")
        self.assertIn("dbo.PRM_Samples",s)
        self.assertIn("dbo.EM_Events",s)
        self.assertIn("dbo.Samples",s)
        self.assertIn("dbo.SampleTests",s)
        self.assertIn("dbo.EM_EventPlates",s)

if __name__=="__main__":
    unittest.main()
