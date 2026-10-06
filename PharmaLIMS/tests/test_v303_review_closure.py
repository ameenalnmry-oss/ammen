import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


def source(name):
    return (ROOT / name).read_text(encoding="utf-8-sig")


def body(name, start, end):
    return source(name).split(start, 1)[1].split(end, 1)[0]


class V303ReviewClosureTests(unittest.TestCase):
    def test_water_entry_and_commit_share_nonnegative_complete_limit_rules(self):
        entry = source("ResultsEntry.xaml.cs")
        commit = source("ResultsEntry.xaml.Part3.cs")
        self.assertIn("WaterNumericResultEvaluator.TryParseNonnegative", entry)
        self.assertEqual(2, entry.count("WaterNumericResultEvaluator.TryParseBinary"))
        self.assertIn("WaterNumericResultEvaluator.Evaluate", entry)
        self.assertIn('status == "Invalid" || status == "NOT ASSESSED"', commit)
        self.assertLess(commit.index('status == "NOT ASSESSED"'), commit.index("UPDATE dbo.SampleTests"))

    def test_numeric_master_is_checked_at_review_and_registration(self):
        for name in ("WaterTestProfileManagement.xaml.cs", "NewSampleDialog.xaml.cs"):
            self.assertIn("WaterNumericResultEvaluator.RequiresNumericLimits", source(name))
            self.assertIn("WaterNumericResultEvaluator.HasCompleteLimits", source(name))

    def test_water_registration_captures_approved_specification_after_authorization_inside_transaction(self):
        code = source("NewSampleDialog.xaml.cs")
        capture = code.index("GetEffectiveWaterTestSpecification(sampleType, testId, pointCode, con, tran)")
        self.assertLess(code.rfind("BeginTransaction", 0, capture), capture)
        self.assertGreater(code.rfind("EnsureUserPermissionInTransaction", 0, capture), code.rfind("BeginTransaction", 0, capture))
        self.assertLess(capture, code.index("tran.Commit()", capture))
        helper = body("DatabaseHelper.cs", "public static DataTable GetEffectiveWaterTestSpecification", "public static")
        self.assertIn("WaterTestProfiles profile WITH(UPDLOCK,HOLDLOCK)", helper)
        self.assertIn("WaterSpecifications specification WITH(UPDLOCK,HOLDLOCK)", helper)
        self.assertIn("new SqlCommand(query, connection, transaction)", helper)
        self.assertIn("specification.ApprovalStatus = N'Approved'", helper)

    def test_native_water_trend_uses_exact_frozen_ranges(self):
        code = source("ReportsTrends.xaml.cs")
        self.assertIn("TrendReportData.WaterStatusExact", code)
        normalization = body("ReportsTrends.xaml.cs", "private static void NormalizeInternalWaterRows", "private static DateTime")
        self.assertIn('row["_SnapshotAlert"]', normalization)
        self.assertIn('row["_SnapshotAction"]', normalization)
        self.assertIn('row["Status"] = "NOT ASSESSED"', normalization)

    def test_external_import_blocks_unsupported_ranges_at_preview_and_commit(self):
        code = source("Services/ExternalTrendImportService.cs")
        self.assertEqual(2, code.count('moduleName == "Water" && WaterNumericResultEvaluator.IsRangeTest'))
        self.assertIn("ExternalTrendNumericContract.TryParseQualified", code)
        self.assertIn("ExternalTrendNumericContract.IsExactlyRepresentable(row.ResultValue)", code)
        self.assertIn("result.Scale = 10", code)

    def test_manual_em_investigation_reauthorizes_and_locks_before_creating_evidence(self):
        creation = body("EMResultsEntry.xaml.Part2.cs", "private bool CreateEMQualityEventFromCurrentResults", "private void AddInsertValue")
        for token in ("CanEnterResults", "CanAccessEM", "WITH(UPDLOCK,HOLDLOCK)", "EmQualityEventEvidenceGuard.EnsureSaved", "BuildSavedQualityEventEvidence(locked)", "FindLinkedQualityEvent(conn, tx)"):
            self.assertIn(token, creation)
        self.assertLess(creation.index("ExecuteInTransaction"), creation.index("EnsureUserPermissionInTransaction"))
        self.assertLess(creation.index("EnsureSaved"), creation.index("FindLinkedQualityEvent(conn, tx)"))
        self.assertLess(creation.index("FindLinkedQualityEvent(conn, tx)"), creation.index("INSERT INTO dbo.QualityEvents"))
        self.assertIn("InsertEMQualityEventAffectedResults(createdQualityEventId, conn, tx, savedEvidence)", creation)

    def test_em_affected_evidence_has_source_plate_identity(self):
        code = body("EMResultsEntry.xaml.Part2.cs", "private int InsertEMQualityEventAffectedResults", "private")
        self.assertIn('"SourceModule"', code)
        self.assertIn('"SourceResultID"', code)
        self.assertIn("item.PlateId", code)
        self.assertNotIn("plateItems", code)
        calculation = body("EMResultsEntry.xaml.Part3.cs", "private static List<EMPlateResultItem> BuildSavedQualityEventEvidence", "private string")
        self.assertIn("EmResultCalculator.StoredEvidenceMatches", calculation)

    def test_prm_results_use_the_same_presence_and_numeric_evaluator_as_regression(self):
        self.assertIn("PrmResultInterpretationEvaluator.Evaluate", source("ProductionRawMaterialResults.xaml.Part2.cs"))
        self.assertIn("TryGetRequiredPresence", source("ProductionRawMaterialSamples.xaml.cs"))
        self.assertIn("bottle", source("Services/PrmNumericSpecificationEvaluator.cs"))

    def test_new_prm_analysis_receipt_guard_precedes_timestamp_write(self):
        code = body("ProductionRawMaterialResults.xaml.cs", "private bool TryRecordPrmAnalysisStart", "private")
        self.assertLess(code.index("LaboratoryReceiptSql.GuardPrmAnalysisStart"), code.index("analysisStartedAt = databaseNow"))
        self.assertLess(code.index("existingStart != null"), code.index("LaboratoryReceiptSql.GuardPrmAnalysisStart"))
        self.assertIn("await V303ReviewClosureIntegration.VerifyAsync", source("tests/PharmaLIMS.DatabaseIntegration/ReviewRemediationIntegration.cs"))

    def test_gpt_release_and_entry_compare_original_counts(self):
        code = source("CultureMediaPreparation.xaml.Part2.cs")
        self.assertEqual(2, code.count("MediaGrowthPromotionEvaluator.Passes("))
        self.assertIn("recoveryPercent != decimal.Round(exactRecovery, 2", code)

    def test_unsigned_release_cannot_pass_and_sbom_reports_actual_signature(self):
        sign = source("scripts/Sign-PublishedArtifact.ps1")
        self.assertIn("throw 'Production release requires Authenticode signing", sign)
        self.assertNotIn("skipped", sign.lower())
        sbom = source("scripts/New-ResolvedSbom.ps1")
        self.assertIn("Get-AuthenticodeSignature", sbom)
        self.assertIn("$signature.Status", sbom)
        self.assertNotIn("Signed self-contained", sbom)
        self.assertIn("./tests/Test-CodeSigningReleaseGuard.ps1", source("scripts/Invoke-ReleaseValidation.ps1"))

    def test_version_and_historical_migration_identity_remain_controlled(self):
        manifest = json.loads(source("Database/MigrationManifest.json"))
        self.assertEqual("2026.10.6.303", manifest["applicationVersion"])
        self.assertIn("20260908_000", source("Infrastructure/SystemPreflightService.cs"))


if __name__ == "__main__":
    unittest.main()
