import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Services" / "MicroEquipmentActivityService.cs"

class MicroEquipmentActivityServiceContract(unittest.TestCase):
    def test_controlled_create_and_confirm(self):
        s=SOURCE.read_text(encoding="utf-8")
        for token in ("CreateDraft(", "ConfirmUse(", "AddLink(", "VersionToken", "UPDLOCK,HOLDLOCK", "OUTPUT INSERTED.ActivityID"):
            self.assertIn(token,s)
        self.assertIn("ActivityStatus=N'Draft'",s)
        self.assertIn("ActivityStatus=N'Submitted'",s)
        self.assertIn("@@ROWCOUNT",s)
        self.assertIn("MicroEquipmentActivityAudit",s)

    def test_no_implicit_sample_assignment(self):
        s=SOURCE.read_text(encoding="utf-8")
        self.assertNotIn("MERGE dbo.LabEquipmentUsage",s)
        self.assertIn("MicroEquipmentActivityLinks",s)
        self.assertIn("MicroEquipmentActivities",s)

if __name__=="__main__":
    unittest.main()
