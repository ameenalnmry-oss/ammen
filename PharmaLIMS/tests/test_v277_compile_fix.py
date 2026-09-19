import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


class V277CompileFixTests(unittest.TestCase):
    def test_system_preflight_cultureinfo_is_fully_qualified(self):
        path = ROOT / "Infrastructure" / "SystemPreflightService.cs"
        text = path.read_text(encoding="utf-8-sig")
        self.assertIn("System.Globalization.CultureInfo.InvariantCulture", text)
        bare = re.findall(r"(?<!System\.Globalization\.)\bCultureInfo\.", text)
        self.assertEqual([], bare)

    def test_compile_fix_is_preserved_in_current_release(self):
        manifest = (ROOT / "Database" / "MigrationManifest.json").read_text(encoding="utf-8-sig")
        self.assertIn('"applicationVersion": "2026.9.18.294"', manifest)
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        self.assertIn("<Version>2026.9.18.294</Version>", project)


if __name__ == "__main__":
    unittest.main()
