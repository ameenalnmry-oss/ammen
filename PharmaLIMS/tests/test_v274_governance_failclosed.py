import json
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class V274GovernanceFailClosedTests(unittest.TestCase):
    def test_quality_event_prechecks_fail_closed_when_schema_is_unavailable(self):
        helper = (ROOT / "DatabaseHelper.QualityEvents.cs").read_text(encoding="utf-8-sig")

        open_method = helper.split("public static bool HasOpenQualityEvent", 1)[1].split("public static string GetOpenQualityEventSummary", 1)[0]
        self.assertIn("throw new InvalidOperationException", open_method)
        self.assertIn("Quality Event compliance table is unavailable", open_method)
        self.assertNotIn('return false;', open_method.split('if (!TableExists("QualityEvents"))', 1)[1].split('string query', 1)[0])

        resolved = helper.split("public static bool AreOosResultsResolvedForApproval", 1)[1].split("internal static void EnsureSampleApprovalQualityGatesInTransaction", 1)[0]
        self.assertIn("Quality Event compliance tables are unavailable", resolved)
        self.assertIn("return false;", resolved)
        self.assertNotIn('if (!TableExists("QualityEvents") || !TableExists("QualityEventAffectedResults"))\n                return true;', resolved)

        any_method = helper.split("public static bool HasAnyQualityEvent", 1)[1].split("public static void AddQualityEventPrintHistory", 1)[0]
        self.assertIn("throw new InvalidOperationException", any_method)

    def test_water_workflow_ui_disables_controlled_actions_when_verification_fails(self):
        results = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        update = results.split("private void UpdateWorkflowButtons()", 1)[1].split("private void BtnSearch_Click", 1)[0]

        self.assertIn("certificateStateVerified = false", update)
        self.assertIn("qualityEventStateVerified = false", update)
        self.assertIn("hasOpenQualityEvent = true; // Fail closed", update)
        self.assertIn("BtnSubmitReview.Visibility = Visibility.Collapsed", update)
        self.assertIn("BtnApprove.Visibility = Visibility.Collapsed", update)
        self.assertIn("BtnCertificate.IsEnabled = false", update)
        self.assertIn("Quality Event Verification Unavailable", update)
        self.assertIn("Run System Preflight / Database Maintenance", update)
        self.assertIn("ApplicationLogger.Error", update)

    def test_sample_details_certificate_unknown_state_is_logged_and_conservative(self):
        details = (ROOT / "SampleDetails.xaml.cs").read_text(encoding="utf-8-sig")
        fill = details.split("private static void FillCOASituation", 1)[1].split("private static void BuildPermissionsAndNextAction", 1)[0]
        permissions = details.split("private static void BuildPermissionsAndNextAction", 1)[1].split("private void ApplySituationToUi", 1)[0]

        self.assertIn('COAStatusText = "Verification unavailable"', fill)
        self.assertIn("ApplicationLogger.Error", fill)
        self.assertIn("ApplicationLogger.Warning", fill)
        self.assertIn('string.Equals(s.COAStatusText, "Verification unavailable"', permissions)
        self.assertIn("s.CanEnterResults = false", permissions)
        self.assertIn("s.CanSubmitForReview = false", permissions)
        self.assertIn("s.CanApprove = false", permissions)
        self.assertIn("s.CanIssueCOA = false", permissions)


    def test_prm_governance_keeps_review_approve_signatures_and_separation_of_duties(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("INSERT dbo.PRM_SpecificationSignatures", registration)
        self.assertIn("The specification reviewer must be independent from the draft creator", registration)
        self.assertIn("The specification approver must be independent from the reviewer", registration)
        self.assertIn("DatabaseHelper.CanQaApproveResults(Login.CurrentUser)", registration)
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", registration)
        self.assertIn('"CanReviewResults"', registration)
        self.assertIn('approval ? "Approve Specification" : "Review Specification"', registration)

        migration = (ROOT / "Database/Migrations/20260913_001_PRM_Standard_Profile_Governance_Hardening.sql").read_text(encoding="utf-8-sig")
        # The only UPDATEs of specification master rows are scoped to the known synthetic v273 creator.
        update_blocks = migration.split("UPDATE dbo.PRM_SpecificationTests")[1:]
        self.assertGreaterEqual(len(update_blocks), 2)
        for block in update_blocks:
            statement = block.split(";", 1)[0]
            self.assertIn("CreatedBy=N'Controlled PRM Standard Profile Readiness 20260913'", statement)
        self.assertIn("A real active Approved site profile is authoritative", migration)
        self.assertIn("never retire/replace it", migration)

    def test_retired_v273_synthetic_approved_profiles_are_rejected_by_runtime_selection(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/PrmSpecificationRepository.cs").read_text(encoding="utf-8-sig")
        marker = "Controlled PRM Standard Profile Readiness 20260913"
        legacy_review = "Controlled PRM Profile Review 20260913"
        legacy_approval = "Controlled PRM Profile Approval 20260913"

        # Registration list, validation, and transactional save gate must all exclude
        # the retired migration's synthetic approval before maintenance reconciliation.
        self.assertGreaterEqual(registration.count(marker), 3)
        self.assertGreaterEqual(registration.count(legacy_review), 3)
        self.assertGreaterEqual(registration.count(legacy_approval), 3)

        # Repository selection/assignment paths must not consume those rows either.
        self.assertGreaterEqual(repository.count(marker), 4)
        self.assertGreaterEqual(repository.count(legacy_review), 4)
        self.assertGreaterEqual(repository.count(legacy_approval), 4)

    def test_standard_wildcard_draft_can_be_reviewed_in_specification_master(self):
        xaml = (ROOT / "ProductionRawMaterialSamples.xaml").read_text(encoding="utf-8-sig")
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('<ComboBoxItem Content="*"', xaml)
        self.assertIn("wildcard * is reserved for a formally reviewed and approved reusable standard profile", xaml)
        self.assertIn("IN (UPPER(LTRIM(RTRIM(@ProductionStage))),N'*')", registration)
        self.assertIn("Specification Master Review -> Approve", registration)

    def test_development_example_is_portable(self):
        config = json.loads((ROOT / "appsettings.Development.example.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(r".\SQLEXPRESS", config["Database"]["Server"])
        self.assertNotIn("Ameen", config["Database"]["Server"])

    def test_current_version_and_migration_count(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.18.294", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


if __name__ == "__main__":
    unittest.main()
