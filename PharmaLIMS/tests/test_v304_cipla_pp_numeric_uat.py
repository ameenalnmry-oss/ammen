"""CIPLA 1035-G-0193: source contracts; real C# regression cases live in ReviewRegression."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
def src(name):
    return (ROOT/name).read_text(encoding="utf-8-sig")
class CiplaPackagingControlledTests(unittest.TestCase):
    def test_structured_limits_match_cipla_tbc_tfc(self):
        v=src("Services/PrmNumericSpecificationEvaluator.cs")
        self.assertIn('code.Equals("TBC", StringComparison.OrdinalIgnoreCase)',v)
        self.assertIn('code.Equals("TFC", StringComparison.OrdinalIgnoreCase)',v)
        self.assertIn('name.Contains("Total Bacterial Count", StringComparison.OrdinalIgnoreCase)',v)
        self.assertIn('name.Contains("Total Fungal Count", StringComparison.OrdinalIgnoreCase)',v)
        self.assertIn('upper.Value != structuredLimit.Value || !upperInclusive',v)
        self.assertIn('cm\\u00B2',v)
    def test_csharp_regression_covers_boundaries_and_conflicts(self):
        v=src("tests/PharmaLIMS.ReviewRegression/MergedRegressionCases.cs")
        for case in ("CIPLA_TBC_MATCHING","CIPLA_TBC_CONFLICT","CIPLA_TFC_MATCHING",
                     "CIPLA_TFC_CONFLICT","CIPLA_SUPERSCRIPT_CM2","CIPLA_CARET_CM2",
                     "CIPLA_TBC_AT_BOUNDARY_OVER","CIPLA_TFC_AT_BOUNDARY_OVER"):
            self.assertIn(case,v)
    def test_uat_certificate_is_development_only(self):
        v=src("Services/PRMCertificateTemplate.cs")
        self.assertIn('isDevelopmentCertificate = AppConfig.IsDevelopment',v)
        self.assertIn('if (isDevelopmentCertificate)',v)
        self.assertIn('UAT / NOT FOR GMP RELEASE - DEVELOPMENT TEST CERTIFICATE',v)
        self.assertIn('class=\'uatNotice\'',v)
        smoke=src("tests/PharmaLIMS.TrendReportSmoke/PrimaryPackagingSmoke.cs")
        self.assertIn('Require(!AppConfig.IsDevelopment || html.Contains("UAT / NOT FOR GMP RELEASE")',smoke)
