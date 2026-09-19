import hashlib
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


class V266AuthenticationSchemaDriftRepairTests(unittest.TestCase):
    def test_original_auth_migration_bytes_and_checksum_are_preserved(self):
        manifest = json.loads(source("Database/MigrationManifest.json"))
        entry = next(m for m in manifest["migrations"] if m["versionKey"] == "20260911_000")
        migration = ROOT / "Database" / entry["file"]
        digest = hashlib.sha256(migration.read_bytes()).hexdigest()
        self.assertEqual("832ffb73859f2db064043bfe72e04744242ea5a645eec9c07ead919d558859b8", digest)
        self.assertEqual(digest, entry["sha256"])

    def test_startup_compatibility_widens_legacy_nvarchar_auth_columns_without_changing_nullability(self):
        migrator = source("Infrastructure/StartupDatabaseMigrator.cs") + source("Infrastructure/StartupDatabaseMigrator.Part2.cs")
        self.assertIn("AuthenticationLoginCompatibilityMigrationKey", migrator)
        self.assertIn("ALTER TABLE dbo.Users ALTER COLUMN PasswordHashNew NVARCHAR(512) NULL", migrator)
        self.assertIn("ALTER TABLE dbo.Users ALTER COLUMN PasswordHashNew NVARCHAR(512) NOT NULL", migrator)
        self.assertIn("ALTER TABLE dbo.Users ALTER COLUMN PasswordSalt NVARCHAR(256) NULL", migrator)
        self.assertIn("ALTER TABLE dbo.Users ALTER COLUMN PasswordSalt NVARCHAR(256) NOT NULL", migrator)
        self.assertIn("max_length<1024", migrator)
        self.assertIn("max_length<512", migrator)

        auth_block = source("Infrastructure/StartupDatabaseMigrator.Part2.cs").split(
            "private static async Task PrepareAuthenticationLoginCompatibilityAsync", 1
        )[1]
        hash_block = auth_block.split("PasswordHashNew is not NVARCHAR", 1)[1].split("PasswordSalt is not NVARCHAR", 1)[0]
        salt_block = auth_block.split("PasswordSalt is not NVARCHAR", 1)[1].split("FailedLoginAttempts is not INT", 1)[0]
        for block in (hash_block, salt_block):
            self.assertIn("is_nullable=1", block)
            self.assertIn("is_nullable=0", block)
            self.assertIn(" NULL;", block)
            self.assertIn(" NOT NULL;", block)

    def test_startup_compatibility_normalizes_legacy_null_security_state_fail_safe(self):
        migrator = source("Infrastructure/StartupDatabaseMigrator.cs") + source("Infrastructure/StartupDatabaseMigrator.Part2.cs")
        self.assertIn("SET FailedLoginAttempts=0", migrator)
        self.assertIn("WHERE FailedLoginAttempts IS NULL", migrator)
        self.assertIn("ALTER TABLE dbo.Users ALTER COLUMN FailedLoginAttempts INT NOT NULL", migrator)
        self.assertIn("SET IsLocked=0", migrator)
        self.assertIn("WHERE IsLocked IS NULL", migrator)
        self.assertIn("ALTER TABLE dbo.Users ALTER COLUMN IsLocked BIT NOT NULL", migrator)
        auth_block = source("Infrastructure/StartupDatabaseMigrator.Part2.cs").split(
            "private static async Task PrepareAuthenticationLoginCompatibilityAsync", 1
        )[1]
        self.assertNotIn("PasswordHashNew=", auth_block)
        self.assertNotIn("PasswordSalt=", auth_block)
        self.assertIn("SET IsActive=0", auth_block)
        self.assertIn("WHERE IsActive IS NULL", auth_block)
        self.assertIn("DECLARE PermissionColumns CURSOR", auth_block)
        self.assertIn("=0 WHERE ", auth_block)
        self.assertNotIn("SET IsActive=1", auth_block)
        self.assertNotIn("CanAccessWater=1", auth_block)

    def test_auth_compatibility_runs_before_original_checksum_controlled_sql(self):
        migrator = source("Infrastructure/StartupDatabaseMigrator.cs")
        call = "await PrepareManifestMigrationCompatibilityAsync(versionKey, connection, transaction)"
        execute = "string migrationSql = System.Text.Encoding.UTF8.GetString(migrationBytes)"
        self.assertIn(call, migrator)
        self.assertIn(execute, migrator)
        self.assertLess(migrator.index(call), migrator.index(execute))

    def test_recorded_auth_postcondition_requires_final_widths(self):
        migrator = source("Infrastructure/StartupDatabaseMigrator.cs")
        post = migrator.split('"20260911_000" => @"', 1)[1].split('"20260906_005" => @"', 1)[0]
        self.assertIn("max_length=-1 OR max_length>=1024", post)
        self.assertIn("max_length=-1 OR max_length>=512", post)


if __name__ == "__main__":
    unittest.main()
