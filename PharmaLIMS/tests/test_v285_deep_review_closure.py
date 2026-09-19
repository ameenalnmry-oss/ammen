import json
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def text(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


class V284DeepReviewClosureTests(unittest.TestCase):
    def test_f01_exact_append_only_contract_is_shared_and_fail_closed(self):
        contract = text("Infrastructure/ComplianceRecordProtectionContract.cs")
        preflight = text("Infrastructure/SystemPreflightService.cs")
        self.assertIn("ComplianceRecordProtectionContract.QuerySql", preflight)
        self.assertIn("triggerRow.name = protected.TriggerName", contract)
        self.assertIn("triggerRow.is_disabled = 0", contract)
        self.assertIn("ev.type_desc = N'UPDATE'", contract)
        self.assertIn("ev.type_desc = N'DELETE'", contract)
        self.assertIn("HasCanonicalAppendOnlyBody", contract)
        self.assertIn('AppConfig.IsProduction ? "BLOCKER" : "WARNING"', preflight)
        for table in (
            "CertificateDocumentSnapshots", "CertificatePrintHistory", "CultureMediaPrintHistory",
            "EM_PlanSignatures", "EM_ScheduleSignatures", "PRM_CertificateSnapshots",
            "PRM_SpecificationSignatures", "QualityEventPrintHistory", "QualityEventSignatures",
            "Water_PlanSignatures",
        ):
            self.assertIn(table, contract)

    def test_f01_database_integration_tampers_exact_trigger_and_leaves_decoy(self):
        program = text("tests/PharmaLIMS.DatabaseIntegration/Program.cs")
        project = text("tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj")
        self.assertIn("ComplianceRecordProtectionContract.cs", project)
        self.assertIn("VerifyComplianceProtectionTamperDetectionAsync", program)
        self.assertIn("DISABLE TRIGGER dbo.[{expectedTrigger}]", program)
        self.assertIn("TRG_v285_AuditTrail_Decoy", program)
        self.assertIn("AFTER INSERT", program)
        self.assertIn("A decoy trigger incorrectly satisfied", program)
        self.assertIn("ENABLE TRIGGER dbo.[{expectedTrigger}]", program)

    def test_f02_exact_published_production_artifact_has_separate_smoke_gate(self):
        smoke = text("tests/PharmaLIMS.ProductionArtifactSmoke/Program.cs")
        runner = text("scripts/Invoke-ProductionArtifactSmoke.ps1")
        release_runner = text("scripts/Invoke-ReleaseValidation.ps1")
        self.assertIn("--app <published PharmaLIMS.exe>", smoke)
        self.assertIn('Environment = "Production"', smoke)
        self.assertIn("PHARMALIMS_PRODUCTION_SMOKE_CONNECTION_STRING", smoke)
        self.assertIn("PHARMALIMS_RUNTIME_SMOKE_READY_EVENT", smoke)
        self.assertIn("CaptureDatabaseFingerprintAsync", smoke)
        self.assertIn("database fingerprint changed", smoke)
        self.assertIn("Invoke-ProductionArtifactSmoke", release_runner)
        self.assertIn("does NOT prove the published Production", release_runner)
        self.assertIn("ProductionArtifactSmoke", runner)

    def test_f03_production_session_timeout_is_bounded_and_visible_in_preflight(self):
        app_config = text("AppConfig.cs")
        preflight = text("Infrastructure/SystemPreflightService.cs")
        self.assertIn("MinimumSessionTimeoutMinutes = 5", app_config)
        self.assertIn("MaximumSessionTimeoutMinutes = 60", app_config)
        self.assertIn("SessionTimeoutMinutes < MinimumSessionTimeoutMinutes", app_config)
        self.assertIn("SessionTimeoutMinutes > MaximumSessionTimeoutMinutes", app_config)
        self.assertIn("Session timeout policy", preflight)
        self.assertIn("controlled range", preflight)

    def test_f04_culture_media_repository_exposes_only_named_operations(self):
        interface = text("Interfaces/ICultureMediaRepository.cs")
        repository = text("Repositories/CultureMediaRepository.cs")
        ui = "\n".join(text(name) for name in (
            "CultureMediaPreparation.xaml.cs",
            "CultureMediaPreparation.xaml.Part2.cs",
            "CultureMediaPreparation.xaml.Part3.cs",
        ))
        for forbidden in ("Query(string sql", "Execute(string sql", "Scalar(string sql", "EnsureMediaMaster("):
            self.assertNotIn(forbidden, interface)
        self.assertIn("enum CultureMediaQuery", interface)
        self.assertIn("enum CultureMediaScalar", interface)
        self.assertIn("enum CultureMediaCommand", interface)
        self.assertIn("private static string SqlFor(CultureMediaQuery", repository)
        self.assertIn("private static string SqlFor(CultureMediaScalar", repository)
        self.assertIn("private static string SqlFor(CultureMediaCommand", repository)
        self.assertNotRegex(ui, r"_repository\.(?:Query|Execute|Scalar)\s*\(")

    def test_f05_ci_signs_smokes_hashes_and_attests_before_upload(self):
        ci = text(".github/workflows/ci.yml")
        sign_at = ci.index("Authenticode-sign published first-party binaries")
        smoke_at = ci.index("Smoke exact signed Production artifact")
        manifest_at = ci.index("Generate and verify publish hash manifest and provenance")
        attest_at = ci.index("Attest publish manifest provenance")
        upload_at = ci.index("Upload signed, smoke-tested package")
        self.assertLess(sign_at, smoke_at)
        self.assertLess(smoke_at, manifest_at)
        self.assertLess(manifest_at, attest_at)
        self.assertLess(attest_at, upload_at)
        self.assertIn("PHARMALIMS_CODESIGN_PFX_BASE64", ci)
        self.assertIn("PHARMALIMS_PRODUCTION_SMOKE_CONNECTION_STRING", ci)
        self.assertIn("actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be", ci)
        self.assertIn("id-token: write", ci)
        self.assertIn("attestations: write", ci)
        for script in (
            "scripts/Sign-PublishedArtifact.ps1",
            "scripts/New-PublishManifest.ps1",
            "scripts/Test-PublishManifest.ps1",
        ):
            self.assertTrue((ROOT / script).is_file(), script)

    def test_f05_publish_manifest_is_path_safe_and_hash_verified(self):
        verifier = text("scripts/Test-PublishManifest.ps1")
        generator = text("scripts/New-PublishManifest.ps1")
        signer = text("scripts/Sign-PublishedArtifact.ps1")
        self.assertIn("Get-FileHash -Algorithm SHA256", verifier)
        self.assertIn("GetFullPath", verifier)
        self.assertIn("escapes publish directory", verifier)
        self.assertIn("Get-FileHash -Algorithm SHA256", generator)
        self.assertIn("PUBLISH_PROVENANCE.json is covered by PUBLISH_MANIFEST_SHA256.txt", generator)
        self.assertIn("signtool", signer.lower())
        self.assertIn(" verify ", signer)

    def test_release_identity_is_v285(self):
        manifest = json.loads(text("Database/MigrationManifest.json"))
        self.assertEqual("2026.9.18.294", manifest["applicationVersion"])
        self.assertIn("<Version>2026.9.18.294</Version>", text("PharmaLIMS.csproj"))
        self.assertIn('AssemblyVersion("2026.9.18.294")', text("AssemblyInfo.cs"))
        self.assertTrue((ROOT / "RELEASE_NOTES_2026.9.18.294.md").is_file())
        self.assertTrue((ROOT / "SBOM_2026.9.18.294.cdx.json").is_file())


if __name__ == "__main__":
    unittest.main()
