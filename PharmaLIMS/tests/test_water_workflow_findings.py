"""Supplementary wiring checks; behavior cases live in ReviewRegression."""
import pathlib
import unittest
ROOT = pathlib.Path(__file__).resolve().parents[1]
def read(name): return (ROOT / name).read_text(encoding='utf-8-sig')
def method(code, start, end): return code[code.index(start):code.index(end, code.index(start))]
class WaterWorkflowFindings(unittest.TestCase):
    def test_pending_completion_guard_runs_before_water_writes(self):
        code = read('ResultsEntry.xaml.Part3.cs')
        self.assertIn('WaterWorkflowEvidenceGuard.EnsurePendingOnlyEdits', code)
        self.assertLess(code.index('WaterWorkflowEvidenceGuard.EnsurePendingOnlyEdits'), code.index('UPDATE dbo.SampleTests'))
        ui = read('ResultsEntry.xaml.cs')
        self.assertIn('WaterWorkflowEvidenceGuard.IsPendingCompletionStatus(status)', ui)
        save = method(ui, 'private async void BtnSaveResults_Click', 'private static DateTime? ReadNullableDateTime')
        self.assertIn('WasWaterResultCompletedAtLoad(item.SampleTestID)', save)
        self.assertIn('!newOosResult', save)
    def test_submission_review_and_approval_validate_saved_visible_evidence(self):
        code = read('ResultsEntry.xaml.cs')
        for start, end in [('private async void BtnSubmitReview_Click', 'private async void BtnReview_Click'), ('private async void BtnReview_Click', 'private async void BtnApprove_Click'), ('private async void BtnApprove_Click', 'private void BtnPrintReport_Click')]:
            body = method(code, start, end)
            self.assertIn('CommitWaterResultGridEdits();', body)
            self.assertIn('EnsureWaterVisibleEvidenceSaved(con, tran);', body)
            self.assertLess(body.index('EnsureWaterVisibleEvidenceSaved(con, tran);'), body.index('AddSampleElectronicSignatureInTransaction('))
    def test_report_preserves_range_and_frozen_numeric_conformity(self):
        code = read('ReportCertificate.xaml.cs')
        formatter = method(code, 'private string FormatReportSpecification(', 'private string GetConformity(')
        self.assertIn('WaterNumericResultEvaluator.IsRangeTest(testName)', formatter)
        self.assertIn('WaterReportNumericContract.FormatRange', formatter)
        conformity = method(code, 'private string GetConformity(', 'private string GetUnitForCertificate(')
        self.assertIn('WaterReportNumericContract.Conformity', conformity)
        self.assertNotIn('value >= 2.0m && value <= 4.0m', conformity)
        self.assertNotIn('IsSoftWaterPoint(sampleType) ? 5.0m : 300.0m', conformity)
    def test_water_behavior_cases_are_portable_and_registered(self):
        project = read('tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj')
        self.assertIn('../../Services/WaterWorkflowEvidenceGuard.cs', project)
        self.assertIn('../../Services/WaterReportNumericContract.cs', project)
        self.assertIn('RunWaterWorkflowFindingCases();', read('tests/PharmaLIMS.ReviewRegression/Program.cs'))
if __name__ == '__main__': unittest.main()
