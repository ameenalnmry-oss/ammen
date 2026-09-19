import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class CertificateAuthorizationSnapshotV269Tests(unittest.TestCase):
    def test_issue_role_is_captured_from_transaction_authorization_read(self):
        source = (ROOT / "DatabaseHelper.Certificates.cs").read_text(encoding="utf-8-sig")
        gate = source.split("private static string ValidateCertificateIssuanceGatesInTransaction", 1)[1].split(
            "public static string IssueCertificateAtomic", 1
        )[0]
        issue = source.split("public static string IssueCertificateAtomic", 1)[1].split(
            "public static string IssueCertificate(", 1
        )[0]
        self.assertIn("WITH (UPDLOCK,HOLDLOCK)", gate)
        self.assertIn("authorizedRole = reader.GetString(4).Trim()", gate)
        self.assertIn("return authorizedRole;", gate)
        self.assertNotIn("GetUserRole(effectiveUser)", issue)
        self.assertIn("userRole = ValidateCertificateIssuanceGatesInTransaction", issue)
        self.assertIn("@IssuedRole", issue)
        self.assertIn("@Role", issue)


if __name__ == "__main__":
    unittest.main()
