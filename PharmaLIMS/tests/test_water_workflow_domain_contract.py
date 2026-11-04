"""Source/installed-policy parity for the pure trusted Water domain.

Optional --site-root validates the immutable installed source supplied by the site.
The executable companion tests exercise the actual pure scientific/evidence rules.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import unittest
import sys

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(add_help=False)
parser.add_argument("--site-root", type=Path)
ARGS, remaining = parser.parse_known_args()
SITE = ARGS.site_root
sys.argv[1:] = remaining


def methods(source):
    found = []
    for match in re.finditer(r"\b(?:private|internal) (?:bool|int|void|string) (\w+)\([^\n]*", source):
        opening = source.index("{", match.start())
        depth, closing = 1, opening + 1
        while depth:
            if source[closing] == "{":
                depth += 1
            elif source[closing] == "}":
                depth -= 1
            closing += 1
        signature = source[match.start():opening].replace("private ", "internal ", 1).replace("ResultItem", "WaterWorkflowScientificItem")
        found.append((re.sub(r"\s+", " ", signature).strip(), source[opening:closing]))
    return found


class DomainContract(unittest.TestCase):
    def test_validator_is_pure(self):
        sources = "\n".join(p.read_text() for p in (ROOT / "Services").glob("WaterWorkflowValidator*.cs"))
        for forbidden in ["SqlCommand", "ExecuteNonQuery", "ExecuteReader", "SqlConnection", "SqlTransaction", "DatabaseHelper", "DateTime.Now", "DateTime.UtcNow"]:
            self.assertNotIn(forbidden, sources)

    def test_snapshot_is_common_query_select_only(self):
        source = (ROOT / "Services/WaterWorkflowSnapshot.cs").read_text()
        self.assertIn("WaterWorkflowSqlContract.SnapshotQuery", source)
        self.assertIn('"WITH (HOLDLOCK)"', source)
        for forbidden in ["UPDLOCK", "UPDATE dbo.", "INSERT dbo.", "DELETE FROM"]:
            self.assertNotIn(forbidden, source)
        self.assertIn("public static WaterWorkflowSnapshot FromReader(SqlDataReader reader)", source)
        self.assertIn("i is 5 or 6", source)  # Entire catalogs are validated under owner locks, not clinical hash inputs.
        self.assertIn("i<13", source)  # SQL clock never participates in the clinical hash.

    def test_remote_shape_has_no_authoritative_plan_fields(self):
        source = (ROOT / "tools/PharmaLIMS.TrustedSigning/WaterWorkflowRequest.cs").read_text()
        request = source[source.index("public sealed record WaterWorkflowRequest"):source.index("public sealed record WaterWorkflowResult")]
        for forbidden in ["ResultStatus", "AlertLimit", "ActionLimit", "TestName", "UserRole", "OriginalDisplay", "MutationPlan", "IsEdited"]:
            self.assertNotIn(forbidden, request)
        self.assertEqual(source.count("JsonUnmappedMemberHandling.Disallow"), 3)

    @unittest.skipUnless(SITE, "--site-root enables supplied installed source parity")
    def test_all_differing_water_helpers_preserve_installed_bytes(self):
        for item in json.loads((SITE / "metadata.json").read_text()):
            if item["path"].startswith("Services/Water") and not item["cloud_equal"]:
                expected = (SITE / item["path"]).read_bytes()
                actual = (ROOT / item["path"]).read_bytes()
                self.assertEqual(hashlib.sha256(actual).hexdigest(), hashlib.sha256(expected).hexdigest(), item["path"])

    @unittest.skipUnless(SITE, "--site-root enables supplied installed scientific parity")
    def test_scientific_methods_are_exact_installed_extraction(self):
        installed = dict(methods((SITE / "ResultsEntry.xaml.cs").read_text()))
        extracted = methods((ROOT / "Services/WaterWorkflowScientific.cs").read_text())
        self.assertGreaterEqual(len(extracted), 18)
        for signature, body in extracted:
            self.assertIn(signature, installed)
            self.assertEqual(body, installed[signature], signature)


if __name__ == "__main__":
    unittest.main()
