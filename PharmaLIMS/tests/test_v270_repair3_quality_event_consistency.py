import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class Repair3QualityEventConsistencyTests(unittest.TestCase):
    def test_dashboard_and_ai_do_not_treat_approved_as_terminal(self):
        for name in ("MainWindow.xaml.cs", "AISystemReview.xaml.cs"):
            source = (ROOT / name).read_text(encoding="utf-8-sig")
            self.assertIn("''Rejected Closed''", source)
            self.assertIn("''QA Closed''", source)
            self.assertNotIn("''Closed'', ''QA Closed'', ''Approved'', ''Cancelled''", source)

    def test_sample_details_uses_current_status_and_query_failure_fails_closed(self):
        source = (ROOT / "SampleDetails.xaml.cs").read_text(encoding="utf-8-sig")
        method = source.split("private static void FillInvestigationSituation", 1)[1].split("private static void FillCOASituation", 1)[0]
        self.assertIn("CurrentStatus AS InvestigationStatus", method)
        self.assertIn("SampleID = @sampleId", method)
        self.assertIn("SourceRecordID = @sampleId", method)
        self.assertIn('status.Equals("Rejected Closed"', method)
        self.assertNotIn('status.Equals("Approved"', method)
        self.assertIn("Unknown/legacy states fail safe as open", method)
        self.assertIn("situation.HasOpenInvestigation = true;", method)
        self.assertIn("Verification unavailable - treated as open", method)

    def test_em_positive_release_never_treats_approved_or_cancelled_as_closed(self):
        helper = (ROOT / "DatabaseHelper.EnvironmentalMonitoring.cs").read_text(encoding="utf-8-sig")
        gate = helper.split("private static void EnsureEmApprovalQualityEventGateInTransaction", 1)[1].split("private static (int Total, int Entered)", 1)[0]
        self.assertIn('status.Equals("Closed"', gate)
        self.assertIn('status.Equals("QA Closed"', gate)
        self.assertNotIn('status.Equals("Approved"', gate)
        self.assertNotIn('status.Equals("Closed - Accepted"', gate)
        self.assertNotIn('status.Equals("Cancelled"', gate)
        entry = (ROOT / "EMResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        ui = entry.split("private bool IsCurrentQualityEventClosed", 1)[1].split("private bool IsAdminWorkflowOverrideAllowed", 1)[0]
        self.assertNotIn('status.Equals("Approved"', ui)
        self.assertNotIn('status.Equals("Closed - Accepted"', ui)

    def test_em_final_report_rechecks_quality_event_gate_at_print_time(self):
        source = (ROOT / "DatabaseHelper.EnvironmentalMonitoring.cs").read_text(encoding="utf-8-sig")
        method = source.split("public static bool CanPrintEMResultReport", 1)[1].split("public static DataTable GetEMEventSignatures", 1)[0]
        self.assertIn("EnsureEmApprovalQualityEventGateInTransaction", method)
        self.assertIn("Report printing is blocked", method)
        self.assertIn("WITH (UPDLOCK, HOLDLOCK)", method)

    def test_prm_approval_issue_reissue_require_qe_schema(self):
        main = (ROOT / "ProductionRawMaterialResults.xaml.cs").read_text(encoding="utf-8-sig")
        part2 = (ROOT / "ProductionRawMaterialResults.xaml.Part2.cs").read_text(encoding="utf-8-sig")
        approve = main.split("private async void BtnApprove_Click", 1)[1].split("private async void BtnIssueCertificate_Click", 1)[0]
        issue = main.split("private async void BtnIssueCertificate_Click", 1)[1].split("private void BtnPrintCertificate_Click", 1)[0]
        reissue = main.split("private async void BtnReissueCertificate_Click", 1)[1].split("private void BtnOpenQualityEvent_Click", 1)[0]
        for block in (approve, issue, reissue):
            self.assertIn("EnsurePrmQualityEventSchemaReadyForActionAsync", block)
        tx_probe = part2.split("private bool HasAnyPrmQualityEventMinimalInTransaction", 1)[1].split("private bool HasOpenPrmQualityEventMinimal", 1)[0]
        self.assertIn("COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus')", tx_probe)
        self.assertIn("controlled Quality Event schema is incomplete", tx_probe)

    def test_fresh_permission_checks_from_v269_are_preserved(self):
        main = (ROOT / "ProductionRawMaterialResults.xaml.cs").read_text(encoding="utf-8-sig")
        part2 = (ROOT / "ProductionRawMaterialResults.xaml.Part2.cs").read_text(encoding="utf-8-sig")
        self.assertIn("DatabaseHelper.CanManageSettings(GetCurrentUserDisplayName())", main)
        self.assertIn("DatabaseHelper.CanManageSettings(GetCurrentUserDisplayName())", part2)
        self.assertNotIn("IsAdminUser() || Login.CanManageSettings", main)
        self.assertNotIn("IsAdminUser() || Login.CanManageSettings", part2)


if __name__ == "__main__":
    unittest.main()
