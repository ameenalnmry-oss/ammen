import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


class V293DeepReviewRemediationTests(unittest.TestCase):
    def test_entered_optional_prm_results_are_authoritative(self):
        service = source("Services/PrmSampleResultStateService.cs")
        ui = source("ProductionRawMaterialResults.xaml.Part2.cs")
        self.assertIn("!isRequired && string.IsNullOrWhiteSpace(result)", service)
        self.assertNotIn("row.RowState == DataRowState.Deleted || !IsRequired(row)", service)
        self.assertIn('string.IsNullOrWhiteSpace(S(row, "ResultValue"))', ui)
        affected_query = ui[ui.index("private bool DoesPrmQualityEventCoverCurrentAffectedResults"):]
        self.assertNotIn("ISNULL(st.RequiredTest, 1) = 1", affected_query)

    def test_prm_refresh_and_selection_have_ui_exception_boundaries(self):
        registration = source("ProductionRawMaterialSamples.xaml.cs")
        self.assertIn('ApplicationLogger.Error("Manual PRM sample refresh failed."', registration)
        self.assertIn('ApplicationLogger.Error("PRM sample selection loading failed."', registration)
        self.assertIn("await Task.Run(() => QuerySampleForEdit(sampleId))", registration)

    def test_prm_category_reset_does_not_fire_cross_workflow_event(self):
        registration = source("ProductionRawMaterialSamples.xaml.cs")
        self.assertIn("bool previousLoading = _isLoading;", registration)
        self.assertIn('category.Equals("Production / In-Process"', registration)
        self.assertIn('SetComboText(CmbProductionStage, "After Mixing")', registration)

    def test_result_type_is_controlled(self):
        registration = source("ProductionRawMaterialSamples.xaml.cs")
        self.assertIn("NormalizeControlledResultType(test.ResultType)", registration)
        self.assertIn("Result Type must be one of: Numeric, Qualitative", registration)

    def test_active_legacy_credentials_block_production(self):
        preflight = source("Infrastructure/SystemPreflightService.cs")
        self.assertIn('AppConfig.IsProduction ? "BLOCKER" : "WARNING"', preflight)
        self.assertIn("Production use is blocked until every active account", preflight)

    def test_entire_migration_payload_is_validated_before_execution(self):
        migrator = source("Infrastructure/StartupDatabaseMigrator.cs")
        verification = source("Infrastructure/StartupDatabaseMigrator.Verification.cs")
        self.assertIn("ValidateCompleteMigrationPayload(root, migrations);", migrator)
        self.assertIn("checksum mismatch", verification)
        self.assertIn("duplicate or blank version key", verification)

    def test_csv_exports_are_permission_checked_hashed_and_audited(self):
        samples = source("SampleManagement.xaml.cs")
        reports = source("ReportsTrends.xaml.cs")
        self.assertIn("DatabaseHelper.CanAccessReports(Login.CurrentUser)", samples)
        for text in (samples, reports):
            self.assertIn("SHA256.HashData", text)
            self.assertIn("UNCONTROLLED COPY", text)
            self.assertIn("AddAuditTrailAdvanced", text)

    def test_ci_can_materialize_then_enforce_lock_graph(self):
        ci = source(".github/workflows/ci.yml")
        self.assertIn("--use-lock-file --force-evaluate", ci)
        self.assertIn("dotnet restore ./PharmaLIMS.csproj --locked-mode", ci)


if __name__ == "__main__":
    unittest.main()
