from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class V272CertificateCompletenessCompatibilityTests(unittest.TestCase):
    def test_certificate_gate_does_not_treat_blank_legacy_status_as_incomplete(self):
        source = (ROOT / "DatabaseHelper.Certificates.cs").read_text(encoding="utf-8-sig")
        gate = source.split("private static string ValidateCertificateIssuanceGatesInTransaction", 1)[1]
        self.assertIn("LegacyStatusGapCount", gate)
        self.assertIn("IN (N'PENDING',N'PARTIALLY ENTERED')", gate)
        self.assertNotIn("IN (N'',N'PENDING',N'PARTIALLY ENTERED')", gate)
        self.assertIn("ValidateLegacyCompletedResultStatusGapsInTransaction", gate)

    def test_legacy_blank_status_is_reassessed_fail_closed_without_mutating_results(self):
        source = (ROOT / "DatabaseHelper.Certificates.cs").read_text(encoding="utf-8-sig")
        self.assertIn("AssessLegacyCompletedWaterResultStatus", source)
        self.assertIn('derivedStatus.Equals("OOS"', source)
        self.assertIn('derivedStatus.Equals("INVALID"', source)
        self.assertIn("Use the controlled result-correction / investigation workflow and obtain QA re-approval", source)
        self.assertIn("Legacy ResultStatus Compatibility Check", source)
        self.assertIn("no approved source result was modified", source)
        helper = source.split("private static void ValidateLegacyCompletedResultStatusGapsInTransaction", 1)[1].split(
            "private static string ValidateCertificateIssuanceGatesInTransaction", 1
        )[0]
        self.assertNotIn("UPDATE dbo.SampleTests", helper)

    def test_explicit_pending_states_still_block_certificate_issuance(self):
        source = (ROOT / "DatabaseHelper.Certificates.cs").read_text(encoding="utf-8-sig")
        self.assertIn("N'PENDING',N'PARTIALLY ENTERED'", source)
        self.assertIn("All assigned tests must have completed results before certificate issuance.", source)


if __name__ == "__main__":
    unittest.main()
