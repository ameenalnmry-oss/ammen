import hashlib
import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


class V290DeepReviewClosureTests(unittest.TestCase):
    def test_v289_signature_migration_bytes_remain_historical_and_unchanged(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        entry = next(x for x in manifest["migrations"] if x["versionKey"] == "20260917_001")
        path = ROOT / "Database" / entry["file"]
        expected = "a6143e2bdbdb56570a7bf6057249866f2e63a048a033f18b7beb2f220dcc338a"
        self.assertEqual(expected, hashlib.sha256(path.read_bytes()).hexdigest())
        self.assertEqual(expected, entry["sha256"])

    def test_user_update_noop_is_rejected_before_mutation_audit_or_signature(self):
        service = text("Services/UserAdministrationService.cs")
        method_start = service.index("public async Task UpdateUserAsync(")
        noop = service.index("No user-account changes were detected", method_start)
        update = service.index("UPDATE dbo.Users", method_start)
        audit = service.index('"User Account Updated"', method_start)
        signature = service.index('"User Account and Permission Update"', method_start)
        self.assertLess(noop, update)
        self.assertLess(noop, audit)
        self.assertLess(noop, signature)
        self.assertIn("Nothing was saved, audited, or electronically signed", service)

    def test_startup_and_preflight_enforce_exact_signature_schema_contract(self):
        verify = text("Infrastructure/StartupDatabaseMigrator.Verification.cs")
        preflight = text("Infrastructure/SystemPreflightService.cs")
        required = [
            "name=N'SignatureID' AND system_type_id=TYPE_ID(N'bigint') AND max_length=8 AND is_nullable=0 AND is_identity=1",
            "name=N'ActionReason' AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=2000 AND is_nullable=0",
            "name=N'SignedAt' AND system_type_id=TYPE_ID(N'datetime2') AND max_length=6 AND scale=0 AND is_nullable=0",
            "FK_UserAdministrationSignatures_TargetUser",
            "is_disabled=0 AND fk.is_not_trusted=0",
            "CK_UserAdministrationSignatures_ActionReason_Specific",
            "CK_UserAdministrationSignatures_Meaning_NotBlank",
            "CK_UserAdministrationSignatures_SignedBy_NotBlank",
            "sys.default_constraints",
            "SYSUTCDATETIME",
            "IX_UserAdministrationSignatures_Target_SignedAt",
            "TRG_UserAdministrationSignatures_AppendOnly",
        ]
        for token in required:
            self.assertIn(token, verify)
            self.assertIn(token, preflight)

    def test_database_integration_contains_exact_signature_schema_gate(self):
        integration = text("tests/PharmaLIMS.DatabaseIntegration/Program.cs")
        self.assertIn("VerifyUserAdministrationSignatureEvidenceSchemaAsync(databaseConnectionString)", integration)
        self.assertIn("UserAdministrationSignatures exact schema contract failed after controlled migrations", integration)
        self.assertIn("User administration signature exact-schema integration PASS", integration)


if __name__ == "__main__":
    unittest.main()
