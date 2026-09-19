from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class V276PrmProfileSignatureEvidenceTests(unittest.TestCase):
    def test_authoritative_profile_predicate_requires_review_and_approval_signatures(self):
        repository = (ROOT / "Repositories/PrmSpecificationRepository.cs").read_text(encoding="utf-8-sig")
        predicate = repository.split("ApprovedProfileEvidencePredicateSql", 1)[1].split("private readonly DatabaseConnection", 1)[0]

        self.assertIn("PRM_SpecificationSignatures reviewSig", predicate)
        self.assertIn("PRM_SpecificationSignatures approveSig", predicate)
        self.assertIn("reviewSig.ActionType=N'Review Specification'", predicate)
        self.assertIn("approveSig.ActionType=N'Approve Specification'", predicate)
        self.assertIn("reviewSig.SignedBy)))=UPPER(LTRIM(RTRIM(configured.ReviewedBy", predicate)
        self.assertIn("approveSig.SignedBy)))=UPPER(LTRIM(RTRIM(configured.ApprovedBy", predicate)
        self.assertIn("reviewSig.SignedAt <= approveSig.SignedAt", predicate)
        self.assertIn("@RequireIndependentApprover=0", predicate)
        self.assertIn("reviewSig.SignedBy)))<>UPPER(LTRIM(RTRIM(approveSig.SignedBy", predicate)

    def test_registration_and_assignment_consume_only_profiles_with_signature_evidence(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/PrmSpecificationRepository.cs").read_text(encoding="utf-8-sig")

        self.assertGreaterEqual(registration.count("PrmSpecificationRepository.ApprovedProfileEvidencePredicateSql"), 3)
        self.assertGreaterEqual(registration.count('@RequireIndependentApprover"'), 3)
        self.assertGreaterEqual(repository.count("+ ApprovedProfileEvidencePredicateSql +"), 4)
        self.assertIn('command.Parameters.Add("@RequireIndependentApprover", SqlDbType.Bit).Value = AppConfig.IsProduction;', repository)
        self.assertIn("An Approved status without matching Review/Approve specification-signature evidence is intentionally excluded from registration.", registration)

    def test_production_scope_ranking_prefers_more_specific_profile(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        choices = registration.split("private int LoadApprovedSpecificationChoices()", 1)[1].split(
            "private static string BuildNoApprovedProfileMessage", 1
        )[0]

        self.assertIn("THEN 2 ELSE 0 END", choices)
        self.assertIn("THEN 1 ELSE 0 END", choices)
        self.assertNotIn("THEN 2 ELSE 1 END", choices)
        self.assertIn("WHERE MatchRank=(SELECT MAX(MatchRank) FROM Candidates)", choices)

    def test_preflight_surfaces_active_approved_profiles_without_signature_evidence(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        block = preflight.split('"PRM approved-profile signature evidence"', 1)[1]

        self.assertIn("unsupportedApprovedProfiles", block)
        self.assertIn("PRM_SpecificationSignatures reviewSig", block)
        self.assertIn("PRM_SpecificationSignatures approveSig", block)
        self.assertIn('AppConfig.IsProduction ? "BLOCKER" : "WARNING"', block)
        self.assertIn("These versions are excluded from new PRM registration", block)


if __name__ == "__main__":
    unittest.main()
