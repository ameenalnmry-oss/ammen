import re
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[1] / "LabResources.xaml.cs")


class Eq001ProductionSigningGuardTests(unittest.TestCase):
    def test_production_guard_precedes_local_signature_and_sql_save(self):
        source = SOURCE.read_text(encoding="utf-8-sig")
        start = source.index("private void BtnSave_Click(")
        end = source.index("private void ", start + 1)
        handler = source[start:end]
        guard = re.search(
            r"if\s*\(AppConfig\.IsProduction\)\s*\{(?P<body>.*?)\}",
            handler, re.S,
        )
        self.assertIsNotNone(guard, "Production equipment change must fail closed")
        self.assertIn("return;", guard.group("body"))
        self.assertIn("Trusted Signing Required", guard.group("body"))
        self.assertLess(guard.start(), handler.index("new ElectronicSignature("))
        self.assertLess(guard.start(), handler.index("SaveEquipment(signature)"))

    def test_production_does_not_rely_on_local_password_validation(self):
        source = SOURCE.read_text(encoding="utf-8-sig")
        handler = source.split("private void BtnSave_Click(", 1)[1].split("private void ", 1)[0]
        self.assertIn("if (AppConfig.IsProduction)", handler)
        self.assertIn("SaveEquipment(signature);", handler)
        self.assertIn("INSERT dbo.LabEquipmentSignatures", source)


if __name__ == "__main__":
    unittest.main()
