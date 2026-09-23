import json
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


class V278ReviewCleanReleaseTests(unittest.TestCase):
    def test_release_identity_and_current_artifacts_are_v278(self):
        migration_manifest = json.loads((ROOT / "Database" / "MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", migration_manifest["applicationVersion"])
        self.assertTrue((ROOT / "RELEASE_NOTES_2026.9.23.295.md").is_file())
        self.assertTrue((ROOT / "SBOM_2026.9.23.295.cdx.json").is_file())
        self.assertFalse((ROOT / "RELEASE_NOTES_2026.9.14.277.md").exists())
        self.assertFalse((ROOT / "SBOM_2026.9.14.277.cdx.json").exists())

    def test_report_certificate_print_permission_is_checked_twice(self):
        text = (ROOT / "ReportCertificate.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("bool canPrint = await Task.Run(CanPrint);", text)
        self.assertIn("BtnPrint.IsEnabled = canPrint;", text)
        self.assertIn("if (!CanPrint())", text)
        self.assertIn("You do not have permission to print controlled reports.", text)

    def test_report_certificate_uses_authenticated_username_for_authorization_identity(self):
        text = (ROOT / "ReportCertificate.xaml.cs").read_text(encoding="utf-8-sig")
        match = re.search(r"private string GetCurrentUserName\(\)\s*\{(?P<body>.*?)\n\s*\}", text, re.S)
        self.assertIsNotNone(match)
        body = match.group("body")
        self.assertIn("Login.CurrentUser", body)
        self.assertNotIn("Login.CurrentUserFullName", body)

    def test_cultureinfo_compile_fix_is_preserved(self):
        text = (ROOT / "Infrastructure" / "SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("System.Globalization.CultureInfo.InvariantCulture", text)
        self.assertEqual([], re.findall(r"(?<!System\.Globalization\.)\bCultureInfo\.", text))


if __name__ == "__main__":
    unittest.main()
