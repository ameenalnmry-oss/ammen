import re
import unittest
from pathlib import Path

MIGRATION = Path(__file__).resolve().parents[1] / "Database" / "Migrations" / "20261010_002_Micro_Equipment_Activity_Ledger.sql"

class MicroEquipmentScopeGuard(unittest.TestCase):
    def test_registered_microbiology_codes(self):
        sql = MIGRATION.read_text(encoding="utf-8")
        self.assertIn("MIC-EQ-[0-9][0-9][0-9]", sql)
        self.assertNotIn("BETWEEN 1 AND 34", sql)
        self.assertIn("Department",sql)
        self.assertIn("IsActive",sql)
        self.assertIn("LEN(e.EquipmentCode)<>10", sql)
    def test_scope_guard_is_fail_closed(self):
        sql = MIGRATION.read_text(encoding="utf-8")
        self.assertIn("THROW 56504", sql)
        self.assertIn("e.EquipmentID IS NULL", sql)
    def test_approved_examples(self):
        def allowed(code):
            return bool(re.fullmatch(r"MIC-EQ-\d{3}",code)) and int(code[-3:]) > 0
        self.assertTrue(allowed("MIC-EQ-001"))
        self.assertTrue(allowed("MIC-EQ-034"))
        self.assertTrue(allowed("MIC-EQ-035"))
        self.assertTrue(allowed("MIC-EQ-999"))
        for code in ["MIC-EQ-000","MIC-EQ-01A","MIC-EQ-034-extra","OTHER-001"]:
            self.assertFalse(allowed(code))

if __name__ == "__main__":
    unittest.main()
