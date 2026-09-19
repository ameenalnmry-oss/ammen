import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


class V283RuntimeSchemaGateClosureTests(unittest.TestCase):
    def test_runtime_smoke_waits_for_post_busy_readiness_signal(self):
        app = text("App.xaml.cs")
        smoke = text("tests/PharmaLIMS.RuntimeSmoke/Program.cs")
        self.assertIn("PHARMALIMS_RUNTIME_SMOKE_READY_EVENT", app)
        self.assertIn("SignalRuntimeSmokeLoginReady();", app)
        self.assertLess(app.index("loginWindow.SetStartupBusy(false);"), app.index("SignalRuntimeSmokeLoginReady();"))
        self.assertIn("ReadinessEventVariable", smoke)
        self.assertIn("WaitForInteractiveLoginAsync", smoke)
        self.assertIn("readinessEvent.WaitOne(0)", smoke)
        self.assertIn("readinessReceived && lastTitle.Equals(ExpectedLoginTitle", smoke)
        self.assertNotIn("WaitForLoginWindowAsync", smoke)

    def test_authentication_postcondition_enforces_permission_column_shape(self):
        migrator = text("Infrastructure/StartupDatabaseMigrator.cs")
        block = migrator.split('"20260911_000" => @"', 1)[1].split('"20260915_000" =>', 1)[0]
        self.assertIn("name=N'IsActive'", block)
        self.assertIn("system_type_id=TYPE_ID(N'bit') AND is_nullable=0", block)
        self.assertIn("N'CanAccessWater', N'CanAccessEM', N'CanRegisterSamples', N'CanEnterResults'", block)
        self.assertIn("N'CanAccessReports', N'CanManageUsers', N'CanManageSettings'", block)
        self.assertIn(") = 11", block)

    def test_release_identity_is_v283(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        self.assertEqual("2026.9.18.294", manifest["applicationVersion"])
        self.assertIn("<Version>2026.9.18.294</Version>", text("PharmaLIMS.csproj"))
        self.assertTrue((ROOT / "RELEASE_NOTES_2026.9.18.294.md").is_file())
        self.assertTrue((ROOT / "SBOM_2026.9.18.294.cdx.json").is_file())


if __name__ == "__main__":
    unittest.main()
