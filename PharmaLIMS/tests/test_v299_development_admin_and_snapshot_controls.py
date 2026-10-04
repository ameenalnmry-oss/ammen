import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


class V299DevelopmentAdminAndSnapshotControls(unittest.TestCase):
    def test_prm_registration_department_is_microbiology_laboratory(self):
        xaml = source("ProductionRawMaterialSamples.xaml")
        self.assertIn('x:Name="TxtDepartment" Text="Microbiology Laboratory"', xaml)
        self.assertNotIn('Text="Laboratory / QC"', xaml)

    def test_development_admin_has_full_permissions_but_production_cannot_enable_them(self):
        app = source("AppConfig.cs")
        login = source("Login.xaml.cs")
        self.assertIn("IsDevelopment && Settings.Value.DevelopmentAdminFullPermissions", app)
        self.assertIn("grantDevelopmentAdminPermissions", login)
        for flag in (
            "CanAccessWater", "CanAccessEM", "CanRegisterSamples", "CanEnterResults",
            "CanReviewResults", "CanApproveResults", "CanIssueCOA", "CanCancelCOA",
            "CanAccessReports", "CanManageUsers", "CanManageSettings",
        ):
            self.assertIn(f"{flag} = grantDevelopmentAdminPermissions ||", login)

    def test_prm_timing_override_is_development_admin_only(self):
        main = source("ProductionRawMaterialResults.xaml.cs")
        part2 = source("ProductionRawMaterialResults.xaml.Part2.cs")
        self.assertIn("IsDevelopmentAdminTimingOverrideAllowed()", main)
        self.assertIn("AppConfig.AllowEarlyMicrobiologyResults &&", part2)
        self.assertIn("IsAdminWorkflowOverrideAllowed()", part2)
        self.assertNotIn("if (!AppConfig.AllowEarlyMicrobiologyResults)", main)

    def test_water_timing_override_is_development_admin_only(self):
        code = source("ResultsEntry.xaml.cs")
        self.assertIn("private static bool IsDevelopmentAdminTimingOverrideAllowed()", code)
        self.assertIn("AppConfig.AllowEarlyMicrobiologyResults &&", code)
        self.assertIn("AppConfig.DevelopmentAdminFullPermissions &&", code)
        self.assertIn("if (IsDevelopmentAdminTimingOverrideAllowed())", code)

    def test_em_and_culture_media_use_admin_scoped_timing_override(self):
        em = source("EMPlanning.xaml.cs")
        media = source("CultureMediaPreparation.xaml.cs")
        media2 = source("CultureMediaPreparation.xaml.Part2.cs")
        self.assertIn("!IsDevelopmentAdminTimingOverride()", em)
        self.assertIn("AppConfig.AllowEarlyMicrobiologyResults", em)
        self.assertIn("!IsDevelopmentAdminTimingOverride()", media)
        self.assertIn("AppConfig.AllowEarlyMicrobiologyResults", media)
        self.assertIn("if (IsDevelopmentAdminTimingOverride())", media2)

    def test_prm_default_timing_is_per_test_not_120_hours_for_everything(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        self.assertIn('TestCode = "TAMC"', code)
        self.assertIn("MinimumElapsedHours = 72m", code)
        self.assertIn('TestCode = "TYMC"', code)
        self.assertIn("MinimumElapsedHours = 120m", code)
        self.assertIn('TestCode = "ECOLI"', code)
        self.assertIn("MinimumElapsedHours = 60m", code)
        self.assertIn('TestCode = "SALMONELLA"', code)
        self.assertIn("MinimumElapsedHours = 54m", code)
        self.assertIn('TestCode = "SAUREUS"', code)
        self.assertIn("MinimumElapsedHours = 36m", code)
        self.assertIn('TestCode = "PAERUGINOSA"', code)
        self.assertIn('TestCode = "CALBICANS"', code)
        self.assertIn("MinimumElapsedHours = 96m", code)

    def test_water_result_save_preserves_immutable_limit_description(self):
        part3 = source("ResultsEntry.xaml.Part3.cs")
        self.assertIn('SpecificationText = Convert.ToString(row["LimitDescription"]', part3)
        save = part3[part3.index("private bool PersistWaterEdits"):]
        self.assertNotIn("LimitDescription=@limitDescription", save)
        self.assertNotIn('@limitDescription", SqlDbType', save)

    def test_water_certificate_uses_snapshot_qualitative_endpoint_and_no_1_3_fallback(self):
        report = source("ReportCertificate.xaml.cs")
        self.assertIn("ConciseApprovedQualitativeCriterion", report)
        self.assertIn("Not more intense than comparator", report)
        self.assertIn("Approved specification snapshot missing", report)
        self.assertNotIn("fallbackLimit = IsPurifiedWater(sampleType) ? 1.3m", report)


    def test_new_sample_sections_are_numbered_once_from_one_to_eight(self):
        xaml = source("NewSampleDialog.xaml")
        for expected in (
            "1. Registration Type",
            "2. Sampling Point / EM Area",
            "3. Environmental Monitoring Details",
            "4. Personnel / Surface Monitoring Context",
            "5. EM Media and Incubation",
            "6. Registration Information",
            "7. Water Sample Receipt / Chain of Custody",
            "8. Water Tests Selection",
        ):
            self.assertEqual(xaml.count(expected), 1, expected)
        self.assertNotIn("5. Registration Information", xaml)

    def test_water_incubation_gate_does_not_concatenate_ids_into_sql_text(self):
        code = source("ResultsEntry.xaml.cs")
        start = code.index("private bool ValidateWaterIncubationCompleteBeforeResultsInTransaction")
        end = code.index("private DateTime? GetSampleDateTime", start)
        method = code[start:end]
        self.assertIn("@EnteredTestIdsXml.nodes('/ids/id')", method)
        self.assertIn('new SqlParameter("@EnteredTestIdsXml", SqlDbType.Xml)', method)
        self.assertNotIn('st.TestID IN (" + string.Join', method)

    def test_trend_pdf_rendering_runs_off_dispatcher_thread(self):
        code = source("ReportsTrends.xaml.cs")
        start = code.index("private async void BtnExportPDF_Click")
        end = code.index("#endregion", start)
        method = code[start:end]
        self.assertIn("await RunOnStaThreadAsync(() =>", method)
        self.assertIn("ExportTrendModel(model)", method)
        self.assertIn("ApartmentState.STA", code)
        self.assertIn("TrendPdfReportWriter.Write", method)
        self.assertIn("IsEnabled = false", method)
        self.assertIn("IsEnabled = true", method)

    def test_prm_na_answers_require_controlled_reason_and_technical_rationale(self):
        xaml = source("PRMQualityEventInvestigation.xaml")
        code = source("PRMQualityEventInvestigation.xaml.cs")
        service = source("Services/Investigations/PRMQualityEventInvestigationService.cs")
        self.assertEqual(xaml.count('Header="N/A Justification"'), 3)
        self.assertIn("IsControlledNaJustification", code)
        self.assertIn("minimumNaCommentLength", code)
        self.assertIn("Other scientifically justified reason", code)
        self.assertIn('CAST(N\'\' AS nvarchar(120)) AS NAJustification', service)
        self.assertIn('string prefix = "[N/A: " + naJustification.Trim() + "]";', service)
        self.assertIn("NAJustification", service)


if __name__ == "__main__":
    unittest.main()
