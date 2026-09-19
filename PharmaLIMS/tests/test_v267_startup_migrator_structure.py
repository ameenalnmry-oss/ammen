from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Infrastructure" / "StartupDatabaseMigrator.Part2.cs"


class StartupMigratorStructureV267Tests(unittest.TestCase):
    def test_auth_compatibility_method_starts_after_previous_method_closes(self):
        source = SOURCE.read_text(encoding="utf-8-sig")
        expected_boundary = (
            "            await command.ExecuteNonQueryAsync().ConfigureAwait(false);\n"
            "        }\n\n"
            "        private static async Task PrepareAuthenticationLoginCompatibilityAsync("
        )
        self.assertIn(expected_boundary, source)

    def test_file_closes_method_class_and_namespace_once(self):
        source = SOURCE.read_text(encoding="utf-8-sig").rstrip()
        self.assertTrue(
            source.endswith(
                "            await command.ExecuteNonQueryAsync().ConfigureAwait(false);\n"
                "        }\n"
                "    }\n"
                "}"
            )
        )


if __name__ == "__main__":
    unittest.main()
