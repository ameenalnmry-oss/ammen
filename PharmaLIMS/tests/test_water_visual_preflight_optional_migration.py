import unittest
from pathlib import Path
S=(Path(__file__).resolve().parents[1]/"Services/WaterEvidencePreflightSql.cs").read_text(encoding="utf8")
class VisualPreflightCompatibility(unittest.TestCase):
 def test_missing_optional_migration_does_not_trigger_sql208(self):
  self.assertIn("CREATE TABLE #WaterVisualMethodEvidence",S)
  self.assertIn("sp_executesql",S)
  self.assertIn("FROM #WaterVisualMethodEvidence v",S)
  self.assertNotIn("FROM dbo.WaterVisualMethodEvidence v",S)
if __name__=="__main__":unittest.main()
