"""v260 source-contract regression tests.

These inspect the production sources; they do not execute C#, WPF, or SQL Server.
Behavioral tests live in PharmaLIMS.ReviewRegression and DatabaseIntegration.
"""
import hashlib
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
def source(relative):
    return (ROOT / relative).read_text(encoding="utf-8-sig")
def family(relative):
    p = ROOT / relative
    return "\n".join(x.read_text(encoding="utf-8-sig") for x in [p, *sorted(p.parent.glob(p.stem + ".Part*.cs"))])
def method_section(text, start, end):
    return text[text.index(start):text.index(end,text.index(start))]

class ReviewRemediationContracts(unittest.TestCase):
    def test_f01_air_volume_is_read_only_one_way(self):
        text=source("EMResultsEntry.xaml")
        column=re.search(r'<DataGridTextColumn[^>]*Header="Air Volume L"[^>]*/>',text).group(0)
        self.assertIn('IsReadOnly="True"',column)
        self.assertIn('Mode=OneWay',column)

    def test_f01_save_never_trusts_ui_volume_or_result(self):
        text=source("EMResultsEntry.xaml.Part3.cs")
        save=text[text.index("private string PersistEmResultsInTransaction"):]
        for untrusted in ["item.AirVolumeLiters","item.AlertLimit","item.ActionLimit","item.ResultCFU","item.Status"]:
            self.assertNotIn(untrusted,save)
        self.assertIn("CalculateLockedPlate(source, item.TotalCount)",save)

    def test_f01_evidence_is_locked_with_parent_and_set(self):
        text=source("EMResultsEntry.xaml.Part3.cs")
        self.assertIn('PlateSnapshotSql(true)',text)
        self.assertIn('EmLimitEvidenceSql.Joins(forUpdate)',text)
        self.assertIn('UPDLOCK, HOLDLOCK',text)

    def test_f02_decimal_storage_and_parameter_match(self):
        calc=source("Services/EmResultCalculator.cs")
        save=source("EMResultsEntry.xaml.Part3.cs")
        self.assertIn("Precision = 28",calc)
        self.assertIn("Scale = 12",calc)
        self.assertIn("Precision = EmResultCalculator.Precision, Scale = EmResultCalculator.Scale",save)
        self.assertNotIn("Convert.ToInt32(Math.Round",family("EMResultsEntry.xaml.cs"))

    def test_f02_decision_uses_rational_comparison(self):
        calc=source("Services/EmResultCalculator.cs")
        self.assertIn("numerator > action.Value * denominator",calc)
        self.assertIn("numerator > alert.Value * denominator",calc)
        self.assertNotIn("storedValue.Value > action",calc)

    def test_f02_trends_exclude_mismatched_stored_evidence(self):
        text=source("Services/EmTrendAssessmentService.cs")
        self.assertIn("stored.Value != computed.Value.Value",text)
        self.assertIn('row["StoredStatus"]',text)
        self.assertIn('row["Result"] = DBNull.Value',text)
        self.assertIn('row["RecalculatedResult"]',text)

    def test_f02_stored_decimal_count_is_validated_before_conversion(self):
        text=source("Services/EmResultCalculator.cs")
        self.assertIn("decimal.Truncate(value) != value",text)
        self.assertIn("value > int.MaxValue",text)
        self.assertIn("TryReadStoredCount",source("Services/EmTrendAssessmentService.cs"))
        self.assertIn('EmResultCalculator.ReadStoredCount(row["TotalCount"])',family("EMResultsEntry.xaml.cs"))

    def test_f03_complete_set_snapshot_is_copied_at_load(self):
        self.assertIn("_loadedPlateSnapshot = dt.Copy()",family("EMResultsEntry.xaml.cs"))
        text=source("Services/ResultSnapshotGuard.cs")
        self.assertIn("loaded.Rows.Count != current.Rows.Count",text)
        self.assertIn("foreach (DataColumn column in loaded.Columns)",text)
        self.assertIn("row[owner]",text)

    def test_f03_snapshot_guard_precedes_any_plate_write(self):
        text=source("EMResultsEntry.xaml.Part3.cs")
        self.assertLess(text.index("ResultSnapshotGuard.EnsureMatches"),text.index("UPDATE dbo.EM_EventPlates"))
        self.assertLess(text.index("EnsureVisibleKeys"),text.index("UPDATE dbo.EM_EventPlates"))

    def test_f03_plate_compare_and_swap_binds_parent(self):
        text=source("EMResultsEntry.xaml.Part3.cs")
        self.assertIn("WHERE Id=@plateId AND EventId=@eventId AND ResultRowVersion=@expectedVersion",text)
        self.assertIn("if (delta.Rows.Count != 1)",text)

    def test_f03_audit_uses_output_inside_transaction(self):
        text=source("EMResultsEntry.xaml.Part3.cs")
        self.assertIn("deleted.ResultCFU",text)
        self.assertIn("inserted.ResultCFU",text)
        self.assertIn("INTO @Changes",text)
        self.assertNotIn("GetOldValue",text)
        self.assertIn("connection, transaction, \"EM_EventPlates\"",text)

    def test_f03_preserves_other_count_evidence(self):
        text=source("EMResultsEntry.xaml.Part3.cs")
        update=text[text.index("UPDATE dbo.EM_EventPlates"):text.index("OUTPUT CONCAT",text.index("UPDATE dbo.EM_EventPlates"))]
        self.assertNotIn("FungalCount=",update)
        self.assertNotIn("CorrectedCount=",update)

    def test_f03_event_summary_comes_from_saved_complete_set(self):
        text=source("EMResultsEntry.xaml.Part3.cs")
        self.assertIn("DataTable saved = ReadEmRows",text)
        self.assertIn("foreach (DataRow row in saved.Rows)",text)
        self.assertIn("EmResultCalculator.StoredEvidenceMatches",text)
        self.assertIn("savedStatuses.Add(expected.Status)",text)
        self.assertIn("EmResultCalculator.Aggregate(savedStatuses)",text)
        self.assertLess(text.index("StoredEvidenceMatches"),text.index("UPDATE dbo.EM_Events"))
        self.assertIn("WHERE Id=@eventId AND ResultRowVersion=@expectedVersion",text)

    def test_f04_water_uses_separate_immutable_snapshot(self):
        text=family("ResultsEntry.xaml.cs")
        self.assertIn("_loadedWaterSnapshot = dt.Copy()",text)
        self.assertIn("_loadedWaterDisplayValues",text)
        self.assertIn("ResultSnapshotGuard.EnsureMatches(_loadedWaterSnapshot",text)

    def test_f04_notes_only_does_not_set_result_columns(self):
        text=source("ResultsEntry.xaml.Part3.cs")
        self.assertIn(': "Remarks=@remarks"',text)
        self.assertIn("object resultValue = source[\"ResultValue\"]",text)
        self.assertIn("if (inputEdited)",text)
        self.assertNotIn("EnteredDate=",text)

    def test_f04_water_compare_and_swap_and_atomic_audit(self):
        text=source("ResultsEntry.xaml.Part3.cs")
        self.assertIn("WHERE SampleTestID=@sampleTestId AND SampleID=@sampleId AND ResultRowVersion=@expectedVersion",text)
        self.assertIn("deleted.ResultValue",text)
        self.assertIn("inserted.ResultValue",text)
        self.assertIn("if (delta.Rows.Count != 1)",text)

    def test_f04_water_timing_is_not_rewritten_on_notes(self):
        text=family("ResultsEntry.xaml.cs")
        self.assertIn("if (pendingCount == 0 && resultEvidenceChanged)",text)
        self.assertIn("AND AnalysisCompletedDateTime IS NULL",text)

    def test_f04_precision_not_silently_rounded(self):
        text=source("ResultsEntry.xaml.cs")
        self.assertIn("WaterResultValueContract.IsExactlyRepresentable(normalizedResult)",text)
        contract=source("Services/WaterResultValueContract.cs")
        self.assertIn("decimal.Round(value, 4, MidpointRounding.AwayFromZero) == value",contract)
        self.assertIn("value >= -MaximumStoredMagnitude && value <= MaximumStoredMagnitude",contract)
        self.assertIn("99999999999999.9999m",contract)

    def test_f05_parser_consumes_all_characters(self):
        text=source("Services/PrmNumericSpecificationEvaluator.cs")
        self.assertIn("while (remaining.Length > 0)",text)
        self.assertIn("op.Index != 0",text)
        self.assertIn("if (!connector.Success) return ClauseParseResult.Invalid",text)
        self.assertNotIn(".Where(ContainsKnownRuleSyntax)",text)

    def test_f05_unknown_specification_is_not_discarded(self):
        text=source("Services/PrmNumericSpecificationEvaluator.cs")
        self.assertIn("StripTestLabel(text).Length == 0",text)
        self.assertIn("text.Length > 4096",text)

    def test_f05_invalid_ranges_and_annotations_fail_closed(self):
        text=source("Services/PrmNumericSpecificationEvaluator.cs")
        self.assertIn("if (first > second) return false",text)
        self.assertIn("annotated != constraint.First",text)

    def test_f06_structured_limit_checks_canonical_interval(self):
        text=source("Services/PrmNumericSpecificationEvaluator.cs")
        self.assertIn("TryGetInterval(parsed.Constraints",text)
        self.assertIn("upper.Value != structuredLimit.Value || !upperInclusive",text)
        self.assertIn("lowClosed && highClosed",text)

    def test_f07_user_load_carries_database_version(self):
        text=source("Repositories/UserRepository.cs")
        self.assertGreaterEqual(text.count("PasswordHashNew, PasswordSalt, AuthenticationRowVersion"),2)
        self.assertIn('SqlDbType.Binary, 8',text)
        self.assertIn("committed.Rows.Count != 1",text)

    def test_f07_single_success_update_is_guarded(self):
        text=source("Services/AuthenticationCommitContract.cs").split("internal const string Sql",1)[1].split('";',1)[0]
        self.assertEqual(text.count("UPDATE dbo.Users"),1)
        self.assertIn("AuthenticationRowVersion=@ExpectedVersion",text)
        self.assertIn("ISNULL(IsActive,1)=1",text)
        self.assertIn("LockedUntil>SYSDATETIME()",text)
        self.assertIn("OUTPUT inserted.AuthenticationRowVersion INTO @Committed",text)

    def test_f07_password_upgrade_shares_cas(self):
        contract=source("Services/AuthenticationCommitContract.cs")
        self.assertIn("PasswordHashNew = CASE WHEN @Upgrade=1",contract)
        self.assertIn("PasswordSalt = CASE WHEN @Upgrade=1",contract)
        repo=source("Repositories/UserRepository.cs")
        self.assertNotIn("UpdatePasswordHashAsync",repo)
        self.assertNotIn("UpdateLoginInfoAsync",repo)

    def test_f07_failure_cannot_weaken_admin_lock(self):
        repo=source("Repositories/UserRepository.cs")
        part=method_section(repo,"public async Task<int> RegisterAuthenticationFailureAsync","public async Task<string> GetRoleAsync")
        self.assertIn("AuthenticationCommitContract.FailureSql",part)
        contract=source("Services/AuthenticationCommitContract.cs").split("internal const string FailureSql",1)[1]
        self.assertIn("AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))",contract)

    def test_f08_permissions_are_checked_inside_save(self):
        text=source("EMResultsEntry.xaml.Part3.cs")
        self.assertLess(text.index('"CanEnterResults"'),text.index("UPDATE dbo.EM_EventPlates"))
        self.assertLess(text.index('"CanAccessEM"'),text.index("UPDATE dbo.EM_EventPlates"))
        self.assertIn("signature.SignedBy",text)

    def test_f08_permission_reader_has_matching_lock_projection(self):
        text=source("DatabaseHelper.SecurityAudit.cs")
        part=method_section(text,"internal static string EnsureUserPermissionInTransaction","internal static string EnsureQaApprovalAuthorizationInTransaction")
        self.assertIn("END AS IsLockedNow",part)
        self.assertIn("reader.GetValue(3)",part)
        self.assertIn("!isActive || isLocked",part)
        self.assertIn("UPDLOCK, HOLDLOCK",part)

    def test_f09_shared_evidence_in_entry_trend_preflight(self):
        self.assertIn("EmLimitEvidenceSql.Joins",family("EMResultsEntry.xaml.cs"))
        self.assertIn("EmLimitEvidenceSql.Joins()",source("EMTrendReport.xaml.cs"))
        self.assertIn("EmLimitEvidenceSql.Joins()",source("Infrastructure/SystemPreflightService.cs"))

    def test_f09_latest_reconciliation_not_older_valid_fallback(self):
        text=source("Services/EmLimitEvidenceSql.cs")
        self.assertIn("ORDER BY X.ReconciliationID DESC",text)
        selector=text[text.index("SELECT TOP(1)"):text.index(") R")]
        self.assertNotIn("ReconciliationSchemaVersion=1",selector)
        self.assertIn("R.ReconciliationSchemaVersion=1",text)

    def test_f09_reconciliation_source_is_visible(self):
        text=source("EMTrendReport.xaml.cs")
        self.assertIn("EVID.ReconciliationID, EVID.EvidenceSource, EVID.EvidenceComplete",text)
        self.assertIn("EmTrendAssessmentService.Apply(loaded)",text)

    def test_f09_sql_plate_alias_matches_shared_fragment(self):
        text=source("EMTrendReport.xaml.cs")
        self.assertIn("FROM dbo.EM_EventPlates P",text)
        self.assertNotIn("FROM dbo.EM_EventPlates p\n",text)

    def test_migration_additive_number_hash_and_types(self):
        manifest=json.loads(source("Database/MigrationManifest.json"))
        self.assertEqual(85, len(manifest["migrations"]))
        remediation=next(item for item in manifest["migrations"] if item["versionKey"]=="20260910_000")
        sql=(ROOT/"Database"/remediation["file"]).read_bytes()
        self.assertEqual(hashlib.sha256(sql).hexdigest(),remediation["sha256"])
        text=sql.decode()
        self.assertIn("system_type_id<>189",text)
        self.assertIn("ResultCFU decimal(28,12)",text)
        self.assertIn("name=N'ResultCalculationVersion' AND system_type_id=52",text)
        self.assertNotRegex(text,r"(?i)\bUPDATE\s+(?:dbo\.)?EM_EventPlates\b")

    def test_preflight_checks_real_rowversion_not_binary(self):
        text=source("Infrastructure/SystemPreflightService.cs")
        self.assertIn("HasReviewRemediationSchema",text)
        self.assertIn("c.system_type_id<>189",text)
        self.assertIn("precision=28 AND scale=12",text)

    def test_pure_project_links_production_not_copied_evaluator(self):
        text=source("tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj")
        for name in ["PrmNumericSpecificationEvaluator","EmResultCalculator","ResultSnapshotGuard","EmTrendAssessmentService"]:
            self.assertIn("../../Services/"+name+".cs",text)
        self.assertNotIn("PackageReference",text)

    def test_sql_project_exercises_shared_auth_evidence_contracts(self):
        text=source("tests/PharmaLIMS.DatabaseIntegration/ReviewRemediationIntegration.cs")
        self.assertIn("AuthenticationCommitContract.Sql",text)
        self.assertIn("EmLimitEvidenceSql.Joins(true)",text)
        self.assertIn("ex.Number==54907",text)
        self.assertIn('StartsWith("PharmaLIMS_Integration_"',text)

    def test_release_gate_checks_native_exit_and_labels_skips(self):
        text=source("scripts/Invoke-ReleaseValidation.ps1")
        self.assertIn("$LASTEXITCODE -ne 0",text)
        self.assertIn("Assert-NativeExit 'WPF Release build'",text)
        self.assertIn("Assert-NativeExit 'Disposable SQL Server integration executable'",text)
        self.assertIn("not a completed release-validation gate",text)

if __name__ == "__main__":
    unittest.main()
