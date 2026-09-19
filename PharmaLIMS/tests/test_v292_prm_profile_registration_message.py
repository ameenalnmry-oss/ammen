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


if __name__ == "__main__":
    unittest.main()
