import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


class V294ReleaseHardeningTests(unittest.TestCase):
    def test_fresh_baseline_ledger_is_verified_by_persisted_checksum(self):
        code = source("Infrastructure/StartupDatabaseMigrator.cs") + source("Infrastructure/StartupDatabaseMigrator.Part2.cs")
        self.assertIn("using System.Globalization;", code)
        self.assertIn("verifyRecordedBaseline", code)
        self.assertIn("MigrationChecksum=@Checksum", code)
        self.assertIn("baseline ledger record was not created with the expected checksum", code)

    def test_user_admin_compatibility_precreates_20260915_columns_with_dynamic_sql(self):
        code = source("Infrastructure/StartupDatabaseMigrator.Part2.cs")
        start = code.index("PrepareUserAdministrationSecurityCompatibilityAsync")
        compatibility = code[start:]
        self.assertIn("IF COL_LENGTH(N'dbo.Users',N'MustChangePassword') IS NULL", compatibility)
        self.assertIn("EXEC(N'ALTER TABLE dbo.Users", compatibility)
        self.assertIn("DF_Users_MustChangePassword_20260915 DEFAULT (0) WITH VALUES", compatibility)
        self.assertIn("EXEC(N'UPDATE dbo.Users SET MustChangePassword=0 WHERE MustChangePassword IS NULL;')", compatibility)
        self.assertIn("IF COL_LENGTH(N'dbo.Users',N'PasswordChangedAt') IS NULL", compatibility)
        self.assertIn("EXEC(N'ALTER TABLE dbo.Users ADD PasswordChangedAt DATETIME2(0) NULL;')", compatibility)
        self.assertNotIn("\n    UPDATE dbo.Users\n    SET MustChangePassword=0", compatibility)

    def test_quality_event_closure_requires_complete_controlled_evidence(self):
        code = source("DatabaseHelper.QualityEvents.cs")
        for expected in (
            "QA conclusion is required before Quality Event closure.",
            "Final disposition is required before Quality Event closure.",
            "A finalized root-cause category is required before Quality Event closure.",
            "Root-cause details are required before Quality Event closure.",
            "Impact assessment is required before Quality Event closure.",
        ):
            self.assertIn(expected, code)
        self.assertIn("ActionType=N'PRM CAPA Action'", code)
        self.assertIn("QualityEventCAPAItems", code)

    def test_interim_quality_event_dispositions_cannot_close(self):
        code = source("DatabaseHelper.QualityEvents.cs")
        for disposition in (
            'finalDisposition.Equals("Retest Approved"',
            'finalDisposition.Equals("Resample Approved"',
            'finalDisposition.Equals("System Corrected / Monitoring Required"',
        ):
            self.assertIn(disposition, code)
        self.assertIn("Complete the follow-up evidence and select a final disposition.", code)

    def test_em_collection_revalidates_prepared_media_in_transaction(self):
        code = source("EMPlanning.xaml.cs")
        self.assertIn("LEFT JOIN dbo.MediaPreparations p WITH(UPDLOCK,HOLDLOCK)", code)
        self.assertIn("EM collection is blocked because the prepared-media release gate is no longer satisfied.", code)
        self.assertIn("p.ExpiryDate<CAST(SYSDATETIME() AS date)", code)

    def test_em_submit_and_report_permissions_are_revalidated(self):
        code = source("DatabaseHelper.EnvironmentalMonitoring.cs")
        submit_start = code.index("public static void SubmitEMEventForReview")
        submit_end = code.index("public static void ReviewEMEvent", submit_start)
        submit = code[submit_start:submit_end]
        self.assertIn('"CanSubmitForReview"', submit)
        self.assertNotIn('"CanEnterResults", "submit EM results for review"', submit)

        print_start = code.index("public static bool CanPrintEMResultReport")
        print_end = code.index("public static DataTable GetEMEventSignatures", print_start)
        print_gate = code[print_start:print_end]
        self.assertIn('"CanAccessReports"', print_gate)
        self.assertIn("UPDLOCK, HOLDLOCK", print_gate)

    def test_legacy_em_result_helper_cannot_regress_locked_status(self):
        code = source("DatabaseHelper.EnvironmentalMonitoring.cs")
        start = code.index("public static void MarkEMResultsEntered")
        end = code.index("public static void SubmitEMEventForReview", start)
        method = code[start:end]
        for status in ("Under Review", "Reviewed", "Approved", "Completed", "Closed", "Cancelled"):
            self.assertIn(status, method)

    def test_em_report_uses_immutable_signer_identity_and_current_print_actor(self):
        db = source("DatabaseHelper.EnvironmentalMonitoring.cs")
        start = db.index("public static DataTable GetEMEventSignatures")
        end = db.index("public static int AddEMEventSignature", start)
        signatures = db[start:end]
        self.assertIn("S.SignedBy AS SignerDisplayName", signatures)
        self.assertNotIn("JOIN dbo.Users", signatures)
        self.assertNotIn("U.FullName", signatures)

        ui = source("EMResultsEntry.xaml.cs")
        self.assertIn("Current controlled report copy prepared for printing", ui)
        self.assertIn("currentCanAccessReports &&", ui)

    def test_prm_submit_permission_matches_ui_contract(self):
        code = source("ProductionRawMaterialResults.xaml.cs")
        start = code.index("private void BtnSubmitReview_Click")
        end = code.index("private void BtnReview_Click", start)
        submit = code[start:end]
        self.assertIn('"CanSubmitForReview"', submit)
        self.assertNotIn('"CanEnterResults", "submit PRM results for review"', submit)

    def test_prm_certificate_reissue_lineage_is_fail_closed(self):
        code = source("ProductionRawMaterialResults.xaml.Part2.cs")
        self.assertIn("private DataRow GetLatestCertificateRow()", code)
        self.assertIn("Reissue must reference the latest PRM certificate/report for this sample.", code)
        self.assertIn("A prior PRM certificate/report already exists for this sample.", code)
        self.assertIn("ReissuedFromCertificateID remains traceable", code)

    def test_prm_document_access_requires_reports_permission(self):
        code = source("ProductionRawMaterialResults.xaml.cs")
        self.assertIn("Reports access permission is required to view or print PRM certificates/reports.", code)
        self.assertIn("Reports access permission is required to preview PRM certificate/report layouts.", code)
        self.assertNotIn("CanAccessReports(GetCurrentUserDisplayName()) || CanIssuePrmCertificate()", code)

    def test_water_legacy_reissue_cannot_fork_certificate_lineage(self):
        code = source("DatabaseHelper.Certificates.cs")
        self.assertIn("not the latest cancelled certificate for this sample", code)
        self.assertIn("prevent a fork in the certificate reissue lineage", code)
        self.assertIn("ORDER BY ISNULL(RevisionNo,-1) DESC, CertificateID DESC", code)

    def test_prm_capa_requires_explicit_capa_action(self):
        code = source("PRMQualityEventInvestigation.xaml.cs")
        self.assertIn('"PRM CAPA Action"', code)
        self.assertIn("hasExplicitCapaAction", code)
        self.assertIn("ordinary investigation notes do not satisfy CAPA evidence", code)
        self.assertIn("EnsurePrmCapaClosureEvidenceInTransaction", code)
        self.assertIn("FROM dbo.QualityEventActions WITH (UPDLOCK, HOLDLOCK)", code)
        self.assertIn("no explicit PRM CAPA Action with a documented description exists in the locked database evidence", code)

    def test_final_result_approval_requires_qa_role_and_permission(self):
        security = source("DatabaseHelper.SecurityAudit.cs")
        self.assertIn("public static bool CanQaApproveResults(string username)", security)
        qa_gate = security[security.index("public static bool CanQaApproveResults"):security.index("public static bool CanIssueCertificate")]
        self.assertIn('RoleIsOneOf(role, "QA", "Quality Assurance")', qa_gate)
        self.assertIn('GetUserPermissionFlag(username, "CanApproveResults", false)', qa_gate)

        water = source("ResultsEntry.xaml.cs")
        self.assertIn("DatabaseHelper.CanQaApproveResults(currentUser)", water)
        water_approve = water[water.index("private void BtnApprove_Click"):water.index("private void BtnPrintReport_Click")]
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", water_approve)
        self.assertNotIn('"CanApproveResults", "approve water results"', water_approve)

        prm = source("ProductionRawMaterialResults.xaml.cs")
        self.assertIn("DatabaseHelper.CanQaApproveResults(GetCurrentUserDisplayName())", prm)
        prm_approve = prm[prm.index("private async void BtnApprove_Click"):prm.index("private async void BtnIssueCertificate_Click")]
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", prm_approve)
        self.assertNotIn('"CanApproveResults",\n                        "approve PRM results"', prm_approve)

        em_db = source("DatabaseHelper.EnvironmentalMonitoring.cs")
        em_approve_start = em_db.index("public static void ApproveEMEvent")
        em_approve = em_db[em_approve_start:em_approve_start + 10000]
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", em_approve)

        em_ui = source("EMResultsEntry.xaml.cs")
        self.assertIn("CanApprove: DatabaseHelper.CanQaApproveResults(username)", em_ui)

        culture = source("CultureMediaPreparation.xaml.cs") + source("CultureMediaPreparation.xaml.Part2.cs") + source("CultureMediaPreparation.xaml.Part3.cs")
        self.assertIn("DatabaseHelper.CanQaApproveResults(Login.CurrentUser)", culture)
        for action in (
            "confirm Culture Media qualification timing controls",
            "release culture media lot",
            "release prepared culture media",
            "reject culture media lot",
            "reject prepared culture media",
            "reconcile culture media stock",
        ):
            self.assertIn(action, culture)
        self.assertGreaterEqual(culture.count("EnsureQaApprovalAuthorizationInTransaction"), 5)

        prm_master = source("ProductionRawMaterialSamples.xaml.cs")
        self.assertIn("approval ? !DatabaseHelper.CanQaApproveResults(Login.CurrentUser)", prm_master)
        specification_workflow = prm_master[prm_master.index("private void ChangeSpecificationState"):prm_master.index("private static string NormalizeControlledResultType")]
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", specification_workflow)
        self.assertIn('"approve a PRM specification"', specification_workflow)

    def test_main_navigation_allows_qa_certificate_roles_into_water_workflow(self):
        code = source("MainWindow.xaml.cs")
        apply_start = code.index("private void ApplyRolePermissions()")
        apply_end = code.index("private void ApplySystemReadinessReport", apply_start)
        permissions = code[apply_start:apply_end]

        results_start = permissions.index("BtnResultsEntry.IsEnabled")
        results_end = permissions.index("BtnEMResults.IsEnabled", results_start)
        results_gate = permissions[results_start:results_end]
        for required in ("Login.CanEnterResults", "Login.CanReviewResults", "Login.CanApproveResults", "Login.CanIssueCOA", "Login.CanCancelCOA"):
            self.assertIn(required, results_gate)

        samples_start = permissions.index("BtnSampleManagement.IsEnabled")
        samples_end = permissions.index("BtnNewSample.IsEnabled", samples_start)
        samples_gate = permissions[samples_start:samples_end]
        for required in ("Login.CanApproveResults", "Login.CanIssueCOA", "Login.CanCancelCOA", "Login.CanAccessReports"):
            self.assertIn(required, samples_gate)

    def test_main_navigation_scrolls_without_hiding_user_footer(self):
        xaml = source("MainWindow.xaml")
        sidebar_start = xaml.index("<!-- Left navigation -->")
        sidebar_end = xaml.index("<!-- Main workspace -->", sidebar_start)
        sidebar = xaml[sidebar_start:sidebar_end]
        self.assertIn('Grid.Row="1"', sidebar)
        self.assertIn('VerticalScrollBarVisibility="Auto"', sidebar)
        self.assertIn('HorizontalScrollBarVisibility="Disabled"', sidebar)
        self.assertIn('<Border Grid.Row="2" Background="#102A42"', sidebar)
        self.assertNotIn('<Border Grid.Row="3" Background="#102A42"', sidebar)


if __name__ == "__main__":
    unittest.main()
