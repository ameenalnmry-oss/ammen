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
        self.assertIn("!IsDevelopmentAdminOverride()", em)
        self.assertIn("!IsDevelopmentAdminOverride()", media)
        self.assertIn("if (IsDevelopmentAdminOverride())", media2)

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


if __name__ == "__main__":
    unittest.main()
