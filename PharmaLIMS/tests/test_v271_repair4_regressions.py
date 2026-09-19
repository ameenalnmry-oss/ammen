import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


class Repair4RegressionTests(unittest.TestCase):
    def test_auth_width_repair_preserves_existing_nullability(self):
        migrator = source("Infrastructure/StartupDatabaseMigrator.Part2.cs")
        block = migrator.split("private static async Task PrepareAuthenticationLoginCompatibilityAsync", 1)[1]

        self.assertIn("PasswordHashNew NVARCHAR(512) NULL", block)
        self.assertIn("PasswordHashNew NVARCHAR(512) NOT NULL", block)
        self.assertIn("PasswordSalt NVARCHAR(256) NULL", block)
        self.assertIn("PasswordSalt NVARCHAR(256) NOT NULL", block)

        hash_section = block.split("PasswordHashNew is not NVARCHAR", 1)[1].split("PasswordSalt is not NVARCHAR", 1)[0]
        salt_section = block.split("PasswordSalt is not NVARCHAR", 1)[1].split("FailedLoginAttempts is not INT", 1)[0]
        for section in (hash_section, salt_section):
            self.assertIn("is_nullable=1", section)
            self.assertIn("is_nullable=0", section)

    def test_mixed_classification_is_visible_but_not_comparable(self):
        service = source("Services/ExternalTrendThreeCycleService.cs")
        get_areas = service.split("public DataTable GetAreas()", 1)[1].split("public DataTable GetMethods", 1)[0]
        self.assertIn('population = mixed', get_areas)
        self.assertIn('"Mixed classification"', get_areas)
        self.assertIn('bool isComparable = !mixed;', get_areas)
        self.assertIn('result.Rows.Add(group.Key, areaName, locationName, population, classification, isComparable);', get_areas)
        self.assertNotIn('population, classification, true);', get_areas)


if __name__ == "__main__":
    unittest.main()
