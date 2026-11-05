import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
class VisualTurbidityGuardTests(unittest.TestCase):
 def test_policy_identifies_visual_turbidity(self):
  s=(ROOT/"Services/WaterTestResourcePolicy.cs").read_text(encoding="utf-8")
  self.assertIn('"Turbidity"',s)
 def test_preflight_requires_controlled_visual_method(self):
  s=(ROOT/"Services/WaterEvidencePreflightSql.cs").read_text(encoding="utf-8")
  self.assertIn("WaterVisualMethodEvidence",s)
  self.assertIn("VisualMethodReference",s)
 def test_visual_numerical_input_rejects_ambiguous_comma(self):
  s=(ROOT/"Services/WaterResourceEvidenceService.cs").read_text(encoding="utf-8")
  self.assertIn("observation.Contains(',')",s)
  self.assertIn("NumberStyles.AllowDecimalPoint",s)
  self.assertNotIn("decimal.TryParse(result.Trim(), NumberStyles.Number",s)
 def test_legacy_signed_record_cannot_be_reclassified_unconditionally(self):
  s=(ROOT/"Services/WaterEvidencePreflightSql.cs").read_text(encoding="utf-8")
  self.assertNotIn("N'TURBIDITY',N'APPEARANCE'",s)
if __name__=="__main__": unittest.main()
