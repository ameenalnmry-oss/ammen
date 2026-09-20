from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class CertificateJsonTransportV268Tests(unittest.TestCase):
    def test_certificate_issue_concatenates_every_for_json_chunk_before_validation(self):
        source = (ROOT / "DatabaseHelper.Certificates.cs").read_text(encoding="utf-8-sig")
        issue = source.split("public static string IssueCertificateAtomic(", 1)[1].split(
            "public static string IssueCertificate(", 1
        )[0]

        self.assertIn("FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES", issue)
        self.assertIn("using SqlDataReader snapshotReader = snapshotCommand.ExecuteReader();", issue)
        self.assertIn("while (snapshotReader.Read())", issue)
        self.assertIn("snapshotBuilder.Append(snapshotReader.GetString(0));", issue)
        self.assertIn("snapshotContent = snapshotBuilder.ToString();", issue)
        self.assertNotIn("snapshotCommand.ExecuteScalar()", issue)
        self.assertLess(issue.index("snapshotBuilder.ToString()"), issue.index("ValidateCertificateSnapshotJson(snapshotContent"))
        self.assertLess(issue.index("ValidateCertificateSnapshotJson(snapshotContent"), issue.index("SHA256.Create()"))
        self.assertLess(issue.index("SHA256.Create()"), issue.index("INSERT dbo.CertificateDocumentSnapshots"))

    def test_quality_event_structured_evidence_concatenates_every_for_json_chunk(self):
        source = (ROOT / "QualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")
        block = source.split("private string CaptureStructuredEvidenceSnapshot(", 1)[1].split(
            "private void RecordStructuredEvidenceHistory(", 1
        )[0]

        self.assertIn("using SqlDataReader reader = command.ExecuteReader();", block)
        self.assertIn("while (reader.Read())", block)
        self.assertIn("json.Append(reader.GetString(0));", block)
        self.assertNotIn("command.ExecuteScalar()", block)
        self.assertIn('return json.Length == 0 ? "[]" : json.ToString();', block)

    def test_v268_transport_regression_keeps_controlled_migration_set_unchanged(self):
        import json
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.18.294", manifest["applicationVersion"])
        self.assertEqual(83, len(manifest["migrations"]))
        auth_entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260911_000")
        self.assertEqual(
            "832ffb73859f2db064043bfe72e04744242ea5a645eec9c07ead919d558859b8",
            auth_entry["sha256"],
        )


if __name__ == "__main__":
    unittest.main()
