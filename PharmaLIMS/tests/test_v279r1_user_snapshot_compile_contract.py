import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Services" / "UserAdministrationService.cs"

class UserSnapshotCompileContractTests(unittest.TestCase):
    def test_user_snapshot_constructor_argument_count_matches_record(self):
        text = SOURCE.read_text(encoding="utf-8")
        record_match = re.search(r"private sealed record UserSnapshot\((.*?)\)\s*\{", text, re.S)
        self.assertIsNotNone(record_match, "UserSnapshot record declaration not found")
        record_params = [p.strip() for p in record_match.group(1).split(",") if p.strip()]
        self.assertEqual(22, len(record_params), "Unexpected UserSnapshot parameter count")

        ctor_match = re.search(r"UserSnapshot snapshot = new UserSnapshot\((.*?)\);", text, re.S)
        self.assertIsNotNone(ctor_match, "UserSnapshot constructor call not found")
        # The constructor expressions in this block do not contain nested commas.
        ctor_args = [a.strip() for a in ctor_match.group(1).split(",") if a.strip()]
        self.assertEqual(len(record_params), len(ctor_args), "UserSnapshot constructor arity drifted from record")

    def test_user_snapshot_reader_indexes_match_select_contract(self):
        text = SOURCE.read_text(encoding="utf-8")
        block = re.search(r"UserSnapshot snapshot = new UserSnapshot\((.*?)\);", text, re.S)
        self.assertIsNotNone(block)
        body = block.group(1)
        self.assertIn("reader.IsDBNull(18)", body)
        self.assertIn("reader.GetBoolean(18)", body)  # MustChangePassword
        self.assertIn("reader.IsDBNull(19) ? Array.Empty<byte>() : (byte[])reader[19]", body)
        self.assertIn("reader.IsDBNull(20)", body)
        self.assertIn("reader.GetInt32(20)", body)  # FailedLoginAttempts
        self.assertIn("reader.GetBoolean(21)", body)  # IsLockedNow
        self.assertNotIn("reader.GetBoolean(19)", body)
        self.assertNotIn("reader[20]", body)

if __name__ == "__main__":
    unittest.main()
