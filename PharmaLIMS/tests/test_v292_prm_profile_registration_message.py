from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class V292PrmProfileRegistrationMessageTests(unittest.TestCase):
    def test_registration_uses_concise_operator_message_without_bypassing_profile_gate(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        validation = registration.split("private void ValidateForm()", 1)[1].split("private void InsertSample()", 1)[0]

        self.assertIn("BuildNoApprovedProfileOperatorMessage(category, itemCode, productionStage)", validation)
        self.assertNotIn("BuildNoApprovedProfileMessage(category, itemCode, productionStage)", validation)
        self.assertIn("configured.ApprovalStatus=N'Approved'", validation)
        self.assertIn("configured.IsActive=1", validation)
        self.assertIn("ApprovedProfileEvidencePredicateSql", validation)

    def test_operator_message_is_safe_actionable_and_short_enough_for_user_facing_error(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        block = registration.split("private static string BuildNoApprovedProfileOperatorMessage", 1)[1].split(
            "private string GetSelectedItemCode", 1
        )[0]

        self.assertIn("Open Specification Master", block)
        self.assertIn("Review -> Approve", block)
        self.assertIn("Registration remains blocked", block)
        self.assertIn("electronic-signature evidence", block)
        self.assertNotIn("Database Maintenance/deployment", block)
        self.assertNotIn("dbo.", block)
        self.assertNotIn("SELECT ", block)

        literals = re.findall(r'"([^"\\]*(?:\\.[^"\\]*)*)"', block)
        literal_chars = sum(len(bytes(value, "utf-8").decode("unicode_escape")) for value in literals)
        self.assertLess(literal_chars, 420)

    def test_detailed_guidance_keeps_governance_and_avoids_blind_maintenance_instruction(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        block = registration.split("private static string BuildNoApprovedProfileMessage", 1)[1].split(
            "private static string BuildNoApprovedProfileOperatorMessage", 1
        )[0]

        self.assertIn("QA must verify the approved site/product specification", block)
        self.assertIn("Specification Master Review -> Approve", block)
        self.assertIn("Use Database Maintenance/deployment only when System Preflight reports", block)
        self.assertIn("intentionally excluded from registration", block)

    def test_primary_packaging_one_general_scope_draft_and_material_codes_remain_separate(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "ProductionRawMaterialSamples.xaml").read_text(encoding="utf-8-sig")
        create = registration.split("private void BtnCreateProfileForScope_Click(", 1)[1].split(
            "private int LoadApprovedSpecificationChoices(", 1
        )[0]
        self.assertIn('bool generalPackaging = category.Equals("Primary Packaging"', create)
        self.assertIn("if (!generalPackaging && string.IsNullOrWhiteSpace(itemCode))", create)
        self.assertIn('TxtMasterItemCode.Text = generalPackaging ? "*" : itemCode;', create)
        self.assertIn("ApplyPharmacopoeialMicrobiologyTemplate(category);", create)
        self.assertIn("NewSpecificationDraft();", create)
        self.assertIn("Save Draft -> independent Review -> QA Approve", create)
        self.assertIn('BtnCreateProfileForScope.Content = generalPackaging', registration)
        self.assertIn('x:Name="BtnCreateProfileForScope"', xaml)
        self.assertIn('Primary Packaging: use Item Code * for ONE category-wide profile.', registration)
        self.assertIn('SelectionChanged="CmbMasterCategory_SelectionChanged"', xaml)
        self.assertIn('x:Name="TxtMasterScopeGuidance"', xaml)
        self.assertIn("private void UpdateMasterScopeGuidance()", registration)
        self.assertIn('Content="Primary Packaging"', xaml)
        self.assertIn('return (TxtMaterialCode.Text ?? string.Empty).Trim();', registration)

    def test_general_profile_matches_any_item_but_specific_approved_profile_wins(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        query = registration.split("private int LoadApprovedSpecificationChoices()", 1)[1].split(
            "private static string BuildNoApprovedProfileMessage(", 1
        )[0]
        self.assertIn('IN', query)
        self.assertIn("(UPPER(LTRIM(RTRIM(@ItemCode))),N'*')", query)
        self.assertIn('THEN 2 ELSE 0 END', query)
        self.assertIn('WHERE MatchRank=(SELECT MAX(MatchRank) FROM Candidates)', query)
        self.assertIn("configured.ApprovalStatus=N'Approved'", query)
        self.assertIn("configured.IsActive=1", query)
        self.assertIn("PrmSpecificationRepository.ApprovedProfileEvidencePredicateSql", query)
        validate = registration.split("private void ValidateForm()", 1)[1].split(
            "private void InsertSample()", 1
        )[0]
        self.assertIn("(UPPER(LTRIM(RTRIM(@ItemCode))),N'*')", validate)
        self.assertIn("ApprovedProfileEvidencePredicateSql", validate)

    def test_new_prm_registration_never_defaults_actual_sampling_to_registration_time(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        layout = (ROOT / "ProductionRawMaterialSamples.xaml").read_text(encoding="utf-8-sig")
        defaults = registration.split("private void SetDefaultValues()", 1)[1].split("private ", 1)[0]
        self.assertIn("DpSampleDate.SelectedDate = null;", defaults)
        self.assertIn("TxtSampleTime.Text = string.Empty;", defaults)
        self.assertNotIn('TxtSampleTime.Text = DateTime.Now.ToString("HH:mm"', registration)
        self.assertNotIn("DpSampleDate.SelectedDate = DateTime.Today;", registration)
        self.assertIn('Text="Actual Sampling Date / Time *"', layout)
        self.assertIn("Do not use the registration time unless sampling occurred then.", layout)
        self.assertIn("Sample Time is required in 24-hour HH:mm format", registration)
        self.assertIn("DpSampleDate.SelectedDate = storedSampleDateTime?.Date;", registration)
        self.assertIn("GuardPrmSamplingTime", registration)
        self.assertIn('"PRM Registration Updated"', registration)

    def test_prm_registration_has_bounded_horizontal_layout_and_internal_table_scrolling(self):
        xaml = (ROOT / "ProductionRawMaterialSamples.xaml").read_text(encoding="utf-8-sig")
        code = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn(
            '<ScrollViewer x:Name="MainContentScroll" Grid.Row="1" '
            'VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">',
            xaml,
        )
        self.assertIn("MainContentScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;", code)
        self.assertNotIn("MainContentScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;", code)
        self.assertIn('x:Name="DgSpecificationTests"', xaml)
        master = xaml.split('x:Name="DgSpecificationTests"', 1)[1].split('</DataGrid>', 1)[0]
        self.assertIn('MinWidth="0"', master)
        self.assertIn('HorizontalAlignment="Stretch"', master)
        self.assertIn('ScrollViewer.HorizontalScrollBarVisibility="Auto"', master)
        samples = xaml.split('x:Name="DgSamples"', 1)[1].split('</DataGrid>', 1)[0]
        self.assertIn('ScrollViewer.HorizontalScrollBarVisibility="Auto"', samples)
        self.assertIn('x:Name="TxtStatus" Text="Ready." FontSize="12" Foreground="#334155" TextWrapping="Wrap"', xaml)


if __name__ == "__main__":
    unittest.main()
