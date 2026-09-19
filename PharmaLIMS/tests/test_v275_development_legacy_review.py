from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class DevelopmentLegacyReviewTests(unittest.TestCase):
    def test_development_legacy_snapshot_gaps_are_informational_only(self):
        source = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('AppConfig.IsDevelopment ? "Info" : "Medium"', source)
        self.assertIn("Legacy Development certificate records predate immutable-snapshot control", source)
        self.assertIn("Legacy Development PRM documents predate immutable-snapshot control", source)
        self.assertIn("disposable Development test data", source)
        self.assertIn("do not migrate these records into Production", source)

    def test_post_control_snapshot_defects_remain_high(self):
        source = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('Add(results, "High", "Certificates"', source)
        self.assertIn('Add(results, "High", "PRM Reports"', source)
        self.assertIn("Post-control certificates without snapshots", source)
        self.assertIn("Post-control PRM documents without snapshots", source)

    def test_production_legacy_wording_and_reconciliation_path_remain_present(self):
        source = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("Legacy active certificates predate the recorded immutable-snapshot control activation", source)
        self.assertIn("Legacy PRM documents predate the recorded immutable-snapshot control activation", source)
        self.assertIn("Legacy Certificate Evidence Reconciliation", source)

    def test_preflight_keeps_legacy_test_data_nonblocking_only_in_development(self):
        source = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn('if (AppConfig.IsDevelopment)', source)
        self.assertIn('"PASS", "Development legacy PRM evidence"', source)
        self.assertIn('"PASS", "Development legacy certificate evidence"', source)
        self.assertIn('"WARNING", "Legacy PRM certificate evidence"', source)
        self.assertIn('"WARNING", "Legacy certificate snapshot evidence"', source)
        self.assertIn("Post-control defects remain BLOCKER findings", source)

    def test_development_review_reports_current_post_control_health_separately(self):
        source = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("Current certificate snapshot control shows no post-control gap", source)
        self.assertIn("Current PRM immutable-snapshot control shows no post-control gap", source)
        self.assertIn("missingWaterSnapshotsAfterControl == 0", source)
        self.assertIn("missingPrmSnapshotsAfterControl == 0", source)


if __name__ == "__main__":
    unittest.main()
