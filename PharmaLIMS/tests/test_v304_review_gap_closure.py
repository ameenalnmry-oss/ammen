import hashlib
import json
import re
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(name):
    return (ROOT / name).read_text(encoding="utf-8-sig")


class Review304WiringContracts(unittest.TestCase):
    def test_gpt_start_uses_the_bound_shared_insert(self):
        code = source("CultureMediaPreparation.xaml.cs") + source("CultureMediaPreparation.xaml.Part2.cs")
        self.assertIn("CultureQualificationStartContract.Sql", code)
        self.assertIn("CultureQualificationStartContract.Parameters", code)
        contract = source("Services/CultureQualificationStartContract.cs")
        self.assertIn('new SqlParameter("@PerformedBy"', contract)
        self.assertIn("SYSDATETIME(), @MinimumIncubationHours", contract)

    def test_water_changed_result_records_signer_and_database_time(self):
        code = source("ResultsEntry.xaml.Part3.cs")
        self.assertIn("EnteredBy=@enteredBy, ResultEnteredDate=SYSDATETIME()", code)
        self.assertRegex(code, r'@enteredBy[^\n]+signature\.SignedBy')
        self.assertIn(': "Remarks=@remarks"', code)

    def test_water_locked_snapshot_contains_all_resource_identity(self):
        code = source("Services/WaterResultSnapshotSql.cs")
        for column in ("EquipmentHistoryID", "EquipmentID", "ResourceEvidenceID", "ExecutionEvidenceID", "EnteredBy", "ResultEnteredDate"):
            self.assertIn(column, code)
        self.assertEqual(4, code.count('+ hint + @" WHERE'))
        self.assertIn("h.ParentRecordID=st.SampleID", code)

    def test_prm_resource_snapshot_and_ui_have_same_owner(self):
        sql = source("Services/PrmSampleResultStateService.cs")
        ui = source("ProductionRawMaterialResults.xaml.cs")
        for code in (sql, ui):
            self.assertIn("AS EquipmentHistoryID", code)
            self.assertIn("h.ParentRecordID=st.SampleID", code)
        self.assertIn("usage.ParentRecordID=st.SampleID", ui)

    def test_water_preflight_executes_shared_policy(self):
        preflight = source("Infrastructure/SystemPreflightService.cs")
        self.assertIn("WaterEvidencePreflightSql.EquipmentUsage", preflight)
        self.assertIn("WaterEvidencePreflightSql.Les", preflight)
        sql = source("Services/WaterEvidencePreflightSql.cs")
        self.assertIn("h.SignedAt<=e.SignedAt", sql)
        self.assertIn("n.EvidenceID>e.EvidenceID", sql)
        self.assertIn("N'NO_INSTRUMENT'", sql)
        self.assertIn("TRY_CONVERT(decimal(18,4),r.ResultSnapshot) IS NULL", sql)

    def test_kit_expiry_uses_transaction_database_date(self):
        code = source("Services/WaterResourceEvidenceService.cs")
        append = code[code.index("public static void Append("):]
        self.assertIn("CAST(SYSDATETIME() AS date)", append)
        self.assertIn("databaseDate", append)
        self.assertIn("@ExecutionDate", append)
        self.assertNotIn("DateTime.Today", code)

    def test_resource_and_specification_history_are_protection_contracts(self):
        code = source("Infrastructure/ComplianceRecordProtectionContract.cs")
        self.assertIn("TRG_WaterResourceEvidence_AppendOnly", code)
        self.assertIn("TRG_PRM_SpecificationContentHistory_AppendOnly", code)
        self.assertIn("CheckReviewClosureSchemaAsync(checks)", source("Infrastructure/SystemPreflightService.cs"))

    def test_specification_signs_full_content_and_exact_version(self):
        code = source("ProductionRawMaterialSamples.xaml.cs")
        self.assertEqual(2, code.count("ResultSnapshotGuard.EnsureMatches(_loadedSpecificationSnapshot"))
        self.assertIn("SpecificationEvidenceService.EnsureReviewIndependent", code)
        self.assertIn("OUTPUT INSERTED.SignatureID", code)
        self.assertIn("signatureId", code)
        self.assertIn("@RequestedVersion", code)
        self.assertIn("SpecificationNumericStorage.EnsureExact(test.SpecificationLimit)", code)
        self.assertIn("SpecificationNumericStorage.EnsureExact(limit)", code)
        history = source("Services/SpecificationEvidenceService.cs")
        self.assertIn("SELECT s.*", history)
        self.assertIn("FOR JSON PATH,INCLUDE_NULL_VALUES", history)
        self.assertIn("SHA256.HashData(Encoding.UTF8.GetBytes(after))", history)

    def test_specification_review_excludes_every_draft_editor(self):
        code = source("Services/SpecificationEvidenceService.cs")
        self.assertIn("CreatedBy", code)
        self.assertIn("ActionType IN(N'Draft Created',N'Draft Updated')", code)
        self.assertIn("ChangedBy", code)

    def test_investigation_three_writers_validate_raw_evidence(self):
        code = source("PRMQualityEventInvestigation.xaml.cs")
        self.assertEqual(3, len(re.findall(r"CaptureInvestigationEvidence\((?:connection|conn),\s*(?:transaction|tx)\)", code)))
        self.assertIn("originalVisibleChecklist", code)
        self.assertIn("originalRawChecklist", code)
        self.assertIn("originalInvestigationHeader", code)
        service = source("Services/Investigations/PRMQualityEventInvestigationEvidence.cs")
        self.assertLess(service.index("if(originalVisibleChecklist!=null)"), service.index('if(value.Equals("N/A"'))
        self.assertIn("CONVERT(varbinary(max),AnswerValue)", service)
        self.assertIn("SqlDbType.NVarChar,-1", service)

    def test_prm_reissue_uses_both_fresh_permissions(self):
        code = source("ProductionRawMaterialResults.xaml.Part2.cs")
        self.assertIn("if (isReissue) ReviewWorkflowAuthorization.EnsurePrmReissue", code)
        auth = source("Services/ReviewWorkflowAuthorization.cs")
        self.assertIn("ISNULL(CanIssueCOA,0)=1 AND ISNULL(CanCancelCOA,0)=1", auth)
        self.assertIn("WITH (UPDLOCK, HOLDLOCK)", auth)

    def test_culture_entry_authorization_precedes_writes(self):
        code = source("CultureMediaPreparation.xaml.Part2.cs") + source("CultureMediaPreparation.xaml.cs")
        self.assertGreaterEqual(code.count("EnsureCultureMediaEntryAuthorizationInTransaction"), 2)
        auth = source("Services/ReviewWorkflowAuthorization.cs")
        self.assertIn("ISNULL(CanEnterResults,0)=1 OR ISNULL(CanRegisterSamples,0)=1", auth)
        self.assertIn("!active || mustChange || locked", auth)

    def test_signed_preparation_and_expected_version_guard(self):
        code = source("CultureMediaPreparation.xaml.Part2.cs")
        self.assertIn("CultureMediaPreparationGuard.EnsureAmendable", code)
        self.assertIn("WorkflowRowVersion=@ExpectedVersion", code)
        for field in ("VisualCheckedBy", "VisualCheckedAt", "SterilityReviewedBy", "SterilityReviewedAt"):
            self.assertRegex(code, r"(?:NULLIF\(LTRIM\(RTRIM\(" + field + r"\)\),N''\)|" + field + r") IS NULL")
        self.assertIn("WorkflowRowVersion", source("Repositories/CultureMediaRepository.cs"))

    def test_frozen_culture_identity_allows_stock_without_history_locks(self):
        migration = source("Database/Migrations/20261010_001_Review_Content_And_Identity_Protection.sql")
        lot = migration[migration.index("TRG_CultureMediaLots_ReferencedIdentity"):]
        self.assertLess(lot.index("RETURN;"), lot.index("FROM dbo.MediaQualifications"))
        self.assertIn("TRG_CultureMedia_ReferencedIdentity", migration)
        self.assertIn("Referenced culture media identity is frozen", migration)
        self.assertIn("CultureMediaWriteContract.GuardLotIdentity", source("CultureMediaPreparation.xaml.cs"))

    def test_em_event_operations_check_source_plan(self):
        helper = source("DatabaseHelper.EnvironmentalMonitoring.cs")
        for method in ("MarkEMResultsEntered", "MarkEMEventUnderInvestigation", "SubmitEMEventForReview", "ReviewEMEvent", "ApproveEMEvent"):
            match = re.search(r"public static (?:void|bool|int) " + method + r"\(", helper)
            self.assertIsNotNone(match, method)
            block = helper[match.start():]
            end = re.search(r"\n        public static ", block[1:])
            if end: block = block[:end.start() + 1]
            self.assertIn("EnsureEmSourcePlanInTransaction", block, method)
        self.assertIn("EnsureEmSourcePlanInTransaction", source("EMResultsEntry.xaml.Part3.cs"))
        self.assertIn("EnsureEmSourcePlanInTransaction", source("EMResultsEntry.xaml.Part2.cs"))

    def test_em_cancel_is_a_shared_transaction_sql_contract(self):
        code = source("DatabaseHelper.EmSourcePlan.cs")
        self.assertIn("EmSourcePlanSql.Cancel", code)
        self.assertIn("LockEmSourcePlanInTransaction(c,t,planId)", code)
        sql = source("Services/EmSourcePlanSql.cs")
        self.assertIn("UPDATE dbo.EM_Events SET WorkflowStatus=N'Cancelled'", sql)
        self.assertIn("LTRIM(RTRIM(E.FinalResult))", sql)
        self.assertIn("N'EM Plan Cancellation'", sql)
        self.assertIn("ApprovedBy IS NOT NULL", sql)

    def test_em_planning_takes_signer_lock_before_plan_lock(self):
        code = source("EMPlanning.xaml.cs")
        lines = code.splitlines()
        sites = [i for i, line in enumerate(lines) if "LockEmSourcePlanInTransaction(connection, transaction, plan.PlanID)" in line]
        self.assertEqual(6, len(sites))
        for i in sites: self.assertIn("EnsureActiveUserInTransaction", lines[i - 1])
        guard = source("DatabaseHelper.EmSourcePlan.cs")
        self.assertIn("LTRIM(RTRIM(WorkflowStatus))", guard)
        self.assertIn('reader.GetValue(2)', guard)

    def test_em_report_rechecks_saved_parent_and_plate_evidence(self):
        helper = source("DatabaseHelper.EnvironmentalMonitoring.cs")
        ui = source("EMResultsEntry.xaml.cs")
        self.assertIn("EmReportEvidenceGuard.EnsureConsistent(rows)", helper)
        self.assertIn("validateLoadedEvidence?.Invoke(conn, tx, rows)", helper)
        self.assertIn("EmQualityEventEvidenceGuard.EnsureEquipmentSaved", ui + source("EMResultsEntry.xaml.Part3.cs"))
        self.assertIn('row["FinalResult"]', source("Services/EmReportEvidenceGuard.cs"))
        printing = helper[helper.index("public static int AddEMEventSignature"):helper.index("public static void MarkEMResultsEntered")]
        self.assertIn('"EM Report Print"', printing)
        self.assertIn("AppConfig.IsProduction && !reportPrint", printing)
        self.assertIn('"CanAccessReports"', printing)

    def test_em_dirty_grid_submit_is_locked_to_captured_event(self):
        code = source("EMResultsEntry.xaml.cs")
        self.assertIn("EnsureEmVisibleEvidenceSaved", code)
        self.assertIn("_loadedPlateSnapshot?.Copy()", code)
        self.assertIn("var equipment = plateItems.ToDictionary", code)
        self.assertIn("ReadEmPlateSnapshotInTransaction", code)
        self.assertIn("CommitEmResultGridEdits", code)

    def test_em_personnel_handoff_uses_recorded_plan_identity(self):
        code = source("EMPlanning.xaml.cs")
        self.assertIn("EmPersonnelHandoffContract.EmployeeKey", code)
        self.assertIn("EmPersonnelHandoffContract.Validate", code)
        self.assertIn("EmployeeId,EmployeeName,FinalResult", code)
        self.assertIn('first.EmployeeID.Trim(), size: 50', code)
        self.assertIn('first.EmployeeName.Trim(), size: 150', code)

    def test_new_migration_is_additive_and_old_hashes_are_unchanged(self):
        manifest = json.loads(source("Database/MigrationManifest.json"))
        old = json.loads(subprocess.check_output(["git", "show", "c70f6107ed82bfee456a41768518991445ad6cd8:PharmaLIMS/Database/MigrationManifest.json"], cwd=ROOT))
        old_entries = {entry["versionKey"]: entry["sha256"] for entry in old["migrations"]}
        current_entries = {entry["versionKey"]: entry["sha256"] for entry in manifest["migrations"]}
        self.assertEqual(old_entries, {key: current_entries[key] for key in old_entries})
        self.assertEqual({"20261010_001"}, current_entries.keys() - old_entries.keys())
        entry = next(entry for entry in manifest["migrations"] if entry["versionKey"] == "20261010_001")
        self.assertEqual(entry["sha256"], hashlib.sha256((ROOT / "Database" / entry["file"]).read_bytes()).hexdigest())
        self.assertIn('"20261010_001" =>', source("Infrastructure/StartupDatabaseMigrator.cs"))


if __name__ == "__main__":
    unittest.main()
