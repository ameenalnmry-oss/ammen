import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


class V298UatWaterReportTests(unittest.TestCase):
    def test_sample_details_does_not_hide_test_query_failure(self):
        code = source("SampleDetails.xaml.cs")
        start = code.index("private void LoadTestsAndResults")
        end = code.index("private void LoadTimeline", start)
        method = code[start:end]
        self.assertIn("DatabaseHelper.ExecuteQuery", method)
        self.assertIn("Expected {expectedCount} test row(s), but loaded {tests.Rows.Count}", method)
        self.assertNotIn("SafeQuery(query", method)

    def test_sample_details_numeric_comparison_is_safe_for_qualitative_results(self):
        code = source("SampleDetails.xaml.cs")
        start = code.index("private void LoadTestsAndResults")
        end = code.index("private void LoadTimeline", start)
        method = code[start:end]
        self.assertIn("TRY_CONVERT(decimal(18,6)", method)
        self.assertNotIn("AND st.ResultValue >", method)

    def test_nonconforming_result_precedes_pending_conclusion(self):
        code = source("ReportCertificate.xaml.cs")
        start = code.index("private void UpdateFinalConclusion")
        end = code.index("private void LoadCertificateNumber", start)
        method = code[start:end]
        self.assertLess(method.index("if (hasNonConform)"), method.index("if (pendingCount > 0)"))
        self.assertIn("NON-CONFORMING RESULT DETECTED - SAMPLE UNDER INVESTIGATION.", method)
        self.assertIn("FINAL QA DISPOSITION PENDING.", method)

    def test_under_investigation_is_treated_as_nonconforming_state(self):
        code = source("ReportCertificate.xaml.cs")
        start = code.index("private bool IsOosSampleStatus")
        end = code.index("private bool IsSystemGenericName", start)
        method = code[start:end]
        self.assertIn('"Under Investigation"', method)

    def test_report_does_not_fabricate_analysis_completion_from_signature(self):
        code = source("ReportCertificate.xaml.cs")
        self.assertIn('lblAnalysisCompletedDate.Text = FirstFormattedDate(row, "AnalysisCompletedDateTime");', code)
        self.assertNotIn('FirstFormattedDate(row, "AnalysisCompletedDateTime", "AnalystSignedAt")', code)
        self.assertIn('lblAnalysisCompletedDate.Text = "Not completed";', code)

    def test_controlled_print_requires_issued_document(self):
        code = source("ReportCertificate.xaml.cs")
        self.assertIn("bool hasIssuedDocument = !string.IsNullOrWhiteSpace(certificateNumber);", code)
        self.assertIn("BtnPrint.IsEnabled = canPrint;", code)
        self.assertIn("BtnPrint.IsEnabled = BtnPrint.IsEnabled && hasIssuedDocument;", code)
        self.assertIn("Controlled printing is available only after all required results are complete", code)

    def test_water_report_uses_approved_snapshot_and_no_universal_conductivity_fallback(self):
        code = source("ReportCertificate.xaml.cs")
        self.assertIn("private string FormatReportSpecification(", code)
        report_formatter = code[code.index("private string FormatReportSpecification("):code.index("private string GetConformity(", code.index("private string FormatReportSpecification("))]
        self.assertIn("immutable approved snapshot", report_formatter)
        self.assertNotIn('IsPurifiedWater(sampleType) ? "NMT 1.3"', report_formatter)
        self.assertNotIn("25°C", report_formatter)
        self.assertNotIn("25 °C", report_formatter)

    def test_qualitative_chemistry_is_driven_by_approved_snapshot_not_legacy_unit(self):
        results = source("ResultsEntry.xaml.cs")
        report = source("ReportCertificate.xaml.cs")
        self.assertIn("private bool IsApprovedComplianceQualitative(ResultItem item)", results)
        self.assertIn("item.SpecificationText", results)
        self.assertIn("private bool IsApprovedComplianceQualitative(", report)
        self.assertIn("storedDescription", report)

    def test_pw_conductivity_fallback_is_1_3_not_2_0(self):
        results = source("ResultsEntry.xaml.cs")
        apply_start = results.index("private void ApplyEffectiveSpecification")
        apply_end = results.index("private bool TryNormalizeResultForSave", apply_start)
        method = results[apply_start:apply_end]
        self.assertIn("item.ActionLimit = 1.30m;", method)
        self.assertNotIn("item.ActionLimit = 2.00m;", method)

    def test_report_rows_expand_instead_of_clipping_wrapped_limits(self):
        xaml = source("ReportCertificate.xaml")
        self.assertIn('MinRowHeight="31"', xaml)
        self.assertNotIn('\n                              RowHeight="31"', xaml)


if __name__ == "__main__":
    unittest.main()
