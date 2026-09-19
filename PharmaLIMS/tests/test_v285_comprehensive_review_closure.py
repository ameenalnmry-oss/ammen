from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
def text(p): return (ROOT/p).read_text(encoding="utf-8-sig")

class V285ComprehensiveReviewClosureTests(unittest.TestCase):
    def test_f01_missing_tables_are_reported_and_required(self):
        contract=text("Infrastructure/ComplianceRecordProtectionContract.cs")
        preflight=text("Infrastructure/SystemPreflightService.cs")
        self.assertIn("TableExists", contract)
        self.assertNotIn("WHERE OBJECT_ID", contract)
        self.assertIn("CultureMediaPrintHistory", preflight)
        self.assertIn("EM_ScheduleSignatures", preflight)
        self.assertIn('row["TableExists"]', preflight)

    def test_f02_behavioral_append_only_integration_exists(self):
        p=text("tests/PharmaLIMS.DatabaseIntegration/Program.cs")
        self.assertIn("deceptive non-protective body", p)
        self.assertIn("Controlled AuditTrail append-only trigger did not behaviorally reject both UPDATE and DELETE", p)
        self.assertIn("CultureMediaPrintHistory_v285_missing_test", p)

    def test_f03_ai_review_uses_authoritative_contract(self):
        p=text("AISystemReview.xaml.cs")
        self.assertIn("ComplianceRecordProtectionContract.QuerySql", p)
        self.assertNotIn("TRG_' + table_info.name + N'_AppendOnly", p)

    def test_f04_production_smoke_uses_sha256_row_fingerprint(self):
        p=text("tests/PharmaLIMS.ProductionArtifactSmoke/Program.cs")
        self.assertIn("DATA-SHA256", p)
        self.assertIn("SHA256.HashData", p)
        self.assertNotIn("CHECKSUM_AGG", p)
        self.assertNotIn("BINARY_CHECKSUM", p)

    def test_f05_ci_generates_resolved_sbom(self):
        ci=text(".github/workflows/ci.yml")
        script=text("scripts/New-ResolvedSbom.ps1")
        self.assertIn("New-ResolvedSbom.ps1", ci)
        self.assertIn("dotnet list $Project package --include-transitive --format json", script)
        self.assertIn("SBOM.cdx.json", ci)

    def test_f06_actions_are_sha_pinned(self):
        ci=text(".github/workflows/ci.yml")
        self.assertIsNone(re.search(r"uses:\s+actions/[^@]+@v\d", ci))
        for action in ["actions/checkout@", "actions/setup-dotnet@", "actions/upload-artifact@", "actions/attest-build-provenance@"]:
            line=next(x.strip() for x in ci.splitlines() if action in x)
            sha=line.split("@",1)[1].split()[0]
            self.assertRegex(sha, r"^[0-9a-f]{40}$")

    def test_f07_non_event_loaders_return_task(self):
        prm=text("PRMQualityEventInvestigation.xaml.cs")
        water=text("ResultsEntry.xaml.cs")
        self.assertIn("private async Task LoadEventAsync()", prm)
        self.assertNotIn("private async void LoadEvent()", prm)
        self.assertIn("private async Task LoadSampleAsync(string sampleNumber)", water)
        self.assertNotIn("private async void LoadSample(string sampleNumber)", water)
        self.assertNotIn("private async void LoadSampleFromUi", water)

if __name__ == '__main__': unittest.main()
