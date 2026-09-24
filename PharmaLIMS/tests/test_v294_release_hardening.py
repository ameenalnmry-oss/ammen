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
        self.assertIn('"CanEnterResults", "submit PRM results for review"', submit)
        self.assertNotIn('"CanSubmitForReview"', submit)

    def test_prm_certificate_reissue_lineage_is_fail_closed(self):
        code = source("ProductionRawMaterialResults.xaml.Part2.cs")
        self.assertIn("private DataRow GetLatestCertificateRow()", code)
        self.assertIn("Reissue must reference the latest PRM certificate/report for this sample.", code)
        self.assertIn("A prior PRM certificate/report already exists for this sample.", code)
        self.assertIn("ReissuedFromCertificateID remains traceable", code)

    def test_prm_historical_closed_allows_only_signed_routed_legacy_reissue(self):
        ui = source("ProductionRawMaterialResults.xaml.cs")
        issue = source("ProductionRawMaterialResults.xaml.Part2.cs")

        self.assertIn("controlledHistoricalLegacyReissue", ui)
        self.assertIn("(!historicalTimingClosed || controlledHistoricalLegacyReissue)", ui)
        self.assertIn("allowHistoricalClosedForControlledLegacyReissue", ui)

        self.assertIn("LegacyCertificateEvidenceReconciliations R WITH(UPDLOCK,HOLDLOCK)", issue)
        self.assertIn("CONTROLLED_REISSUE_REQUIRED", issue)
        self.assertIn("R.ReconciliationID=", issue)
        self.assertIn("SELECT MAX(R2.ReconciliationID)", issue)
        self.assertIn("allowHistoricalClosedForControlledLegacyReissue: controlledHistoricalLegacyReissue", issue)
        self.assertIn("LoadControlledHistoricalLegacyResultsInTransaction", issue)
        self.assertIn("PRM_TimingMigrationTestEvidence e WITH(HOLDLOCK)", issue)
        self.assertIn("e.TimingMigrationTestEvidenceID AS HistoricalEvidenceID", issue)
        self.assertNotIn("e.EvidenceID", issue)
        self.assertIn("HistoricalResultValue", issue)
        self.assertIn("HistoricalInterpretation", issue)
        self.assertIn("entered after the source certificate issue date", issue)
        self.assertIn("The legacy result will not be reinterpreted retrospectively.", issue)
        self.assertIn("No retrospective result evidence will be created.", issue)
        self.assertIn("No retrospective signatures will be fabricated.", issue)
        self.assertIn("routedControlledLegacyReissue", ui)
        self.assertIn('? S(GetCurrentSampleRow(), "ResultInterpretation").Trim()', ui)
        self.assertIn('actionType.Equals("Result Entry"', issue)
        self.assertIn('actionType.Equals("Review"', issue)
        self.assertIn('actionType.Equals("Approval"', issue)
        self.assertIn('actionType.Equals("Certificate Reissue"', issue)

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

    def test_water_certificate_print_revalidates_exact_issued_document(self):
        helper = source("DatabaseHelper.Certificates.cs")
        self.assertIn("public static bool ValidateIssuedCertificateSnapshot(int sampleId, string certificateNumber, out string message)", helper)
        exact_gate = helper[helper.index("private static bool ValidateIssuedCertificateSnapshotCore"):helper.index("private static bool ValidateCertificateSnapshotJson")]
        self.assertIn("certificate.CertificateNumber=@CertificateNumber", exact_gate)
        self.assertIn("ISNULL(certificate.IsCancelled,0)=0", exact_gate)
        self.assertIn("IN(N'ACTIVE',N'ISSUED')", exact_gate)
        self.assertIn("The exact issued certificate/report is no longer active", exact_gate)

        report = source("ReportCertificate.xaml.cs")
        print_start = report.index("private void BtnPrint_Click")
        print_gate = report[print_start:print_start + 8000]
        self.assertIn("ValidateIssuedCertificateSnapshot(sampleId, certificateNumber, out string snapshotMessage)", print_gate)
        self.assertNotIn("ValidateIssuedCertificateSnapshot(sampleId, out string snapshotMessage)", print_gate)

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

    def test_prm_issued_certificate_record_rejects_tampering_but_allows_controlled_cancellation(self):
        migration = source("Database/Migrations/20260921_003_Protect_PRM_Certificate_Issued_Evidence.sql")
        self.assertIn("TRG_PRM_Certificates_ProtectIssuedEvidence_20260921", migration)
        self.assertIn("AFTER UPDATE, DELETE", migration)
        for field in (
            "CertificateNumber", "SampleID", "CertificateType", "ReportTitle",
            "IssueDate", "IssuedBy", "RevisionNo", "ReissuedFromCertificateID",
            "VerificationCode", "ReportHash", "CreatedBy", "CreatedDate",
        ):
            self.assertIn("UPDATE(" + field + ")", migration)
        self.assertIn("Issued PRM certificate identity/document evidence is immutable", migration)
        self.assertIn("CertificateStatus<>N''Active''", migration)
        self.assertIn("CertificateStatus<>N''Cancelled''", migration)
        self.assertIn("CancelledBy", migration)
        self.assertIn("CancelledDate", migration)
        self.assertIn("CancellationReason", migration)
        self.assertIn("Issued PRM certificate records cannot be deleted", migration)

        integration = source("tests/PharmaLIMS.DatabaseIntegration/Program.cs")
        self.assertIn("VerifyPrmIssuedCertificateEvidenceProtectionAsync(databaseConnectionString)", integration)
        self.assertIn("PRM issued-certificate protection allowed ReportHash tampering", integration)
        self.assertIn("PRM issued-certificate protection allowed certificate deletion", integration)

    def test_prm_controlled_open_binds_snapshot_to_certificate_identity(self):
        loader = source("ProductionRawMaterialResults.xaml.Part2.cs")
        self.assertIn("int expectedSampleId", loader)
        self.assertIn("string expectedCertificateNumber", loader)
        self.assertIn("SELECT TOP(2) SnapshotID,SampleID,CertificateNumber,HtmlContent,SnapshotHash", loader)
        self.assertIn("snapshot.Rows.Count != 1", loader)
        self.assertIn("snapshotSampleId != expectedSampleId", loader)
        self.assertIn("snapshotCertificateNumber.Equals(expectedCertificateNumber.Trim()", loader)
        self.assertIn("PRM certificate snapshot identity does not match the active certificate", loader)

        ui = source("ProductionRawMaterialResults.xaml.cs")
        self.assertIn("LoadPrmCertificateSnapshotHtml(", ui)
        self.assertIn("_selectedSampleId", ui)
        self.assertIn("certificateNumber", ui)

    def test_prm_certificate_history_is_append_only_and_open_action_is_not_mislabeled_as_print(self):
        contract = source("Infrastructure/ComplianceRecordProtectionContract.cs")
        migration = source("Database/Migrations/20260921_002_Protect_PRM_Certificate_History.sql")
        self.assertIn("PRM_CertificateHistory", contract)
        self.assertIn("TRG_PRM_CertificateHistory_AppendOnly_20260921", contract)
        self.assertIn("INSTEAD OF UPDATE, DELETE", migration)
        self.assertIn("PRM certificate lifecycle history is append-only and cannot be updated or deleted", migration)

        xaml = source("ProductionRawMaterialResults.xaml")
        self.assertIn('x:Name="BtnPrintCertificate" Content="Open Controlled Certificate"', xaml)
        self.assertNotIn('x:Name="BtnPrintCertificate" Content="Print Certificate"', xaml)

        code = source("ProductionRawMaterialResults.xaml.cs")
        self.assertIn('ShowOperationError("Open Controlled Certificate", ex);', code)

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

    def test_preflight_exposes_prm_specification_remediation_without_unlocking_registration(self):
        xaml = source("SystemPreflight.xaml")
        code = source("SystemPreflight.xaml.cs")
        prm = source("ProductionRawMaterialSamples.xaml.cs")

        self.assertIn('x:Name="BtnPrmSpecifications"', xaml)
        self.assertIn('Content="Manage PRM Specifications"', xaml)
        self.assertIn('Click="BtnPrmSpecifications_Click"', xaml)
        self.assertIn('"PRM approved-profile signature evidence"', code)
        self.assertIn("new ProductionRawMaterialSamples(specificationMasterOnly: true)", code)
        self.assertIn("await RunPreflightAsync();", code)

        self.assertIn("public ProductionRawMaterialSamples(bool specificationMasterOnly)", prm)
        self.assertIn("_specificationMasterOnly = specificationMasterOnly;", prm)
        self.assertIn("OpenSpecificationMasterPanel();", prm)
        self.assertIn('Title = "PRM Specification Master";', prm)
        self.assertIn("if (_specificationMasterOnly)", prm)
        self.assertIn("Close();", prm)

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
