"""Guard manually entered calibration-range storage and review contracts."""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


class ManualRangeContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.sql = (ROOT / "Database/Migrations/20261011_002_Micro_Equipment_Structured_Evidence.sql").read_text(encoding="utf-8-sig")
        cls.ui = (ROOT / "LabResources.xaml.cs").read_text(encoding="utf-8-sig")
        cls.service = (ROOT / "Database/Migrations/20261014_001_Micro_Equipment_Signed_Status_Guard.sql").read_text(encoding="utf-8-sig").replace(chr(39) * 2, chr(39))

    def test_manual_range_boundaries(self):
        for low, observed, high, expected in [
            (20, 20, 25, True), (20, 25, 25, True),
            (20, 19.999999, 25, False), (20, 25.000001, 25, False),
            (-20, -19, -18, True), (-20, -21, -18, False),
        ]:
            with self.subTest(low=low, observed=observed, high=high):
                self.assertEqual(low <= observed <= high, expected)
        self.assertIn("ObservedValue BETWEEN NormalMinimum AND NormalMaximum", self.sql)

    def test_range_is_an_alternative_not_an_extra_reference(self):
        self.assertIn("ReferenceValue IS NULL AND MaximumAbsoluteError IS NULL AND NormalMinimum IS NOT NULL", self.sql)
        self.assertIn("Use either manual normal range or reference/tolerance, not both.", self.ui)
        self.assertIn("NormalMinimum<=NormalMaximum", self.sql)

    def test_all_points_protected(self):
        self.assertIn("cp.Outcome IS NULL", self.service)
        self.assertIn("p.PointNo=1", self.service)
        self.assertIn("p.ObservedValue=s.ObservedValue", self.service)
        self.assertIn("cp.NormalMinimum IS NOT NULL", self.service)
        self.assertIn("ActivityType<>N'Calibration' OR NOT EXISTS", self.service)
        self.assertIn("cp.ActivityID=@Activity AND cp.Outcome<>N'PASS'", self.service)
        self.assertIn("se.EvidenceType=ActivityType AND se.Outcome=N'PASS'", self.service)

    def test_print_and_signature_include_manual_limits(self):
        self.assertIn('Field(point, "NormalMinimum")', self.ui)
        self.assertIn('Field(point, "NormalMaximum")', self.ui)
        self.assertIn('doc.Blocks.Add(pointsTable)', self.ui)
        self.assertIn("MaxAbsoluteError,NormalMinimum,NormalMaximum,AbsoluteError,Outcome", self.service)

    def test_manual_range_sql_parameters_preserve_six_decimals(self):
        self.assertIn('minParameter.Precision = 18; minParameter.Scale = 6;', self.ui)
        self.assertIn('maxParameter.Precision = 18; maxParameter.Scale = 6;', self.ui)

    def test_append_only_evidence(self):
        self.assertIn("TRG_MicroEquipmentCalibrationPoints_AppendOnly", self.sql)
        self.assertIn("TRG_MicroEquipmentStructuredEvidence_AppendOnly", self.sql)


if __name__ == "__main__":
    unittest.main()
