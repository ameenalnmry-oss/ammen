import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


class V294PrmPharmacopoeialTemplateTests(unittest.TestCase):
    def test_release_version_is_v294(self):
        manifest = json.loads(source("Database/MigrationManifest.json"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertIn("<Version>2026.9.23.295</Version>", source("PharmaLIMS.csproj"))

    def test_specification_master_exposes_template_loader(self):
        xaml = source("ProductionRawMaterialSamples.xaml")
        code = source("ProductionRawMaterialSamples.xaml.cs")
        self.assertIn("Load Controlled Template", xaml)
        self.assertIn("BtnLoadPharmacopoeialTemplate_Click", xaml)
        self.assertIn("private void ApplyPharmacopoeialMicrobiologyTemplate", code)

    def test_finished_product_and_stability_use_harmonized_oral_nonaqueous_template(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        self.assertIn('SpecificationText = "NMT 2000 CFU/g"', code)
        self.assertIn('SpecificationText = "NMT 200 CFU/g"', code)
        self.assertIn('TestCode = "ECOLI"', code)
        self.assertIn('SpecificationText = "Absent in 1 g"', code)
        self.assertIn("non-aqueous preparations for oral use", code)

    def test_raw_material_default_does_not_invent_universal_specified_organisms(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        raw_start = code.index('if (normalized.Equals("Raw Material"')
        raw_end = code.index('bool oralSolidScope', raw_start)
        raw_block = code[raw_start:raw_end]
        self.assertIn('SpecificationText = "NMT 2000 CFU/g or mL"', raw_block)
        self.assertIn('SpecificationText = "NMT 200 CFU/g or mL"', raw_block)
        self.assertNotIn('TestCode = "ECOLI"', raw_block)
        self.assertIn("material-monograph/risk-assessment dependent", raw_block)

    def test_in_process_uses_separate_medica_controlled_test_rows(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        start = code.index('if (normalized.Equals("Production / In-Process"')
        end = code.index('TxtMasterReference.Text =\n                "USP <61>/<62>/<1111>', start)
        block = code[start:end]
        self.assertIn('TxtMasterReference.Text = "MQC-G-0021"', block)
        self.assertIn('TestCode = "TAMC"', block)
        self.assertIn('SpecificationText = "NMT 1000 CFU/g"', block)
        self.assertIn('TestCode = "TYMC"', block)
        self.assertIn('SpecificationText = "NMT 100 CFU/g"', block)
        self.assertIn('TestCode = "ECOLI"', block)
        self.assertIn('TestCode = "SALMONELLA"', block)
        self.assertIn('TestCode = "SAUREUS"', block)
        self.assertIn('TestCode = "PAERUGINOSA"', block)
        self.assertIn('TestCode = "CALBICANS"', block)
        self.assertNotIn('TMC&TMYC', block)

    def test_create_profile_for_scope_preloads_template_but_does_not_approve(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        handler_start = code.index("private void BtnCreateProfileForScope_Click")
        handler_end = code.index("private int LoadApprovedSpecificationChoices", handler_start)
        handler = code[handler_start:handler_end]
        self.assertIn("ApplyPharmacopoeialMicrobiologyTemplate(category);", handler)
        self.assertNotIn("ApprovalStatus=N'Approved'", handler)
        self.assertNotIn("Approve Specification", handler)

    def test_clone_active_approved_creates_unsaved_draft_only(self):
        xaml = source("ProductionRawMaterialSamples.xaml")
        code = source("ProductionRawMaterialSamples.xaml.cs")
        self.assertIn('Content="Clone Active Approved"', xaml)
        self.assertIn('Click="BtnCloneActiveApproved_Click"', xaml)
        handler_start = code.index("private void BtnCloneActiveApproved_Click")
        handler_end = code.index("private void BtnLoadPharmacopoeialTemplate_Click", handler_start)
        handler = code[handler_start:handler_end]
        self.assertIn("ApprovalStatus=N'Approved'", handler)
        self.assertIn("IsActive=1", handler)
        self.assertIn("_masterVersion = 0;", handler)
        self.assertIn('TxtMasterVersion.Text = "New";', handler)
        self.assertNotIn("INSERT dbo.PRM_SpecificationTests", handler)
        self.assertNotIn("UPDATE dbo.PRM_SpecificationTests", handler)
        self.assertNotIn("DELETE dbo.PRM_SpecificationTests", handler)


    def test_clone_active_approved_does_not_invent_missing_timing(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        start = code.index("private void BtnCloneActiveApproved_Click")
        end = code.index("private void BtnLoadPharmacopoeialTemplate_Click", start)
        handler = code[start:end]
        self.assertIn(": 0m,", handler)
        self.assertIn("clonedTimingRequiresCompletion", handler)
        self.assertIn("no controlled Minimum Elapsed Hours", handler)
        self.assertNotIn(": 120m,", handler)

    def test_load_specification_does_not_invent_missing_timing(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        start = code.index("private void BtnLoadSpecification_Click")
        end = code.index("private void BtnSaveSpecification_Click", start)
        handler = code[start:end]
        self.assertIn(": 0m,", handler)
        self.assertNotIn(": 120m,", handler)

    def test_new_blank_specification_requires_explicit_timing(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        start = code.index("private void NewSpecificationDraft()")
        end = code.index("private string MasterCategory", start)
        handler = code[start:end]
        self.assertIn("MinimumElapsedHours = 0m", handler)
        draft_class = code[code.index("private sealed class SpecificationTestDraft"):]
        self.assertIn("public decimal MinimumElapsedHours { get; set; } = 0m;", draft_class)

    def test_draft_resave_preserves_original_creator_identity(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        start = code.index("private void BtnSaveSpecification_Click")
        end = code.index("private void BtnReviewSpecification_Click", start)
        handler = code[start:end]
        self.assertIn("SELECT TOP(1) CreatedBy, CreatedDate", handler)
        self.assertIn("draftCreatedBy", handler)
        self.assertIn("draftCreatedDate", handler)
        self.assertIn("CreatedBy,CreatedDate", handler)
        self.assertNotIn('insert.Parameters.Add("@User"', handler)


if __name__ == "__main__":
    unittest.main()
