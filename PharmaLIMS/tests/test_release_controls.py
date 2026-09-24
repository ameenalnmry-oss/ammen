import hashlib
import json
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


DATABASE_HELPER_FILES = (
    "DatabaseHelper.cs",
    "DatabaseHelper.QualityEvents.cs",
    "DatabaseHelper.SecurityAudit.cs",
    "DatabaseHelper.EnvironmentalMonitoring.cs",
    "DatabaseHelper.Certificates.cs",
)


def read_database_helper_source() -> str:
    return "\n".join(
        (ROOT / relative).read_text(encoding="utf-8-sig")
        for relative in DATABASE_HELPER_FILES
    )


def read_source_family(relative: str) -> str:
    """Read one C# source file and its controlled partial continuation files."""
    path = ROOT / relative
    parts = [path]
    parts.extend(sorted(path.parent.glob(path.stem + ".Part*.cs")))
    return "\n".join(part.read_text(encoding="utf-8-sig") for part in parts if part.is_file())


class ReleaseControlsTests(unittest.TestCase):
    def test_every_sql_migration_is_controlled_and_hash_matches(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        entries = manifest["migrations"]
        listed = {Path(item["file"]).name: item for item in entries}
        actual = {path.name: path for path in (ROOT / "Database/Migrations").glob("*.sql")}
        self.assertEqual(set(actual), set(listed))
        self.assertEqual(len(entries), len(listed), "Migration version/file entries must be unique")
        for name, path in actual.items():
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            self.assertEqual(digest, listed[name]["sha256"].lower(), name)

    def test_results_workflow_uses_database_permissions_not_role_names(self):
        source = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        permission_calls = (
            "DatabaseHelper.CanEditResults(currentUser)",
            "DatabaseHelper.CanSubmitForReview(currentUser)",
            "DatabaseHelper.CanReviewResults(currentUser)",
            "DatabaseHelper.CanQaApproveResults(currentUser)",
            "DatabaseHelper.CanIssueCertificate(currentUser)",
            "DatabaseHelper.CanCancelCertificate(currentUser)",
        )
        for call in permission_calls:
            self.assertIn(call, source)
        self.assertNotRegex(source, r"private bool CanApproveSample\(\).*RoleIs")
        self.assertNotIn("private string GetCurrentRole()", source)
        self.assertIn("string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction", source)
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", source)
        self.assertIn("string signedBy,\n            string signerRole", source)

    def test_other_critical_workflows_use_fresh_database_permissions(self):
        expectations = {
            "CultureMediaPreparation.xaml.cs": (
                "DatabaseHelper.CanReviewResults(Login.CurrentUser)",
                "DatabaseHelper.CanQaApproveResults(Login.CurrentUser)",
                "DatabaseHelper.CanAccessReports(Login.CurrentUser)",
            ),
            "ExternalTrendImportDialog.xaml.cs": (
                "DatabaseHelper.CanEditResults(CurrentUser())",
                "DatabaseHelper.CanApproveResults(CurrentUser())",
            ),
            "ProductionRawMaterialResults.xaml.cs": (
                "DatabaseHelper.CanEditResults(GetCurrentUserDisplayName())",
                "DatabaseHelper.CanIssueCertificate(GetCurrentUserDisplayName())",
                "DatabaseHelper.CanCancelCertificate(GetCurrentUserDisplayName())",
            ),
        }
        for relative, required_calls in expectations.items():
            source = (ROOT / relative).read_text(encoding="utf-8-sig")
            for call in required_calls:
                self.assertIn(call, source, relative)

        planning = read_source_family("EMPlanning.xaml.cs")
        self.assertIn("DatabaseHelper.CanApproveResults(CurrentUser())", planning)
        self.assertNotIn('CurrentRole().Contains("QA"', planning)

        reports = read_source_family("ReportsTrends.xaml.cs")
        permission_block = reports.split("private bool UserHasPermission", 1)[1].split("#endregion", 1)[0]
        self.assertIn("DatabaseHelper.CanAccessReports(Login.CurrentUser)", permission_block)
        self.assertNotIn("Login.CurrentUserRole", permission_block)
        self.assertNotIn("Login.CanAccessReports", permission_block)

    def test_v152_prm_results_open_is_decoupled_from_quality_event_reconciliation(self):
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")

        results_sql = readiness.split("private const string ResultsReadinessSql", 1)[1].split(
            "private const string QualityEventCompatibilityReadySql", 1
        )[0]
        self.assertNotIn("QualityEvents", results_sql)
        self.assertNotIn("SourceModule", results_sql)
        self.assertNotIn("SourceRecordID", results_sql)
        self.assertNotIn("QualityEventAffectedResults", results_sql)
        self.assertNotIn("QualityEventActions", results_sql)
        self.assertNotIn("QualityEventChecklistAnswers", results_sql)
        self.assertNotIn("QualityEventPrintHistory", results_sql)

        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        approve = prm.split("private async void BtnApprove_Click", 1)[1].split(
            "private async void BtnIssueCertificate_Click", 1
        )[0]
        self.assertIn('bool isNonconforming = overall.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase);', approve)
        self.assertIn("bool hasAnyQualityEvent = HasAnyPrmQualityEventMinimal();", approve)
        self.assertIn("if (isNonconforming || hasAnyQualityEvent)", approve)
        self.assertIn("await EnsurePrmQualityEventSchemaReadyForActionAsync();", approve)
        self.assertIn("HasAnyPrmQualityEventMinimalInTransaction(conn, tx)", approve)
        self.assertLess(approve.index("EnsurePrmQualityEventSchemaReadyForActionAsync"), approve.index("GetPrmQualityEventState()"))
        self.assertNotIn("ApplyControlledMigrationAsync", prm)
        self.assertIn("HasAnyPrmQualityEventMinimal()", prm)
        self.assertIn("HasAnyPrmQualityEventMinimalInTransaction(conn, tx)", prm)

    def test_v157_prm_quality_event_schema_is_startup_owned_not_legacy_shimmed(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        development = json.loads((ROOT / "appsettings.Development.example.json").read_text(encoding="utf-8-sig"))
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        by_key = {item["versionKey"]: item for item in manifest["migrations"]}

        self.assertFalse(development["Runtime"]["ApplyStartupDatabaseUpdates"])
        self.assertNotIn("prmQualityEventLinkColumnsSql", migrator)
        self.assertNotIn("prmQualityEventReconciliationColumnsSql", migrator)
        self.assertIn("Retired PRM Quality Event reconciliation migrations are intentionally", migrator)
        self.assertNotIn("ApplyControlledMigrationAsync", readiness)

        current = by_key["20260826_001"]
        current_path = ROOT / "Database" / current["file"]
        current_sql = current_path.read_text(encoding="utf-8-sig")
        self.assertEqual(hashlib.sha256(current_path.read_bytes()).hexdigest(), current["sha256"].lower())
        self.assertNotRegex(current_sql, r"(?i)ALTER\s+TABLE[^;]+ALTER\s+COLUMN")
        for key in ("20260825_002", "20260825_003", "20260825_004", "20260825_005", "20260825_006", "20260824_003"):
            self.assertEqual("20260826_001", by_key[key].get("supersededBy"), key)

    def test_water_results_workflow_revalidates_state_inside_transaction(self):
        source = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("LockAndValidateSampleStatusInTransaction", source)
        self.assertIn("FROM dbo.Samples WITH (UPDLOCK, HOLDLOCK)", source)
        self.assertIn("AND ISNULL(LTRIM(RTRIM(Status)), N'') = @expectedStatus", source)
        self.assertIn('statusBeforeSave, "Result save"', source)
        self.assertIn('status, "Submit for review"', source)
        self.assertIn('status, "Technical review"', source)
        self.assertIn('status, "QA approval"', source)
        self.assertIn("GetPendingResultsCountInTransaction(con, tran)", source)

        guarded_calls = re.findall(r"UpdateSampleStatusInTransaction\(con, tran, [^;]+\);", source)
        self.assertGreaterEqual(len(guarded_calls), 5)
        for call in guarded_calls:
            self.assertGreaterEqual(call.count(","), 4, call)

    def test_em_critical_actions_do_not_use_production_role_bypass(self):
        em = read_source_family("EMResultsEntry.xaml.cs")
        helper = read_database_helper_source()

        self.assertNotIn("IsAdminUser()", em)
        self.assertIn('DatabaseHelper.CanEditResults(Login.CurrentUser ?? "")', em)
        self.assertIn('DatabaseHelper.CanSubmitForReview(Login.CurrentUser ?? "")', em)
        self.assertIn('DatabaseHelper.CanReviewResults(Login.CurrentUser ?? "")', em)
        self.assertIn('DatabaseHelper.CanQaApproveResults(Login.CurrentUser ?? "")', em)

        self.assertIn("EnsureUserPermissionInTransaction", helper)
        self.assertIn('effectiveSubmittedBy, "CanSubmitForReview", "submit EM results for review"', helper)
        self.assertIn('effectiveReviewedBy, "CanReviewResults", "review EM results"', helper)
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", helper)
        self.assertIn('effectiveApprovedBy, "approve EM results"', helper)
        self.assertIn("GetLockedEMWorkflowStatusInTransaction", helper)
        self.assertIn("FROM dbo.EM_Events WITH (UPDLOCK, HOLDLOCK)", helper)
        self.assertIn("HasEmSignatureInTransaction", helper)
        self.assertIn("EnsureEmApprovalQualityEventGateInTransaction", helper)
        self.assertIn("FROM dbo.EM_EventSignatures WITH (UPDLOCK, HOLDLOCK)", helper)
        self.assertIn("FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)", helper)
        self.assertIn('"EM Result Entry",\n                        "EM Submit for Review"', helper)
        self.assertIn('"EM Submit for Review",\n                        "EM Review"', helper)
        self.assertIn("Workflow action blocked", em)
        self.assertIn("if (updated != 1)", helper)

    def test_development_admin_override_is_feature_flag_controlled_everywhere(self):
        helper = read_database_helper_source()
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        em = read_source_family("EMResultsEntry.xaml.cs")
        culture = read_source_family("CultureMediaPreparation.xaml.cs")

        self.assertIn("return AppConfig.DevelopmentAdminFullPermissions && IsAdminUser();", prm)
        self.assertIn("if (!AppConfig.DevelopmentAdminFullPermissions)", em)
        self.assertIn("AppConfig.DevelopmentAdminFullPermissions && IsAdministrativeRole(role)", helper)
        self.assertIn("IsDevelopmentAdminOverrideForRole", culture)
        self.assertNotIn("isDevelopmentAdministrator = !AppConfig.IsProduction", helper)

    def test_culture_media_sod_and_permissions_are_revalidated_inside_transactions(self):
        source = read_source_family("CultureMediaPreparation.xaml.cs")

        for action in (
            "review culture media qualification",
            "release culture media lot",
            "sign prepared-media visual review",
            "sign prepared-media sterility review",
            "release prepared culture media",
            "reject culture media lot",
            "reject prepared culture media",
            "reconcile culture media stock",
        ):
            self.assertIn(action, source)

        self.assertGreaterEqual(source.count("EnsureQaApprovalAuthorizationInTransaction"), 5)
        self.assertGreaterEqual(source.count("WITH (UPDLOCK, HOLDLOCK)"), 5)
        self.assertIn("The qualification performer cannot perform the independent review.", source)
        self.assertIn("The final releaser must be independent of both the qualification performer and reviewer.", source)
        self.assertIn("The final releaser must be independent of the preparer, visual checker, and sterility reviewer.", source)

    def test_legacy_login_is_one_time_verified_migration_but_signature_never_accepts_legacy(self):
        auth = (ROOT / "Services/AuthService.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        commit = (ROOT / "Services/AuthenticationCommitContract.cs").read_text(encoding="utf-8-sig")

        authenticate = auth.split("public async Task<User?> AuthenticateAsync", 1)[1].split(
            "public async Task<bool> ValidateCurrentUserPasswordAsync", 1
        )[0]
        signature_validation = auth.split("public async Task<bool> ValidateCurrentUserPasswordAsync", 1)[1].split(
            "public User? GetCurrentUser()", 1
        )[0]

        self.assertNotIn("PasswordVerificationResult.ValidLegacy && AppConfig.IsProduction", authenticate)
        self.assertNotIn("Production sign-in blocked", authenticate)
        self.assertIn("One-time secure credential migration started", authenticate)
        self.assertIn("FinalizeVerifiedUserAsync", authenticate)
        self.assertIn("PasswordVerificationResult.ValidLegacy && AppConfig.IsProduction", signature_validation)
        self.assertIn("Electronic-signature validation blocked", signature_validation)

        self.assertIn("PasswordHashNew = CASE WHEN @Upgrade=1", commit)
        self.assertIn("PasswordSalt = CASE WHEN @Upgrade=1", commit)
        self.assertIn("PasswordHash = CASE WHEN @Upgrade=1 THEN N'[MIGRATED]'", commit)
        self.assertIn("AuthenticationRowVersion=@ExpectedVersion", commit)

        self.assertIn('"BLOCKER", "User identity"', preflight)
        self.assertIn('"WARNING", "Secure credential transition"', preflight)
        self.assertIn("pending one-time secure credential migration", preflight)
        self.assertIn("No default password or authentication bypass is used", preflight)

    def test_production_publish_never_deploys_the_example_configuration(self):
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        validator = (ROOT / "scripts/validate_project.py").read_text(encoding="utf-8-sig")
        manifest_generator = (ROOT / "scripts/generate_source_manifest.py").read_text(encoding="utf-8-sig")

        self.assertIn('None Update="appsettings.Production.json"', project)
        self.assertNotIn('None Update="appsettings.Production.example.json"\n\t\t\t  Condition="\'$(Configuration)\' != \'Debug\'"', project)
        self.assertIn("Production publish blocked: create a site-specific appsettings.Production.json", project)
        self.assertIn('production_config_path = ROOT / "appsettings.Production.json"', validator)
        self.assertIn('"appsettings.Production.json",', validator)
        self.assertIn('"appsettings.Production.json",', manifest_generator)
        self.assertIn("Application:SiteName must be a real validated site name", validator)
        self.assertIn("Database:Server must be a real controlled SQL Server target", validator)

    def test_development_configuration_auto_repairs_auth_only_and_keeps_broader_maintenance_explicit(self):
        settings = json.loads((ROOT / "appsettings.Development.example.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("Development", settings["Environment"])
        self.assertFalse(settings["Runtime"]["ApplyStartupDatabaseUpdates"])
        self.assertTrue(settings["Runtime"]["AllowEarlyMicrobiologyResults"])

        app_config = (ROOT / "AppConfig.cs").read_text(encoding="utf-8-sig")
        self.assertIn("PHARMALIMS_APPLY_STARTUP_DATABASE_UPDATES", app_config)
        self.assertIn("bool.TryParse(startupUpdatesOverride", app_config)
        self.assertIn("IsDevelopment && Settings.Value.AllowEarlyMicrobiologyResults", app_config)

        app = (ROOT / "App.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("ApplyRequiredUpdatesAsync", app)
        self.assertNotIn("Preparing database. Controlled updates are being checked", app)
        self.assertIn("ApplyControlledMigrationAsync", app)
        self.assertIn("AuthenticationLoginCompatibilityMigrationKey", app)
        self.assertIn("Development auto-reconciles authentication prerequisites only.", app)

        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        for forbidden in ("ApplyControlledMigrationAsync", "ApplyRequiredUpdatesAsync", "EnsureDevelopmentCurrentSchemaAsync", "StartupDatabaseMigrator"):
            self.assertNotIn(forbidden, readiness)
        self.assertIn("commandTimeoutSeconds: ReadinessCommandTimeoutSeconds", readiness)
        self.assertIn("name=N'SampleTestID'", readiness)
        self.assertIn("is_nullable=1", readiness)
        self.assertIn("signed Database Maintenance action", readiness)
        self.assertIn("approved database migration manifest", readiness)

        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("BtnDatabaseMaintenance_Click", main)
        self.assertIn("await migrator.ApplyRequiredUpdatesAsUserAsync(Login.CurrentUser);", main)
        self.assertIn("AppConfig.IsDevelopment", main)

    def test_production_configuration_fails_closed(self):
        settings = json.loads((ROOT / "appsettings.Production.example.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("Production", settings["Environment"])
        self.assertTrue(settings["Database"]["Encrypt"])
        self.assertFalse(settings["Database"]["TrustServerCertificate"])
        self.assertFalse(settings["Runtime"]["ApplyStartupDatabaseUpdates"])
        self.assertFalse(settings["Runtime"]["DevelopmentAdminFullPermissions"])
        self.assertFalse(settings["Runtime"]["AllowEarlyMicrobiologyResults"])
        self.assertTrue(settings["Compliance"]["EnforceAuditTrail"])
        self.assertTrue(settings["Compliance"]["EnforceElectronicSignatureStorage"])

    def test_wpf_markup_is_explicit_for_clean_source_builds(self):
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        self.assertIn("<UseWPF>true</UseWPF>", project)
        self.assertIn("<EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>", project)
        self.assertIn("<EnableDefaultPageItems>false</EnableDefaultPageItems>", project)
        self.assertIn('<ApplicationDefinition Include="App.xaml">', project)
        self.assertIn('<Page Include="**\\*.xaml" Exclude="App.xaml;bin\\**;obj\\**;tools\\**;tests\\**">', project)
        self.assertIn("<Generator>MSBuild:Compile</Generator>", project)

    def test_source_debug_does_not_pin_a_dotnet_runtime_patch(self):
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        self.assertIn("<TargetFramework>net8.0-windows7.0</TargetFramework>", project)
        self.assertNotIn("<RuntimeFrameworkVersion>", project)
        self.assertNotIn("<RollForward>LatestPatch</RollForward>", project)

        ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8-sig")
        self.assertIn("--self-contained true", ci)

    def test_early_microbiology_results_are_development_only_and_audited(self):
        water = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        em = read_source_family("EMPlanning.xaml.cs")

        self.assertIn("ValidateWaterIncubationCompleteBeforeResultsInTransaction", water)
        self.assertIn("SELECT SYSDATETIME();", water)
        self.assertIn("AppConfig.AllowEarlyMicrobiologyResults", water)
        self.assertIn('"Development Incubation Timing Override"', water)
        self.assertIn("AddAuditTrailAdvanced", water)

        self.assertIn("developmentTimingOverrideUsed && !AppConfig.AllowEarlyMicrobiologyResults", em)
        self.assertGreaterEqual(em.count('"Development Incubation Timing Override"'), 3)
        self.assertIn('"IncubationPhase1TargetEnd"', em)
        self.assertIn('"IncubationPhase2TargetEnd"', em)
        self.assertIn('"PlannedIncubationEnd"', em)

    def test_water_timestamp_correction_is_signed_atomic_and_locked(self):
        xaml = (ROOT / "ResultsEntry.xaml").read_text(encoding="utf-8-sig")
        source = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn('x:Name="BtnCorrectTimes"', xaml)
        self.assertIn('Click="BtnCorrectTimes_Click"', xaml)
        correction = source.split("private void BtnCorrectTimes_Click", 1)[1].split(
            "private void BtnStartAnalysis_Click", 1
        )[0]
        for required in (
            '"CanRegisterSamples"',
            "EnsureUserPermissionInTransaction",
            "LockAndValidateSampleStatusInTransaction",
            "WITH (UPDLOCK, HOLDLOCK)",
            '"Water Timestamp Correction"',
            "AddSampleElectronicSignatureInTransaction",
            "AddAuditTrailAdvanced",
            "SamplingDateTime=@Sampling",
            "ReceivedDateTime=@Received",
            "IncubationStartedDateTime=@IncubationStart",
            "IncubationEndDate=@IncubationEnd",
            "AnalysisStartedDateTime=@AnalysisStart",
        ):
            self.assertIn(required, correction)
        self.assertIn("SELECT SYSDATETIME();", correction)
        self.assertIn("WaterTimestampsEqual(original, current)", correction)

    def test_package_has_no_local_build_artifacts(self):
        prohibited = {".vs", "bin", "obj", "artifacts", "__pycache__"}
        offenders = [
            str(path.relative_to(ROOT))
            for path in ROOT.rglob("*")
            if path.is_file() and any(part in prohibited for part in path.relative_to(ROOT).parts)
        ]
        self.assertEqual([], offenders)

    def test_delivery_keeps_only_current_release_metadata(self):
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        version = re.search(r"<Version>([^<]+)</Version>", project).group(1)
        self.assertEqual([f"RELEASE_NOTES_{version}.md"], sorted(path.name for path in ROOT.glob("RELEASE_NOTES_*.md")))
        self.assertEqual([f"SBOM_{version}.cdx.json"], sorted(path.name for path in ROOT.glob("SBOM_*.cdx.json")))
        self.assertFalse((ROOT / "appsettings.json").exists(), "Runtime appsettings is generated from the environment template")
        self.assertFalse((ROOT / "BATCH21_FIX_MATRIX.md").exists())
        self.assertFalse((ROOT / "Trend_Data").exists())

        release_gate = (ROOT / "scripts/Invoke-ReleaseValidation.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("validate_project.py --delivery-package", release_gate)
        self.assertIn("validate_project.py --production-publish", release_gate)
        self.assertIn("unittest discover", release_gate)
        self.assertIn("Test-SourceManifest.ps1", release_gate)

    def test_prm_sample_cards_load_without_blocking_the_ui_thread(self):
        source = read_source_family("ProductionRawMaterialResults.xaml.cs")
        self.assertIn("await LoadSamplesAsync()", source)
        self.assertIn("await Task.Run(() => LoadSamplesTable(search, group))", source)
        self.assertIn("commandTimeoutSeconds: 10", source)

    def test_certificate_and_sample_management_load_without_blocking_navigation(self):
        certificate = (ROOT / "ReportCertificate.xaml.cs").read_text(encoding="utf-8-sig")
        management = (ROOT / "SampleManagement.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("this.sampleId = sampleId;\n            LoadReportData();", certificate)
        self.assertIn("await LoadReportDataAsync()", certificate)
        self.assertIn("await Task.Run(() => DatabaseHelper.HasIssuedCertificateSnapshot(sampleId))", certificate)
        self.assertIn("await LoadSamplingPointsFilterAsync()", management)
        self.assertIn("await LoadSamplesAsync()", management)

    def test_permission_checks_use_exact_identity_and_do_not_sync_wait(self):
        source = read_database_helper_source()
        repository = (ROOT / "Repositories/UserRepository.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("GetRoleAsync(cleanUsername).GetAwaiter().GetResult()", source)
        self.assertNotIn(".GetAwaiter()\n                    .GetResult()", source)
        self.assertIn("WHERE Username = @UserIdentifier", source)
        self.assertIn("ISNULL([{permissionColumn}], 0) AS PermissionGranted", source)
        self.assertIn("AppConfig.DevelopmentAdminFullPermissions", source)
        self.assertNotIn("OR FullName = @UserIdentifier", source)
        self.assertNotIn("OR FullName = @UserIdentifier", repository)

    def test_prm_registration_requires_explicit_time_permission_and_atomic_assignment(self):
        xaml = (ROOT / "ProductionRawMaterialSamples.xaml").read_text(encoding="utf-8-sig")
        source = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/PrmSpecificationRepository.cs").read_text(encoding="utf-8-sig")

        self.assertIn('x:Name="TxtSampleTime"', xaml)
        self.assertIn('ToolTip="24-hour sampling time, e.g. 18:30"', xaml)
        self.assertIn("GetRequiredSampleDateTime()", source)
        self.assertIn('"H:mm", "HH:mm"', source)
        self.assertNotIn("DateTime.Now.TimeOfDay", source)
        self.assertIn('TxtSampleTime.Text = storedSampleDateTime.HasValue', source)

        save = source.split("private void BtnSave_Click", 1)[1].split("private void ValidateForm", 1)[0]
        self.assertIn("DatabaseHelper.CanRegisterSamples(actor)", save)

        insert = source.split("private void InsertSample()", 1)[1].split("private void UpdateSample()", 1)[0]
        self.assertIn("DatabaseHelper.ExecuteInTransaction", insert)
        self.assertIn("EnsureUserPermissionInTransaction", insert)
        self.assertIn('"CanRegisterSamples"', insert)
        self.assertIn("EnsureApprovedSpecificationInTransaction", insert)
        self.assertIn("GetNextSampleNumber(connection, transaction, category)", insert)
        self.assertIn("EnsureSampleTestsAssigned(connection, transaction, sampleId)", insert)
        self.assertIn("assignedTests <= 0", insert)
        self.assertIn('"PRM Sample Registered"', insert)
        self.assertIn("AddAuditTrailAdvanced", insert)

        update = source.split("private void UpdateSample()", 1)[1].split("private SqlParameter[] BuildParameters", 1)[0]
        self.assertIn("EnsureUserPermissionInTransaction", update)
        self.assertIn("WITH (UPDLOCK, HOLDLOCK)", update)
        self.assertIn("Sample Category cannot be changed after PRM registration", update)
        self.assertNotIn("SampleCategory = @SampleCategory", update)
        self.assertIn("ReplaceUnenteredSampleTests(connection, transaction", update)
        self.assertIn("EnsureSampleTestsAssigned(connection, transaction", update)
        self.assertIn('"PRM Registration Updated"', update)
        self.assertIn("AddAuditTrailAdvanced", update)

        self.assertIn("internal int EnsureSampleTestsAssigned(", repository)
        self.assertIn("internal int ReplaceUnenteredSampleTests(", repository)

    def test_prm_result_save_recovers_missing_analysis_start_with_controlled_signature(self):
        source = read_source_family("ProductionRawMaterialResults.xaml.cs")

        self.assertIn("private bool EnsureAnalysisStartedForResultSave()", source)
        self.assertIn("private bool TryRecordPrmAnalysisStart(bool invokedFromResultSave)", source)
        self.assertNotIn("TryPromptForPrmAnalysisStart", source)
        self.assertIn('RequestPrmSignature("PRM Analysis Start")', source)
        self.assertIn('"CanEnterResults",\n                    "start PRM analysis"', source)
        self.assertIn("FROM dbo.PRM_Samples WITH (UPDLOCK, HOLDLOCK)", source)
        self.assertIn('ExecuteScalarInTransaction(conn, tx, "SELECT SYSDATETIME();")', source)
        self.assertIn("analysisStartedAt = databaseNow", source)
        self.assertIn("SET AnalysisStartedDate = @AnalysisStartedDate", source)
        self.assertIn('new SqlParameter("@AnalysisStartedDate", SqlDbType.DateTime2)', source)
        self.assertIn('AddPrmElectronicSignatureInTransaction(conn, tx, "Analysis Start", signature);', source)
        self.assertIn('"AnalysisStartedDate",\n                    null,\n                    TxtSampleNo?.Text,\n                    "PRM"', source)
        self.assertIn("SQL Server time is earlier than Sample Date / Time", source)
        self.assertIn("A user-selectable/backdated Analysis Start is intentionally not supported", source)

        start_handler = source.split("private void BtnStartAnalysis_Click", 1)[1].split(
            "private bool EnsureAnalysisStartedForResultSave", 1
        )[0]
        self.assertIn("TryRecordPrmAnalysisStart(false)", start_handler)
        self.assertNotIn("RefreshSelectedSample()", start_handler)

        save = source.split("private void BtnSaveResults_Click", 1)[1].split(
            "private void BtnSubmitReview_Click", 1
        )[0]
        self.assertLess(save.index("CommitGridEdit();"), save.index("EnsureAnalysisStartedForResultSave()"))
        self.assertLess(save.index("EnsureAnalysisStartedForResultSave()"), save.index('RequestPrmSignature("PRM Result Entry")'))
        self.assertIn("AnalysisStartedDate IS NOT NULL", save)

    def test_prm_review_approval_and_certificate_lifecycle_reauthorize_atomically(self):
        source = read_source_family("ProductionRawMaterialResults.xaml.cs")

        review = source.split("private void BtnReview_Click", 1)[1].split("private async void BtnApprove_Click", 1)[0]
        self.assertIn("DatabaseHelper.ExecuteInTransaction", review)
        self.assertIn("EnsureUserPermissionInTransaction", review)
        self.assertIn('"CanReviewResults"', review)
        self.assertIn("GetLockedPrmSampleStatusInTransaction", review)
        self.assertIn("HasSignerPerformedPrmActionInTransaction", review)
        self.assertIn('AddPrmElectronicSignatureInTransaction(conn, tx, "Review", signature, signerRole)', review)
        self.assertIn("DatabaseHelper.AddAuditTrailAdvanced", review)
        self.assertNotIn('AddPrmAudit("Technical Review"', review)

        approve = source.split("private async void BtnApprove_Click", 1)[1].split("private async void BtnIssueCertificate_Click", 1)[0]
        self.assertIn("DatabaseHelper.ExecuteInTransaction", approve)
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", approve)
        self.assertIn('"approve PRM results"', approve)
        self.assertIn("GetLockedPrmSampleStatusInTransaction", approve)
        self.assertIn("HasSignerPerformedPrmActionInTransaction", approve)
        self.assertIn("GetPrmQualityEventStateInTransaction", approve)
        self.assertIn('AddPrmElectronicSignatureInTransaction(conn, tx, "Approval", signature, signerRole)', approve)
        self.assertIn("DatabaseHelper.AddAuditTrailAdvanced", approve)
        self.assertNotIn('AddPrmAudit("QA Approval"', approve)

        issue = source.split("private string IssueCertificate(", 1)[1].split("private void CancelCertificate(", 1)[0]
        self.assertIn("EnsureUserPermissionInTransaction", issue)
        self.assertIn('"CanIssueCOA"', issue)
        self.assertIn('isReissue ? "reissue PRM certificate/report" : "issue PRM certificate/report"', issue)
        self.assertIn('"Certificate Cancelled for Reissue"', issue)
        self.assertIn("DatabaseHelper.AddAuditTrailAdvanced", issue)
        self.assertIn('"PRM_Samples", _selectedSampleId, issueAction', issue)
        self.assertIn('AddPrmElectronicSignatureInTransaction(conn, tx, issueAction, signature, signerRole)', issue)

        cancel = source.split("private void CancelCertificate(", 1)[1].split("private int CancelCertificateInTransaction", 1)[0]
        self.assertIn("EnsureUserPermissionInTransaction", cancel)
        self.assertIn('"CanCancelCOA"', cancel)
        self.assertIn("GetLockedPrmSampleStatusInTransaction", cancel)
        self.assertIn('lockedStatus.Equals("Certificate Issued"', cancel)
        self.assertIn('UpdatePrmSampleInTransaction(conn, tx, "Approved", null, "Cancelled", lockedStatus)', cancel)
        self.assertIn('AddPrmElectronicSignatureInTransaction(conn, tx, "Certificate Cancellation", signature, signerRole)', cancel)
        self.assertIn("DatabaseHelper.AddAuditTrailAdvanced", cancel)
        self.assertIn('"PRM_Samples", _selectedSampleId, "Certificate Cancellation"', cancel)

    def test_prm_choice_screen_has_no_false_default_selection_or_floating_action(self):
        xaml = (ROOT / "ProductionRawMaterialResults.xaml").read_text(encoding="utf-8-sig")
        source = read_source_family("ProductionRawMaterialResults.xaml.cs")
        production_button = xaml.split('x:Name="BtnChooseProductionResults"', 1)[1].split(">", 1)[0]
        self.assertIn('Background="White"', production_button)
        loaded_section = source.split("private async void ProductionRawMaterialResults_Loaded", 1)[1].split("private void ShowChoiceView", 1)[0]
        self.assertNotIn("EnsureQualityEventButtonExists", loaded_section)
        self.assertIn("_runtimeQualityEventButton.Visibility = Visibility.Collapsed", source)

    def test_sampling_point_distinct_query_orders_in_outer_select(self):
        source = (ROOT / "SampleManagement.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn(") distinct_points\n                    ORDER BY", source)
        self.assertNotIn(") q\n                    ORDER BY\n                        CASE WHEN PointCode", source)

    def test_heavy_operational_windows_load_off_the_ui_thread(self):
        prm_samples = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        prm_results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        planning = read_source_family("EMPlanning.xaml.cs")
        culture_media = read_source_family("CultureMediaPreparation.xaml.cs")
        trend = (ROOT / "EMTrendReport.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("await PrmSchemaReadinessService.EnsureRegistrationReadyAsync()", prm_samples)
        self.assertIn("await PrmSchemaReadinessService.EnsureResultsReadyAsync()", prm_results)
        self.assertIn("await LoadSamplesAsync()", prm_samples)
        self.assertIn("await Task.Run(() => QueryInitialPlanningData(waterType))", planning)
        self.assertIn("if (!IsLoaded || _initialLoadInProgress) return;", planning)
        self.assertIn("await Task.Run(EnsureCultureMediaSopSchema)", culture_media)
        self.assertIn("await Task.WhenAll(", culture_media)
        self.assertNotIn("EnsureCultureMediaSopSchema();\n            LoadAllData();", culture_media)
        self.assertIn("await Task.Run(() =>", trend)
        self.assertIn("commandTimeoutSeconds: 20", trend)

    def test_quality_event_windows_defer_and_background_database_loading(self):
        general = read_source_family("QualityEventInvestigation.xaml.cs")
        prm = (ROOT / "PRMQualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("Loaded += QualityEventInvestigation_Loaded", general)
        self.assertIn("QualityEventLoadData loaded = await Task.Run", general)
        self.assertIn("Loaded += PRMQualityEventInvestigation_Loaded", prm)
        self.assertIn("PRMEventLoadData loaded = await Task.Run", prm)
        self.assertIn("if (isLoadingEvent) return;", general)
        self.assertIn("if (isLoadingEvent) return;", prm)

    def test_operational_query_fallbacks_are_logged(self):
        sample_details = (ROOT / "SampleDetails.xaml.cs").read_text(encoding="utf-8-sig")
        prm = (ROOT / "PRMQualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")
        prm_service = (ROOT / "Services/Investigations/PRMQualityEventInvestigationService.cs").read_text(encoding="utf-8-sig")
        helper = read_database_helper_source()

        self.assertIn("Sample Details query failed", sample_details)
        self.assertIn("Unable to load affected PRM results", prm)
        self.assertIn("Unable to load PRM actions", prm)
        self.assertIn("Unable to load quality-event checklist", helper)

        affected_method = prm_service.split("public static DataTable GetPRMAffectedResults", 1)[1].split(
            "public static DataTable GetPRMActions", 1
        )[0]
        actions_method = prm_service.split("public static DataTable GetPRMActions", 1)[1].split(
            "public static void AddPRMQualityEventAction", 1
        )[0]
        self.assertNotIn("catch", affected_method, "PRM affected-result query failures must reach the UI logging boundary")
        self.assertNotIn("catch", actions_method, "PRM action query failures must reach the UI logging boundary")

    def test_em_approved_limits_are_controlled_and_snapshotted(self):
        em_results = read_source_family("EMResultsEntry.xaml.cs")
        em_xaml = (ROOT / "EMResultsEntry.xaml").read_text(encoding="utf-8-sig")
        limits = (ROOT / "EMLimitsManagement.xaml.cs").read_text(encoding="utf-8-sig")
        trend = (ROOT / "EMTrendReport.xaml.cs").read_text(encoding="utf-8-sig")
        helper = read_database_helper_source()
        migration = (ROOT / "Database/Migrations/20260819_001_EM_Approved_Limits_Snapshot_Control.sql").read_text(encoding="utf-8-sig")

        self.assertIn("DatabaseHelper.CanManageSettings(Login.CurrentUser", limits)
        self.assertIn("new ElectronicSignature(recordKey", limits)
        self.assertIn("EM_GradeLimitSignatures", limits)
        self.assertIn("EM Approved Limit Update", limits)
        self.assertIn("Action Limit cannot be lower than Alert Limit", limits)
        self.assertIn("BtnManageLimits_Click", em_results)
        self.assertNotIn("AlertLimitSnapshot = @alertLimitSnapshot", em_results)
        self.assertNotIn("ActionLimitSnapshot = @actionLimitSnapshot", em_results)
        self.assertIn("EVID.AlertLimitSnapshot AS AlertLimit", trend)
        self.assertIn("EmLimitEvidenceSql.Joins()", trend)
        self.assertIn("EVID.ActionLimitSnapshot AS ActionLimit", trend)
        self.assertIn('Header="Unit" Binding="{Binding Unit}"', em_xaml)
        self.assertNotIn('Header="Result CFU/plate"', em_xaml)
        self.assertIn("public static bool CanManageSettings(string username)", helper)
        self.assertIn("CREATE TABLE dbo.EM_GradeLimitSignatures", migration)
        self.assertIn("AlertLimitSnapshot", migration)
        hardening = (ROOT / "Database/Migrations/20260828_002_EM_Water_Planning_Integrity_Hardening.sql").read_text(encoding="utf-8-sig")
        self.assertIn("TRG_EM_EventPlates_FreezeLimits_20260828", hardening)
        self.assertIn("PharmaLIMS will not substitute current master limits for historical evidence", em_results)
        self.assertIn("EM_LimitSnapshotReconciliations", em_results)
        self.assertNotIn("EMPlateLimitInfo configuredLimits = needsConfiguredLimits", em_results)
        self.assertIn("Approved EM limits are incomplete for", em_results)
        self.assertIn("CK_EM_GradeLimits_ActionGEAlert_20260819", migration)



    def test_em_snapshot_migration_is_targeted_and_development_only(self):
        em_results = read_source_family("EMResultsEntry.xaml.cs")
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")

        self.assertIn("bool snapshotReady = await Task.Run(IsEmLimitSnapshotSchemaReady)", em_results)
        self.assertIn("if (!snapshotReady)", em_results)
        self.assertNotIn("ApplyControlledMigrationAsync", em_results)
        self.assertNotIn("StartupDatabaseMigrator", em_results)
        self.assertIn("System Preflight", em_results)
        self.assertIn("Database Maintenance", em_results)
        self.assertIn("await migrator.ApplyRequiredUpdatesAsUserAsync(Login.CurrentUser);", main)
        self.assertIn("AppConfig.IsDevelopment", main)
        self.assertIn("Database Maintenance", main)
        self.assertIn("public async Task ApplyControlledMigrationAsync(string versionKey)", migrator)
        self.assertIn("ApplyManifestMigrationsAsync(normalizedVersionKey)", migrator)
        self.assertIn("private async Task ApplyManifestMigrationsAsync(string? onlyVersionKey = null", migrator)

    def test_prm_schema_readiness_is_read_only_and_maintenance_gated(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")

        registration_loaded = registration.split(
            "private async void ProductionRawMaterialSamples_Loaded", 1
        )[1].split("private void BtnOpenSpecificationMaster_Click", 1)[0]
        results_loaded = results.split(
            "private async void ProductionRawMaterialResults_Loaded", 1
        )[1].split("private void ShowChoiceView", 1)[0]

        self.assertIn("await PrmSchemaReadinessService.EnsureRegistrationReadyAsync()", registration_loaded)
        self.assertIn("await PrmSchemaReadinessService.EnsureResultsReadyAsync()", results_loaded)
        for forbidden in ("ApplyControlledMigrationAsync", "ApplyRequiredUpdatesAsync", "EnsureDevelopmentCurrentSchemaAsync", "StartupDatabaseMigrator"):
            self.assertNotIn(forbidden, readiness)
        self.assertNotIn("DevelopmentMigrationGate", readiness)
        self.assertNotIn("StandardProfilesReadySql", readiness)
        self.assertNotIn("COUNT(1)=28", readiness)
        self.assertIn("PrmSchemaReadinessResult.Blocked", readiness)
        self.assertIn("commandTimeoutSeconds: ReadinessCommandTimeoutSeconds", readiness)
        self.assertIn("signed Database Maintenance action", readiness)
        self.assertNotIn("DevelopmentMaintenanceGate", migrator)
        self.assertNotIn("EnsureDevelopmentCurrentSchemaAsync", migrator)
        self.assertIn("skipUnrecordedHistoricalMigrations", migrator)
        self.assertIn("Development database has no checksum-verified historical baseline", migrator)
        self.assertIn("ALTER TABLE dbo.QualityEvents ALTER COLUMN SampleID INT NULL", migrator)
        self.assertIn("ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN SampleTestID INT NULL", migrator)
        self.assertIn("ALTER TABLE dbo.QualityEvents ALTER COLUMN QualityEventID INT NOT NULL", migrator)
        self.assertIn("await migrator.ApplyRequiredUpdatesAsUserAsync(Login.CurrentUser);", main)

        for required_object in (
            "PRM_NumberSequences", "PRM_Samples", "PRM_SampleTests", "PRM_Reports",
            "PRM_ReportHistory", "PRM_SpecificationTests", "PRM_Certificates",
            "PRM_CertificateHistory", "PRM_ElectronicSignatures", "QualityEvents",
            "QualityEventAffectedResults", "QualityEventChecklistQuestions",
        ):
            self.assertIn(required_object, readiness)

        self.assertIn("retired and will be skipped in favor of current migration", migrator)
        self.assertIn("Runtime code must not request retired migrations", migrator)

    def test_v173_prm_legacy_state_reconciliation_precedes_20260823_001_and_does_not_fabricate_review(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        keys = [item["versionKey"] for item in manifest["migrations"]]
        self.assertIn("20260822_006", keys)
        self.assertLess(keys.index("20260822_006"), keys.index("20260823_001"))

        migration = (ROOT / "Database/Migrations/20260822_006_PRM_Legacy_Specification_State_Reconciliation.sql").read_text(encoding="utf-8-sig")
        self.assertIn("ApprovalStatus=N''Obsolete''", migration)
        self.assertIn("IsActive=0", migration)
        self.assertIn("IsDefaultForCategory=0", migration)
        self.assertIn("approval identity/date", migration)
        self.assertIn("EXEC sys.sp_executesql", migration)
        self.assertNotIn("SET ReviewedBy=", migration)
        self.assertNotIn("SET ReviewedDate=", migration)

        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260822_006")
        migration_path = ROOT / "Database" / entry["file"]
        self.assertEqual(hashlib.sha256(migration_path.read_bytes()).hexdigest(), entry["sha256"].lower())

        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn('"20260822_006" => @"', migrator)
        self.assertIn("NOT EXISTS", migrator.split('"20260822_006" => @"', 1)[1].split('"20260823_001" => @"', 1)[0])

    def test_em_snapshot_migration_repairs_legacy_grade_limit_candidate_key(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        migration = ROOT / "Database/Migrations/20260819_001_EM_Approved_Limits_Snapshot_Control.sql"

        self.assertIn('versionKey.Equals("20260819_001", StringComparison.Ordinal)', migrator)
        self.assertIn("UX_EM_GradeLimits_Id_20260819", migrator)
        self.assertIn("CREATE UNIQUE NONCLUSTERED INDEX", migrator)
        self.assertIn("HAVING COUNT_BIG(*) > 1", migrator)
        self.assertIn("Existing dbo.EM_GradeLimits contains duplicate Id values", migrator)
        self.assertIn("COL_LENGTH(N'dbo.EM_GradeLimits', N'Id') IS NULL", migrator)

        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260819_001")
        self.assertEqual(hashlib.sha256(migration.read_bytes()).hexdigest(), entry["sha256"].lower())

    def test_quality_event_closure_routes_em_signatures_to_em_records(self):
        helper = read_database_helper_source()
        close_method = helper.split("public static void CloseQualityEvent", 1)[1].split(
            "public static DataTable GetQualityEventChecklist", 1
        )[0]

        self.assertIn("linkedSampleId", close_method)
        self.assertIn("linkedEmEventId", close_method)
        self.assertIn('detectionSource.Equals("Environmental Monitoring"', close_method)
        self.assertIn("FROM dbo.EM_Events", close_method)
        self.assertIn("INSERT INTO dbo.EM_EventSignatures", close_method)
        self.assertIn("'Quality Event Closure'", close_method)
        self.assertIn("if (linkedSampleId.HasValue)", close_method)
        self.assertIn("UPDATE dbo.Samples", close_method)
        self.assertIn('new SqlParameter("@sampleId", linkedSampleId.Value)', close_method)

        general = read_source_family("QualityEventInvestigation.xaml.cs")
        self.assertIn("The linked laboratory record can continue according to QA disposition.", general)

    def test_em_checklist_master_is_synchronized_by_definition_not_count(self):
        helper = read_database_helper_source()
        method = helper.split("public static void EnsureEnvironmentalMonitoringChecklistQuestions()", 1)[1].split(
            "public static DataTable GetQualityEventChecklist", 1
        )[0]

        self.assertNotIn("existingCount >= questions.Length", method)
        self.assertNotIn("existingCountValue", method)
        self.assertIn("UPDATE dbo.QualityEventChecklistQuestions", method)
        self.assertIn("IF @@ROWCOUNT = 0", method)
        self.assertIn("AppliesToTestCategory = N'Environmental Monitoring'", method)
        for section in (
            "EM Sampling", "EM Media", "EM Incubation", "EM Result Review", "EM Identification",
            "Cleaning / Disinfection", "HVAC / Facility", "Trend Review", "Impact Assessment",
            "Follow-up Monitoring", "CAPA", "QA Disposition",
        ):
            self.assertIn(f'("{section}"', method)

        investigation = read_source_family("QualityEventInvestigation.xaml.cs")
        em_results = read_source_family("EMResultsEntry.xaml.cs")
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("EnsureEnvironmentalMonitoringChecklistQuestions", investigation)
        self.assertNotIn("EnsureEnvironmentalMonitoringChecklistQuestions", em_results)
        self.assertNotIn("DatabaseHelper.EnsureEnvironmentalMonitoringChecklistQuestions", main)
        maintenance = (ROOT / "tools/PharmaLIMS.DatabaseMaintenance/Program.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("EnsureEnvironmentalMonitoringChecklistQuestions", maintenance)
        controlled_master = (ROOT / "Database/Migrations/20260826_003_EM_Investigation_Checklist_Master_Data.sql").read_text(encoding="utf-8-sig")
        self.assertIn("Environmental Monitoring", controlled_master)
        self.assertIn("@Questions", controlled_master)
        self.assertIn("ExpectedAnswer=N'Yes'", controlled_master)
        self.assertIn("QuestionLogic=N'PositiveCheck'", controlled_master)
        self.assertIn("CheckControlledMasterDataAsync", preflight)
        self.assertIn("Environmental Monitoring", preflight)

    def test_quality_event_checklist_deduplicates_legacy_questions_preferring_saved_answers(self):
        helper = read_database_helper_source()
        method = helper.split("public static DataTable GetQualityEventChecklist", 1)[1].split(
            "private static void EnsureQualityEventChecklistV12Columns", 1
        )[0]

        self.assertIn("RankedChecklist AS", method)
        self.assertIn("ROW_NUMBER() OVER", method)
        self.assertIn("pc.InvestigationProfile = 'Environmental Monitoring'", method)
        self.assertIn("ISNULL(q.SortOrder, 0) BETWEEN 7900 AND 8370", method)
        self.assertIn("N'EM|' + CONVERT(nvarchar(20), q.SortOrder)", method)
        self.assertIn("NULLIF(LTRIM(RTRIM(ISNULL(a.AnswerValue, N''))), N'') IS NOT NULL", method)
        self.assertIn("a.AnsweredDate DESC", method)
        self.assertIn("WHERE DuplicateRank = 1", method)

    def test_quality_event_validation_uses_persisted_grid_values_and_stable_em_identity(self):
        investigation = read_source_family("QualityEventInvestigation.xaml.cs")
        xaml = (ROOT / "QualityEventInvestigation.xaml").read_text(encoding="utf-8-sig")

        self.assertIn("CollapseLogicalChecklistDuplicates(table)", investigation)
        self.assertIn("BuildChecklistLogicalKey", investigation)
        self.assertIn('return "EM|" + sortOrder.ToString()', investigation)
        self.assertIn("NormalizationForm.FormKC", investigation)
        self.assertIn("InvestigationGrid_PreparingCellForEdit", investigation)
        self.assertIn("ChecklistAnswerComboBox_SelectionChanged", investigation)
        self.assertIn("InvestigationGrid_CellEditEnding", investigation)
        self.assertIn("CommitGridEdits(dgChecklist)", investigation)
        self.assertIn("grid.CommitEdit(DataGridEditingUnit.Cell, true)", investigation)
        self.assertIn("grid.CommitEdit(DataGridEditingUnit.Row, true)", investigation)
        self.assertIn("rowView.EndEdit()", investigation)
        self.assertIn("PersistDraftAndReloadForValidation", investigation)
        self.assertIn("ReloadInvestigationTablesForValidation", investigation)
        self.assertIn("BuildValidationIssueSummary", investigation)
        self.assertGreaterEqual(xaml.count('CellEditEnding="InvestigationGrid_CellEditEnding"'), 6)
        self.assertEqual(1, xaml.count('PreparingCellForEdit="InvestigationGrid_PreparingCellForEdit"'))

    def test_quality_event_workflow_autosaves_before_validation_and_defers_qa_attestations(self):
        investigation = read_source_family("QualityEventInvestigation.xaml.cs")

        submit = investigation.split("private async void BtnSubmitQA_Click", 1)[1].split(
            "private async void BtnCloseEvent_Click", 1
        )[0]
        close = investigation.split("private async void BtnCloseEvent_Click", 1)[1].split(
            "private bool IsQaOrAdmin", 1
        )[0]
        update_buttons = investigation.split("private void UpdateButtons", 1)[1].split(
            "private void SetInvestigationReadOnly", 1
        )[0]

        self.assertLess(submit.index('PersistDraftAndReloadForValidation("QA review")'), submit.index("ValidateBeforeSubmitToQA()"))
        self.assertLess(close.index('PersistDraftAndReloadForValidation("QA closure")'), close.index("ValidateBeforeClosure()"))
        self.assertNotIn("EnsureQaPermission();", submit)
        self.assertIn("EnsureQaPermission();", close)
        self.assertNotIn("&& qaAuthorized &&", update_buttons.split("BtnSubmitQA.IsEnabled", 1)[1].split("BtnCloseEvent", 1)[0])
        self.assertIn("IsQaClosureOnlyChecklistItem", investigation)
        self.assertIn('stage.Equals("QA review"', investigation)
        self.assertIn('normalizedSection == "QA DISPOSITION"', investigation)
        self.assertIn("await LoadEventAsync();", investigation)

    def test_quality_event_dates_are_culture_independent_and_canonicalized(self):
        investigation = read_source_family("QualityEventInvestigation.xaml.cs")
        xaml = (ROOT / "QualityEventInvestigation.xaml").read_text(encoding="utf-8-sig")

        self.assertIn("TryParseFlexibleDate", investigation)
        self.assertIn("NormalizeDateInput", investigation)
        self.assertIn('"yyyy-MM-dd"', investigation)
        self.assertIn('"dd/MM/yyyy"', investigation)
        self.assertIn('CultureInfo.GetCultureInfo("en-GB")', investigation)
        self.assertIn('CultureInfo.GetCultureInfo("ar-IQ")', investigation)
        self.assertIn('ParseDatabaseDateOrDBNull(row, "DueDate", "CAPA Due Date")', investigation)
        self.assertIn('ParseDatabaseDateOrDBNull(row, "DateReceived", "Distribution Date Received")', investigation)
        self.assertIn('Due Date (YYYY-MM-DD)', xaml)
        self.assertIn('Date Received (YYYY-MM-DD)', xaml)

    def test_compiler_hotfix_96(self):
        em_source = read_source_family("EMResultsEntry.xaml.cs")
        self.assertIn("using PharmaLIMS.Infrastructure;", em_source)
        self.assertIn("ApplicationLogger.Error", em_source)

        trend_source = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("if (summaries[index].Mean.HasValue)", trend_source)
        self.assertIn("(double)summaries[index].Mean!.Value", trend_source)
        self.assertNotIn("summaries[index].Mean ?? 0m", trend_source)

    def test_startup_window_is_visible_while_migrations_run_and_legacy_baseline_is_not_repeated(self):
        app = (ROOT / "App.xaml.cs").read_text(encoding="utf-8-sig")
        login = (ROOT / "Login.xaml.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")

        self.assertIn("loginWindow.Show();", app)
        self.assertNotIn("ApplyRequiredUpdatesAsync", app)
        self.assertNotIn("Dispatcher.Yield", app)
        self.assertIn("loginWindow.SetStartupBusy(false);", app)
        self.assertIn("Preparing database...", login)

        self.assertIn('LegacyStartupBaselineKey = "20260722_003"', migrator)
        self.assertIn("IsLegacyStartupBaselineRecordedAsync", migrator)
        self.assertIn("VersionKey = @BaselineKey", migrator)
        self.assertIn("NULLIF(LTRIM(RTRIM(MigrationChecksum))", migrator)
        self.assertNotIn("VersionKey >= @BaselineKey", migrator)
        self.assertIn("ProvisionFreshDatabaseBaselineAsync", migrator)
        self.assertIn('root.TryGetProperty("freshInstallBaseline"', migrator)
        self.assertIn("executeHistoricalMigrations: true", migrator)
        self.assertIn("skipUnrecordedHistoricalMigrations: false", migrator)
        self.assertIn("Controlled fresh-install baseline verified", migrator)
        self.assertIn("Recorded fresh-install baseline checksum does not match", migrator)
        self.assertIn("safe, resumable fresh-install state", migrator)
        self.assertIn("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0", migrator)
        self.assertIn("Automatic legacy ledger stamping is prohibited", migrator)
        self.assertIn("Recorded migration has no controlled checksum and cannot be auto-stamped", migrator)
        self.assertIn("Historical migration is absent from the checksum-verified ledger", migrator)
        self.assertIn("SET LOCK_TIMEOUT", migrator)
        self.assertIn("sys.sp_getapplock", migrator)
        self.assertIn("PharmaLIMS.SchemaMigration", migrator)
        self.assertIn("Applying controlled database migration", migrator)

    def test_fresh_install_baseline_is_controlled_empty_and_packaged(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        baseline = manifest["freshInstallBaseline"]
        baseline_path = ROOT / "Database" / baseline["file"]
        source = baseline_path.read_text(encoding="utf-8-sig")
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")

        self.assertTrue(baseline_path.is_file())
        self.assertEqual(hashlib.sha256(baseline_path.read_bytes()).hexdigest(), baseline["sha256"].lower())
        self.assertIn('Content Include="Database\\Baseline\\*.sql"', project)
        for table in (
            "Users", "Tests", "WaterSamplingPoints", "SamplingPoints", "WaterTestProfiles",
            "WaterSpecifications", "Samples", "SampleTests", "LIMS_NumberSequences",
            "Certificates", "CertificatePrintHistory", "AuditTrail", "ElectronicSignatures", "EM_Areas",
            "EM_Events", "MediaNumberSequences", "CultureMediaLots", "MediaPreparations", "MediaQualifications",
        ):
            self.assertIn(f"CREATE TABLE dbo.{table}", source)
        self.assertNotRegex(source, r"(?im)^\s*GO\s*$")
        self.assertNotRegex(source, r"(?i)INSERT\s+(?:INTO\s+)?dbo\.(?:Users|Tests|WaterSamplingPoints|WaterSpecifications|EM_Areas|CultureMedia)\b")
        self.assertIn("Fresh-install baseline refused", source)

    def test_prm_oral_tablet_remediation_is_draft_item_scoped_and_fail_closed(self):
        source = (ROOT / "Database/Migrations/20260823_001_PRM_Specification_Remediation.sql").read_text(encoding="utf-8-sig")

        self.assertIn("N'NMT 100 CFU/g'", source)
        self.assertIn("N'NMT 10 CFU/g'", source)
        for organism in ("SALMONELLA", "ECOLI", "SAUREUS", "PAERUGINOSA", "CALBICANS"):
            self.assertIn(organism, source)
        self.assertIn("N'Finished Product'", source)
        self.assertIn("N'Stability'", source)
        draft_section = source.split("DECLARE @Draft TABLE", 1)[1]
        self.assertNotIn("N'Raw Material'", draft_section)
        self.assertNotIn("N'Production / In-Process'", draft_section)
        self.assertIn("N'Draft',NULL,NULL,NULL,NULL,NULL,0,N'Controlled Remediation 20260823',0", source)
        self.assertIn("Historical PRM_SampleTests snapshots are not changed", source)
        self.assertIn("ApprovalStatus=N'Reviewed'", source)
        self.assertIn("Legacy PRM specification states require controlled reconciliation", source)

    def test_system_seeded_media_requirements_require_real_qa_activation(self):
        migration = (ROOT / "Database/Migrations/20260823_002_Culture_Media_Requirement_Approval_Gate.sql").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")

        self.assertIn("ApprovalStatus=N'Draft'", migration)
        self.assertIn("IsActive=0", migration)
        self.assertIn("ApprovedBy=NULL", migration)
        self.assertIn("IsActive=1", migration)
        self.assertIn("ApprovalStatus<>N'Approved'", migration)
        self.assertNotIn("N'System Baseline - QA confirmation required', SYSUTCDATETIME()", migrator)
        self.assertIn("N'Draft', NULL, NULL", migrator)

    def test_fresh_install_includes_structured_quality_event_evidence_schema(self):
        source = (ROOT / "Database/Migrations/20260823_003_Quality_Event_Structured_Evidence_Schema.sql").read_text(encoding="utf-8-sig")
        for table in (
            "QualityEventActions", "QualityEventChecklistQuestions", "QualityEventChecklistAnswers",
            "QualityEventRootCauseWhys", "QualityEventImpactAssessments", "QualityEventCAPAItems",
            "QualityEventRetesting", "QualityEventDistribution", "QualityEventRelatedItems",
            "QualityEventSignatures", "QualityEventPrintHistory",
        ):
            self.assertIn(f"CREATE TABLE dbo.{table}", source)
        self.assertIn("UQ_QualityEventChecklistAnswers", source)
        self.assertIn("FK_QualityEventActions_Event", source)
        self.assertIn("FK_QualityEventRelatedItems_Event", source)
        self.assertIn("TRG_QualityEventSignatures_AppendOnly", source)
        self.assertIn("TRG_QualityEventPrintHistory_AppendOnly", source)

    def test_fresh_install_includes_controlled_water_planning_schema(self):
        source = (ROOT / "Database/Migrations/20260823_004_Water_Planning_Schema.sql").read_text(encoding="utf-8-sig")
        verification = (ROOT / "Database/Verification/20260823_Phase1_Stabilization_Verification.sql").read_text(encoding="utf-8-sig")
        for table in (
            "Water_Plans", "Water_PlanSamples", "Water_PlanSampleTests",
            "Water_PlanSampleAttempts", "Water_PlanSignatures",
        ):
            self.assertIn(f"CREATE TABLE dbo.{table}", source)
            self.assertIn(f"N'{table}'", verification)
        self.assertIn("PointCodeSnapshot", source)
        self.assertIn("TestNameSnapshot", source)
        self.assertIn("TRG_Water_PlanSignatures_AppendOnly", source)

    def test_external_trend_snapshot_schema_is_reconciled_without_data_rewrite(self):
        migration = (ROOT / "Database/Migrations/20260826_004_External_Trend_Current_State_Reconciliation.sql").read_text(encoding="utf-8-sig")
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")

        for column in (
            "MethodName", "UnitName", "PeriodCount", "PeriodDefinitionJson",
            "SourceAnchorBatchID", "SourceAnchorImportNumber", "SourceCutoffAt",
            "SourceBatchManifestJson", "SourceBatchManifestSha256", "SnapshotHashSha256",
            "ReviewerRole", "SignatureMeaning", "SignatureReason", "SignedAt",
        ):
            self.assertIn(column, migration)
            self.assertIn(column, service)
        self.assertIn("FK_EMTrendReviewSnapshots_SourceAnchorBatch", migration)
        self.assertIn("CK_EMTrendReviewSnapshots_DatesV2", migration)
        self.assertIn("CK_EMTrendReviewSnapshots_ControlledV2", migration)
        self.assertIn("TR_EMTrendReviewSnapshots_Immutable", migration)
        self.assertNotRegex(migration, r"(?i)\bUPDATE\s+dbo\.EMTrendReviewSnapshots\b")
        self.assertIn("IsApprovalSnapshotSchemaReady", service)
        self.assertIn("EnsureApprovalSnapshotSchemaAsync", window)
        self.assertNotIn("ApplyControlledMigrationAsync", window)
        self.assertNotIn("StartupDatabaseMigrator", window)
        self.assertIn("System Preflight", window)
        self.assertIn("Database Maintenance", window)
        self.assertIn("await migrator.ApplyRequiredUpdatesAsUserAsync(Login.CurrentUser);", main)

    def test_approved_historical_em_report_uses_recorded_limits_without_reconciliation(self):
        source = read_source_family("EMResultsEntry.xaml.cs")
        helper = read_database_helper_source()
        load_plates = source.split("private void LoadPlates(int eventId)", 1)[1].split("private bool HasEnteredResult", 1)[0]
        can_print = source.split("private bool CanPrintApprovedEMReport", 1)[1].split("private void UpdateWorkflowButtons", 1)[0]
        print_click = source.split("private void BtnPrintReport_Click", 1)[1].split("private void ShowEMReportPreview", 1)[0]
        helper_gate = helper.split("public static bool CanPrintEMResultReport", 1)[1].split("public static DataTable GetEMEventSignatures", 1)[0]

        self.assertIn('bool hasAlertSnapshot = row["AlertLimitSnapshot"] != DBNull.Value;', load_plates)
        self.assertIn('bool hasActionSnapshot = row["ActionLimitSnapshot"] != DBNull.Value;', load_plates)
        self.assertIn('bool hasAirVolumeSnapshot = !activeAir || row["AirVolumeLitersSnapshot"] != DBNull.Value;', load_plates)
        self.assertIn('string frozenUnit = row.GetSafeString("ResultUnitSnapshot");', load_plates)
        self.assertIn("PharmaLIMS will not substitute current master limits for historical evidence", load_plates)
        self.assertNotIn("needsConfiguredLimits", load_plates)
        self.assertNotIn("GetEMPlateLimits(grade, method)", load_plates)
        self.assertIn("HasLimitSnapshot = hasAlertSnapshot && hasActionSnapshot && !string.IsNullOrWhiteSpace(frozenUnit) && hasAirVolumeSnapshot", load_plates)
        self.assertIn("Approved EM limits are incomplete for", can_print)
        self.assertIn("IsActiveAirSampling(item.Method)", can_print)
        self.assertNotIn("HasLimitSnapshot", can_print)
        for snapshot_gate in ("AlertLimitSnapshot", "ActionLimitSnapshot", "HasLimitSnapshot"):
            self.assertNotIn(snapshot_gate, helper_gate)
        self.assertNotIn("TryReconcileHistoricalLimitSnapshotsForDevelopment", source)
        self.assertNotIn("HistoricalLimitSnapshotCandidate", source)
        self.assertIn("BtnReconcileLegacySnapshot_Click", source)
        self.assertIn("EMLegacySnapshotReconciliation", source)
        self.assertNotIn("ExecuteInTransaction", print_click)
        self.assertNotRegex(print_click, r"\bUPDATE\s+dbo\.")
        self.assertIn("? CalculateFinalResult()", source)
        self.assertIn("recorded approved alert and action limits used for this report", source)
        self.assertNotIn("effective at the time of sampling", source)

    def test_external_trend_review_hardening_uses_resumable_current_state_migration(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        current = ROOT / "Database/Migrations/20260826_004_External_Trend_Current_State_Reconciliation.sql"
        current_text = current.read_text(encoding="utf-8-sig")

        self.assertIn("ApplyResumableCurrentStateMigrationAsync", migrator)
        self.assertIn('executionMode.Equals("resumable", StringComparison.OrdinalIgnoreCase)', migrator)
        self.assertIn("ResumableMigrationLockTimeoutMilliseconds = 120000", migrator)
        self.assertIn("ResumableMigrationStepCommandTimeoutSeconds = 180", migrator)
        self.assertIn("SqlConnection.ClearAllPools();", migrator)
        self.assertNotIn('versionKey.Equals("20260815_001", StringComparison.Ordinal)', migrator)

        for retired in ("20260815_001", "20260816_001", "20260823_005"):
            entry = next(item for item in manifest["migrations"] if item["versionKey"] == retired)
            self.assertEqual("20260826_004", entry.get("supersededBy"))

        current_entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260826_004")
        self.assertEqual("resumable", current_entry.get("executionMode"))
        self.assertEqual(hashlib.sha256(current.read_bytes()).hexdigest(), current_entry["sha256"].lower())
        self.assertNotIn("BEGIN TRANSACTION", current_text.upper())
        self.assertIn("SET LOCK_TIMEOUT 120000", current_text)
        self.assertIn("CK_EMTrendReviewSnapshots_DatesV2", current_text)
        self.assertIn("CK_EMTrendReviewSnapshots_ControlledV2", current_text)
        self.assertIn("TR_EMTrendReviewSnapshots_Immutable", current_text)
        self.assertIn("TR_ExternalTrendImportBatches_PreventDuplicateApprovedRows", current_text)
        self.assertIn("IX_ExternalTrendImportRows_SeriesLookupV2", current_text)
        self.assertNotRegex(current_text, r"(?i)\bUPDATE\s+dbo\.ExternalTrendImportRows\b")
        self.assertNotRegex(current_text, r"(?i)\bUPDATE\s+dbo\.EMTrendReviewSnapshots\b")


    def test_em_and_water_open_picker_statuses_are_not_conflated(self):
        em_results = read_source_family("EMResultsEntry.xaml.cs")
        sample_management = (ROOT / "SampleManagement.xaml.cs").read_text(encoding="utf-8-sig")
        water_results = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        water_xaml = (ROOT / "ResultsEntry.xaml").read_text(encoding="utf-8-sig")

        self.assertIn('AS WorkflowStatus', em_results)
        self.assertIn('AS ResultStatus', em_results)
        self.assertIn("NOT IN ('APPROVED', 'CLOSED', 'CANCELLED')", em_results)
        self.assertIn('Title = "Open EM Events"', em_results)
        self.assertIn('Header = "Workflow"', em_results)
        self.assertIn('Header = "Result"', em_results)
        self.assertIn("THEN 'OOS / Investigation'", sample_management)
        self.assertIn("THEN 'Alert / QA Review'", sample_management)
        self.assertIn('Title = "Open Water Samples"', water_results)
        self.assertIn('Content="Load Open Water"', water_xaml)

    def test_em_final_result_display_is_separate_from_workflow_stage(self):
        em_results = read_source_family("EMResultsEntry.xaml.cs")

        self.assertIn('private string currentFinalResult = "Pending";', em_results)
        self.assertIn('currentFinalResult = row.GetSafeString("FinalResult")', em_results)
        self.assertIn('SetFinalResultStatus(currentFinalResult);', em_results)
        self.assertNotIn('SetFinalResultStatus(workflowStatus);', em_results)
        self.assertIn('string workflow = FirstNonEmpty(workflowStatus);', em_results)
        self.assertIn('string result = plateItems != null && plateItems.Count > 0', em_results)
        self.assertIn('? CalculateFinalResult()', em_results)
        self.assertIn(': FirstNonEmpty(currentFinalResult, lblFinalResult.Text);', em_results)

        progress = em_results.split("private void UpdateWorkflowProgress()", 1)[1].split(
            "private static void ResetWorkflowStep", 1
        )[0]
        submitted_block = progress.split("SetWorkflowStepComplete(StepEntry, StepEntryText);", 1)[1].split(
            "SetWorkflowStepComplete(StepReviewed, StepReviewedText);", 1
        )[0]
        self.assertNotIn('status.Equals("Results Entered"', submitted_block)
        self.assertIn('status.Equals("Under Review"', submitted_block)


    def test_em_quality_event_gmp_closure_gate_and_balanced_report_v117(self):
        investigation = read_source_family("QualityEventInvestigation.xaml.cs")
        xaml = (ROOT / "QualityEventInvestigation.xaml").read_text(encoding="utf-8-sig")
        database = read_database_helper_source()

        self.assertIn('"Phase I - Laboratory Review"', database)
        self.assertIn('"Root Cause / Fishbone"', database)
        self.assertIn('BETWEEN 7900 AND 8370', database)
        self.assertIn('ValidateEnvironmentalMonitoringEvidence()', investigation)
        self.assertIn('ValidateEnvironmentalMonitoringFishboneEvidence()', investigation)
        self.assertIn('IsWeakInvestigationNarrative', investigation)
        self.assertIn('minimumWhyLevels', investigation)
        self.assertIn('ValidateEnvironmentalMonitoringQaConclusionCoverage', investigation)
        self.assertIn('A Major/Critical Environmental Monitoring Quality Event requires at least one documented distribution recipient', investigation)
        self.assertIn('Area Released after Corrective Action', investigation)
        self.assertIn('while the corrective action is still', investigation)
        self.assertIn('DataTable rows = FilterMeaningfulRows(retestingTable, "RetestPerformed");', investigation)
        self.assertIn('Version: 05 (Balanced GMP Report)', investigation)
        self.assertIn('QEIR-V15.7', investigation)
        self.assertIn('TryCreateQualityEventReportLogo', investigation)
        self.assertIn('medica-logo.png', investigation)
        self.assertIn('QualityEventReportPaginator', investigation)
        self.assertIn('PageCount.ToString', investigation)
        self.assertIn('FilterEnvironmentalTechnicalChecklist', investigation)
        self.assertIn('AddChecklistInChunks', investigation)
        self.assertIn('Not verified', investigation)
        self.assertIn('Impact Status', xaml)
        self.assertIn('<sys:String>Effectiveness Verified</sys:String>', xaml)
        self.assertIn('Use in QA Disposition (Narrative)', xaml)
        self.assertIn('DRAFT - INVESTIGATION OPEN - NOT FOR FINAL GMP RELEASE', investigation)
        self.assertIn('ReportStarColumn', investigation)
        self.assertIn('GridUnitType.Star', investigation)
        self.assertIn('TryLoadQualityEventReportLogoSource', investigation)
        self.assertIn('dc.DrawImage(headerLogo', investigation)
        self.assertIn('continuationTitle.BreakPageBefore = true', investigation)
        self.assertIn('loadedClosedBy', investigation)
        self.assertIn('loadedClosedDate', investigation)
        self.assertIn('Not closed - final QA closure has not been completed.', investigation)
        qa_block = investigation.split('private void AddPhaseVIIQADisposition', 1)[1].split('private void AddPhaseVIIIDistribution', 1)[0]
        self.assertNotIn('DateTime.Now.ToString("yyyy-MM-dd HH:mm")', qa_block)
        self.assertNotIn('"Closed By", FirstNonEmpty(currentUser)', qa_block)

    def test_v118_authentication_identity_and_lockout_hardening(self):
        auth = (ROOT / "Services/AuthService.cs").read_text(encoding="utf-8-sig")
        interface = (ROOT / "Services/IAuthService.cs").read_text(encoding="utf-8-sig")
        repo = (ROOT / "Repositories/UserRepository.cs").read_text(encoding="utf-8-sig")
        password = (ROOT / "Infrastructure/PasswordSecurity.cs").read_text(encoding="utf-8-sig")
        config = (ROOT / "AppConfig.cs").read_text(encoding="utf-8-sig")
        database = read_database_helper_source()

        self.assertIn("CurrentIterations = 600000", password)
        self.assertIn("LegacyIterations = 100000", password)
        self.assertIn("CryptographicOperations.FixedTimeEquals", password)
        self.assertIn("RecordSuccessfulLoginAsync", auth)
        self.assertLess(auth.index("if (!await FinalizeVerifiedUserAsync"), auth.index("_currentUser = user"))
        self.assertIn("RegisterAuthenticationFailureAsync(freshUser)", auth)
        self.assertIn("Login.ClearCurrentUserContext()", auth)
        self.assertNotIn("SetCurrentUser", interface)
        self.assertNotIn("DELETE FROM Users", repo)
        self.assertIn("Direct user deactivation through UserRepository is disabled", repo)
        self.assertIn("error: true", repo)
        self.assertIn("Duplicate usernames were detected", repo)
        self.assertIn("Environment must be exactly 'Production' or 'Development'", config)
        self.assertIn("IsDevelopment && Settings.Value.DevelopmentAdminFullPermissions", config)
        self.assertNotIn("GetUserByCredentials", database)
        self.assertNotIn("ValidateCurrentUserPassword", database)

    def test_v118_quality_event_closure_reauthorizes_inside_transaction(self):
        helper = read_database_helper_source()
        em = read_source_family("QualityEventInvestigation.xaml.cs")
        prm = (ROOT / "PRMQualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/QualityEventRepository.cs").read_text(encoding="utf-8-sig")

        self.assertIn("EnsureQaClosureAuthorizationInTransaction", helper)
        close_method = helper.split("public static void CloseQualityEvent", 1)[1].split("public static", 1)[0]
        self.assertIn("EnsureQaClosureAuthorizationInTransaction", close_method)
        self.assertIn("CanApproveResults", helper)
        self.assertIn('RoleIsOneOf(role, "QA", "Quality Assurance")', helper)
        self.assertIn("DatabaseHelper.CanCloseQualityEvent(currentUser)", em)
        self.assertIn("DatabaseHelper.CanCloseQualityEvent(currentUser)", prm)
        self.assertIn("EnsureQaClosureAuthorizationInTransaction", prm)
        self.assertNotIn('Contains("QA"', em)
        self.assertNotIn('Contains("Admin"', em)
        self.assertNotIn("DELETE FROM QualityEvents", repository)
        self.assertIn("cannot be physically deleted", repository)

    def test_v118_unique_user_identity_migration_is_fail_closed(self):
        migration = (ROOT / "Database/Migrations/20260821_001_Enforce_Unique_User_Identity.sql").read_text(encoding="utf-8-sig")
        verification = (ROOT / "Database/Verification/20260821_User_Identity_Security_Verification.sql").read_text(encoding="utf-8-sig")
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))

        self.assertIn("UX_Users_Username_20260821", migration)
        self.assertIn("CK_Users_Username_Controlled_20260821", migration)
        self.assertIn("HAVING COUNT(*) > 1", migration)
        self.assertIn("THROW 52003", migration)
        self.assertIn("UniqueUserIdentity", verification)
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260821_001")
        path = ROOT / "Database" / entry["file"]
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), entry["sha256"].lower())

    def test_v118_import_export_and_error_boundary_hardening(self):
        importer = (ROOT / "Services/ExternalTrendImportService.cs").read_text(encoding="utf-8-sig")
        csv_security = (ROOT / "Infrastructure/CsvSecurity.cs").read_text(encoding="utf-8-sig")
        user_error = (ROOT / "Infrastructure/UserFacingError.cs").read_text(encoding="utf-8-sig")
        reports = read_source_family("ReportsTrends.xaml.cs")
        sample_management = (ROOT / "SampleManagement.xaml.cs").read_text(encoding="utf-8-sig")

        for marker in ("MaximumArchiveExpandedBytes", "MaximumWorksheetXmlBytes", "MaximumColumns", "MaximumSharedStrings"):
            self.assertIn(marker, importer)
        self.assertIn("DtdProcessing.Prohibit", importer)
        self.assertIn("XmlResolver = null", importer)
        self.assertIn("MaximumCompressionRatio", importer)
        self.assertIn("IsFormulaLike", csv_security)
        self.assertIn('text = "\'" + text', csv_security)
        self.assertIn("ApplicationLogger.Error", user_error)
        self.assertIn("Reference:", user_error)
        self.assertIn("Infrastructure.CsvSecurity.Escape", reports)
        self.assertIn("Infrastructure.CsvSecurity.Escape", sample_management)

    def test_v118_regulated_ui_does_not_expose_raw_exception_messages(self):
        regulated_windows = [
            "EMLimitsManagement.xaml.cs",
            "EMResultsEntry.xaml.cs",
            "EMTrendReport.xaml.cs",
            "ExternalTrendImportDialog.xaml.cs",
            "ExternalTrendThreeCycleReviewWindow.xaml.cs",
            "Login.xaml.cs",
            "MainWindow.xaml.cs",
            "NewSampleDialog.xaml.cs",
            "PRMQualityEventInvestigation.xaml.cs",
            "ProductionRawMaterialResults.xaml.cs",
            "ProductionRawMaterialSamples.xaml.cs",
            "QualityEventInvestigation.xaml.cs",
            "ReportCertificate.xaml.cs",
            "ReportsTrends.xaml.cs",
            "ResultsEntry.xaml.cs",
            "SampleDetails.xaml.cs",
            "SampleManagement.xaml.cs",
        ]
        for relative in regulated_windows:
            source = (ROOT / relative).read_text(encoding="utf-8-sig")
            self.assertNotRegex(source, r"MessageBox\.Show\(\s*ex\.Message", relative)
            self.assertNotRegex(source, r"\{ex\.Message\}", relative)
            self.assertNotIn("? exception.Message", source, relative)

    def test_v118_temp_files_dependency_and_actor_identity_hardening(self):
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        samples = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        controlled_temp = (ROOT / "Infrastructure/ControlledTempFiles.cs").read_text(encoding="utf-8-sig")
        culture = read_source_family("CultureMediaPreparation.xaml.cs")
        em = read_source_family("EMResultsEntry.xaml.cs")
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")

        self.assertIn("ControlledTempFiles.WriteHtmlAndOpen", results)
        self.assertIn("ControlledTempFiles.WriteHtmlAndOpen", samples)
        self.assertIn('Guid.NewGuid().ToString("N")', controlled_temp)
        self.assertIn('"PharmaLIMS-Controlled"', controlled_temp)
        self.assertIn("CleanupStaleFiles", controlled_temp)
        self.assertIn("ScheduleDelete", controlled_temp)
        self.assertIn("FileMode.CreateNew", controlled_temp)
        prm_identity = samples.split("private static string GetCurrentUserDisplayName", 1)[1].split("private void SetDefaultValues", 1)[0]
        self.assertIn("Login.CurrentUser", prm_identity)
        self.assertNotIn("CurrentUserFullName", prm_identity)
        culture_identity = culture.split("private string GetCurrentUserName", 1)[1].split("// ==================== LOADING", 1)[0]
        self.assertIn("Login.CurrentUser", culture_identity)
        self.assertNotIn("CurrentUserFullName", culture_identity)
        signature_check = em.split("private bool HasCurrentUserSignedAction", 1)[1].split("private", 1)[0]
        self.assertNotIn("CurrentUserFullName", signature_check)
        self.assertIn('PackageReference Include="Microsoft.Data.SqlClient" Version="6.1.6"', project)

    def test_v118_certificate_cancellation_is_atomic_and_reauthorized(self):
        helper = read_database_helper_source()
        controlled = helper.split("public static string CancelCertificateControlled", 1)[1].split(
            "private static int AddCertificateLifecycleAuditInTransaction", 1
        )[0]
        compatibility = helper.split("public static string CancelCertificate(int sampleId", 1)[1].split("\n        }", 1)[0]

        self.assertIn("ExecuteInTransaction", controlled)
        self.assertIn("EnsureUserPermissionInTransaction", controlled)
        self.assertIn('"CanCancelCOA"', controlled)
        self.assertIn("AddCertificateLifecycleAuditInTransaction", controlled)
        self.assertIn("AddAuditTrailAdvanced", controlled)
        self.assertIn("UserFacingError.SafeMessage", controlled)
        self.assertIn("return CancelCertificateControlled(sampleId, cancelledBy, reason);", compatibility)
        self.assertNotIn("UPDATE Certificates", compatibility)

    def test_v118_em_schema_validation_is_async(self):
        em = read_source_family("EMResultsEntry.xaml.cs")
        self.assertIn("private async Task EnsureEMStatusColumnsAreTextAsync()", em)
        self.assertIn("await Task.Run(() => DatabaseHelper.ExecuteScalar(compatibilityQuery))", em)
        self.assertNotIn("ApplyControlledMigrationAsync", em)
        self.assertNotIn("StartupDatabaseMigrator", em)
        self.assertNotIn(".GetAwaiter().GetResult()", em)
        self.assertIn("await EnsureEMStatusColumnsAreTextAsync();", em)
        self.assertIn("No database migration is allowed from an operational EM screen", em)

    def test_v118_startup_and_internal_review_errors_are_redacted(self):
        app = (ROOT / "App.xaml.cs").read_text(encoding="utf-8-sig")
        review = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn('UserFacingError.SafeMessage(exception, "PharmaLIMS startup")', app)
        self.assertNotIn("return root.Message", app)
        self.assertNotIn("ex.Message", review)
        self.assertIn("UserFacingError.SafeMessage", review)

    def test_v118_em_workflow_change_signature_and_audit_are_one_transaction(self):
        helper = read_database_helper_source()
        methods = [
            ("MarkEMResultsEntered", "EM Result Entry"),
            ("MarkEMEventUnderInvestigation", "EM Investigation Required"),
            ("SubmitEMEventForReview", "EM Submit for Review"),
            ("ReviewEMEvent", "EM Review"),
            ("ApproveEMEvent", "EM Approval"),
        ]
        for index, (name, action) in enumerate(methods):
            start = helper.index("public static void " + name)
            next_positions = [
                helper.find("public static void ", start + 20),
                helper.find("public static bool ", start + 20),
                helper.find("public static string ", start + 20),
            ]
            ends = [position for position in next_positions if position != -1]
            end = min(ends) if ends else len(helper)
            block = helper[start:end]
            self.assertIn("ExecuteInTransaction", block, name)
            self.assertIn("AddAuditTrailAdvanced(\n", block, name)
            self.assertIn("conn, tx", block, name)
            self.assertIn(action, block, name)
            self.assertIn("EM_EventSignatures", block, name)
        self.assertIn("planStatusBefore", helper)
        self.assertIn("planStatusAfter", helper)

    def test_v118_em_and_water_planning_audits_are_atomic_and_roles_are_fresh(self):
        planning = read_source_family("EMPlanning.xaml.cs")

        self.assertNotIn("DatabaseHelper.AddAuditTrailAdvanced(\"Water_Plans\", planId", planning)
        self.assertNotIn("DatabaseHelper.AddAuditTrailAdvanced(\"Water_PlanSamples\", item.WaterPlanSampleID", planning)
        self.assertIn("DatabaseHelper.AddAuditTrailAdvanced(\n                    connection, transaction", planning)
        self.assertIn("Audit(connection, transaction, plan", planning)
        self.assertIn('"CanApproveResults", "cancel an EM plan"', planning)
        self.assertIn('"CanApproveResults", "cancel a water plan"', planning)
        self.assertIn('"CanReviewResults", "review an EM schedule"', planning)
        self.assertIn('"CanApproveResults", "approve an EM schedule"', planning)
        self.assertIn("string currentRole = DatabaseHelper.EnsureActiveUserInTransaction", planning)
        self.assertNotIn('command.Parameters.AddWithValue("@Role", CurrentRole())', planning)

    def test_v118_prm_quality_event_creation_is_atomic_and_requires_affected_results(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        create = prm.split("private int CreatePrmQualityEvent()", 1)[1].split(
            "private void EnsureQualityEventButtonExists", 1
        )[0]
        affected = prm.split("private void AddAffectedPrmResultsToQualityEvent(", 1)[1].split(
            "private DataRow GetCurrentSampleRow", 1
        )[0]

        self.assertIn("DatabaseHelper.ExecuteInTransaction", create)
        self.assertIn("AddAffectedPrmResultsToQualityEvent(connection, transaction", create)
        self.assertIn("DatabaseHelper.AddAuditTrailAdvanced(\n                    connection,\n                    transaction", create)
        self.assertIn("WITH (UPDLOCK, HOLDLOCK)", affected)
        self.assertIn("cannot be opened without at least one traceable", affected)
        self.assertNotIn("catch", affected)


    def test_v119_controlled_temp_and_error_boundary_import_system_io_explicitly(self):
        controlled_temp = (ROOT / "Infrastructure/ControlledTempFiles.cs").read_text(encoding="utf-8-sig")
        user_error = (ROOT / "Infrastructure/UserFacingError.cs").read_text(encoding="utf-8-sig")

        self.assertIn("using System.IO;", controlled_temp)
        self.assertIn("using System.IO;", user_error)
        for symbol in (
            "Path", "Directory", "File", "FileStream", "StreamWriter",
            "FileMode", "FileAccess", "FileShare", "FileOptions", "SearchOption"
        ):
            self.assertIn(symbol, controlled_temp)
        self.assertIn("IOException", user_error)


    def test_v120_prm_numeric_interpretation_recovers_controlled_legacy_limits(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        evaluator = (ROOT / "Services/PrmNumericSpecificationEvaluator.cs").read_text(encoding="utf-8-sig")

        self.assertIn("string specificationLimit = row.Table.Columns.Contains(\"SpecificationLimit\")", prm)
        self.assertIn("string testCode = row.Table.Columns.Contains(\"TestCode\")", prm)
        self.assertIn("PrmNumericSpecificationEvaluator.Evaluate", prm)
        self.assertIn("ContainsKnownRuleSyntax(fallbackName)", evaluator)
        self.assertIn("IsMicrobialCountUpperLimitTest(testCode, fallbackName)", evaluator)
        self.assertIn("structuredLimit.HasValue && IsMicrobialCountUpperLimitTest", evaluator)
        self.assertIn('name.Contains("Total Yeast and Mold Count"', evaluator)
        self.assertIn('code.Equals("TYMC"', evaluator)

        submit = prm.split("private void BtnSubmitReview_Click", 1)[1].split(
            "private void BtnReview_Click", 1
        )[0]
        self.assertLess(
            submit.index("string overall = UpdateOverallInterpretation();"),
            submit.index("if (HasPendingResultChanges())"),
        )
        self.assertIn("derived interpretation changes", submit)

        quality_event = prm.split("private async void BtnQualityEvent_Click", 1)[1].split(
            "private int GetOpenPrmQualityEventId", 1
        )[0]
        self.assertLess(
            quality_event.index("string overall = UpdateOverallInterpretation();"),
            quality_event.index("if (HasPendingResultChanges())"),
        )
        self.assertIn("derived interpretation changes", quality_event)



    def test_v121_prm_derived_interpretation_noop_does_not_dirty_rows(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")

        self.assertIn("private static bool SetDerivedInterpretationIfChanged", prm)
        self.assertIn("if (string.Equals(current, next, StringComparison.Ordinal))", prm)
        self.assertIn("SetDerivedInterpretationIfChanged(row, interpretation);", prm)

        save = prm.split("private void SaveResultsFromGrid", 1)[1].split(
            "private static bool SetDerivedInterpretationIfChanged", 1
        )[0]
        self.assertIn("SetDerivedInterpretationIfChanged(row, interpretation);", save)
        self.assertNotIn('row["Interpretation"] = interpretation;', save)

        overall = prm.split("private string UpdateOverallInterpretation", 1)[1].split(
            "private string IssueCertificate", 1
        )[0]
        self.assertIn("SetDerivedInterpretationIfChanged(row, interpretation);", overall)
        self.assertNotIn('row["Interpretation"] = interpretation;', overall)

    def test_v122_prm_certificate_numbering_reconciles_existing_history(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")

        issue = prm.split("private async void BtnIssueCertificate_Click", 1)[1].split(
            "private void BtnPrintCertificate_Click", 1
        )[0]
        self.assertIn("await EnsurePrmCertificateSchemaReadyForActionAsync();", issue)

        generator = prm.split("private string GenerateCertificateNumber", 1)[1].split(
            "private static void EnsurePrmCertificateSchemaCompatibility", 1
        )[0]
        self.assertIn("@ExistingMax", generator)
        self.assertIn("FROM dbo.PRM_Certificates WITH (UPDLOCK, HOLDLOCK)", generator)
        self.assertIn("@ExistingMax + 1", generator)
        self.assertIn("WHILE EXISTS", generator)
        self.assertIn("WHERE CertificateNumber = @CertificateNumber", generator)
        self.assertNotIn("MERGE dbo.PRM_NumberSequences", generator)

        schema = prm.split("private static void EnsurePrmCertificateSchemaCompatibility", 1)[1].split(
            "private int GetNextRevisionNo", 1
        )[0]
        for token in (
            "PRM_NumberSequences", "PRM_Certificates", "PRM_CertificateHistory",
            "PRM_CertificateSnapshots", "PRM_ElectronicSignatures", "PRM_Samples", "PRM_SampleTests",
            "CertificateNumber", "ReportHash", "SnapshotHash", "MeaningOfSignature", "SampleStatus",
        ):
            self.assertIn(token, schema)
        self.assertIn("@Missing", schema)
        self.assertIn("STRING_AGG", schema)
        self.assertIn("System Preflight", schema)
        self.assertIn("explicit Development Database Maintenance", schema)

    def test_internal_trend_loads_water_and_em_points_after_window_render(self):
        reports = read_source_family("ReportsTrends.xaml.cs")

        loaded = reports.split("private void ReportsTrends_Loaded", 1)[1].split(
            "#endregion", 1
        )[0]
        self.assertIn("Dispatcher.BeginInvoke(DispatcherPriority.Background", loaded)
        self.assertIn("LoadSamplingPoints(GetSelectedCategory());", loaded)

        loader = reports.split("private void LoadSamplingPoints", 1)[1].split(
            "private void SetDefaultDates", 1
        )[0]
        self.assertIn("FROM WaterSamplingPoints", loader)
        self.assertIn("FROM SamplingPoints", loader)
        self.assertIn("ISNULL(Status, '') = 'Active'", loader)
        self.assertIn("ISNULL(IsActive, 1) = 1", loader)

    def test_database_maintenance_requires_tracked_workflow_windows_and_resumable_ddl(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")

        self.assertIn("private readonly HashSet<Window> _activeWorkflowWindows", main)
        self.assertIn("TrackWorkflowWindow(window);", main)
        self.assertIn("GetActiveWorkflowWindow()", main)
        self.assertIn("_activeWorkflowWindows.FirstOrDefault(window => window.IsVisible)", main)
        self.assertNotIn("Application.Current?.Windows\n                .OfType<Window>()", main)
        self.assertIn("Controlled schema maintenance runs only after this application instance has no active workflow windows", main)
        self.assertIn("Quiescing pooled application database sessions", migrator)
        self.assertIn("SqlConnection.ClearAllPools();", migrator)
        self.assertIn("CK_EMTrendReviewSnapshots_DatesV2", service)

    def test_database_maintenance_does_not_treat_untracked_blank_utility_window_as_workflow(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")

        guard = main.split("private async void BtnDatabaseMaintenance_Click", 1)[1].split(
            "ElectronicSignature signature = GetService<ElectronicSignature>();", 1
        )[0]
        tracker = main.split("private Window? GetActiveWorkflowWindow", 1)[1].split(
            "private void BtnSampleManagement_Click", 1
        )[0]

        self.assertIn("Window? openWorkflowWindow = GetActiveWorkflowWindow();", guard)
        self.assertNotIn("Application.Current", guard)
        self.assertIn("openWorkflowWindow.GetType().Name", guard)
        self.assertIn("_activeWorkflowWindows.Add(window)", tracker)
        self.assertIn("_activeWorkflowWindows.Remove(window)", tracker)

    def test_external_trend_all_areas_filters_to_reviewable_source_combinations(self):
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")

        self.assertIn("service.GetGenerationData(method, parameter, batchFrom, batchTo)", window)
        self.assertIn("generation.SourceManifestSha256ByArea.Keys", window)
        self.assertIn("reviewableAreaCodes.Contains", window)
        self.assertIn("outside the selected method/parameter scope were excluded", window)
        self.assertNotIn("completenessExceptions.Add(new DataCompletenessException(excludedCode", window)
        self.assertIn("public ExternalTrendGenerationData GetGenerationData", service)
        self.assertIn("SourceManifestBatchQuery", service)
        self.assertIn("b.Status = N'Approved'", service)
        self.assertIn("b.ApprovedAt IS NOT NULL", service)
        self.assertIn("MethodName = @MethodName", service)
        self.assertIn("r.ParameterName = @ParameterName", service)

    def test_external_trend_qa_approval_is_signed_atomic_and_source_anchored(self):
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml").read_text(encoding="utf-8-sig")
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        helper = read_database_helper_source()
        migration = (ROOT / "Database/Migrations/20260826_004_External_Trend_Current_State_Reconciliation.sql").read_text(encoding="utf-8-sig")

        self.assertIn('x:Name="btnApproveSnapshot"', xaml)
        self.assertIn('Content="QA Approve Snapshot"', xaml)
        self.assertIn('new(recordLabel, user, "External EM Trend Review Approval")', window)
        self.assertIn("service.SaveSignedReviewSnapshots", window)
        self.assertIn("service.GetGenerationData", window)
        self.assertIn("SourceManifestSha256ByArea", window)
        self.assertIn("BuildSourceManifestMapFromObservationRows", service)
        self.assertIn("ReadSourceManifestMapInTransaction", service)
        self.assertIn("comment.Length < 10", window)
        self.assertNotIn("service.SaveReviewSnapshot", window)

        self.assertIn("DatabaseHelper.ExecuteInTransaction", service)
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", service)
        self.assertIn("ExpectedSourceManifestSha256", service)
        self.assertIn("SourceBatchManifestSha256", service)
        self.assertIn("SnapshotHashSha256", service)
        self.assertIn("OUTPUT INSERTED.EMTrendReviewSnapshotID", service)
        self.assertIn("WITH (HOLDLOCK)", service)
        self.assertIn("manifestCache", service)
        self.assertIn("unitCache", service)
        self.assertIn("DatabaseHelper.AddAuditTrailAdvanced", service)
        self.assertIn("SourceCutoffAt", service)
        self.assertIn("SignatureMeaning", service)
        self.assertIn("SignatureReason", service)
        self.assertIn("SignedAt", service)

        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", helper)
        self.assertIn("return EnsureQaApprovalAuthorizationInTransaction", helper)
        self.assertIn("TR_EMTrendReviewSnapshots_Immutable", migration)
        self.assertIn("CK_EMTrendReviewSnapshots_ControlledV2", migration)

    def test_v129_internal_water_trend_separates_pw_ptw_limits_statistics_and_scales(self):
        reports = read_source_family("ReportsTrends.xaml.cs")

        self.assertIn("NormalizeWaterProfile", reports)
        self.assertIn('return "PTW";', reports)
        self.assertIn('return "PW";', reports)
        self.assertIn("BuildWaterProfileTrendChartModel", reports)
        self.assertIn("PW/PTW separated scales", reports)
        self.assertIn("if (hasSpecificationSnapshot)", reports)
        self.assertIn("A stored specification snapshot is immutable historical evidence", reports)
        self.assertIn("return (snapAlert, snapAction);", reports)
        self.assertIn("GetWaterProfileRuleLimits", reports)
        self.assertIn('return (null, 2.00d);', reports)
        self.assertIn('return (null, 500.00d);', reports)
        self.assertIn("PW and PTW are assessed independently using their own controlled limits", reports)
        self.assertIn("BuildStatisticsLines", reports)
        self.assertIn('identityColumn = hasWaterProfile ? "WaterProfile"', reports)
        self.assertIn("_HasSpecSnapshot", reports)
        self.assertNotIn("trustStoredStatus", reports)
        self.assertIn('"Status" => "Trend Status"', reports)

    def test_v129_external_trend_canonicalizes_area_aliases_and_deduplicates_observations(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")

        self.assertIn("CanonicalizeAreaCode", service)
        self.assertIn('StartsWith("EXT-EM-AA-"', service)
        self.assertIn('.Replace("BLUK", "BULK"', service)
        self.assertIn('.Replace("FIILING", "FILLING"', service)
        self.assertIn('.Replace("DIRTRY", "DIRTY"', service)
        self.assertIn('.Replace("EQUIPEMENT", "EQUIPMENT"', service)
        self.assertIn('.Replace("ARAE", "AREA"', service)
        self.assertIn("DeduplicateObservations", service)
        self.assertIn("ObservationFingerprint(DataRow row", service)
        self.assertIn("aliasCounts", service)
        self.assertIn('"ALIAS|" + item.Fingerprint', service)
        self.assertIn('ThenByDescending(item => item.Row.Field<DateTimeOffset>("ApprovedAt"))', service)
        self.assertNotIn("ROW_NUMBER() OVER", service)

    def test_v129_external_trend_is_three_cycle_and_separates_excursion_from_trend_conclusion(self):
        xaml = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")

        self.assertIn('Content="Custom date range" Tag="Custom" IsSelected="True"', xaml)
        self.assertIn("generatedPeriods.Count < 1 || generatedPeriods.Count > 3", window)
        self.assertIn("GetSummariesFromGeneration", window)
        self.assertIn("periods.Select((period, index)", window)
        self.assertNotIn(".Where(item => item.Summary.Count > 0)", window)
        self.assertIn("TrendConclusion", window)
        self.assertIn("ExcursionStatus", window)
        self.assertIn('return "INCREASING";', window)
        self.assertIn('return "DECREASING";', window)
        self.assertIn('return "NO CONSISTENT DIRECTION";', window)
        self.assertIn("No unapproved percentage threshold is invented", window)
        self.assertIn("request.Periods.Count < 1 || request.Periods.Count > 3", service)

    def test_v129_external_trend_approved_pdf_references_immutable_snapshot_approval(self):
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")

        self.assertIn("ExternalTrendReviewSaveResult? approvedReview", window)
        self.assertIn("EM_Three_Cycle_Trend_Review_FULL_APPROVED.pdf", window)
        self.assertIn("EM_Three_Cycle_Trend_Summary_APPROVED.pdf", window)
        self.assertIn("QA APPROVED - controlled presentation of immutable PharmaLIMS snapshot(s)", window)
        self.assertIn("QA ELECTRONIC APPROVAL RECORD", window)
        self.assertIn("Immutable Snapshot ID(s)", window)
        self.assertIn("approval.SnapshotIds", window)
        self.assertIn("QA Approved Snapshot Presentation", window)
        self.assertIn("CSV remains a draft data export", window)
        self.assertIn("ExternalTrendObservationSnapshot", window)
        self.assertIn("observations = request.Observations", service)
        self.assertIn("Trend observation snapshot data is required", service)

    def test_v129_external_trend_exports_real_data_completeness_appendix(self):
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("completenessExceptions", window)
        self.assertIn("AddPeriodCompletenessExceptions", window)
        self.assertIn("No numeric observations exist within the selected dates", window)
        self.assertIn("WriteDataCompletenessAppendix", window)
        self.assertIn("Data Completeness Exceptions", window)
        self.assertIn("Fewer than two exact numeric observations are available for quantitative period comparison", window)

    def test_v138_external_trend_pdf_is_concise_readable_and_numeric_date_aligned(self):
        xaml = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")

        selector_catalog = service.split("private DataTable GetSelectorCatalog", 1)[1].split(
            "public DataTable GetAreas", 1
        )[0]
        self.assertIn("r.ResultValue IS NOT NULL", selector_catalog)
        self.assertIn("MIN(CAST(r.RecordDateTime AS date)) AS FirstDate", selector_catalog)
        self.assertIn("MAX(CAST(r.RecordDateTime AS date)) AS LastDate", selector_catalog)
        self.assertIn('x:Name="chkIncludeAreaPages"', xaml)
        self.assertIn('IsChecked="False"', xaml)
        self.assertIn("WritePdfExecutiveSummary", window)
        self.assertIn("GetReportCompletenessExceptions", window)
        self.assertIn('"ALL INCLUDED AREAS"', window)
        self.assertIn("const int rowsPerPage = 19", window)
        self.assertIn("FitText", window)
        self.assertIn("medica-logo.png", window)


    def test_v130_external_trend_fingerprint_sql_closes_upper_before_concat_separator(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")

        self.assertNotIn("ObservationFingerprintExpression", service)
        self.assertNotIn("ObservationIdentityExpression", service)
        self.assertNotIn("UPPER(LTRIM(RTRIM(ISNULL({alias}", service)
        self.assertIn("ObservationFingerprint(DataRow row", service)


    def test_v131_external_trend_result_qualifier_is_schema_compatible(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")

        self.assertIn("HasResultQualifierColumn()", service)
        self.assertIn("COL_LENGTH(N'dbo.ExternalTrendImportRows', N'ResultQualifier')", service)
        self.assertIn('includeResultQualifier ? "r.ResultQualifier"', service)
        self.assertIn("CAST(N'' AS nvarchar(100)) AS ResultQualifier", service)
        self.assertIn('row["ResultQualifier"]', service)

    def test_v132_external_trend_generation_uses_fixed_batch_query_count(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")

        generation = service.split("public ExternalTrendGenerationData GetGenerationData", 1)[1].split("public DataTable GetResults", 1)[0]
        self.assertIn("DataTable rawRows = ReadApprovedObservationRows", generation)
        self.assertIn("BuildSourceManifestMapFromObservationRows", generation)
        self.assertNotIn("sourceBefore", generation)
        self.assertNotIn("sourceAfter", generation)
        self.assertNotIn("InteractiveReadTimeoutSeconds", service)
        self.assertIn("AppConfig.CommandTimeoutSeconds", service)
        self.assertNotIn("ROW_NUMBER() OVER", service)
        self.assertNotIn('CanonicalAreaExpression("r") = @AreaCode', service)
        self.assertIn("GROUP BY r.EntityCode", service)
        self.assertIn("service.GetGenerationData(method, parameter, batchFrom, batchTo)", window)
        self.assertNotIn("Task.WhenAll(periods.Select", window)
        self.assertNotIn("GetSummariesAsync", window)
        self.assertIn("LoadAllAreasFromGeneration", window)
        self.assertIn("manifestCache", service)
        self.assertIn("unitCache", service)

    def test_v196_external_trend_pdf_uses_medica_controlled_report_identity(self):
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("MEDICA PHARMACEUTICAL INDUSTRY", window)
        self.assertIn("Microbiology Department", window)
        self.assertIn("Form No: MQC-R-TREND-001", window)
        self.assertIn("Procedure Ref: MQC-G-0009", window)
        self.assertIn("Annexure: A11 / G1/1 (Graph) | A12 / F4/1 (Summary)", window)
        self.assertIn("EXTERNAL ENVIRONMENTAL MONITORING TREND REVIEW REPORT", window)
        self.assertNotIn('DrawCentered(gfx, "Use As Such"', window)
        self.assertIn("EM_Three_Cycle_Trend_Review_FULL_APPROVED.pdf", window)

    def test_v196_external_trend_report_interprets_results_and_builds_executive_summary(self):
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("BuildPortfolioAssessment", window)
        self.assertIn("EXECUTIVE INTERPRETATION", window)
        self.assertIn("Recommended QA disposition", window)
        self.assertIn("CalculateRegulatoryInterpretation", window)
        self.assertIn("CalculateRecommendedDisposition", window)
        self.assertIn('return "INVESTIGATION / CAPA REVIEW";', window)
        self.assertIn('return "ENHANCED QA REVIEW";', window)
        self.assertIn('return "DATA COMPLETENESS REVIEW";', window)
        self.assertIn('return "ROUTINE MONITORING";', window)
        self.assertIn("CalculateMeanShiftDescription", window)

    def test_v196_external_trend_report_has_regulatory_methodology_and_server_time(self):
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")

        self.assertIn("WriteRegulatoryMethodologyAppendix", window)
        self.assertIn("ICH Q10", window)
        self.assertIn("ICH Q9(R1)", window)
        self.assertIn("WHO GMP", window)
        self.assertIn("Automated interpretation does not replace QA judgement", window)
        self.assertIn("service.GetDatabaseTime()", window)
        self.assertIn("SELECT SYSDATETIMEOFFSET();", service)
        self.assertNotIn('Generated: {DateTime.Now', window)

    def test_v197_external_trend_window_is_responsive_and_actions_remain_visible(self):
        xaml = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml").read_text(encoding="utf-8-sig")

        self.assertIn('x:Name="tabsReview"', xaml)
        self.assertIn('Header="Visual Trend"', xaml)
        self.assertIn('Header="Period Summary"', xaml)
        self.assertIn('Header="Interpretation / QA"', xaml)
        self.assertIn('Grid.Row="3" Background="#FFFFFF"', xaml)
        self.assertIn('AutoGenerateColumns="False"', xaml)
        self.assertIn('SelectionChanged="DgSummary_SelectionChanged"', xaml)
        self.assertNotIn('AutoGenerateColumns="True"', xaml)

    def test_v197_external_trend_all_area_rows_drive_the_visible_chart(self):
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("private void DgSummary_SelectionChanged", window)
        self.assertIn("reviewPackets.FirstOrDefault", window)
        self.assertIn("BuildComparisonChart(packet.AreaCode, packet.Summaries)", window)
        self.assertIn("dgSummary.SelectedIndex = 0", window)

    def test_v197_external_trend_cycle_dates_are_synchronized_and_unambiguous(self):
        xaml = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn('x:Name="lblStartDateCaption"', xaml)
        self.assertIn('Text="From date"', xaml)
        self.assertIn('x:Name="pnlCycleLength"', xaml)
        self.assertIn("dpReviewStart.IsEnabled = custom", window)
        self.assertIn("pnlCycleLength.Visibility = custom ? Visibility.Collapsed : Visibility.Visible", window)
        self.assertIn("DateTime calculatedStart = end.Date.AddMonths(-totalMonths).AddDays(1)", window)
        self.assertIn("the start date was calculated automatically", window)

    def test_v209_external_import_finds_headers_after_introductory_rows(self):
        service = (ROOT / "Services/ExternalTrendImportService.cs").read_text(encoding="utf-8-sig")

        self.assertIn("int headerIndex = result", service)
        self.assertIn("Take(Math.Min(result.Count, 50))", service)
        self.assertIn("return result.Skip(headerIndex).ToList()", service)

    def test_v197_external_trend_never_infers_normal_from_missing_or_variable_controls(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("LimitsComplete", service)
        self.assertIn("LimitsConsistent", service)
        self.assertIn("UnitsConsistent", service)
        self.assertIn('return "NOT ASSESSED";', window)
        self.assertIn('return "DATA QUALITY / LIMIT REVIEW";', window)
        self.assertIn('return "LIMIT CHANGE / QA REVIEW";', window)
        self.assertIn("ConsistentObservationLimit", window)
        self.assertIn("each observation's own approved source limit", window)


    def test_v198_external_trend_uses_cached_selector_catalog_and_date_scoped_source_reads(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("private DataTable GetSelectorCatalog", service)
        self.assertIn("private DataTable? selectorCatalog", service)
        self.assertIn("GetSelectorCatalog(refresh: true)", service)
        self.assertIn("ExternalTrendSelectorTimeoutSeconds = 60", service)
        self.assertIn("ExternalTrendReviewTimeoutSeconds = 90", service)
        self.assertIn("r.RecordDateTime >= @From", service)
        self.assertIn("r.RecordDateTime < @ToExclusive", service)
        self.assertIn("reviewFrom = from.Date.ToString", service)
        self.assertIn("reviewTo = to.Date.ToString", service)
        self.assertIn("GetSourceManifestMap(methodName, parameterName, from, to)", service)
        self.assertIn("ReadSourceManifestMapInTransaction(connection, transaction, request.MethodName, request.ParameterName, requestFrom, requestTo)", service)
        self.assertIn("ReadControlledUnitMapInTransaction(connection, transaction, request.MethodName, request.ParameterName, requestFrom, requestTo)", service)
        self.assertNotIn("15-second interactive timeout", window)
        self.assertIn("No review or approval snapshot was saved", window)

    def test_v199_external_trend_selector_is_population_scoped_and_never_blank_by_binding_design(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("NormalizeTrendPopulation", service)
        self.assertIn('compact.Contains("ISO 8"', service)
        self.assertIn('compact.Contains("GRADE D"', service)
        self.assertIn("GetMethodsForAreas", service)
        self.assertIn("GetParametersForAreas", service)
        self.assertIn("ExternalTrendAreaOption", window)
        self.assertIn("visibleAreaOptions", window)
        self.assertIn("ApplyPopulationFilter(selectFirst: true)", window)
        self.assertIn("cboArea.SelectedIndex = 0", window)
        self.assertIn("CurrentVisibleAreaCodes", window)
        self.assertIn("service.GetMethodsForAreas(areaScope)", window)
        self.assertIn("service.GetParametersForAreas(areaScope, method)", window)
        self.assertNotIn("areas.DefaultView.RowFilter", window)
        self.assertIn("CurrentDataRangeLabel.ToLowerInvariant()", window)

    def test_v200_external_trend_generation_is_single_read_and_sargable(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        migration = (ROOT / "Database/Migrations/20260829_001_External_Trend_Generation_Performance_V3.sql").read_text(encoding="utf-8-sig")
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))

        generation = service.split("public ExternalTrendGenerationData GetGenerationData", 1)[1].split("public DataTable GetResults", 1)[0]
        self.assertIn("DataTable rawRows = ReadApprovedObservationRows", generation)
        self.assertIn("BuildSourceManifestMapFromObservationRows", generation)
        self.assertNotIn("sourceBefore", generation)
        self.assertNotIn("sourceAfter", generation)
        self.assertIn("MethodFilterPredicate", service)
        self.assertIn('MethodName = @MethodName', service)
        self.assertIn("OPTION (RECOMPILE)", service)
        self.assertIn("b.ImportNumber, b.SourceSystem, b.OriginalFileName, b.FileHashSha256", service)
        self.assertIn("catch (Exception ex)", window)
        self.assertIn("Handle SQL/query failures inside the worker thread", window)
        self.assertIn("IX_ExternalTrendImportRows_MethodParameterDate_20260829", migration)
        self.assertIn("IX_ExternalTrendImportBatches_ApprovedEM_20260829", migration)
        self.assertIn("IsPerformanceSchemaReady", service)
        self.assertIn("External Trend remains available", window)
        self.assertNotIn("Run controlled Database Maintenance, then reopen this screen", window)
        xaml = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml").read_text(encoding="utf-8-sig")
        self.assertIn('x:Name="btnGenerateReview"', xaml)
        self.assertIn("20260829_001", [item["versionKey"] for item in manifest["migrations"]])

    def test_v136_prm_historical_sample_open_is_read_only(self):
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/PrmSpecificationRepository.cs").read_text(encoding="utf-8-sig")

        loader = results.split("private void LoadApprovedTestsForSample", 1)[1].split(
            "private void BtnStartAnalysis_Click", 1
        )[0]
        self.assertIn("if (!requireConfiguredSpecification)", loader)
        self.assertIn("return;", loader)
        self.assertIn("EnsureSampleTestsAssigned(_selectedSampleId)", loader)
        self.assertIn("if (!_isLoading)", registration)
        self.assertIn("Never auto-link a current master profile to a historical sample", repository)
        resolver = repository.split("private static (string SpecificationNo, string SampleCategory) ResolveEffectiveSampleDefinition", 1)[1].split(
            "private static string FindSingleApprovedProfile", 1
        )[0]
        self.assertNotIn("UPDATE dbo.PRM_Samples", resolver)

    def test_v136_prm_specification_is_exact_item_stage_and_version_scoped(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/PrmSpecificationRepository.cs").read_text(encoding="utf-8-sig")
        migration = (ROOT / "Database/Migrations/20260824_001_PRM_Item_Stage_And_Investigation_Controls.sql").read_text(encoding="utf-8-sig")

        self.assertIn("TxtMasterItemCode", registration)
        self.assertIn("CmbMasterProductionStage", registration)
        self.assertIn("SpecificationVersionNo = @SpecificationVersionNo", registration)
        self.assertIn("specificationBindingChanged", registration)
        self.assertIn("originalItemCode", registration)
        self.assertIn("originalProductionStage", registration)
        self.assertIn("SourceSpecificationTestID, SpecificationVersionNo, SpecificationItemCode, SpecificationProductionStage", repository)
        self.assertIn("configured.SpecificationTestID, configured.VersionNo, configured.ItemCode, configured.ProductionStage", repository)
        self.assertIn("ALTER TABLE dbo.PRM_SpecificationTests ADD ProductionStage", migration)
        self.assertIn("ALTER TABLE dbo.PRM_Samples ADD SpecificationVersionNo", migration)
        self.assertIn("Historical samples and PRM_SampleTests are deliberately not rewritten", migration)

    def test_prm_registration_explains_and_repairs_empty_profile_selection_without_bypass(self):
        xaml = (ROOT / "ProductionRawMaterialSamples.xaml").read_text(encoding="utf-8-sig")
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")

        for scope_control in ("TxtMaterialCode", "TxtProductCode", "TxtStbProductCode"):
            control = xaml.split(f'x:Name="{scope_control}"', 1)[1].split("/>", 1)[0]
            self.assertIn('LostFocus="ProfileScope_LostFocus"', control)

        self.assertIn('x:Name="TxtProfileGuidance"', xaml)
        self.assertIn('Click="BtnCreateProfileForScope_Click"', xaml)
        self.assertIn("SetComboText(CmbMasterCategory, category)", registration)
        self.assertIn("TxtMasterItemCode.Text = itemCode", registration)
        self.assertIn("SetComboText(CmbMasterProductionStage, productionStage)", registration)
        self.assertIn("LoadApprovedSpecificationChoices();\n                specificationNo =", registration)
        self.assertIn("BuildNoApprovedProfileMessage(category, itemCode, productionStage)", registration)
        self.assertIn("configured.ApprovalStatus=N'Approved'", registration)
        self.assertIn("configured.IsActive=1", registration)
        self.assertIn("The controlled standard profile is unavailable", registration)
        self.assertIn("Database Maintenance/deployment", registration)

        validation = registration.split("private void ValidateForm()", 1)[1].split("private void InsertSample()", 1)[0]
        self.assertNotIn("AppConfig.IsDevelopment", validation)
        self.assertNotIn("AllowLegacyPrmSpecificationFallback", validation)
        self.assertIn("if (Convert.ToInt32(approvedSpecification", validation)

    def test_v141_prm_standard_profiles_are_reusable_and_exact_scope_still_wins(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/PrmSpecificationRepository.cs").read_text(encoding="utf-8-sig")
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        migration = (ROOT / "Database/Migrations/20260824_002_PRM_Standard_Microbiology_Profiles.sql").read_text(encoding="utf-8-sig")

        for profile in (
            "MIC-RM-STANDARD-001", "MIC-IP-ORAL-TABLET-STD-001",
            "MIC-FP-ORAL-TABLET-STD-001", "MIC-ST-ORAL-TABLET-STD-001",
        ):
            self.assertIn(profile, migration)

        # Specification master content is controlled master data, not a schema
        # readiness invariant. Adding an approved specification must not make
        # PRM registration/results/investigation report "schema not ready".
        self.assertNotIn("MIC-RM-STANDARD-001", readiness)
        self.assertNotIn("COUNT(1)=28", readiness)

        for organism in ("SALMONELLA", "ECOLI", "SAUREUS", "PAERUGINOSA", "CALBICANS"):
            self.assertIn(organism, migration)

        self.assertIn("N'NMT 1000 CFU/g or mL'", migration)
        self.assertIn("N'NMT 100 CFU/g or mL'", migration)
        self.assertIn("N'NMT 100 CFU/g'", migration)
        self.assertIn("N'NMT 10 CFU/g'", migration)
        self.assertIn("WHERE MatchRank=(SELECT MAX(MatchRank) FROM Candidates)", registration)
        self.assertGreaterEqual(repository.count("IN (UPPER(context.ItemCode),N'*')"), 2)
        self.assertGreaterEqual(repository.count("IN (UPPER(context.ProductionStage),N'*')"), 2)
        self.assertNotIn("activeMigrationKey", readiness)
        self.assertNotIn("ApplyControlledMigrationAsync", readiness)

    def test_v142_operational_windows_are_responsive_and_enter_navigation_is_shared(self):
        helper = (ROOT / "Infrastructure/WindowUsability.cs").read_text(encoding="utf-8-sig")

        for required_control in (
            "TryMoveFocusOnEnter",
            "FocusNavigationDirection.Next",
            "FindAncestor<DataGrid>",
            "FindAncestor<ButtonBase>",
            "textBox.AcceptsReturn",
            "comboBox.IsDropDownOpen",
            "BringIntoView",
            "SelectAll",
            "HorizontalScrollBarVisibility = ScrollBarVisibility.Auto",
            "ResizeMode.CanResizeWithGrip",
            "WindowState.Maximized",
        ):
            self.assertIn(required_control, helper)

        full_screen_windows = (
            "AISystemReview.xaml", "CultureMediaPreparation.xaml", "EMCollectionDialog.xaml",
            "EMLimitsManagement.xaml", "EMPlanning.xaml", "EMResultsEntry.xaml",
            "EMTrendReport.xaml", "ExternalTrendImportDialog.xaml",
            "ExternalTrendThreeCycleReviewWindow.xaml", "MainWindow.xaml",
            "NewSampleDialog.xaml", "PRMQualityEventInvestigation.xaml",
            "ProductionRawMaterialResults.xaml", "ProductionRawMaterialSamples.xaml",
            "QualityEventInvestigation.xaml", "ReportCertificate.xaml", "ReportsTrends.xaml",
            "ResultsEntry.xaml", "SampleDetails.xaml", "SampleManagement.xaml", "SystemPreflight.xaml",
        )
        for file_name in full_screen_windows:
            xaml = (ROOT / file_name).read_text(encoding="utf-8-sig")
            self.assertIn('WindowState="Maximized"', xaml, file_name)

        for file_name in (
            "NewSampleDialog.xaml.cs", "EMCollectionDialog.xaml.cs", "EMResultsEntry.xaml.cs",
        ):
            code = (ROOT / file_name).read_text(encoding="utf-8-sig")
            self.assertIn("WindowUsability.TryMoveFocusOnEnter(e)", code, file_name)

        samples = (ROOT / "ProductionRawMaterialSamples.xaml").read_text(encoding="utf-8-sig")
        em_results = (ROOT / "EMResultsEntry.xaml").read_text(encoding="utf-8-sig")
        self.assertIn('HorizontalScrollBarVisibility="Auto"', samples)
        # EM Results keeps horizontal scrolling at the DataGrid level only.
        # The page-level ScrollViewer must constrain content to the viewport,
        # otherwise WPF measures the nested grids at infinite width and can
        # collapse/reflow the explicit result columns at runtime.
        self.assertIn('HorizontalScrollBarVisibility="Disabled"', em_results)
        self.assertGreaterEqual(em_results.count('HorizontalScrollBarVisibility="Auto"'), 2)
        self.assertNotIn(
            '<ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto">',
            em_results,
        )
        self.assertIn('x:Name="dgActiveAirPlates"', em_results)
        self.assertIn('x:Name="dgSettlePlates"', em_results)
        self.assertGreaterEqual(em_results.count('HorizontalAlignment="Stretch"'), 2)

        sample_management = (ROOT / "SampleManagement.xaml").read_text(encoding="utf-8-sig")
        prm_results = (ROOT / "ProductionRawMaterialResults.xaml").read_text(encoding="utf-8-sig")
        self.assertIn('WindowUsability.KeepEnterBehavior="True"', sample_management)
        self.assertIn('WindowUsability.KeepEnterBehavior="True"', prm_results)

    def test_v143_prm_quality_event_button_is_visible_beside_approval_and_wraps(self):
        xaml = (ROOT / "ProductionRawMaterialResults.xaml").read_text(encoding="utf-8-sig")
        code = read_source_family("ProductionRawMaterialResults.xaml.cs")

        workflow = xaml.split('x:Name="WorkflowActionsPanel"', 1)[1].split("</WrapPanel>", 1)[0]
        self.assertIn('x:Name="BtnQualityEvent"', workflow)
        self.assertIn('Click="BtnQualityEvent_Click"', workflow)
        self.assertLess(workflow.index('x:Name="BtnApprove"'), workflow.index('x:Name="BtnQualityEvent"'))
        self.assertLess(workflow.index('x:Name="BtnQualityEvent"'), workflow.index('x:Name="BtnIssueCertificate"'))
        self.assertIn('Grid.Row="1"', workflow)
        self.assertIn('HorizontalAlignment="Left"', workflow)
        self.assertIn("Panel targetPanel = WorkflowActionsPanel ?? FindWorkflowPanel();", code)
        self.assertIn("button.Visibility = Visibility.Visible", code)

    def test_v136_prm_stability_and_reports_preserve_traceability(self):
        registration = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        template = (ROOT / "Services/PRMCertificateTemplate.cs").read_text(encoding="utf-8-sig")

        self.assertIn("Chamber No. is required for Stability samples", registration)
        self.assertIn("Protocol No. is required for Stability samples", registration)
        self.assertIn("@StabilityChamberNo", registration)
        self.assertIn("@StabilityProtocolNo", registration)
        self.assertIn("if (value < 0m)", results)
        self.assertIn('canonical.Append("SpecificationVersionNo=")', results)
        self.assertIn('canonical.Append("StabilityChamberNo=")', results)
        self.assertIn('"Specification No."', template)
        self.assertIn('"Specification Version"', template)
        self.assertIn('"Chamber No."', template)
        self.assertIn('"Protocol No."', template)

    def test_v136_prm_quality_event_is_authorized_serialized_and_fail_closed(self):
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        investigation = (ROOT / "PRMQualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")
        helper = read_database_helper_source()

        self.assertIn("EnsureQualityEventManagementAuthorizationInTransaction", results)
        self.assertIn("sys.sp_getapplock", results)
        self.assertIn("WITH(UPDLOCK,HOLDLOCK)", results)
        self.assertIn("SubmitInvestigationToQa(signature)", investigation)
        self.assertIn("Quality Event Submit to QA", investigation)
        self.assertIn("No controlled PRM investigation checklist is configured", investigation)
        self.assertIn("The PRM Quality Event is closed and cannot be modified", investigation)
        self.assertIn("EnsureQualityEventManagementAuthorizationInTransaction", helper)
        self.assertIn("WHEN SourceModule=N'PRM' THEN 'PRM Microbiology'", helper)

    def test_v137_certificate_snapshot_json_is_validated_before_read_or_write(self):
        helper = read_database_helper_source()
        report = (ROOT / "ReportCertificate.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertGreaterEqual(helper.count("ISJSON(snapshot.SnapshotContent)=1"), 2)
        self.assertIn("ValidateCertificateSnapshotJson", helper)
        self.assertIn("JsonDocument.Parse(content)", helper)
        self.assertIn('root.TryGetProperty("Tests"', helper)
        self.assertIn("malformed JSON", helper)
        self.assertIn("if (!ValidateCertificateSnapshotJson(snapshotContent", helper)
        self.assertIn("ValidateIssuedCertificateSnapshot(sampleId", report)
        self.assertIn("Certificate/report loading and printing were blocked", report)

    def test_v144_prm_quality_event_schema_and_cross_module_links_are_reconciled(self):
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        investigation = (ROOT / "PRMQualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")
        service = (ROOT / "Services/Investigations/PRMQualityEventInvestigationService.cs").read_text(encoding="utf-8-sig")
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        migration = (ROOT / "Database/Migrations/20260826_001_PRM_Quality_Event_Current_Baseline.sql").read_text(encoding="utf-8-sig")

        create = results.split("private int CreatePrmQualityEvent()", 1)[1].split(
            "private void EnsureQualityEventButtonExists", 1
        )[0]
        self.assertIn("GeneratePrmQualityEventNumber(connection, transaction)", create)
        self.assertIn("INSERT dbo.QualityEventActions", create)

        affected = results.split("private void AddAffectedPrmResultsToQualityEvent(", 1)[1].split(
            "private DataRow GetCurrentSampleRow", 1
        )[0]
        self.assertIn("SourceModule = N'PRM'", affected)
        self.assertIn("SourceResultID = @SourceResultID", affected)

        self.assertIn("QualityEventCompatibilityReadySql", readiness)
        self.assertNotIn("ApplyControlledMigrationAsync", readiness)
        self.assertIn("UX_QualityEventAffectedResults_Source_20260826", migration)
        self.assertIn("TRG_QualityEventPrintHistory_AppendOnly", migration)
        self.assertNotRegex(migration, r"(?i)\bDROP\s+(TABLE|COLUMN|CONSTRAINT|INDEX)\b|\bTRUNCATE\s+TABLE\b|\bDELETE\s+FROM\b")
        self.assertNotRegex(migration, r"(?i)ALTER\s+TABLE[^;]+ALTER\s+COLUMN")
        self.assertNotIn("SET SampleID = NULL", migration)
        self.assertNotIn("SET QualityEventID=NULL", migration)

        header_query = service.split("public static DataTable GetPRMQualityEventHeader", 1)[1].split(
            "private static DataTable GetPRMSampleFast", 1
        )[0]
        self.assertIn("UPPER(LTRIM(RTRIM(ISNULL(SourceModule, N'')))) = N'PRM'", header_query)
        self.assertIn("CASE WHEN SourceModule = N'PRM' THEN SourceResultID ELSE SampleTestID END AS SampleTestID", service)

        close_handler = investigation.split("private void BtnCloseInvestigation_Click", 1)[1].split(
            "private void BtnCloseWindow_Click", 1
        )[0]
        self.assertIn("DatabaseHelper.SaveQualityEventInvestigation", close_handler)
        self.assertIn("SavePRMQualityEventChecklistAnswers", close_handler)
        self.assertIn("Quality Event Closure", close_handler)

    def test_v145_every_literal_controlled_migration_request_exists_in_manifest(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        controlled_keys = {entry["versionKey"] for entry in manifest["migrations"]}
        requested_keys = set()

        for source_path in ROOT.rglob("*.cs"):
            source = source_path.read_text(encoding="utf-8-sig")
            requested_keys.update(re.findall(r'ApplyControlledMigrationAsync\("([0-9A-Z_]+)"\)', source))

        # Operational windows must never mutate schema on demand. The migrator API remains
        # available only for explicit maintenance tooling and controlled deployment support.
        self.assertEqual(set(), requested_keys)
        self.assertEqual(set(), requested_keys - controlled_keys)

        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        for operational in (
            "EMResultsEntry.xaml.cs", "ExternalTrendThreeCycleReviewWindow.xaml.cs",
            "ProductionRawMaterialResults.xaml.cs", "ProductionRawMaterialSamples.xaml.cs",
            "QualityEventInvestigation.xaml.cs",
        ):
            self.assertNotIn("ApplyControlledMigrationAsync", (ROOT / operational).read_text(encoding="utf-8-sig"))
        self.assertNotIn("ApplyControlledMigrationAsync", readiness)

    def test_v162_auxiliary_projects_are_isolated_from_wpf_compile(self):
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        self.assertIn('<Compile Remove="tools\\**\\*.cs" />', project)
        self.assertIn('<Compile Remove="tests\\**\\*.cs" />', project)
        self.assertIn('Exclude="App.xaml;bin\\**;obj\\**;tools\\**;tests\\**"', project)

        # Both auxiliary entry points intentionally use a global Program class,
        # so compiling either into the WPF project would recreate CS0101.
        maintenance = (ROOT / "tools/PharmaLIMS.DatabaseMaintenance/Program.cs").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn("internal static class Program", maintenance)
        self.assertIn("internal static class Program", integration)

    def test_v161_structural_module_closure_controls(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        main_xaml = (ROOT / "MainWindow.xaml").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        water_register = (ROOT / "NewSampleDialog.xaml.cs").read_text(encoding="utf-8-sig")
        water_results = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        helper = read_database_helper_source()
        em = read_source_family("EMResultsEntry.xaml.cs")
        external = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        reports = read_source_family("ReportsTrends.xaml.cs")
        users = (ROOT / "Repositories/UserRepository.cs").read_text(encoding="utf-8-sig")
        ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        maintenance = (ROOT / "tools/PharmaLIMS.DatabaseMaintenance/Program.cs").read_text(encoding="utf-8-sig")
        maintenance_script = (ROOT / "scripts/Invoke-DevelopmentDatabaseMaintenance.ps1").read_text(encoding="utf-8-sig")

        self.assertIn('Content="System Preflight"', main_xaml)
        self.assertIn('Content="Database Maintenance"', main_xaml)
        self.assertIn("RefreshSystemReadinessAsync", main)
        self.assertIn("EnsureRuntimeReadyForWorkflow", main)
        self.assertIn("PRM_Samples WHERE SampleStatus IS NULL OR SampleStatus NOT IN", main)
        self.assertIn("PRM_Samples WHERE CreatedDate < DATEADD", main)
        self.assertNotIn("PRM_Samples WHERE ISNULL(Status", main)
        self.assertIn("await migrator.ApplyRequiredUpdatesAsUserAsync(Login.CurrentUser);", main)
        self.assertIn("ElectronicSignature signature = GetService<ElectronicSignature>();", main)
        self.assertIn("CheckCriticalSchemaColumnsAsync", preflight)
        self.assertIn("CheckControlledMasterDataAsync", preflight)
        self.assertIn('item.Area == "Database Schema"', preflight)
        self.assertIn('item.Area == "Deployment"', preflight)

        self.assertIn('"Water Sample Registered"', water_register)
        self.assertLess(water_register.index('"Water Sample Registered"'), water_register.index("tran.Commit();", water_register.index('"Water Sample Registered"')))
        self.assertIn("Sample registration is blocked to prevent assigning guessed or hard-coded tests", helper)
        self.assertNotIn("new[] { 1, 2, 3", helper)
        self.assertIn("GetPersistedResultValue", water_results)
        self.assertIn("CreateOrUpdateQualityEventForOOS(\n                            con,\n                            tran,", water_results)
        oos_atomic = helper.split("public static string CreateOrUpdateQualityEventForOOS(", 2)[2].split("public static string CreateOrUpdateQualityEventForAlert", 1)[0]
        self.assertIn("SqlTransaction tx", oos_atomic)
        self.assertIn("affectedInserted", oos_atomic)
        self.assertIn("The OOS Quality Event has no linked affected result", oos_atomic)
        self.assertIn("AddAuditTrailAdvanced", oos_atomic)

        self.assertNotIn("ApplyControlledMigrationAsync", em)
        self.assertNotIn("ApplyControlledMigrationAsync", external)
        self.assertIn("DatabaseHelper.ExecuteInTransaction", em)
        self.assertIn("No EM ALERT/ACTION/OOS result was linked", em)

        self.assertIn("A stored specification snapshot is immutable historical evidence", reports)
        self.assertIn("Direct user creation through UserRepository is disabled", users)
        self.assertIn("UserAdministrationService", users)
        self.assertIn("Unified source / build / SQL / WPF runtime validation", ci)
        self.assertIn("Invoke-ReleaseValidation.ps1 -RunDatabaseIntegration -RunRuntimeSmoke", ci)
        release_runner = (ROOT / "scripts/Invoke-ReleaseValidation.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("MSSQLLocalDB", release_runner)
        self.assertIn("Database migration/schema integration PASS", integration)
        self.assertIn("VerifySchemaAsync", integration)
        self.assertIn("--confirm-development-maintenance", maintenance)
        self.assertIn("AppConfig.IsDevelopment", maintenance)
        self.assertIn("ApplyRequiredUpdatesAsync", maintenance)
        self.assertIn("SystemPreflightService", maintenance)
        self.assertIn("report.CanProceed", maintenance)
        self.assertIn("--confirm-development-maintenance", maintenance_script)

    def test_v147_legacy_quality_event_prerequisite_preserves_evidence_and_precedes_003(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        entries = manifest["migrations"]
        current = next(entry for entry in entries if entry["versionKey"] == "20260826_001")
        retired = {"20260825_002", "20260825_003", "20260825_004", "20260825_005", "20260825_006", "20260824_003"}
        for entry in entries:
            if entry["versionKey"] in retired:
                self.assertEqual("20260826_001", entry.get("supersededBy"), entry["versionKey"])

        migration_path = ROOT / "Database" / current["file"]
        migration = migration_path.read_text(encoding="utf-8-sig")
        self.assertEqual(hashlib.sha256(migration_path.read_bytes()).hexdigest(), current["sha256"])
        self.assertIn("Existing PRM samples, results, Quality Events, signatures, actions, answers", migration)
        self.assertNotRegex(migration, r"(?i)\bDROP\s+(TABLE|COLUMN|CONSTRAINT|INDEX)\b|\bTRUNCATE\s+TABLE\b|\bDELETE\s+FROM\b")
        self.assertNotRegex(migration, r"(?i)ALTER\s+TABLE[^;]+ALTER\s+COLUMN")
        self.assertNotIn("SET QualityEventID=NULL", migration)
        self.assertNotIn("SET SampleID = NULL", migration)

        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn("retired and will be skipped in favor of current migration", migrator)
        self.assertIn("Runtime code must not request retired migrations", migrator)

    def test_v160_prm_certificate_snapshot_reloads_persisted_issue_signature(self):
        source = read_source_family("ProductionRawMaterialResults.xaml.cs")
        issue = source.split("private string IssueCertificate", 1)[1].split("private void CancelCertificate", 1)[0]
        loader = source.split("private DataTable LoadPrmElectronicSignatureSnapshotInTransaction", 1)[1].split("private static string LoadPrmCertificateSnapshotHtml", 1)[0]

        self.assertIn("string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction", issue)
        self.assertIn("AddPrmElectronicSignatureInTransaction(conn, tx, issueAction, signature, signerRole);", issue)
        self.assertIn("signatureSnapshot = LoadPrmElectronicSignatureSnapshotInTransaction(conn, tx);", issue)
        self.assertLess(
            issue.index("AddPrmElectronicSignatureInTransaction(conn, tx, issueAction, signature, signerRole);"),
            issue.index("signatureSnapshot = LoadPrmElectronicSignatureSnapshotInTransaction(conn, tx);")
        )
        self.assertLess(
            issue.index("signatureSnapshot = LoadPrmElectronicSignatureSnapshotInTransaction(conn, tx);"),
            issue.index("PRMCertificateTemplate.Build(sample, results, certificateSnapshotRow, signatureSnapshot)")
        )
        self.assertIn("FROM dbo.PRM_ElectronicSignatures S WITH(HOLDLOCK)", loader)
        self.assertIn("LEFT JOIN dbo.Users U WITH(HOLDLOCK)", loader)
        self.assertIn("WHERE S.SampleID=@SampleID", loader)
        self.assertNotIn("Rows.Add", loader)
        self.assertNotIn("AppendPrmSignatureSnapshot", source)
        self.assertIn('string issuanceStage = "issuer authorization";', issue)
        self.assertIn('issuanceStage = "immutable snapshot generation";', issue)

    def test_v149_regulated_raw_base_exception_messages_are_redacted(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        investigation = read_source_family("QualityEventInvestigation.xaml.cs")

        self.assertNotIn("ex.GetBaseException().Message", planning)
        self.assertNotIn("ex.GetBaseException().Message", investigation)
        self.assertIn('UserFacingError.SafeMessage(ex, "Environmental Monitoring operation")', planning)
        self.assertIn('UserFacingError.SafeMessage(ex, "Quality Event loading")', investigation)

    def test_v149_em_permission_ui_load_is_async_and_cached(self):
        em = read_source_family("EMResultsEntry.xaml.cs")
        constructor = em.split("public EMResultsEntry(IServiceProvider serviceProvider", 1)[1].split("public void OpenEvent", 1)[0]
        loaded = em.split("private async void Window_Loaded", 1)[1].split("private void Window_PreviewKeyDown", 1)[0]
        permission_loader = em.split("private async Task LoadUserPermissionsAsync", 1)[1].split("private void SetResultsGridsReadOnly", 1)[0]
        workflow = em.split("private void UpdateWorkflowButtons", 1)[1].split("private bool IsEMResultsLockedForEditing", 1)[0]

        self.assertNotIn("LoadUserPermissions", constructor)
        self.assertIn("await LoadUserPermissionsAsync();", loaded)
        self.assertIn("await Task.Run", permission_loader)
        for marker in ("currentCanEditResults", "currentCanSubmitForReview", "currentCanReviewResults", "currentCanApproveResults", "currentCanManageSettings"):
            self.assertIn(marker, permission_loader)
        self.assertNotIn("DatabaseHelper.CanEditResults", workflow)
        self.assertNotIn("DatabaseHelper.CanSubmitForReview", workflow)
        self.assertNotIn("DatabaseHelper.CanReviewResults", workflow)
        self.assertNotIn("DatabaseHelper.CanApproveResults", workflow)
        self.assertNotIn("DatabaseHelper.CanManageSettings", workflow)
        self.assertIn('DatabaseHelper.CanEditResults(Login.CurrentUser ?? "")', em)
        self.assertIn('DatabaseHelper.CanSubmitForReview(Login.CurrentUser ?? "")', em)
        self.assertIn('DatabaseHelper.CanReviewResults(Login.CurrentUser ?? "")', em)
        self.assertIn('DatabaseHelper.CanQaApproveResults(Login.CurrentUser ?? "")', em)

    def test_v165_prm_migration_guidance_uses_explicit_maintenance_and_manifest_order(self):
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        entries = manifest["migrations"]
        keys = [entry["versionKey"] for entry in entries]

        self.assertNotIn("20260825_003", readiness)
        self.assertNotIn("20260825_006", readiness)
        self.assertIn("signed Database Maintenance action", readiness)
        self.assertIn("approved database migration manifest through the controlled deployment process", readiness)
        self.assertIn("commandTimeoutSeconds: ReadinessCommandTimeoutSeconds", readiness)
        for forbidden in ("ApplyControlledMigrationAsync", "ApplyRequiredUpdatesAsync", "EnsureDevelopmentCurrentSchemaAsync", "StartupDatabaseMigrator"):
            self.assertNotIn(forbidden, readiness)

        self.assertLess(keys.index("20260826_001"), keys.index("20260826_002"))
        current = next(entry for entry in entries if entry["versionKey"] == "20260826_001")
        hardening = next(entry for entry in entries if entry["versionKey"] == "20260826_002")
        self.assertEqual("20260826_001_PRM_Quality_Event_Current_Baseline", current["description"])
        self.assertEqual("20260826_002_PRM_Quality_Event_Operational_Readiness", hardening["description"])

    def test_v164_final_review_exposes_explicit_all_tests_filter(self):
        reports = read_source_family("ReportsTrends.xaml.cs")
        self.assertIn('allTestsRow["TestName"] = "All Tests";', reports)
        self.assertIn('allTestsRow["TestID"] = 0;', reports)
        self.assertIn('return selectedTestId > 0 ? selectedTestId : null;', reports)
        self.assertIn('selectedTest.Equals("All Tests", StringComparison.OrdinalIgnoreCase)', reports)
        self.assertIn('cboTest.SelectedIndex = 0;', reports)

    def test_v164_final_review_prm_investigation_ui_matches_available_workflow(self):
        xaml = (ROOT / "ProductionRawMaterialResults.xaml").read_text(encoding="utf-8-sig")
        code = read_source_family("ProductionRawMaterialResults.xaml.cs")
        investigation = (ROOT / "PRMQualityEventInvestigation.xaml").read_text(encoding="utf-8-sig")
        self.assertIn('x:Name="BtnQualityEvent" Content="Investigation"', xaml)
        self.assertIn('Content = "Investigation"', code)
        self.assertIn('string content = "Investigation";', code)
        self.assertNotIn('The actual Close Investigation button will be added', investigation)
        self.assertIn('Content="Close Investigation"', investigation)

    def test_v165_prm_certificate_schema_is_read_only_during_workflow_actions(self):
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        self.assertIn("EnsurePrmCertificateSchemaReadyForActionAsync", results)
        gate = results.split("private static Task EnsurePrmCertificateSchemaReadyForActionAsync", 1)[1].split(
            "private static void EnsurePrmCertificateSchemaCompatibility", 1
        )[0]
        self.assertIn("EnsurePrmCertificateSchemaCompatibility();", gate)
        self.assertIn("Task.CompletedTask", gate)
        self.assertNotIn("StartupDatabaseMigrator", gate)
        self.assertNotIn("EnsureDevelopmentCurrentSchemaAsync", gate)
        self.assertNotIn("ApplyRequiredUpdatesAsync", gate)
        issue = results.split("private async void BtnIssueCertificate_Click", 1)[1].split(
            "private void BtnPrintCertificate_Click", 1
        )[0]
        self.assertIn("await EnsurePrmCertificateSchemaReadyForActionAsync()", issue)
        reissue = results.split("private async void BtnReissueCertificate_Click", 1)[1].split(
            "private void", 1
        )[0]
        self.assertIn("await EnsurePrmCertificateSchemaReadyForActionAsync()", reissue)

    def test_v164_ai_system_review_uses_current_prm_sample_status_column(self):
        review = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("PRM_Samples WHERE SampleStatus IN", review)
        self.assertIn("PRM_Samples WHERE SampleStatus = N'Reviewed'", review)
        self.assertNotIn("PRM_Samples WHERE Status IN", review)
        self.assertNotIn("PRM_Samples WHERE Status = N'Reviewed'", review)

    def test_v165_prm_investigation_readiness_never_migrates_from_the_button(self):
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        migration = (ROOT / "Database/Migrations/20260826_002_PRM_Quality_Event_Operational_Readiness.sql").read_text(encoding="utf-8-sig")

        self.assertNotIn("StandardProfilesReadySql", readiness)
        self.assertNotIn("COUNT(1)=28", readiness)
        for forbidden in ("ApplyControlledMigrationAsync", "ApplyRequiredUpdatesAsync", "EnsureDevelopmentCurrentSchemaAsync", "StartupDatabaseMigrator"):
            self.assertNotIn(forbidden, readiness)
        self.assertNotIn("DevelopmentMaintenanceGate", migrator)
        self.assertIn("commandTimeoutSeconds: ReadinessCommandTimeoutSeconds", readiness)
        self.assertIn("name=N'SampleTestID'", readiness)
        self.assertIn("is_nullable=1", readiness)
        self.assertIn("signed Database Maintenance action", readiness)

        handler = results.split("private async void BtnQualityEvent_Click", 1)[1].split(
            "private void", 1
        )[0]
        schema_gate = handler.index("EnsurePrmQualityEventSchemaReadyForActionAsync")
        legacy_reconciliation_probe = handler.index("HasUnreconciledLegacyPrmEvidence")
        self.assertLess(schema_gate, legacy_reconciliation_probe)

        create = results.split("private int CreatePrmQualityEvent()", 1)[1].split(
            "private void EnsureQualityEventButtonExists", 1
        )[0]
        self.assertIn("OUTPUT inserted.QualityEventID", create)
        self.assertNotIn("SCOPE_IDENTITY", create)
        self.assertIn("NEXT VALUE FOR", migration)
        self.assertIn("ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN SampleTestID INT NULL", migration)
        central_gate = results.split("private void EnsureCentralQualityEventCompatibility()", 1)[1].split(
            "private void AddAffectedPrmResultsToQualityEvent", 1
        )[0]
        self.assertIn("commandTimeoutSeconds: 5", central_gate)
        self.assertNotIn("ExecuteScalar", central_gate)
        self.assertIn("TRG_QualityEventPrintHistory_AppendOnly", migration)
        self.assertIn("Database Maintenance", migration)
        self.assertIn("approved controlled deployment process", migration)
        for destructive in ("DELETE FROM dbo.QualityEvents", "TRUNCATE TABLE", "DROP TABLE"):
            self.assertNotIn(destructive, migration)

    def test_v148_legacy_quality_event_structure_is_reconciled_before_003(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        entries = manifest["migrations"]
        current = next(entry for entry in entries if entry["versionKey"] == "20260826_001")
        old_003 = next(entry for entry in entries if entry["versionKey"] == "20260825_003")
        bridge_006 = next(entry for entry in entries if entry["versionKey"] == "20260825_006")
        self.assertEqual("20260826_001", old_003.get("supersededBy"))
        self.assertEqual("20260826_001", bridge_006.get("supersededBy"))
        self.assertEqual("20260826_001_PRM_Quality_Event_Current_Baseline", current["description"])

        current_sql = (ROOT / "Database" / current["file"]).read_text(encoding="utf-8-sig")
        self.assertIn("Runtime workflow buttons never change database schema", current_sql)
        self.assertIn("No existing relationship value is cleared or rewritten", current_sql)
        self.assertNotIn("PRM_QualityEventRuntimeBridgeEvidence", current_sql)

        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("activeMigrationKey", readiness)
        self.assertNotIn("ApplyControlledMigrationAsync", readiness)

        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn('migration.TryGetProperty("supersededBy"', preflight)

    def test_v150_affected_result_structure_prerequisite_repairs_missing_legacy_projection(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        current = next(entry for entry in manifest["migrations"] if entry["versionKey"] == "20260826_001")
        migration_path = ROOT / "Database" / current["file"]
        migration = migration_path.read_text(encoding="utf-8-sig")

        self.assertIn("CREATE TABLE dbo.QualityEventAffectedResults", migration)
        self.assertIn("AffectedResultID INT IDENTITY(1,1)", migration)
        self.assertIn("ALTER TABLE dbo.QualityEventAffectedResults ADD QualityEventID INT NULL", migration)
        self.assertIn("ALTER TABLE dbo.QualityEventAffectedResults ADD SourceModule NVARCHAR(80) NULL", migration)
        self.assertIn("ALTER TABLE dbo.QualityEventAffectedResults ADD SourceResultID INT NULL", migration)
        self.assertIn("Duplicate PRM affected-result source links exist and require data review", migration)
        self.assertNotIn("SET QualityEventID=EventID", migration)
        self.assertNotIn("TRY_CONVERT", migration)
        self.assertNotRegex(migration, r"(?i)ALTER\s+TABLE[^;]+ALTER\s+COLUMN")

    def test_v151_minimal_quality_event_identity_baseline_precedes_broader_legacy_repair(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        entries = manifest["migrations"]
        current = next(entry for entry in entries if entry["versionKey"] == "20260826_001")
        current_sql = (ROOT / "Database" / current["file"]).read_text(encoding="utf-8-sig")

        self.assertIn("QualityEvents.QualityEventID must be a non-null INT identity/key column", current_sql)
        self.assertIn("single-column unique key", current_sql)
        self.assertIn("CREATE TABLE dbo.QualityEventAffectedResults", current_sql)
        self.assertIn("FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID)", current_sql)
        self.assertNotRegex(current_sql, r"(?i)\bDROP\s+(TABLE|COLUMN|CONSTRAINT|INDEX)\b|\bTRUNCATE\s+TABLE\b|\bDELETE\s+FROM\b")

        for key in ("20260825_005", "20260825_004", "20260825_002", "20260825_006", "20260825_003", "20260824_003"):
            entry = next(item for item in entries if item["versionKey"] == key)
            self.assertEqual("20260826_001", entry.get("supersededBy"), key)

        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("QualityEventMinimalIdentityReadySql", readiness.split("private static async Task<PrmSchemaReadinessResult> EnsureReadyAsync",1)[1])
        self.assertNotIn("ApplyControlledMigrationAsync", readiness)


    def test_v215_development_preflight_fails_closed_until_schema_is_ready(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        database = (ROOT / "Infrastructure/DatabaseConnection.cs").read_text(encoding="utf-8-sig")

        # v224: the bounded operational probe remains available for diagnostics, but it
        # must never authorize regulated workflows. Runtime readiness always uses the
        # complete read-only System Preflight.
        self.assertIn("RunOperationalReadinessAsync", preflight)
        self.assertIn("IsDatabaseMaintenanceActiveAsync", preflight)
        self.assertIn("APPLOCK_TEST", preflight)
        self.assertNotIn("TryAcquirePreflightLeaseAsync", preflight)
        readiness = main.split("private async Task RefreshSystemReadinessAsync", 1)[1].split("private void ApplyRuntimeReadinessGate", 1)[0]
        self.assertIn("SystemPreflightReport report = await service.RunAsync();", readiness)
        self.assertNotIn("RunOperationalReadinessAsync", readiness)
        self.assertNotIn("operationalOnly", readiness)
        loaded = main.split("private async void Window_Loaded", 1)[1].split("private async void BtnRefreshDashboard_Click", 1)[0]
        self.assertIn("await RefreshSystemReadinessAsync", loaded)
        self.assertIn("if (_runtimePreflightChecked && _runtimePreflightReady)", loaded)
        self.assertIn("await RefreshDashboardAsync()", loaded)
        self.assertLess(loaded.index("await RefreshSystemReadinessAsync"), loaded.index("await RefreshDashboardAsync()"))
        self.assertNotIn("Task.WhenAll", loaded)
        self.assertIn("_runtimePreflightChecked &&\n                                 _runtimePreflightReady", main)
        self.assertNotIn("AppConfig.IsDevelopment || (_runtimePreflightChecked && _runtimePreflightReady)", main)
        self.assertNotIn("if (AppConfig.IsDevelopment)\n                return true;", main)
        self.assertIn("Development readiness unavailable - workflows blocked", main)
        self.assertIn("Dashboard blocked until System Preflight passes.", main)
        self.assertIn("CancellationToken cancellationToken = default", database)
        self.assertIn("OpenAsync(cancellationToken)", database)


    def test_v166_database_lifecycle_is_serialized_restartable_and_diagnostic(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        maintenance = (ROOT / "tools/PharmaLIMS.DatabaseMaintenance/Program.cs").read_text(encoding="utf-8-sig")
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))

        self.assertIn("_preflightRunning", main)
        self.assertIn("_dashboardRefreshRunning", main)
        self.assertIn("!_maintenanceRunning && !_preflightRunning", main)
        self.assertIn("!_preflightRunning && !_dashboardRefreshRunning", main)
        self.assertIn("maintenanceVerification: true", main)

        self.assertIn("APPLOCK_TEST(N'public', N'PharmaLIMS.SchemaMigration', N'Shared', N'Session')", preflight)
        self.assertIn("Database lifecycle coordination", preflight)
        self.assertIn("BuildPreflightDatabaseFailureDetails", preflight)
        self.assertNotIn("sys.sp_getapplock", preflight)
        self.assertNotIn("sys.sp_releaseapplock", preflight)

        self.assertIn("AcquireMigrationSessionApplicationLockAsync", migrator)
        self.assertIn("@LockMode=N'Exclusive'", migrator)
        self.assertIn("@LockOwner=N'Session'", migrator)
        self.assertIn("ApplyManifestMigrationEntryAsync", migrator)
        entry = migrator.split("private async Task ApplyManifestMigrationEntryAsync", 1)[1].split(
            "private static async Task AcquireMigrationSessionApplicationLockAsync", 1
        )[0]
        self.assertIn("ExecuteInTransactionAsync", entry)
        self.assertIn("database remained busy", entry)
        self.assertIn("ProgressChanged", migrator)
        self.assertIn("ProgressChanged", maintenance)

        controlled_keys = {item["versionKey"] for item in manifest["migrations"]}
        self.assertIn("20260826_003", controlled_keys)
        self.assertNotIn("EnsureEnvironmentalMonitoringChecklistQuestions", main)
        self.assertNotIn("EnsureEnvironmentalMonitoringChecklistQuestions", maintenance)

    def test_v167_lifecycle_leases_are_explicitly_released_and_maintenance_has_one_owner(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("IsDatabaseMaintenanceActiveAsync", preflight)
        self.assertIn("APPLOCK_TEST", preflight)
        self.assertNotIn("ReleasePreflightLeaseAsync", preflight)
        self.assertNotIn("sys.sp_releaseapplock", preflight)
        self.assertNotIn("CreateUnpooledConnection", preflight)

        required_updates = migrator.split("private async Task ApplyRequiredUpdatesCoreAsync(string? authorizedUsername)", 1)[1].split(
            "public async Task ApplyControlledMigrationAsync", 1
        )[0]
        self.assertLess(
            required_updates.index("AcquireMigrationSessionApplicationLockAsync(maintenanceLease)"),
            required_updates.index("ProvisionFreshDatabaseBaselineAsync()")
        )
        self.assertGreaterEqual(required_updates.count("existingMaintenanceLease: maintenanceLease"), 3)
        self.assertIn("ReleaseMigrationSessionApplicationLockAsync(maintenanceLease)", required_updates)
        self.assertNotIn("AcquireMigrationApplicationLockAsync", migrator)
        self.assertIn("sys.sp_releaseapplock", migrator)
        self.assertIn("SqlConnection.ClearPool(connection)", migrator)
        self.assertIn("Database maintenance stopped before the controlled migration chain completed.", main)



    def test_v170_culture_media_approval_migration_has_compile_safe_legacy_bridge(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        migration = (ROOT / "Database/Migrations/20260823_002_Culture_Media_Requirement_Approval_Gate.sql").read_text(encoding="utf-8-sig")

        bridge = migrator.split('versionKey.Equals("20260823_002"', 1)[1].split(
            '// Migration 20260823_001', 1
        )[0]
        self.assertIn("CultureMediaQualificationRequirements", bridge)
        self.assertIn("EXEC(N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ApprovalStatus", bridge)
        self.assertIn("ReviewedBy", bridge)
        self.assertIn("ReviewedAt", bridge)
        self.assertIn("ApprovedBy", bridge)
        self.assertIn("ApprovedAt", bridge)
        self.assertIn("N''Growth Promotion''", bridge)
        self.assertIn("EXEC sys.sp_executesql", bridge)
        self.assertIn("return;", bridge)
        self.assertIn("UPDATE dbo.CultureMediaQualificationRequirements", migration)
        self.assertIn("ApprovalStatus", migration)

    def test_v170_database_maintenance_reports_schema_audit_and_verification_separately(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        user_error = (ROOT / "Infrastructure/UserFacingError.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")

        maintenance = main.split("private async void BtnDatabaseMaintenance_Click", 1)[1].split(
            "private void StartClock", 1
        )[0]
        self.assertIn("bool migrationsCompleted = false", maintenance)
        self.assertIn("maintenance audit evidence failed", maintenance)
        self.assertIn("post-maintenance verification could not run", maintenance)
        self.assertIn("Do not rerun migrations just to clear this message", maintenance)
        self.assertIn("the failing migration was not recorded", maintenance)
        self.assertIn("DatabaseMigrationException", user_error)
        self.assertIn("DatabaseMigrationException.FromSql", migrator)
        self.assertIn("SQL Server error {exception.Number}", migrator)

    def test_v170_non_timeout_sql_failure_preserves_exact_migration_identity(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        entry = migrator.split("private async Task ApplyManifestMigrationEntryAsync", 1)[1].split(
            "private async Task ApplyResumableCurrentStateMigrationAsync", 1
        )[0]
        resumable = migrator.split("private async Task ApplyResumableCurrentStateMigrationAsync", 1)[1].split(
            "private static async Task AcquireMigrationSessionApplicationLockAsync", 1
        )[0]
        self.assertIn('catch (SqlException ex)', entry)
        self.assertIn('DatabaseMigrationException.FromSql(versionKey, "schema execution", ex)', entry)
        self.assertIn('DatabaseMigrationException.FromSql(versionKey, $"resumable schema execution {currentStepLabel}", ex)', resumable)
        self.assertIn('DatabaseMigrationException.FromSql(versionKey, "migration ledger check", ex)', resumable)
        self.assertIn('DatabaseMigrationException.FromSql(versionKey, "migration ledger recording", ex)', resumable)

    def test_v170_preflight_does_not_report_ready_when_culture_or_prm_migration_columns_are_missing(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        for column in (
            "CultureMediaQualificationRequirements',N'ApprovalStatus",
            "CultureMediaQualificationRequirements',N'ReviewedBy",
            "CultureMediaQualificationRequirements',N'ReviewedAt",
            "PRM_SpecificationTests',N'ReviewedBy",
            "PRM_SpecificationTests',N'IsDefaultForCategory",
            "PRM_SpecificationTests',N'ItemCode",
            "PRM_SpecificationTests',N'ProductionStage",
            "PRM_SpecificationTests',N'CompendialReference",
        ):
            self.assertIn(column, preflight)


    def test_v171_recorded_migrations_are_replayed_when_structural_postconditions_are_missing(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        entry = migrator.split("private async Task ApplyManifestMigrationEntryAsync", 1)[1].split(
            "private async Task ApplyResumableCurrentStateMigrationAsync", 1
        )[0]
        verifier = migrator.split("private static async Task<bool> RecordedMigrationPostconditionsSatisfiedAsync", 1)[1].split(
            "private static async Task PrepareManifestMigrationCompatibilityAsync", 1
        )[0]

        self.assertIn("RecordedMigrationPostconditionsSatisfiedAsync", entry)
        self.assertIn("replayRecordedMigration", entry)
        self.assertIn("ledger identity is not trusted as structural proof by itself", entry)
        self.assertIn("Repairing recorded migration", entry)
        for version in (
            "20260722_005", "20260811_001", "20260819_001",
            "20260823_001", "20260823_002", "20260824_001", "20260825_001",
        ):
            self.assertIn(f'"{version}" =>', verifier)

    def test_v171_compile_before_alter_bridges_cover_early_prm_and_external_trend_migrations(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        compatibility = migrator.split("private static async Task PrepareManifestMigrationCompatibilityAsync", 1)[1]

        prm = compatibility.split('versionKey.Equals("20260722_005"', 1)[1].split(
            'versionKey.Equals("20260811_001"', 1
        )[0]
        self.assertIn("CompendialReference", prm)
        self.assertIn("EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD ItemCode", prm)
        self.assertIn("IX_PRM_SpecificationTests_ItemCategoryStatus", (ROOT / "Database/Migrations/20260722_005_Link_PRM_Specifications_To_Items.sql").read_text(encoding="utf-8-sig"))

        external = compatibility.split('versionKey.Equals("20260811_001"', 1)[1].split(
            'versionKey.Equals("20260819_001"', 1
        )[0]
        self.assertIn("AreaClassification", external)
        self.assertIn("EXEC(N'ALTER TABLE dbo.ExternalTrendImportRows ADD AreaClassification", external)

        em = compatibility.split('versionKey.Equals("20260819_001"', 1)[1].split(
            'versionKey.Equals("20260826_001"', 1
        )[0]
        self.assertIn("EXEC sys.sp_executesql", em)
        self.assertIn("CREATE UNIQUE NONCLUSTERED INDEX UX_EM_GradeLimits_Id_20260819", em)

    def test_v171_database_ddl_wait_window_is_not_the_short_application_lock_window(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn("StartupMigrationLockTimeoutMilliseconds = 15000", migrator)
        self.assertIn("MigrationDdlLockTimeoutMilliseconds = 120000", migrator)
        configure = migrator.split("private static async Task ConfigureMigrationLockTimeoutAsync", 1)[1].split(
            "private async Task ApplyManifestMigrationsAsync", 1
        )[0]
        self.assertIn("MigrationDdlLockTimeoutMilliseconds", configure)
        self.assertNotIn("StartupMigrationLockTimeoutMilliseconds", configure)

    def test_v171_preflight_requires_structural_controls_not_ledger_and_columns_only(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("CheckCriticalSchemaControlsAsync", preflight)
        for control in (
            "CK_PRM_SpecificationTests_Approval",
            "CK_CultureMediaQualificationRequirements_Approval_20260823",
            "FK_PRM_SampleTests_SourceSpecification_20260824",
            "CK_PRM_Samples_SpecificationVersion_20260824",
            "IX_PRM_SpecificationTests_ExactScope_20260824",
            "IX_PRM_SampleTests_FrozenSource_20260824",
            "CK_EM_GradeLimits_NonNegative_20260819",
            "CK_EM_GradeLimits_ActionGEAlert_20260819",
            "FK_EM_GradeLimitSignatures_GradeLimit",
        ):
            self.assertIn(control, preflight)
        self.assertIn("is_not_trusted=0", preflight)

    def test_v172_prm_readiness_uses_true_async_probe_and_absorbs_timeout_without_worker_exception(self):
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("ReadinessDatabase.ExecuteQueryAsync", readiness)
        self.assertNotIn("Task.Run(() =>", readiness)
        self.assertIn("catch (SqlException ex) when (ex.Number == -2 || ex.Number == 1222)", readiness)
        self.assertIn("catch (OperationCanceledException)", readiness)
        self.assertIn("ReadinessProbeState.Busy", readiness)
        self.assertIn("No schema change or workflow record change was attempted", readiness)

    def test_v172_database_connection_supports_bounded_timeouts_for_all_basic_commands(self):
        connection = (ROOT / "Infrastructure/DatabaseConnection.cs").read_text(encoding="utf-8-sig")
        helper = read_database_helper_source()
        self.assertIn("ExecuteNonQuery(string query, SqlParameter[]? parameters = null, int? commandTimeoutSeconds = null)", connection)
        self.assertIn("ExecuteScalar(string query, SqlParameter[]? parameters = null, int? commandTimeoutSeconds = null)", connection)
        self.assertIn("CancellationToken cancellationToken = default", connection)
        self.assertIn("ExecuteNonQuery(string query, SqlParameter[] parameters = null, int? commandTimeoutSeconds = null)", helper)
        self.assertIn("ExecuteScalar(string query, SqlParameter[] parameters = null, int? commandTimeoutSeconds = null)", helper)

    def test_v172_startup_serializes_readiness_before_dashboard_and_bounds_dashboard_reads(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        loaded = main.split("private async void Window_Loaded", 1)[1].split("private async void BtnRefreshDashboard_Click", 1)[0]
        self.assertIn("await RefreshSystemReadinessAsync", loaded)
        self.assertIn("await RefreshDashboardAsync()", loaded)
        self.assertLess(loaded.index("await RefreshSystemReadinessAsync"), loaded.index("await RefreshDashboardAsync()"))
        self.assertNotIn("Task.WhenAll", loaded)
        self.assertIn("DatabaseHelper.ExecuteScalar(query, commandTimeoutSeconds: 10)", main)

    def test_v174_quality_event_legacy_identity_reconciliation_avoids_second_identity(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        entries = manifest["migrations"]
        keys = [entry["versionKey"] for entry in entries]
        self.assertIn("20260825_007", keys)
        self.assertLess(keys.index("20260825_007"), keys.index("20260826_001"))
        prerequisite = (ROOT / "Database/Migrations/20260825_007_PRM_Quality_Event_Legacy_Identity_Reconciliation.sql").read_text(encoding="utf-8-sig")
        self.assertIn("SQL Server error 2744", prerequisite)
        self.assertIn("ADD AffectedResultID INT NULL", prerequisite)
        self.assertIn("NEXT VALUE FOR dbo.Seq_AffectedResultID_20260825_007", prerequisite)
        self.assertIn("ADD QualityEventActionID INT NULL", prerequisite)
        self.assertIn("ADD AnswerID BIGINT NULL", prerequisite)
        self.assertIn("ADD QualityEventPrintID BIGINT NULL", prerequisite)
        self.assertNotIn("ADD AffectedResultID INT IDENTITY", prerequisite)
        self.assertNotIn("ADD QualityEventActionID INT IDENTITY", prerequisite)
        self.assertNotIn("ADD AnswerID BIGINT IDENTITY", prerequisite)
        self.assertNotIn("ADD QualityEventPrintID BIGINT IDENTITY", prerequisite)
        expected = next(entry for entry in entries if entry["versionKey"] == "20260825_007")["sha256"].lower()
        self.assertEqual(hashlib.sha256((ROOT / "Database/Migrations/20260825_007_PRM_Quality_Event_Legacy_Identity_Reconciliation.sql").read_bytes()).hexdigest(), expected)

    def test_v174_quality_event_insert_ids_use_output_inserted_not_scope_identity(self):
        repository = (ROOT / "Repositories/QualityEventRepository.cs").read_text(encoding="utf-8-sig")
        helper = read_database_helper_source()
        em = read_source_family("EMResultsEntry.xaml.cs")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn("OUTPUT INSERTED.AffectedResultID", repository)
        self.assertIn("OUTPUT INSERTED.QualityEventActionID", repository)
        self.assertIn("OUTPUT INSERTED.QualityEventID", repository)
        self.assertGreaterEqual(helper.count("OUTPUT INSERTED.QualityEventID"), 2)
        self.assertIn("OUTPUT INSERTED.[", em)
        bridge = migrator.split('if (versionKey.Equals("20260826_001", StringComparison.Ordinal))', 1)[1].split('// Migration 20260823_002', 1)[0]
        self.assertNotIn("ADD QualityEventID INT IDENTITY", bridge)
        self.assertNotIn("ADD AffectedResultID INT IDENTITY", bridge)
        self.assertNotIn("ADD QualityEventActionID INT IDENTITY", bridge)
        self.assertNotIn("ADD AnswerID BIGINT IDENTITY", bridge)
        self.assertNotIn("ADD QualityEventPrintID BIGINT IDENTITY", bridge)


    def test_v175_active_quality_event_migrations_do_not_embed_convert_inside_exec_string_expression(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        active_keys = {"20260825_007", "20260826_001", "20260826_002", "20260826_003", "20260826_004"}
        for entry in manifest["migrations"]:
            if entry["versionKey"] not in active_keys:
                continue
            migration = (ROOT / "Database" / entry["file"]).read_text(encoding="utf-8-sig")
            self.assertNotRegex(
                migration,
                r"EXEC\s*\(\s*N'[^\n]*\+\s*(?:CONVERT|CAST|FORMAT|QUOTENAME|REPLACE|ISNULL|COALESCE)\s*\(",
                entry["versionKey"],
            )
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertNotIn(
            "EXEC(N'ALTER SEQUENCE dbo.Seq_QualityEventChecklistQuestionID_20260824_Compat RESTART WITH ' + CONVERT",
            migrator,
        )

    def test_v175_migration_syntax_errors_surface_sql_line_and_message(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn("exception.Number is 102 or 156", migrator)
        self.assertIn("exception.Errors[0].LineNumber", migrator)
        self.assertIn('SQL Server error {exception.Number}{lineText}: {detail}', migrator)

    def test_v176_legacy_identity_reconciliation_defers_new_column_binding(self):
        migration = (ROOT / "Database/Migrations/20260825_007_PRM_Quality_Event_Legacy_Identity_Reconciliation.sql").read_text(encoding="utf-8-sig")
        self.assertIn("@DuplicatePrintIdCount", migration)
        self.assertIn("@NextValue=@NextPrintBig OUTPUT", migration)
        self.assertIn("@NextValue=@NextPrintInt OUTPUT", migration)
        self.assertNotIn("IF EXISTS (SELECT QualityEventPrintID FROM dbo.QualityEventPrintHistory", migration)
        self.assertNotIn("SELECT @NextPrintBig=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID))", migration)
        self.assertNotIn("SELECT @NextPrintInt=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID))", migration)
        for old_static in (
            "SELECT @NextQualityEventID=ISNULL(MAX(CONVERT(BIGINT,QualityEventID))",
            "SELECT @NextAffectedResultID=ISNULL(MAX(CONVERT(BIGINT,AffectedResultID))",
            "SELECT @NextActionID=ISNULL(MAX(CONVERT(BIGINT,QualityEventActionID))",
            "SELECT @NextAnswerBig=ISNULL(MAX(CONVERT(BIGINT,AnswerID))",
            "SELECT @NextAnswerInt=ISNULL(MAX(CONVERT(BIGINT,AnswerID))",
        ):
            self.assertNotIn(old_static, migration)

    def test_v176_current_baseline_handles_actual_legacy_affected_result_schema(self):
        migration = (ROOT / "Database/Migrations/20260826_001_PRM_Quality_Event_Current_Baseline.sql").read_text(encoding="utf-8-sig")
        self.assertIn("@DuplicateAffectedSourceLinks", migration)
        self.assertIn("EXEC(N'CREATE UNIQUE INDEX UX_QualityEventAffectedResults_Source_20260826", migration)
        self.assertIn("EXEC(N'CREATE INDEX IX_QualityEventActions_Event_20260826", migration)
        self.assertIn("@DuplicateChecklistAnswerLinks", migration)
        self.assertIn("EXEC(N'CREATE UNIQUE INDEX UQ_QualityEventChecklistAnswers_20260826", migration)
        self.assertIn("EXEC(N'CREATE INDEX IX_QualityEventPrintHistory_Event_20260826", migration)
        self.assertIn("EXEC(N'CREATE INDEX IX_QualityEvents_SourceStatus_20260826", migration)
        legacy = (ROOT / "Database/Migrations/20260825_007_PRM_Quality_Event_Legacy_Identity_Reconciliation.sql").read_text(encoding="utf-8-sig")
        self.assertIn("ALTER COLUMN ResultValue NVARCHAR(200) NULL", legacy)
        self.assertIn("ALTER COLUMN SpecificationLimit NVARCHAR(500) NULL", legacy)
        self.assertIn("ALTER COLUMN FailureType NVARCHAR(120) NULL", legacy)
        self.assertNotIn("SELECT QualityEventID, SourceModule, SourceResultID\n        FROM dbo.QualityEventAffectedResults", migration)



    def test_v177_legacy_identity_backfill_never_updates_existing_identity_columns(self):
        migration = (ROOT / "Database/Migrations/20260825_007_PRM_Quality_Event_Legacy_Identity_Reconciliation.sql").read_text(encoding="utf-8-sig")

        guarded_backfills = (
            ("@AffectedResultIsIdentity", "IF @AffectedResultIsIdentity=0", "SET AffectedResultID=NEXT VALUE FOR"),
            ("@ActionIdIsIdentity", "IF @ActionIdIsIdentity=0", "SET QualityEventActionID=NEXT VALUE FOR"),
            ("@AnswerIsIdentity", "IF @AnswerIsIdentity=0", "SET AnswerID=NEXT VALUE FOR"),
            ("@PrintIsIdentity", "IF @PrintIsIdentity=0", "SET QualityEventPrintID=NEXT VALUE FOR"),
        )
        for identity_flag, guard, update in guarded_backfills:
            self.assertIn(identity_flag, migration)
            self.assertIn(guard, migration)
            self.assertIn(update, migration)
            self.assertLess(migration.index(identity_flag), migration.index(update))

        self.assertIn("Never UPDATE an existing IDENTITY column (SQL Server error 8102)", migration)
        self.assertNotIn("SET IDENTITY_INSERT", migration)

        # The canonical event foreign-key columns are only copied from legacy IDs
        # after those canonical columns are newly added as normal nullable INTs.
        for safe_bridge in (
            "ALTER TABLE dbo.QualityEventAffectedResults ADD QualityEventID INT NULL",
            "ALTER TABLE dbo.QualityEventActions ADD QualityEventID INT NULL",
            "ALTER TABLE dbo.QualityEventChecklistAnswers ADD QualityEventID INT NULL",
            "ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventID INT NULL",
        ):
            self.assertIn(safe_bridge, migration)



    def test_v178_quality_event_repository_supports_prm_qualitative_source_evidence(self):
        model = (ROOT / "Models/QualityEvent.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/QualityEventRepository.cs").read_text(encoding="utf-8-sig")

        self.assertIn("public int? SampleTestId", model)
        self.assertIn("public string? SourceModule", model)
        self.assertIn("public int? SourceResultId", model)
        self.assertIn("public int? TestId", model)
        self.assertIn("public string? ResultValue", model)
        self.assertIn("SourceModule, SourceResultID", repository)
        self.assertIn('ResultValue = row.GetSafeString("ResultValue")', repository)
        self.assertIn('new SqlParameter("@ResultValue", SqlDbType.NVarChar, 200)', repository)
        self.assertIn('new SqlParameter("@SourceResultId", SqlDbType.Int)', repository)
        self.assertNotIn('ResultValue = row.GetSafeDecimal("ResultValue")', repository)

    def test_v178_prm_readiness_and_preflight_validate_affected_result_types(self):
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")

        for required in (
            "name=N'ResultValue'",
            "system_type_id=TYPE_ID(N'nvarchar')",
            "name=N'SourceResultID'",
            "system_type_id=TYPE_ID(N'int')",
            "is_nullable=1",
        ):
            self.assertIn(required, readiness)

        self.assertIn("QualityEventAffectedResults.ResultValue NVARCHAR(200+)", preflight)
        self.assertIn("QualityEventAffectedResults.SourceResultID nullable INT", preflight)
        self.assertIn("QualityEventAffectedResults.SampleTestID nullable INT", preflight)

    def test_v178_external_trend_reads_are_async_bounded_and_nonblocking_on_transient_locks(self):
        service = (ROOT / "Services/ExternalTrendImportService.cs").read_text(encoding="utf-8-sig")
        dialog = (ROOT / "ExternalTrendImportDialog.xaml.cs").read_text(encoding="utf-8-sig")

        for required in (
            "ExecuteReadWithRetryAsync",
            "GetImportHistoryAsync",
            "GetBatchMethodsAsync",
            "GetBatchParametersAsync",
            "ReadLockTimeoutMilliseconds",
            "ReadRetryDelays",
            "exception.Number == -2 || exception.Number == 1222",
        ):
            self.assertIn(required, service)

        self.assertNotIn("Task.Run(importService.GetImportHistory)", dialog)
        self.assertNotIn("Task.Run(() => importService.GetBatchMethods", dialog)
        self.assertNotIn("Task.Run(() => importService.GetBatchParameters", dialog)
        self.assertIn("historySelectionCancellation", dialog)
        self.assertIn("parameterSelectionCancellation", dialog)
        self.assertIn("Import history was not refreshed", dialog)
        self.assertNotIn("WITH (NOLOCK)", service)

    def test_v178_database_integration_rehearses_actual_legacy_quality_event_drift(self):
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")

        for required in (
            "VerifyLegacyQualityEventUpgradeAsync",
            "LegacyQualityEventSchemaSql",
            "PrintHistoryID int IDENTITY(1,1)",
            "AffectedResultID int IDENTITY(1,1)",
            "QualityEventActionID int IDENTITY(1,1)",
            "AnswerID int IDENTITY(1,1)",
            "ResultValue decimal(18,4)",
            'new[] { "20260825_007", "20260826_001", "20260826_002" }',
            "Multiple IDENTITY columns detected.",
            "Qualitative PRM affected-result evidence was not preserved.",
            "Legacy PRM Quality Event migration rehearsal PASS.",
        ):
            self.assertIn(required, integration)


    def test_v179_database_maintenance_blocks_all_database_read_workspaces(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        gate = main.split("private void ApplyRuntimeReadinessGate", 1)[1].split(
            "private static bool IsAdministrativeRole", 1
        )[0]

        self.assertIn("BtnReports.IsEnabled = false;", gate)
        self.assertIn("BtnAIReview.IsEnabled = false;", gate)
        self.assertIn("Unavailable while controlled database maintenance is running.", gate)
        self.assertIn("BtnSystemPreflight.IsEnabled = !_maintenanceRunning", gate)
        self.assertIn("BtnRefreshDashboard.IsEnabled = !_maintenanceRunning", gate)

    def test_v179_resumable_external_trend_migration_runs_as_bounded_visible_steps(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        migration = (ROOT / "Database/Migrations/20260826_004_External_Trend_Current_State_Reconciliation.sql").read_text(encoding="utf-8-sig")

        self.assertIn('ResumableMigrationStepMarker = "-- PHARMALIMS_STEP:"', migrator)
        self.assertIn("ResumableMigrationStepCommandTimeoutSeconds = 180", migrator)
        self.assertIn("SplitResumableMigrationSteps", migrator)
        self.assertIn('currentStepLabel = $"[{index + 1}/{steps.Count}] {step.Name}"', migrator)
        self.assertIn('ProgressChanged?.Invoke($"Applying {versionKey} {currentStepLabel}")', migrator)
        self.assertIn("exceeded the 180-second controlled step window", migrator)
        self.assertGreaterEqual(migration.count("-- PHARMALIMS_STEP:"), 10)
        self.assertIn("-- PHARMALIMS_STEP: Final current-state verification", migration)
        self.assertNotIn("BEGIN TRANSACTION", migration.upper())

    def test_v179_ai_review_separates_current_session_errors_and_legacy_snapshot_gaps(self):
        review = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("Current session: errors=", review)
        self.assertIn("Historical 7-day total", review)
        self.assertIn("Starting PharmaLIMS.", review)
        self.assertIn("VersionKey=N'20260722_003'", review)
        self.assertIn("Legacy certificates without snapshots", review)
        self.assertIn("Post-control certificates without snapshots", review)
        self.assertIn("Legacy PRM documents without snapshots", review)
        self.assertIn("Post-control PRM documents without snapshots", review)
        self.assertIn("do not fabricate snapshots retrospectively", review)


    def test_v180_external_trend_performance_indexes_do_not_gate_schema_reconciliation(self):
        migration = (ROOT / "Database/Migrations/20260826_004_External_Trend_Current_State_Reconciliation.sql").read_text(encoding="utf-8-sig")
        start = migration.index("-- PHARMALIMS_STEP: Current External Trend read index readiness")
        end = migration.index("-- PHARMALIMS_STEP: Final current-state verification")
        readiness = migration[start:end]

        self.assertIn("Performance-only read indexes are deliberately not a schema-integrity gate", readiness)
        self.assertIn("optional External Trend read index(es) are not present", readiness)
        self.assertNotIn("CREATE INDEX", readiness.upper())
        self.assertIn("-- PHARMALIMS_STEP: Final current-state verification", migration)



    def test_v181_prm_quality_event_number_reconciles_legacy_event_numbers_before_allocation(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        allocation = prm.split("private string GeneratePrmQualityEventNumber", 1)[1].split(
            "private static async Task EnsurePrmQualityEventSchemaReadyForActionAsync", 1
        )[0]
        self.assertIn("@ExistingEventMax", allocation)
        self.assertIn("FROM dbo.QualityEvents WITH (UPDLOCK,HOLDLOCK)", allocation)
        self.assertIn("EventNumber LIKE @Stem+N'%'", allocation)
        self.assertIn("ISNULL(target.LastNumber,0) > @ExistingEventMax", allocation)
        self.assertIn("@ExistingEventMax + 1", allocation)
        self.assertIn("Legacy PRM Quality Events were originally numbered from MAX(EventNumber)", allocation)
        self.assertIn("creationStage = \"Quality Event number allocation\"", prm)
        self.assertIn("transaction was rolled back; no partial record was committed", prm)

        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("VerifyPrmQualityEventNumberReconciliationAsync", integration)
        self.assertIn("PRM-QE-2026-0003", integration)
        self.assertIn("PRM-QE-2026-0004", integration)
        self.assertIn("Legacy PRM Quality Event number reconciliation PASS.", integration)


    def test_v182_prm_ui_disables_approval_when_quality_gate_is_not_satisfied(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        workflow = prm.split("private void UpdateWorkflowControls", 1)[1].split(
            "private async void BtnSearch_Click", 1
        )[0]
        self.assertIn("IsCurrentPrmQualityStateApprovable", workflow)
        self.assertIn("BtnApprove.IsEnabled = baseApprovalAllowed && qualityApprovalAllowed", workflow)
        self.assertIn("Approval blocked: an open PRM Quality Event / Investigation exists", workflow)
        self.assertIn("no longer matches the current result/specification snapshot", workflow)
        self.assertIn("IsCurrentPrmCertificateStateIssuable", workflow)

    def test_v182_prm_quality_event_summary_and_responsive_results_grid_are_visible(self):
        xaml = (ROOT / "ProductionRawMaterialResults.xaml").read_text(encoding="utf-8-sig")
        self.assertIn('x:Name="TxtQualityEventNo"', xaml)
        self.assertIn('x:Name="TxtInvestigationStatus"', xaml)
        self.assertIn('x:Name="TxtInvestigationDisposition"', xaml)
        remarks_column = re.search(r'<DataGridTextColumn\b[^>]*\bHeader="Remarks"[^>]*/>', xaml)
        self.assertIsNotNone(remarks_column)
        width = re.search(r'\bWidth="([0-9.]+)\*"', remarks_column.group())
        min_width = re.search(r'\bMinWidth="(\d+)"', remarks_column.group())
        self.assertIsNotNone(width)
        self.assertIsNotNone(min_width)
        self.assertGreater(float(width.group(1)), 0)
        self.assertGreaterEqual(int(min_width.group(1)), 120)

    def test_v182_prm_closed_investigation_must_cover_current_failures_and_use_final_outcome(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        state = prm.split('private const string PrmQualityEventStateSql = @"', 1)[1].split('";', 1)[0]
        self.assertIn("QualityEventAffectedResults affected", state)
        self.assertIn("affected.SourceResultID = st.SampleTestID", state)
        self.assertIn("Confirmed OOS - Original Result Retained", state)
        self.assertIn("OOS Invalidated - Assignable Laboratory Cause", state)
        self.assertIn("OOS Not Confirmed - Scientifically Justified", state)
        self.assertIn("Escalated to Batch / Material Disposition", state)
        self.assertNotIn("FinalDisposition <> N'Pending'", state)

    def test_v182_prm_investigation_disposition_is_not_batch_release_decision(self):
        xaml = (ROOT / "PRMQualityEventInvestigation.xaml").read_text(encoding="utf-8-sig")
        combo = xaml.split('x:Name="cboFinalDisposition"', 1)[1].split("</ComboBox>", 1)[0]
        self.assertIn("Investigation outcome only", combo)
        self.assertIn("Confirmed OOS - Original Result Retained", combo)
        self.assertIn("Retest / Resample Required", combo)
        self.assertNotIn('Content="Released"', combo)
        self.assertNotIn('Content="Rejected"', combo)

        code = (ROOT / "PRMQualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")
        close_gate = code.split("private bool ValidateBeforeCloseInvestigation", 1)[1].split(
            "private bool SaveInvestigation", 1
        )[0]
        self.assertIn("cannot be closed while retest/resample is still required", close_gate)
        self.assertIn("Final batch/material release or rejection remains outside this screen", close_gate)

    def test_v182_prm_certificate_gate_blocks_open_investigation_even_for_conforming_result(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        gate = prm.split("private void EnsureCertificateInterpretationIsIssuable(string overall)", 1)[1].split(
            "private static bool IsControlledNonReleaseReportCategory", 1
        )[0]
        self.assertLess(gate.index("HasAnyPrmQualityEventMinimal()"), gate.index('overall.Equals("Conforms"'))
        self.assertIn("GetPrmQualityEventState()", gate)
        self.assertIn("legacy/unversioned", gate)

        tx_gate = prm.split("private void EnsureCertificateInterpretationIsIssuableInTransaction", 1)[1].split(
            "private static bool IsQualitativeAbsenceTest", 1
        )[0]
        self.assertLess(tx_gate.index("HasAnyPrmQualityEventMinimalInTransaction"), tx_gate.index('overall.Equals("Conforms"'))
        self.assertIn("PrmQualityEventStateSql", tx_gate)
        self.assertIn("legacy/unversioned", tx_gate)

    def test_v182_prm_new_quality_event_opens_investigation_directly_without_intermediate_popup(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        handler = prm.split("private async void BtnQualityEvent_Click", 1)[1].split(
            "private void CommitGridEdit", 1
        )[0]
        creation = handler.split("eventId = CreatePrmQualityEvent();", 1)[1].split(
            "PRMQualityEventInvestigation window", 1
        )[0]
        self.assertIn("Opening investigation", creation)
        self.assertNotIn("MessageBox.Show", creation)
        self.assertIn("DoesPrmQualityEventCoverCurrentAffectedResults", handler)

    def test_v182_nonconforming_report_states_it_is_not_final_batch_disposition(self):
        template = (ROOT / "Services/PRMCertificateTemplate.cs").read_text(encoding="utf-8-sig")
        self.assertIn("This microbiology document does not constitute final batch/material disposition.", template)


    def test_v183_prm_investigation_evidence_is_bound_to_exact_result_and_specification_snapshot(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        migration = (ROOT / "Database/Migrations/20260827_001_PRM_Investigation_Evidence_Version_Binding.sql").read_text(encoding="utf-8-sig")
        state = prm.split('private const string PrmQualityEventStateSql = @"', 1)[1].split('";', 1)[0]

        self.assertIn("SpecificationNumericLimit", migration)
        self.assertIn("EvidenceSchemaVersion", migration)
        self.assertIn("DEFAULT (0) WITH VALUES", migration)
        self.assertIn("EvidenceSchemaVersion,", prm)
        self.assertIn("SpecificationNumericLimit,", prm)
        self.assertIn("ISNULL(affected.EvidenceSchemaVersion, 0) <> 1", state)
        self.assertIn("affected.SpecificationNumericLimit = st.SpecificationLimit", state)
        self.assertIn("CONVERT(VARBINARY(MAX), ISNULL(affected.ResultValue, N''))", state)
        self.assertIn("EnsurePrmResultEvidenceIsMutableInTransaction(conn, tx, testId)", prm)
        self.assertIn("already part of controlled Quality Event evidence and cannot be overwritten", prm)

    def test_v183_prm_investigation_closure_revalidates_evidence_inside_transaction(self):
        investigation = (ROOT / "PRMQualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")
        close_method = investigation.split("private void BtnCloseInvestigation_Click", 1)[1].split(
            "private void BtnCloseWindow_Click", 1
        )[0]
        self.assertIn("EnsureAffectedPrmEvidenceMatchesCurrentResultsInTransaction(conn, tx, qualityEventId)", close_method)
        helper = investigation.split("private static void EnsureAffectedPrmEvidenceMatchesCurrentResultsInTransaction", 1)[1].split(
            "private static bool IsClosedQualityEventStatus", 1
        )[0]
        self.assertIn("WITH (UPDLOCK, HOLDLOCK)", helper)
        self.assertIn("EvidenceSchemaVersion", helper)
        self.assertIn("SpecificationNumericLimit", helper)
        self.assertIn("affected-result evidence no longer matches", helper)

    def test_v183_database_maintenance_error_facade_is_public_and_buildable_across_projects(self):
        error = (ROOT / "Infrastructure/UserFacingError.cs").read_text(encoding="utf-8-sig")
        maintenance = (ROOT / "tools/PharmaLIMS.DatabaseMaintenance/Program.cs").read_text(encoding="utf-8-sig")
        solution = (ROOT / "pharmaLIMS.slnx").read_text(encoding="utf-8-sig")

        self.assertIn("public static class UserFacingError", error)
        self.assertIn("public static string SafeMessage", error)
        self.assertIn("UserFacingError.SafeMessage", maintenance)
        self.assertIn('Project Path="PharmaLIMS.csproj"', solution)
        self.assertIn('Project Path="tools/PharmaLIMS.DatabaseMaintenance/PharmaLIMS.DatabaseMaintenance.csproj"', solution)
        self.assertIn('Project Path="tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj"', solution)

    def test_v183_external_trend_approval_revalidates_explicit_permission_inside_transaction(self):
        service = (ROOT / "Services/ExternalTrendImportService.cs").read_text(encoding="utf-8-sig")
        method = service.split("private static void ChangeStatus", 1)[1].split("private static TrendImportRow? ParseRow", 1)[0]

        self.assertIn("DatabaseHelper.ExecuteInTransaction", method)
        self.assertIn("DatabaseHelper.EnsureUserPermissionInTransaction", method)
        self.assertIn('"CanApproveResults"', method)
        self.assertIn("WITH (UPDLOCK, HOLDLOCK)", method)
        self.assertNotIn('RoleIs(userRole, "Admin")', method)
        self.assertNotIn('RoleIs(userRole, "Administrator")', method)
        self.assertNotIn('RoleIs(userRole, "QA")', method)
        self.assertNotIn("DatabaseHelper.CanApproveResults(user.Trim())", method)

    def test_v183_production_navigation_and_ai_review_use_explicit_permission_flags(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        permissions = main.split("private void ApplyRolePermissions", 1)[1].split(
            "private async Task RefreshSystemReadinessAsync", 1
        )[0]
        review = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")
        review_gate = review.split("private static bool HasReviewPermission", 1)[1].split(
            "private void UpdateSummary", 1
        )[0]

        self.assertIn("BtnAIReview.IsEnabled = Login.CanManageSettings;", permissions)
        self.assertNotIn("isAdmin", permissions)
        self.assertNotIn('role.Equals("Admin"', permissions)
        self.assertIn("return Login.CanManageSettings;", review_gate)
        self.assertNotIn("CurrentUserRole", review_gate)
        self.assertNotIn("Admin", review_gate)
        self.assertNotIn("Administrator", review_gate)

    def test_v183_ci_materializes_production_config_from_protected_secret_and_builds_solution(self):
        ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8-sig")
        profile = (ROOT / "Properties/PublishProfiles/FolderProfile.pubxml").read_text(encoding="utf-8-sig")

        release_runner = (ROOT / "scripts/Invoke-ReleaseValidation.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("dotnet restore PharmaLIMS.csproj --runtime win-x64", release_runner)
        self.assertIn("dotnet build PharmaLIMS.csproj --configuration Release --runtime win-x64 --no-restore", release_runner)
        self.assertIn("Invoke-ReleaseValidation.ps1 -RunDatabaseIntegration -RunRuntimeSmoke", ci)
        self.assertNotIn("dotnet restore pharmaLIMS.slnx", ci)
        self.assertNotIn("dotnet build pharmaLIMS.slnx", ci)
        self.assertIn("PHARMALIMS_PRODUCTION_APPSETTINGS_BASE64", ci)
        self.assertIn("secrets.PHARMALIMS_PRODUCTION_APPSETTINGS_BASE64", ci)
        self.assertIn("Remove-Item -LiteralPath appsettings.Production.json", ci)
        self.assertIn("if: github.event_name != 'pull_request'", ci)
        self.assertIn("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", profile)
        self.assertIn("<SelfContained>true</SelfContained>", profile)
        self.assertNotIn("<SelfContained>false</SelfContained>", profile)

    def test_v183_resumable_migration_null_contract_is_explicit(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        method = migrator.split("private static List<ResumableMigrationStep> SplitResumableMigrationSteps", 1)[1].split(
            "private static async Task AcquireMigrationSessionApplicationLockAsync", 1
        )[0]
        self.assertIn("ArgumentNullException.ThrowIfNull(migrationSql);", method)
        self.assertIn("new StringReader(migrationSql)", method)
        self.assertNotIn("new StringReader(migrationSql ?? string.Empty)", method)

    def test_v183_database_integration_rehearses_same_id_result_and_specification_drift(self):
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("VerifyPrmInvestigationEvidenceBindingAsync", integration)
        self.assertIn("changed result value on the same SampleTestID", integration)
        self.assertIn("changed numeric specification on the same SampleTestID", integration)
        self.assertIn("Legacy/unversioned PRM evidence did not fail closed", integration)
        self.assertIn("PRM investigation evidence version-binding rehearsal PASS.", integration)


    def test_v184_legacy_prm_evidence_requires_explicit_signed_reconciliation(self):
        migration = (ROOT / "Database/Migrations/20260827_002_PRM_Legacy_Evidence_Reconciliation.sql").read_text(encoding="utf-8-sig")
        self.assertIn("PRM_QualityEventEvidenceReconciliations", migration)
        self.assertIn("LegacyQualityEventID", migration)
        self.assertIn("ReplacementQualityEventID", migration)
        self.assertIn("ElectronicSignatureID", migration)
        self.assertIn("ReconciliationSchemaVersion", migration)
        self.assertIn("TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002", migration)
        self.assertIn("AFTER UPDATE, DELETE", migration)

    def test_v184_prm_gate_ignores_legacy_v0_only_after_valid_reconciliation(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        state = prm.split('private const string PrmQualityEventStateSql = @"', 1)[1].split('";', 1)[0]
        self.assertIn("ValidLegacyReconciliations", state)
        self.assertIn("rec.LegacyQualityEventID", state)
        self.assertIn("rec.ReplacementQualityEventID", state)
        self.assertIn("rec.ElectronicSignatureID", state)
        self.assertIn("rec.ReconciliationSchemaVersion = 1", state)
        self.assertIn("replacementAffected.ResultValue", state)
        self.assertIn("currentResult.ResultValue", state)
        self.assertIn("ValidLegacyReconciliations validRec", state)

    def test_v184_prm_reconciliation_is_invalidated_by_current_snapshot_change(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        legacy = prm.split("private bool HasUnreconciledLegacyPrmEvidence", 1)[1].split("private int GetLatestPrmQualityEventId", 1)[0]
        for token in (
            "replacementAffected.TestName",
            "replacementAffected.ResultValue",
            "replacementAffected.SpecificationLimit",
            "replacementAffected.SpecificationNumericLimit",
            "replacementAffected.Unit",
            "replacementAffected.FailureType",
        ):
            self.assertIn(token, legacy)
        self.assertIn("currentResult.ResultValue", legacy)
        self.assertIn("currentResult.SpecificationText", legacy)

    def test_v184_ui_can_open_controlled_legacy_reconciliation_investigation(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        self.assertIn('content = "Reconcile Legacy Investigation"', prm)
        self.assertIn("legacyReconciliationRequired = HasUnreconciledLegacyPrmEvidence()", prm)
        self.assertIn("eventId = CreatePrmQualityEvent(true);", prm)
        self.assertIn("includeLegacyReconciliationEvidence", prm)
        self.assertIn("historical pre-v183 affected-result evidence", prm)

    def test_v184_qa_closure_creates_reconciliation_inside_same_transaction(self):
        source = (ROOT / "PRMQualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")
        close = source.split("private void BtnCloseInvestigation_Click", 1)[1].split("private void BtnCloseWindow_Click", 1)[0]
        self.assertIn("EnsureReplacementEventCoversCurrentAndLegacyEvidenceInTransaction", close)
        self.assertIn("OUTPUT INSERTED.SignatureID", close)
        self.assertIn("CreateLegacyPrmEvidenceReconciliationsInTransaction", close)
        self.assertIn("PRM Quality Event Closure and Legacy Evidence Reconciliation", close)
        self.assertIn("historical evidence will not be rewritten", close)

    def test_v184_database_integration_covers_v0_then_v1_reconciliation_and_invalidation(self):
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("VerifyPrmLegacyEvidenceReconciliationAsync", integration)
        self.assertIn("Legacy v0 PRM evidence was not fail-closed before explicit reconciliation", integration)
        self.assertIn("A signed valid v1 replacement did not reconcile", integration)
        self.assertIn("Changing the current result did not invalidate", integration)
        self.assertIn("PRM legacy v0 -> signed v1 evidence reconciliation rehearsal PASS.", integration)



    def test_v185_prm_investigation_schema_gate_precedes_reconciliation_sql(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        handler = prm.split("private async void BtnQualityEvent_Click", 1)[1].split("private void CommitGridEdit", 1)[0]
        self.assertLess(
            handler.index("EnsurePrmQualityEventSchemaReadyForActionAsync"),
            handler.index("HasUnreconciledLegacyPrmEvidence"),
        )
        self.assertIn("openEventAfterGate", handler)

    def test_v185_prm_legacy_reconciliation_probe_is_metadata_guarded(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        helper = prm.split("private bool IsPrmLegacyReconciliationSchemaReady", 1)[1].split("private bool HasUnreconciledLegacyPrmEvidence", 1)[0]
        self.assertIn("OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations'", helper)
        self.assertIn("COL_LENGTH(N'dbo.QualityEventAffectedResults',N'EvidenceSchemaVersion')", helper)
        legacy = prm.split("private bool HasUnreconciledLegacyPrmEvidence", 1)[1].split("private int GetLatestPrmQualityEventId", 1)[0]
        self.assertIn("!IsPrmLegacyReconciliationSchemaReady()", legacy)
        self.assertNotIn("IF OBJECT_ID", legacy)
        self.assertIn('content = "Database Update Required"', prm)
        self.assertIn("IsPrmLegacyReconciliationSchemaReady", prm)

    def test_v186_prm_results_preflight_routes_stale_schema_to_central_maintenance(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        handler = main.split("private async void BtnPRMResults_Click", 1)[1].split("private void BtnReports_Click", 1)[0]
        self.assertIn("EnsureQualityEventReadyAsync", handler)
        self.assertIn("PRM Database Update Required", handler)
        self.assertIn("RequestDevelopmentDatabaseMaintenanceFromWorkflow", handler)
        self.assertLess(handler.index("EnsureQualityEventReadyAsync"), handler.index("ShowWorkspaceWindow"))

    def test_v186_stale_prm_investigation_button_is_actionable_without_workflow_ddl(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        state = prm.split("private void UpdateQualityEventButtonState", 1)[1].split("private static void ApplyQualityEventButtonState", 1)[0]
        route = prm.split("private void RouteToDevelopmentDatabaseMaintenance", 1)[1].split("private async void BtnQualityEvent_Click", 1)[0]
        handler = prm.split("private async void BtnQualityEvent_Click", 1)[1].split("private void CommitGridEdit", 1)[0]
        self.assertIn('content = "Update Database"', state)
        self.assertIn("RouteToDevelopmentDatabaseMaintenance", handler)
        self.assertIn("Close();", route)
        self.assertIn("RequestDevelopmentDatabaseMaintenanceFromWorkflow", route)
        for ddl_token in ("ALTER TABLE", "CREATE TABLE", "DROP TABLE", "ApplyRequiredUpdatesAsync"):
            self.assertNotIn(ddl_token, route)


    def test_v187_login_authentication_is_bounded_and_cancellable(self):
        login = (ROOT / "Login.xaml.cs").read_text(encoding="utf-8-sig")
        interface = (ROOT / "Services/IAuthService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("TimeSpan.FromSeconds(Math.Max(40, AppConfig.CommandTimeoutSeconds + 15))", login)
        self.assertIn("new CancellationTokenSource(SignInTimeout)", login)
        self.assertIn("AuthenticateAsync(username, password, signInCts.Token)", login)
        self.assertIn("catch (OperationCanceledException", login)
        self.assertIn("No session was created", login)
        self.assertIn("CancellationToken cancellationToken = default", interface)
        self.assertNotIn("Task.WhenAny", login)

    def test_v187_authentication_propagates_cancellation_to_sql_writes(self):
        auth = (ROOT / "Services/AuthService.cs").read_text(encoding="utf-8-sig")
        users = (ROOT / "Repositories/UserRepository.cs").read_text(encoding="utf-8-sig")
        self.assertIn("GetByUsernameAsync(username, cancellationToken)", auth)
        self.assertIn("RegisterAuthenticationFailureAsync(user, cancellationToken)", auth)
        self.assertIn("FinalizeVerifiedUserAsync(user, password, verification, true, cancellationToken)", auth)
        self.assertIn("user, upgradedHash, upgradedSalt, cancellationToken", auth)
        self.assertIn("AuthenticationCommitContract.Sql", users)
        self.assertIn("CompleteAuthenticationAsync", users)
        self.assertIn("cancellationToken: cancellationToken", users)
        self.assertNotIn("catch (Exception", auth)  # Cancellation propagates without a swallowing catch.

    def test_v187_login_warmup_cannot_linger_indefinitely(self):
        login = (ROOT / "Login.xaml.cs").read_text(encoding="utf-8-sig")
        warmup = login.split("private static async Task WarmDatabaseConnectionAsync", 1)[1]
        self.assertIn("TimeSpan.FromSeconds(5)", warmup)
        self.assertIn("OpenAsync(warmupCts.Token)", warmup)
        self.assertIn("Login remains available", warmup)


    def test_v188_prm_20260827_001_compile_before_alter_is_bridged_without_rewriting_migration(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        compatibility = migrator.split("private static async Task PrepareManifestMigrationCompatibilityAsync", 1)[1]
        bridge = compatibility.split('versionKey.Equals("20260827_001"', 1)[1].split(
            'versionKey.Equals("20260827_002"', 1
        )[0]
        migration = (ROOT / "Database/Migrations/20260827_001_PRM_Investigation_Evidence_Version_Binding.sql").read_text(encoding="utf-8-sig")
        self.assertIn("EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD EvidenceSchemaVersion", bridge)
        self.assertIn("SpecificationNumericLimit DECIMAL(18,3)", bridge)
        self.assertIn("DEFAULT (0) WITH VALUES", bridge)
        self.assertIn("EvidenceSchemaVersion", migration)
        self.assertIn("IX_QEAffected_PRM_EvidenceBinding_20260827_001", migration)

    def test_v188_prm_20260827_002_conditional_table_create_is_precreated_for_index_compilation(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        compatibility = migrator.split("private static async Task PrepareManifestMigrationCompatibilityAsync", 1)[1]
        bridge = compatibility.split('versionKey.Equals("20260827_002"', 1)[1].split(
            'versionKey.Equals("20260819_001"', 1
        )[0]
        self.assertIn("EXEC(N'CREATE TABLE dbo.PRM_QualityEventEvidenceReconciliations", bridge)
        self.assertIn("FK_PRM_QEEvidenceReconciliations_LegacyEvent_20260827_002", bridge)
        self.assertIn("ReconciliationSchemaVersion TINYINT NOT NULL", bridge)
        self.assertIn("does not match the controlled 20260827_002 structure", bridge)

    def test_v188_recorded_prm_evidence_migrations_require_structural_postconditions(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        verifier = migrator.split("private static async Task<bool> RecordedMigrationPostconditionsSatisfiedAsync", 1)[1].split(
            "private static async Task PrepareManifestMigrationCompatibilityAsync", 1
        )[0]
        self.assertIn('"20260827_001" =>', verifier)
        self.assertIn("IX_QEAffected_PRM_EvidenceBinding_20260827_001", verifier)
        self.assertIn('"20260827_002" =>', verifier)
        self.assertIn("TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002", verifier)



    def test_v189_release_binary_cannot_be_switched_to_development_by_json(self):
        app_config = (ROOT / "AppConfig.cs").read_text(encoding="utf-8-sig")
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        self.assertIn("#if DEBUG", app_config)
        self.assertIn('private const string CompiledEnvironmentName = "Development";', app_config)
        self.assertIn('private const string CompiledEnvironmentName = "Production";', app_config)
        self.assertIn("public static string EnvironmentName => CompiledEnvironmentName", app_config)
        self.assertIn("settings.EnvironmentName.Equals(CompiledEnvironmentName", app_config)
        self.assertIn("blocks environment switching by editing appsettings.json", app_config)
        self.assertIn("Production publish blocked: a Debug binary is compiled for Development", project)

    def test_v189_account_lockout_and_session_timeout_do_not_trust_workstation_clock(self):
        users = (ROOT / "Repositories/UserRepository.cs").read_text(encoding="utf-8-sig")
        auth = (ROOT / "Services/AuthService.cs").read_text(encoding="utf-8-sig")
        app = (ROOT / "App.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("LockedUntil > SYSDATETIME()", users)
        failure_sql = (ROOT / "Services/AuthenticationCommitContract.cs").read_text(encoding="utf-8-sig").split("internal const string FailureSql", 1)[1]
        self.assertIn("DATEADD(MINUTE,15,SYSDATETIME())", failure_sql)
        self.assertIn("AuthenticationCommitContract.FailureSql", users)
        self.assertIn("return user.IsLocked;", auth)
        self.assertNotIn("LockedUntil.Value > DateTime.Now", auth)
        self.assertIn("Stopwatch.GetTimestamp()", app)
        self.assertIn("Stopwatch.GetElapsedTime", app)

    def test_v189_water_historical_results_are_snapshot_first_and_master_independent(self):
        db = read_database_helper_source()
        reports = read_source_family("ReportsTrends.xaml.cs")
        results = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        details = (ROOT / "SampleDetails.xaml.cs").read_text(encoding="utf-8-sig")
        certificate = (ROOT / "ReportCertificate.xaml.cs").read_text(encoding="utf-8-sig")
        for source in (db, reports, results, details, certificate):
            self.assertIn("LEFT JOIN Tests t", source.replace("dbo.Tests", "Tests"))
        self.assertIn("CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END", db)
        self.assertIn("effectiveActionExpression = \"CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END\"", reports)
        self.assertIn("CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END", details)
        self.assertIn("CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END AS ActionLimit", certificate)
        self.assertNotIn("INNER JOIN dbo.Tests t ON t.TestID=st.TestID\nWHERE st.SampleID=@SampleID", results)

    def test_v189_quality_event_affected_results_freeze_sample_test_evidence(self):
        db = read_database_helper_source()
        oos = db.split("INSERT INTO dbo.QualityEventAffectedResults", 1)[1].split("AddAuditTrailAdvanced", 1)[0]
        self.assertIn("NULLIF(st.TestNameSnapshot,N'') IS NOT NULL", oos)
        self.assertIn("st.ActionLimitSnapshot", oos)
        self.assertIn("st.AlertLimitSnapshot", oos)
        self.assertIn("st.UnitSnapshot", oos)
        self.assertIn("LEFT JOIN dbo.Tests t", oos)

    def test_v189_quality_event_structured_evidence_has_append_only_old_new_history(self):
        source = read_source_family("QualityEventInvestigation.xaml.cs")
        migration = (ROOT / "Database/Migrations/20260828_001_Quality_Event_Evidence_History_And_Time_Hardening.sql").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("CaptureStructuredEvidenceSnapshot", source)
        self.assertIn("RecordStructuredEvidenceHistory", source)
        self.assertIn("OldRowsJson", source)
        self.assertIn("NewRowsJson", source)
        self.assertIn("ChangeReason", source)
        self.assertIn("QualityEventInvestigationEvidenceHistory", migration)
        self.assertIn("TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001", migration)
        self.assertIn("AFTER UPDATE, DELETE", migration)
        self.assertIn("ISJSON(OldRowsJson) = 1", migration)
        self.assertIn("TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001", preflight)

    def test_v189_em_and_media_release_timing_use_database_clock_for_quality_gates(self):
        em = read_source_family("EMPlanning.xaml.cs")
        media = read_source_family("CultureMediaPreparation.xaml.cs")
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        self.assertGreaterEqual(em.count("DatabaseHelper.GetAuthoritativeDatabaseTime()"), 4)
        self.assertNotIn("developmentTimingOverrideUsed = DateTime.Now", em)
        self.assertIn("CONVERT(date, SYSDATETIME()) AS DatabaseDate", media)
        self.assertIn("preparedExpiry.Value.Date < databaseDate", media)
        self.assertIn("SELECT SYSDATETIME();", prm)
        self.assertIn("analysisStartedAt = databaseNow", prm)
        self.assertIn("SELECT DATEPART(YEAR, SYSDATETIME());", prm)

    def test_v189_database_manifest_and_postcondition_control_evidence_history(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260828_001")
        self.assertEqual("Migrations/20260828_001_Quality_Event_Evidence_History_And_Time_Hardening.sql", entry["file"])
        self.assertEqual(64, len(entry["sha256"]))
        self.assertIn('"20260828_001" =>', migrator)
        self.assertIn("IX_QEInvestigationEvidenceHistory_Event_20260828_001", migrator)
        self.assertIn("TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001", migrator)



    def test_v190_em_planning_print_and_schedule_controls_are_traceable(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        self.assertIn("Procedure Ref: MQC-I-0018", planning)
        self.assertIn("EM Sampling Data Sheet Printed", planning)
        self.assertIn("EM Sample Labels Printed", planning)
        self.assertIn("ScheduleVersion", planning)
        self.assertIn("SELECT CAST(SYSDATETIME() AS date);", planning)
        self.assertIn("AdvanceToFuture(schedule.NextDueDate, schedule.Frequency, databaseToday)", planning)
        self.assertIn("IncubationTemperature,IncubatorNo1,IncubatorNo2", planning)
        self.assertIn("BuildIncubationTemperatureSummary()", planning)
        reports = (ROOT / "ReportsTrends.xaml").read_text(encoding="utf-8-sig")
        self.assertIn('Content="Internal EM Trend Review"', reports)
        self.assertNotIn('Content="Six-Month EM Trend"', (ROOT / "EMPlanning.xaml").read_text(encoding="utf-8-sig"))

    def test_v190_em_result_report_preserves_excursion_identity_and_server_time(self):
        em = read_source_family("EMResultsEntry.xaml.cs")
        helper = read_database_helper_source()
        self.assertIn("reportGeneratedAt = DatabaseHelper.GetAuthoritativeDatabaseTime();", em)
        self.assertIn("Procedure Ref: MQC-I-0018", em)
        self.assertIn('"EMRR-" + eventReference', em)
        self.assertIn("ACTION / OOS - QA Disposition Completed", em)
        self.assertIn("does not convert the excursion into a within-limit result", em)
        self.assertIn("Media Lot / Preparation Ref.", em)
        self.assertIn("SignerDisplayName", helper)
        signature_start = helper.index("public static DataTable GetEMEventSignatures")
        signature_end = helper.index("public static int AddEMEventSignature", signature_start)
        signatures = helper[signature_start:signature_end]
        self.assertIn("S.SignedBy AS SignerDisplayName", signatures)
        self.assertNotIn("JOIN dbo.Users", signatures)
        self.assertNotIn("U.FullName", signatures)

    def test_v190_prm_reports_use_unambiguous_dates_and_display_names(self):
        template = (ROOT / "Services/PRMCertificateTemplate.cs").read_text(encoding="utf-8-sig")
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        self.assertIn('"Specification No."', template)
        self.assertIn('"Specification Version"', template)
        self.assertIn('"Manufacturing Date"', template)
        self.assertIn('"Packaging Date"', template)
        self.assertIn('"Expiry Date"', template)
        self.assertNotIn('"Mfg / Packaging / Expiry"', template)
        self.assertIn('FormatDateOnly(sample, "ManufacturingDate")', template)
        self.assertIn("SampledByDisplay", template)
        self.assertIn("SignerDisplayName", template)
        self.assertIn("Data Integrity:</b> SHA-256 hash recorded", template)
        self.assertIn("Generated from the immutable issue record at:", template)
        self.assertNotIn("Page 1 of 1", template)
        self.assertIn("SampledByDisplay", results)
        self.assertIn("SignerDisplayName", results)
        self.assertIn("LEFT JOIN dbo.Users U WITH(HOLDLOCK)", results)

    def test_v190_em_trend_print_is_controlled_and_audited(self):
        trend = (ROOT / "EMTrendReport.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("DatabaseHelper.GetAuthoritativeDatabaseTime().Date", trend)
        self.assertIn("Reports permission is required to print the Environmental Monitoring trend.", trend)
        self.assertIn("Procedure Ref: MQC-G-0009", trend)
        self.assertIn("MQC-G-0009/G1/1", trend)
        self.assertIn("MQC-G-0009/F4/1", trend)
        self.assertIn("SYSTEM-GENERATED REVIEW DRAFT", trend)
        self.assertIn("EM Trend Review Printed", trend)


    def test_v191_em_planning_layout_is_not_horizontally_collapsed(self):
        xaml = (ROOT / "EMPlanning.xaml").read_text(encoding="utf-8-sig")
        code = read_source_family("EMPlanning.xaml.cs")
        self.assertIn('HorizontalScrollBarVisibility="Disabled"', xaml)
        self.assertIn('Header="Area Code"', xaml)
        self.assertIn('Header="Area Name"', xaml)
        self.assertIn('Header="Grade / Classification"', xaml)
        self.assertIn('Content="Select All Areas"', xaml)
        self.assertIn('Content="Clear Selection"', xaml)
        self.assertIn('Content="Ad-hoc"', xaml)
        self.assertIn('x:Name="cboSource" Style="{StaticResource Field}" IsEnabled="False"><ComboBoxItem Content="Ad-hoc"/></ComboBox>', xaml)
        self.assertIn('const string source = "Ad-hoc";', code)
        self.assertIn('SelectAllAreas_Click', code)
        self.assertIn('ClearAreas_Click', code)

    def test_v191_prm_current_layout_preview_preserves_issued_snapshot(self):
        xaml = (ROOT / "ProductionRawMaterialResults.xaml").read_text(encoding="utf-8-sig")
        code = read_source_family("ProductionRawMaterialResults.xaml.cs")
        self.assertIn('BtnPreviewCurrentLayout', xaml)
        self.assertIn('Preview Current Layout', xaml)
        self.assertIn('PREVIEW - CURRENT TEMPLATE LAYOUT - NOT A CONTROLLED RECORD', code)
        self.assertIn('immutable issued snapshot was not modified', code)
        self.assertIn('LoadPrmCertificateSnapshotHtml', code)
        self.assertIn('Controlled certificate content opened from the immutable issue snapshot.', code)

    def test_v191_prm_report_conclusion_uses_status_specific_visual_state(self):
        template = (ROOT / "Services/PRMCertificateTemplate.cs").read_text(encoding="utf-8-sig")
        self.assertIn('conclusion.conclusionDnc', template)
        self.assertIn('conclusion.conclusionConform', template)
        self.assertIn('conclusion.conclusionReview', template)
        self.assertIn('GetConclusionCss(overall)', template)
        self.assertIn('<table class=\'results\'><thead>', template)
        self.assertIn('page-break-inside:avoid', template)
        self.assertIn('related quality-event investigation and disposition are retained', template)


    def test_v192_water_plan_frequency_and_point_selection_are_controlled(self):
        xaml = (ROOT / "EMPlanning.xaml").read_text(encoding="utf-8-sig")
        code = read_source_family("EMPlanning.xaml.cs")
        self.assertIn('x:Name="cboWaterFrequency"', xaml)
        self.assertIn('IsEnabled="False" ToolTip="Frequency is controlled by water type: Purified = Fortnightly; Potable = Monthly."', xaml)
        self.assertIn('Content="Select All Points"', xaml)
        self.assertIn('Content="Clear Selection"', xaml)
        self.assertIn('ApplyControlledWaterFrequency()', code)
        self.assertIn('SelectAllWaterPoints_Click', code)
        self.assertIn('ClearWaterPoints_Click', code)
        self.assertIn('? "Fortnightly"', code)
        self.assertIn(': "Monthly";', code)

    def test_v192_water_plan_create_has_schema_gate_typed_sql_and_stage_logging(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        create = planning.split("private void CreateWaterPlan()", 1)[1].split("private static string GetNextWaterPlanNo", 1)[0]
        self.assertIn("EnsureWaterPlanningSchemaReady();", create)
        self.assertIn('plan.Parameters.Add("@No", SqlDbType.NVarChar, 50)', create)
        self.assertIn('sample.Parameters.Add("@Plan", SqlDbType.Int)', create)
        self.assertIn('Water plan creation failed during {stage}', create)
        self.assertIn('Water plan creation did not return a valid plan identifier', create)
        self.assertIn('Use Refresh before creating another plan', create)
        self.assertIn('ExecuteWaterUi(CreateWaterPlan, "Create water plan")', planning)
        self.assertIn('ShowWaterError', planning)

    def test_v192_water_plan_schema_is_part_of_system_preflight(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        for token in [
            "(N'Water_Plans',N'WaterPlanID')",
            "(N'Water_Plans',N'AnalysisProfile')",
            "(N'Water_PlanSamples',N'AnalysisProfile')",
            "(N'Water_PlanSignatures',N'MeaningOfSignature')",
        ]:
            self.assertIn(token, preflight)

    def test_v192_error_unwrapping_and_rollback_preserve_original_failure(self):
        errors = (ROOT / "Infrastructure/UserFacingError.cs").read_text(encoding="utf-8-sig")
        connection = (ROOT / "Infrastructure/DatabaseConnection.cs").read_text(encoding="utf-8-sig")
        self.assertIn("Exception effectiveException = Unwrap(exception);", errors)
        self.assertIn("TargetInvocationException", errors)
        self.assertIn("AggregateException", errors)
        self.assertIn("TypeInitializationException", errors)
        self.assertIn("Database transaction rollback failed after an earlier operation failure", connection)
        self.assertIn("throw;", connection)



    def test_v193_water_plan_tests_freeze_after_distribution(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        self.assertIn('if (!plan.Status.Equals("Planned", StringComparison.OrdinalIgnoreCase))', planning)
        self.assertIn("Water Plan tests are frozen after Distribution", planning)
        self.assertIn("WITH(UPDLOCK,HOLDLOCK)", planning)
        self.assertIn("DELETE dbo.Water_PlanSampleTests", planning)
        self.assertIn("The Water Plan was distributed or changed while test assignment was open", planning)

    def test_v193_water_plan_registration_uses_exact_frozen_tests_and_atomic_link(self):
        dialog = (ROOT / "NewSampleDialog.xaml.cs").read_text(encoding="utf-8-sig")
        planning = read_source_family("EMPlanning.xaml.cs")
        self.assertIn("_plannedWaterTestIds", dialog)
        self.assertIn("IsEnabled = !_isPlanRegistration", dialog)
        self.assertIn("btnSelectAll.IsEnabled = false", dialog)
        self.assertIn("btnClearAll.IsEnabled = false", dialog)
        self.assertIn("_selectedTestIds.SetEquals(_plannedWaterTestIds)", dialog)
        self.assertIn("The Water Plan test assignment changed after the registration window opened", dialog)
        self.assertIn("INSERT dbo.Water_PlanSampleAttempts", dialog)
        self.assertIn("UPDATE dbo.Water_PlanSamples", dialog)
        self.assertIn("tran.Commit();", dialog)
        register = planning.split("private void RegisterWaterSample_Click", 1)[1].split("private void OpenWaterResults_Click", 1)[0]
        self.assertNotIn("INSERT INTO dbo.Water_PlanSampleAttempts", register)
        self.assertNotIn("UPDATE dbo.Water_PlanSamples", register)

    def test_v193_em_schedule_approval_freezes_exact_point_identity(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        migration = (ROOT / "Database/Migrations/20260828_002_EM_Water_Planning_Integrity_Hardening.sql").read_text(encoding="utf-8-sig")
        self.assertIn("EM_SchedulePointSnapshots", planning)
        self.assertIn("LoadApprovedSchedulePointSnapshots", planning)
        self.assertIn("ApprovedSchedulePointSnapshot", planning)
        self.assertIn("ApprovedPointCount", planning)
        self.assertIn("CREATE TABLE dbo.EM_SchedulePointSnapshots", migration)
        self.assertIn("TRG_EM_SchedulePointSnapshots_AppendOnly_20260828", migration)
        self.assertIn("UQ_EM_SchedulePointSnapshots_Sequence", migration)
        self.assertIn("Historical approved schedules are intentionally NOT reconstructed from current master data", migration)
        self.assertNotIn("LegacySnapshotSource", migration)

    def test_v193_em_plate_limits_are_frozen_at_creation_and_live_fallback_is_forbidden(self):
        results = read_source_family("EMResultsEntry.xaml.cs")
        migration = (ROOT / "Database/Migrations/20260828_002_EM_Water_Planning_Integrity_Hardening.sql").read_text(encoding="utf-8-sig")
        load_plates = results.split("private void LoadPlates(int eventId)", 1)[1].split("private bool HasEnteredResult", 1)[0]
        self.assertIn("TRG_EM_EventPlates_FreezeLimits_20260828", migration)
        self.assertIn("TRG_EM_EventPlates_ProtectLimits_20260828", migration)
        self.assertIn("Frozen EM limit snapshots cannot be changed", migration)
        self.assertNotIn("AlertLimitSnapshot = @alertLimitSnapshot", results)
        self.assertNotIn("ActionLimitSnapshot = @actionLimitSnapshot", results)
        self.assertIn("AlertLimitSnapshot", migration)
        self.assertIn("ActionLimitSnapshot", migration)
        self.assertIn("ResultUnitSnapshot", migration)
        self.assertIn("AirVolumeLitersSnapshot", migration)
        self.assertIn("Historical EM_EventPlates with missing snapshots are intentionally NOT backfilled", migration)
        self.assertIn("will not substitute current master limits for historical evidence", load_plates)
        self.assertNotIn("GetEMPlateLimits(grade, method)", load_plates)

    def test_v194_em_schedule_seed_identity_and_sampling_context_are_complete(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        xaml = (ROOT / "EMPlanning.xaml").read_text(encoding="utf-8-sig")
        self.assertIn("private sealed record SampleSeed(int TemplateID, int TemplateSequenceNo, string Method, string Location);", planning)
        schedule_seeds = planning.split("private static IEnumerable<SampleSeed> BuildScheduleSeeds", 1)[1].split("private static List<ApprovedSchedulePointSnapshot>", 1)[0]
        self.assertIn("SELECT Id,SequenceNo,Method,PlateCode", schedule_seeds)
        self.assertIn('Convert.ToInt32(reader["Id"], CultureInfo.InvariantCulture)', schedule_seeds)
        self.assertIn('Convert.ToInt32(reader["SequenceNo"], CultureInfo.InvariantCulture)', schedule_seeds)
        self.assertIn("ComposeSamplingLocation(location", schedule_seeds)
        self.assertIn("BuildSeeds(connection, transaction, area, method, txtLocation.Text.Trim())", planning)
        self.assertIn("ComposeSamplingLocation(locationContext", planning)
        self.assertIn("Sampling Context / Surface (optional)", xaml)
        self.assertIn("does not replace the controlled point identity", xaml)

    def test_v194_water_distribution_is_parent_gated_and_atomic(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        distribute = planning.split("private void DistributeWater_Click", 1)[1].split("private void RegisterWaterSample_Click", 1)[0]
        self.assertIn("WITH(UPDLOCK,HOLDLOCK)", distribute)
        self.assertIn("plannedSampleCount", distribute)
        self.assertIn("if (parent.ExecuteNonQuery() != 1)", distribute)
        self.assertIn("if (children.ExecuteNonQuery() != plannedSampleCount)", distribute)
        self.assertIn("No partial distribution was committed", distribute)
        self.assertLess(distribute.index("parent.ExecuteNonQuery()"), distribute.index("children.ExecuteNonQuery()"))
        self.assertIn("InsertWaterSignature", distribute)
        self.assertIn("AddAuditTrailAdvanced", distribute)

    def test_v194_em_historical_snapshot_reconciliation_is_append_only_and_strict(self):
        migration = (ROOT / "Database/Migrations/20260828_003_EM_Limit_Snapshot_Reconciliation_And_Strict_Creation.sql").read_text(encoding="utf-8-sig")
        dialog = (ROOT / "EMLegacySnapshotReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        results = read_source_family("EMResultsEntry.xaml.cs")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")

        self.assertIn("CREATE TABLE dbo.EM_LimitSnapshotReconciliations", migration)
        self.assertIn("TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828", migration)
        self.assertIn("THROW 53547", migration)
        self.assertIn("Plate creation was rolled back", migration)
        self.assertIn("AlertLimitSnapshot = approvedLimit.AlertLimitTotal", migration)
        self.assertIn("ActionLimitSnapshot = approvedLimit.ActionLimitTotal", migration)
        self.assertNotIn("COALESCE(plate.AlertLimitSnapshot, approvedLimit.AlertLimitTotal)", migration)
        self.assertIn("SupersedesReconciliationID", migration)
        self.assertIn("Reconcile Historical EM Limit Snapshot", dialog)
        self.assertIn("EnsureUserPermissionInTransaction", dialog)
        self.assertIn('"CanApproveResults"', dialog)
        self.assertIn("ElectronicSignature", dialog)
        self.assertIn("EM_LimitSnapshotReconciliations", results)
        self.assertIn("EmLimitEvidenceSql.Joins(forUpdate)", results)
        self.assertIn("Signed Historical Reconciliation #",
                      (ROOT / "Services/EmLimitEvidenceSql.cs").read_text(encoding="utf-8-sig"))
        self.assertNotIn("item.Unit = GetDefaultUnit", results)
        self.assertNotIn('item.Unit = "CFU/m3"', results)
        self.assertNotIn("DefaultUnit(_selected.Method)", dialog)
        self.assertIn("CheckHistoricalEmSnapshotIntegrityAsync", preflight)
        self.assertIn("EM_LimitSnapshotReconciliations", preflight)
        self.assertIn("20260828_003", [item["versionKey"] for item in manifest["migrations"]])
        self.assertIn("VerifyEmLimitSnapshotReconciliationAsync", integration)

    def test_v195_em_reconciliation_migration_repairs_legacy_plate_candidate_key_without_changing_migration(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        migration_path = ROOT / "Database/Migrations/20260828_003_EM_Limit_Snapshot_Reconciliation_And_Strict_Creation.sql"
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260828_003")
        migration_bytes = migration_path.read_bytes()
        self.assertEqual(hashlib.sha256(migration_bytes).hexdigest(), entry["sha256"])
        compatibility = migrator.split('if (versionKey.Equals("20260828_003", StringComparison.Ordinal))', 1)[1].split('if (versionKey.Equals("20260819_001", StringComparison.Ordinal))', 1)[0]
        self.assertIn("UX_EM_EventPlates_Id_20260828_003", compatibility)
        self.assertIn("CREATE UNIQUE NONCLUSTERED INDEX", compatibility)
        self.assertIn("duplicate Id values", compatibility)
        self.assertIn("NULL Id values", compatibility)
        self.assertIn("i.is_unique = 1", compatibility)
        self.assertIn("i.has_filter = 0", compatibility)
        self.assertIn("c.name = N'Id'", compatibility)
        self.assertIn("return;", compatibility)

    def test_v194_em_preflight_blocks_active_unresolved_historical_evidence(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        check = preflight.split("private async Task CheckHistoricalEmSnapshotIntegrityAsync", 1)[1].split("private async Task CheckMigrationLedgerAsync", 1)[0]
        self.assertIn("UnresolvedActive", check)
        self.assertIn('"BLOCKER"', check)
        self.assertIn("signed reconciliation", check)
        self.assertIn("EM_LimitSnapshotReconciliations", check)
        protection = (ROOT / "Infrastructure/ComplianceRecordProtectionContract.cs").read_text(encoding="utf-8-sig")
        self.assertIn("EM_LimitSnapshotReconciliations", protection)
        self.assertIn("ComplianceRecordProtectionContract.QuerySql", preflight.split("private async Task CheckComplianceRecordProtectionAsync",1)[1])

    def test_v193_em_temperature_excursions_are_recorded_and_open_quality_events(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        helper = read_database_helper_source()
        migration = (ROOT / "Database/Migrations/20260828_002_EM_Water_Planning_Integrity_Hardening.sql").read_text(encoding="utf-8-sig")
        self.assertIn("IncubationPhase1Excursion", planning)
        self.assertIn("IncubationPhase2Excursion", planning)
        self.assertIn('"Incubation Excursion"', planning)
        self.assertIn("EnsureOpenSourceQualityEvent", planning)
        self.assertIn("public static int EnsureOpenSourceQualityEvent", helper)
        self.assertIn("public static bool HasOpenSourceQualityEvent", helper)
        self.assertIn("value < -20m || value > 80m", planning)
        self.assertNotIn("must be between 20 and 25", planning)
        self.assertNotIn("must be between 30 and 35", planning)
        self.assertIn("IncubationPhase1Excursion", migration)
        self.assertIn("IncubationPhase2Excursion", migration)

    def test_v193_collection_and_negative_control_failures_are_recorded_not_hidden(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        dialog = (ROOT / "EMCollectionDialog.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('"Collection Excursion"', planning)
        self.assertIn('"Negative Control Failure"', planning)
        self.assertIn("Recollection Required", planning)
        self.assertIn("Control Failed", planning)
        self.assertIn("CollectionExcursion", planning)
        self.assertNotIn('Plate condition must be "Acceptable"', dialog)
        self.assertNotIn('Media kit condition must be "Released / Within Expiry"', dialog)

    def test_v193_water_planning_schema_and_referential_integrity_are_preflighted(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        migration = (ROOT / "Database/Migrations/20260828_002_EM_Water_Planning_Integrity_Hardening.sql").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "EMPlanning.xaml").read_text(encoding="utf-8-sig")
        self.assertIn("Water_PlanSampleTests", planning)
        self.assertIn("Water_PlanSampleAttempts", planning)
        self.assertIn("Water_PlanSampleTests", preflight)
        self.assertIn("Water_PlanSampleAttempts", preflight)
        self.assertIn("FK_Water_PlanSampleTests_Tests_20260828", migration)
        self.assertIn("FK_Water_PlanSampleAttempts_Samples_20260828", migration)
        self.assertIn("FK_Water_PlanSampleTests_Tests_20260828", preflight)
        self.assertIn("FK_Water_PlanSampleAttempts_Samples_20260828", preflight)
        self.assertIn("TRG_Water_PlanSampleTests_FreezeDistributed_20260828", migration)
        self.assertIn("TRG_Water_PlanSamples_ProtectDistributed_20260828", migration)
        self.assertIn("TRG_Water_PlanSampleTests_FreezeDistributed_20260828", preflight)
        self.assertIn("TRG_Water_PlanSamples_ProtectDistributed_20260828", preflight)
        self.assertIn("TRG_EM_SchedulePointSnapshots_AppendOnly_20260828", preflight)
        self.assertIn("AFTER INSERT, UPDATE, DELETE", migration)
        self.assertIn("TRG_EM_EventPlates_FreezeLimits_20260828", preflight)
        self.assertIn("TRG_EM_EventPlates_ProtectLimits_20260828", preflight)
        self.assertIn('Content="Routine"', xaml)
        self.assertNotIn('Content="Scheduled"', xaml)

    def test_v193_quality_event_gate_blocks_em_results_handoff_until_excursion_is_closed(self):
        planning = read_source_family("EMPlanning.xaml.cs")
        helper = read_database_helper_source()
        self.assertIn('DatabaseHelper.HasOpenSourceQualityEvent("EM Planning", plan.PlanID)', planning)
        self.assertIn("QA disposition is required before release to EM Results", planning)
        self.assertIn("UPPER(LTRIM(RTRIM(ISNULL(CurrentStatus,N'Open')))) NOT IN", helper)

    def test_v201_water_qualitative_results_persist_as_decimal_encoding(self):
        water = read_source_family("ResultsEntry.xaml.cs")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("ResultValue decimal(18,4)", integration)
        self.assertIn("private decimal GetPersistedResultValue", water)
        self.assertIn('new SqlParameter("@resultValue", SqlDbType.Decimal)', water)
        self.assertIn("Precision = 18, Scale = 4", water)
        self.assertNotIn('new SqlParameter("@resultValue", SqlDbType.NVarChar, 200)', water)
        self.assertIn('raw.Equals("Absence", StringComparison.OrdinalIgnoreCase)', water)
        self.assertIn('raw.Equals("Presence", StringComparison.OrdinalIgnoreCase)', water)


    def test_v202_oral_tablet_standard_uses_compendial_1000_100_limits_without_rewriting_history(self):
        migration = (ROOT / "Database/Migrations/20260830_001_PRM_Oral_Tablet_Compendial_Microbial_Limits.sql").read_text(encoding="utf-8-sig")
        self.assertIn("N'NMT 1000 CFU/g'", migration)
        self.assertIn("N'NMT 100 CFU/g'", migration)
        self.assertIn("CONVERT(DECIMAL(18,3),1000)", migration)
        self.assertIn("CONVERT(DECIMAL(18,3),100)", migration)
        self.assertIn("USP <61>/<62>/<1111>", migration)
        self.assertIn("Historical PRM_SampleTests snapshots are deliberately not changed", migration)
        self.assertNotRegex(migration, r"(?i)UPDATE\s+(?:dbo\.)?PRM_SampleTests")
        for profile in (
            "MIC-IP-ORAL-TABLET-STD-001",
            "MIC-FP-ORAL-TABLET-STD-001",
            "MIC-ST-ORAL-TABLET-STD-001",
        ):
            self.assertIn(profile, migration)

    def test_v203_oral_tablet_reference_text_distinguishes_usp1111_criteria_from_site_controls(self):
        migration = (ROOT / "Database/Migrations/20260830_002_PRM_Oral_Tablet_Compendial_Reference_Clarification.sql").read_text(encoding="utf-8-sig")
        self.assertIn("USP <61>/<1111>; non-aqueous oral preparation acceptance criterion", migration)
        self.assertIn("USP <62>/<1111>; E. coli absent in 1 g for non-aqueous oral preparations", migration)
        self.assertIn("USP <62> method; site-defined objectionable-organism control", migration)
        self.assertIn("Migration 20260830_001 remains unchanged to preserve the checksum", migration)
        self.assertIn("CONVERT(DECIMAL(18,3),1000)", migration)
        self.assertIn("CONVERT(DECIMAL(18,3),100)", migration)
        self.assertNotRegex(migration, r"(?i)UPDATE\s+(?:dbo\.)?PRM_SampleTests")
        self.assertNotIn("USP <61>/<62>/<1111>", migration)

    def test_v204_trend_nmt_semantics_are_consistent_and_equality_is_not_excursion(self):
        ext = (ROOT / "Services/ExternalTrendImportService.cs").read_text(encoding="utf-8-sig")
        cycles = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        review = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        em = (ROOT / "EMTrendReport.xaml.cs").read_text(encoding="utf-8-sig")
        exact_import = ext.split('return action.HasValue && result > action.Value', 1)[1].split('private static bool TryParseDate', 1)[0]
        self.assertIn('? "FAIL"', exact_import)
        self.assertIn('alert.HasValue && result > alert.Value', exact_import)
        # Qualified lower-bound results may legitimately use >= to prove that an
        # excursion is certain; exact NMT results still use strict >.
        self.assertIn('if (q == ">")', ext)
        self.assertIn('result >= action.Value', ext)
        self.assertIn("result > action.Value", cycles)
        self.assertIn("result > alert.Value", cycles)
        self.assertIn("point.Result > point.ActionLimit", review)
        self.assertIn("point.Result > point.AlertLimit", review)
        calculator = (ROOT / "Services/EmResultCalculator.cs").read_text(encoding="utf-8-sig")
        assessment = (ROOT / "Services/EmTrendAssessmentService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("numerator > action.Value * denominator", calculator)
        self.assertIn("numerator > alert.Value * denominator", calculator)
        self.assertIn("EmResultCalculator.Calculate(", assessment)
        self.assertIn("EmTrendAssessmentService.Apply(loaded)", em)
        self.assertIn("Equality with an NMT limit is not an excursion", em)

    def test_v204_em_trend_has_fungal_counts_narrative_and_frozen_area_context(self):
        em = (ROOT / "EMTrendReport.xaml.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "EMTrendReport.xaml").read_text(encoding="utf-8-sig")
        migration = (ROOT / "Database/Migrations/20260830_003_EM_Trend_Historical_Context_And_Consistency.sql").read_text(encoding="utf-8-sig")
        self.assertIn("P.FungalCount", em)
        self.assertIn("FungalMinimum", em)
        self.assertIn("Impact of Seasonal Changes", em)
        self.assertIn("Analysis of Change in Microbial Flora", em)
        self.assertIn("Analysis of Excursions", em)
        self.assertIn("Review of Alert and Action Levels", em)
        self.assertIn('x:Name="txtNarrative"', xaml)
        self.assertIn("AreaCodeSnapshot", migration)
        self.assertIn("AreaNameSnapshot", migration)
        self.assertIn("GradeSnapshot", migration)
        self.assertIn("TRG_EM_Events_ProtectAreaSnapshot_20260830", migration)
        self.assertIn("Legacy current-master reconciliation - verify historical identity", migration)

    def test_v204_water_trend_does_not_reinterpret_history_from_current_master_or_hardcoded_point_limits(self):
        trend = read_source_family("ReportsTrends.xaml.cs")
        effective = trend.split("private static (double? Alert, double? Action) GetEffectiveWaterLimits",1)[1].split("private static double? TryGetNullableDouble",1)[0]
        self.assertIn("return (snapAlert, snapAction)", effective)
        self.assertIn("return (null, null)", effective)
        self.assertNotIn("masterAlertValue", effective)
        self.assertNotIn("profileRule", effective)
        summary = trend.split("private DataTable LoadSummaryData",1)[1].split("Builds the Status Summary chart model",1)[0]
        self.assertIn("NormalizeInternalWaterRows(summaryData)", summary)

    def test_v204_prm_censored_results_are_not_used_as_exact_statistics(self):
        trend = read_source_family("ReportsTrends.xaml.cs")
        self.assertIn("TryGetTrendNumericValue", trend)
        self.assertIn("qualified/censored result(s) excluded", trend)
        self.assertIn("censored values", trend)

    def test_v204_migration_is_manifested_and_preflighted(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260830_003")
        migration = ROOT / "Database" / entry["file"]
        self.assertEqual(hashlib.sha256(migration.read_bytes()).hexdigest(), entry["sha256"])
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("TRG_EM_Events_ProtectAreaSnapshot_20260830", preflight)


    def test_v205_reports_trends_static_numeric_helper_compiles_by_contract(self):
        trend = read_source_family("ReportsTrends.xaml.cs")
        self.assertIn("private static bool TryGetDouble(object value, out double result)", trend)
        helper = trend.split("private static bool TryGetTrendNumericValue", 1)[1].split("private static string NormalizeWaterProfile", 1)[0]
        self.assertIn("return TryGetDouble(value, out result);", helper)


    def test_v206_external_import_development_admin_override_is_explicit_and_production_keeps_sod(self):
        service = (ROOT / "Services/ExternalTrendImportService.cs").read_text(encoding="utf-8-sig")
        block = service.split("private static void ChangeStatus", 1)[1].split("private static Dictionary<string, int> ResolveColumns", 1)[0]
        self.assertIn("AppConfig.DevelopmentAdminFullPermissions", block)
        self.assertIn('userRole.Equals("Admin"', block)
        self.assertIn("if (!developmentAdminOverride && importedBy.Equals", block)
        self.assertIn("Segregation of duties: the importer cannot approve or reject the same batch.", block)

    def test_v206_external_em_import_accepts_grade_d_and_iso8_as_classified(self):
        service = (ROOT / "Services/ExternalTrendImportService.cs").read_text(encoding="utf-8-sig")
        normalizer = service.split("private static bool TryNormalizeAreaClassification", 1)[1].split("private static string NormalizeHeader", 1)[0]
        self.assertIn('compact.Equals("GradeD"', normalizer)
        self.assertIn('compact.Equals("GradeDProduction"', normalizer)
        self.assertIn('compact.Equals("ISO8"', normalizer)
        self.assertIn('compact.Equals("ISO8Production"', normalizer)
        self.assertIn('normalized = "Classified";', normalizer)

    def test_v206_external_trend_legacy_unspecified_is_visible_but_not_silently_reclassified(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        population = service.split("private static string NormalizeTrendPopulation", 1)[1].split("private static SqlParameter[] MethodParameterParameters", 1)[0]
        self.assertIn('return "Classification Required";', population)
        self.assertIn('value.StartsWith("UNSPECIFIED"', population)
        areas = service.split("public DataTable GetAreas()", 1)[1].split("public DataTable GetMethods", 1)[0]
        self.assertIn('Where(value => !value.Equals("Classification Required"', areas)
        self.assertIn('?? "Classification Required"', areas)
        self.assertIn('"QA Classification Required"', areas)

    def test_v206_external_trend_screen_recovers_from_empty_population_and_exposes_import_approval(self):
        xaml = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml").read_text(encoding="utf-8-sig")
        code = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('Content="Import / Approve Data"', xaml)
        self.assertIn('Content="Classification Required (legacy)"', xaml)
        self.assertIn("OpenImportApproval_Click", code)
        self.assertIn("GetImportAvailability", code)
        self.assertIn("cboPopulation.SelectedIndex = 0; // All classifications", code)
        self.assertIn("Showing All classifications instead", code)
        self.assertIn("ReviewContainsUnresolvedClassification", code)
        self.assertIn("QA approval is blocked because one or more reviewed areas use legacy/unresolved AreaClassification", code)


    def test_v207_external_trend_uses_pending_numeric_import_as_explicit_draft_when_no_approved_source_exists(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("public enum ExternalTrendDataMode", service)
        self.assertIn("PendingDraft", service)
        self.assertIn('private string BatchStatus => IsDraftMode ? "Pending Approval" : "Approved";', service)
        self.assertIn("WHERE b.Status = @BatchStatus", service)
        self.assertIn("COALESCE(b.ApprovedAt, b.ImportedAt) AS ApprovedAt", service)
        self.assertIn("availability.ApprovedNumericRows > 0", window)
        self.assertIn("availability.PendingNumericRows > 0", window)
        self.assertIn("ExternalTrendDataMode.PendingDraft", window)
        self.assertIn("DRAFT DATA SOURCE", window)
        self.assertIn("btnApproveSnapshot.IsEnabled = !service.IsDraftMode", window)
        self.assertIn("QA snapshot approval is blocked because the current review uses a staged import", window)
        self.assertIn("DRAFT - SOURCE IMPORT PENDING APPROVAL", window)
        save = service.split("public ExternalTrendReviewSaveResult SaveSignedReviewSnapshots", 1)[1].split("public static TrendCycleSummary Summarize", 1)[0]
        self.assertIn("if (IsDraftMode)", save)
        self.assertIn("Pending Approval", save)



    def test_v208_external_import_preserves_qualifiers_method_identity_and_historical_limit_changes(self):
        service = (ROOT / "Services/ExternalTrendImportService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("TryParseQualifiedDecimal", service)
        self.assertIn('row.ResultQualifier ?? string.Empty', service)
        self.assertIn('(row.MethodName ?? string.Empty).Trim().ToUpperInvariant()', service)
        self.assertIn('Method is required for Environmental Monitoring trend rows.', service)
        self.assertIn('row.ParameterName.Trim() + "|" + row.AreaClassification.Trim() + "|" + (row.MethodName ?? string.Empty).Trim()', service)
        self.assertIn('Severity = "Warning"', service)
        self.assertIn('Row-specific historical limits are retained', service)
        self.assertIn('ResultQualifier, UnitName, AlertLimit, ActionLimit', service)

    def test_v208_external_import_dialog_reparses_on_module_change_and_routes_water_separately(self):
        xaml = (ROOT / "ExternalTrendImportDialog.xaml").read_text(encoding="utf-8-sig")
        code = (ROOT / "ExternalTrendImportDialog.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('SelectionChanged="ModuleSelectionChanged"', xaml)
        self.assertNotIn('SelectedIndex="0"', xaml)
        self.assertIn('ExternalTrendImportDialog(string preferredModule)', code)
        self.assertIn('importService.ParseFile(txtFilePath.Text, SelectedModule())', code)
        self.assertIn('new ExternalTrendThreeCycleReviewWindow { Owner = this }.ShowDialog()', code)
        self.assertIn('new ReportsTrends(batchId, parameter)', code)

    def test_v208_external_review_uses_latest_source_scope_and_location_without_dead_selectors(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        window = (ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("LatestApprovedAt", service)
        self.assertIn("LatestPendingAt", service)
        self.assertIn("pendingIsNewer", window)
        self.assertIn("GetAvailableDateRangeForAreas", service)
        self.assertIn("CurrentVisibleAreaCodes", window)
        self.assertIn("SourceCalendarBoundary", service)
        self.assertIn("LocationSummary = GetAreaLocationSummary", window)
        self.assertIn("Showing All classifications instead", window)
        self.assertIn("Classification Required", service)

    def test_v208_external_trend_censored_results_are_not_exact_statistics(self):
        service = (ROOT / "Services/ExternalTrendThreeCycleService.cs").read_text(encoding="utf-8-sig")
        reports = read_source_family("ReportsTrends.xaml.cs")
        self.assertIn("ExactCount", service)
        self.assertIn("CensoredCount", service)
        self.assertIn("EvaluateQualifiedSignal", service)
        self.assertIn("qualified/censored boundary", reports)
        self.assertIn("qualifiedPointsByEntity", reports)
        self.assertIn('row.Table.Columns.Contains("ResultQualifier")', reports)
        self.assertIn("excluded from exact statistics", reports)

    def test_v208_internal_em_trend_never_pools_incompatible_units_and_has_real_chart(self):
        xaml = (ROOT / "EMTrendReport.xaml").read_text(encoding="utf-8-sig")
        code = (ROOT / "EMTrendReport.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('xmlns:oxy="http://oxyplot.org/wpf"', xaml)
        self.assertIn('x:Name="TrendChart"', xaml)
        self.assertIn('SelectionChanged="GridSummary_SelectionChanged"', xaml)
        self.assertIn("Method/unit-specific statistics (different units are never pooled)", code)
        self.assertIn('GroupBy(r => $"{Convert.ToString(r["Method"]', code)
        self.assertIn("Comparison with Previous Period / Previous Year", code)
        self.assertIn("TimeSpan selectedDuration = end - start", code)
        self.assertIn("SliceDetails(loaded, previousPeriodStart, start)", code)
        self.assertIn("SliceDetails(loaded, start.AddYears(-1), end.AddYears(-1))", code)
        self.assertIn("Procedure Ref: MQC-G-0009", code)
        self.assertIn("DRAFT - INCLUDES NON-APPROVED EVENTS", code)

    def test_v208_reports_trends_status_population_and_limit_controls_are_consistent(self):
        xaml = (ROOT / "ReportsTrends.xaml").read_text(encoding="utf-8-sig")
        code = read_source_family("ReportsTrends.xaml.cs")
        self.assertIn('Checked="LimitsVisibilityChanged"', xaml)
        self.assertIn('Unchecked="LimitsVisibilityChanged"', xaml)
        self.assertIn("PENDING", code)
        self.assertIn("NOT ASSESSED", code)
        self.assertIn("pendingSeries", code)
        self.assertIn("notAssessedSeries", code)
        self.assertIn("@summaryTestId", code)
        self.assertIn("@selectedTest", code)


    def test_v209_trend_build_blockers_are_closed(self):
        em = (ROOT / "EMTrendReport.xaml.cs").read_text(encoding="utf-8-sig")
        dialog = (ROOT / "ExternalTrendImportDialog.xaml.cs").read_text(encoding="utf-8-sig")
        service = (ROOT / "Services/ExternalTrendImportService.cs").read_text(encoding="utf-8-sig")
        self.assertNotRegex(em, r"(?<!System\.Windows\.)FontWeights\.(Bold|Normal)")
        analyze = dialog.split("private void AnalyzeImport_Click", 1)[1].split("private void ResetNewImport", 1)[0]
        self.assertIn('int batchId = Convert.ToInt32(row["ImportBatchID"]);', analyze)
        self.assertIn("private static bool LooksLikeSpreadsheetFormula", service)
        self.assertIn("private static bool TryParseDecimal", service)
        self.assertIn("values.Any(LooksLikeSpreadsheetFormula)", service)
        self.assertIn("if (TryParseDecimal(text, out decimal value))", service)


    def test_v211_timing_migration_is_manifested_and_preflighted(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260905_001")
        migration = ROOT / "Database" / entry["file"]
        self.assertEqual(hashlib.sha256(migration.read_bytes()).hexdigest(), entry["sha256"])
        sql = migration.read_text(encoding="utf-8-sig")
        self.assertIn("PRM_SpecificationTests ADD MinimumElapsedHours", sql)
        self.assertIn("PRM_SampleTests ADD MinimumElapsedHours", sql)
        self.assertIn("CultureMediaQualificationRequirements ADD MinimumIncubationHours", sql)
        self.assertIn("MediaQualifications ADD QualificationStartedAt", sql)
        self.assertIn("MediaQualifications ADD MinimumIncubationHoursSnapshot", sql)
        self.assertIn("MediaQualifications ADD IncubationCompletedAt", sql)
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        for marker in (
            "PRM_SpecificationTests',N'MinimumElapsedHours",
            "PRM_SampleTests',N'MinimumElapsedHours",
            "CultureMediaQualificationRequirements',N'MinimumIncubationHours",
            "MediaQualifications',N'QualificationStartedAt",
            "CK_PRM_SpecificationTests_MinimumElapsedHours_20260905",
            "CK_MediaQualifications_MinHoursSnapshot_20260905",
        ):
            self.assertIn(marker, preflight)

    def test_v211_prm_results_are_database_time_gated_by_frozen_specification_timing(self):
        samples = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        repo = (ROOT / "Repositories/PrmSpecificationRepository.cs").read_text(encoding="utf-8-sig")
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        readiness = (ROOT / "Infrastructure/PrmSchemaReadinessService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("MinimumElapsedHours", samples)
        self.assertIn("configured.MinimumElapsedHours", repo)
        self.assertIn("source.MinimumElapsedHours", repo)
        self.assertIn("EnsurePrmResultTimingGateInTransaction", results)
        self.assertIn("EnsureAllPrmResultTimingGatesElapsedInTransaction", results)
        self.assertIn("SYSDATETIME()", results)
        self.assertIn("AppConfig.AllowEarlyMicrobiologyResults", results)
        self.assertIn("Development PRM Timing Override", results)
        self.assertIn("PRM_SampleTests',N'MinimumElapsedHours", readiness)

    def test_v211_production_em_requires_controlled_planning_provenance(self):
        registration = (ROOT / "NewSampleDialog.xaml.cs").read_text(encoding="utf-8-sig")
        results = read_source_family("EMResultsEntry.xaml.cs")
        self.assertIn("if (AppConfig.IsProduction)", registration)
        self.assertIn("Direct EM event registration is disabled in Production", registration)
        self.assertIn("EnsureControlledEmPlanningProvenance", results)
        self.assertIn("if (!AppConfig.IsProduction || currentPlanId > 0)", results)
        self.assertIn('EnsureControlledEmPlanningProvenance("Result Entry")', results)
        self.assertIn('EnsureControlledEmPlanningProvenance("Submit for Review")', results)
        self.assertIn('EnsureControlledEmPlanningProvenance("Review")', results)
        self.assertIn('EnsureControlledEmPlanningProvenance("Approval")', results)

    def test_v211_culture_media_qualification_has_controlled_incubation_gate(self):
        xaml = (ROOT / "CultureMediaPreparation.xaml").read_text(encoding="utf-8-sig")
        code = read_source_family("CultureMediaPreparation.xaml.cs")
        culture_repo = (ROOT / "Repositories/CultureMediaRepository.cs").read_text(encoding="utf-8-sig")
        self.assertIn('Content="Start Qualification"', xaml)
        self.assertIn('Click="BtnStartQualification_Click"', xaml)
        self.assertIn("QualificationStartedAt", code)
        self.assertIn("MinimumIncubationHoursSnapshot", code)
        self.assertIn("IncubationCompletedAt", code)
        self.assertIn("r.ApprovalStatus = N'Approved'", code + culture_repo)
        self.assertIn("GetControlledQualificationMinimumIncubationHours", code)
        self.assertIn("EnsureQualificationIncubationElapsedInTransaction", code)
        self.assertIn("SYSDATETIME()", code)
        self.assertIn("AppConfig.AllowEarlyMicrobiologyResults", code)
        self.assertIn("Development Culture Media Timing Override", code)
        self.assertIn("No active QA-approved Culture Media qualification requirements", code)


    def test_v212_prm_analysis_start_is_authoritative_and_not_backdateable(self):
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        xaml = (ROOT / "ProductionRawMaterialResults.xaml").read_text(encoding="utf-8-sig")
        self.assertNotIn("TryPromptForPrmAnalysisStart", results)
        self.assertIn('ExecuteScalarInTransaction(conn, tx, "SELECT SYSDATETIME();")', results)
        self.assertIn("analysisStartedAt = databaseNow", results)
        self.assertIn("A user-selectable/backdated Analysis Start is intentionally not supported", results)
        self.assertIn('x:Name="BtnTimingReconciliation"', xaml)
        self.assertIn("BtnTimingReconciliation_Click", results)
        self.assertIn("EnsurePrmTimingResultEntryAllowedInTransaction", results)
        self.assertIn("QA Timing Reconciliation has already locked the re-entered result evidence", results)
        self.assertIn("historicalTimingClosed", results)

    def test_v212_prm_legacy_timing_evidence_is_reconciled_fail_closed(self):
        migration = (ROOT / "Database/Migrations/20260906_001_GMP_Timing_Governance_Hardening.sql").read_text(encoding="utf-8-sig")
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        self.assertIn("PRM_TimingMigrationHistory", migration)
        self.assertIn("PRM_TimingMigrationTestEvidence", migration)
        self.assertIn("TR_PRM_TimingMigrationTestEvidence_AppendOnly_20260906", migration)
        self.assertIn("HasControlledQualityEventEvidence", migration)
        self.assertIn("qear.SampleTestID IN", migration)
        self.assertIn("Historical Closed - Quality Event Evidence", migration)
        self.assertIn("QualityEventAffectedResults", migration)
        self.assertIn("SET SampleStatus=N'In Progress'", migration)
        self.assertIn("ResultValue=NULL", migration)
        self.assertIn("TimingReconciliationStatus", migration)
        self.assertIn("t.EnteredDate < DATEADD", migration)
        self.assertIn("AnalysisStartSignatureAt", migration)
        self.assertIn("AnalysisStartProvenanceIssue", migration)
        self.assertIn("DATEADD(MINUTE,-5,signatureEvidence.SignedAt)", migration)
        self.assertIn("ORDER BY es.SignedAt DESC, es.SignatureID DESC", migration)
        self.assertIn("A user-selectable/backdated Analysis Start is intentionally not supported", results)
        self.assertIn("TimingReconciliationStatus=N'Required'", migration)
        self.assertIn("TimingReconciliationStatus=N'Reconciled'", results)
        self.assertIn("EnsurePrmTimingReconciliationClearedInTransaction", results)
        self.assertIn("t.EnteredDate < DATEADD", results)
        self.assertIn('"Certificate / Report Issuance"', results)
        self.assertIn("allowHistoricalClosedForControlledLegacyReissue: controlledHistoricalLegacyReissue", results)
        self.assertIn("AnalysisStartedDate=CASE WHEN h.AnalysisStartProvenanceIssue IS NOT NULL THEN NULL", migration)
        self.assertIn("Controlled Restart Required", migration)
        self.assertIn("evidence.HasEnteredResult=0", migration)

    def test_v212_prm_timing_master_requires_controlled_reapproval(self):
        migration = (ROOT / "Database/Migrations/20260906_001_GMP_Timing_Governance_Hardening.sql").read_text(encoding="utf-8-sig")
        master = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("PRM_SpecificationTimingReapprovalHistory", migration)
        self.assertIn("PRM_TimingGovernanceMigrationState", migration)
        self.assertIn("InitialSpecificationTimingReapprovalCaptured", migration)
        self.assertIn("TR_PRM_TimingGovernanceMigrationState_AppendOnly_20260906", migration)
        self.assertIn("SET ApprovalStatus=N'Draft'", migration)
        self.assertIn("ReviewedBy=NULL", migration)
        self.assertIn("ApprovedBy=NULL", migration)
        self.assertIn("MinimumElapsedHours IS NOT NULL AND MinimumElapsedHours > 0", master)
        self.assertIn("Every required test must contain an approved Minimum Elapsed Hours", master)

    def test_v212_culture_media_timing_is_qa_confirmed_and_frozen_per_requirement(self):
        migration = (ROOT / "Database/Migrations/20260906_001_GMP_Timing_Governance_Hardening.sql").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "CultureMediaPreparation.xaml").read_text(encoding="utf-8-sig")
        code = read_source_family("CultureMediaPreparation.xaml.cs")
        self.assertIn("MediaQualificationRequirementSnapshots", migration)
        self.assertIn("TimingConfirmedMinimumIncubationHours", migration)
        self.assertIn('Content="QA Confirm Timing"', xaml)
        self.assertIn("BtnConfirmQualificationTiming_Click", code)
        self.assertIn("CreateQualificationRequirementSnapshotsInTransaction", code)
        self.assertIn("requirementSnapshotCount", code)
        self.assertIn("requirementSnapshotMaximum", code)
        self.assertIn("EnsureQualificationTestTimingEvidenceInTransaction", code)
        self.assertIn("test.CreatedDate < DATEADD(MINUTE", code)
        self.assertIn("a required frozen test has no result row", code)
        self.assertIn("The Culture Media qualification does not contain a complete immutable requirement timing snapshot", code)

    def test_v212_culture_media_final_release_date_uses_database_release_date(self):
        code = read_source_family("CultureMediaPreparation.xaml.cs")
        xaml = (ROOT / "CultureMediaPreparation.xaml").read_text(encoding="utf-8-sig")
        release = code.split("private void BtnFinalizeLotRelease_Click", 1)[1].split("private void BtnPrintReleaseReport_Click", 1)[0]
        receipt_save = code.split("private void SaveReceiptSopFieldsInTransaction", 1)[1].split("private void LoadReceiptSopFields", 1)[0]
        self.assertIn("SELECT ReleaseDate", release)
        self.assertIn("databaseReleaseDate", release)
        self.assertNotIn('DateTime.Today.ToString("yyyy-MM-dd"', release)
        self.assertIn('x:Name="DpReceiptLotReleaseDate" IsEnabled="False"', xaml)
        self.assertNotIn('"DateOfReleaseOfLot"', receipt_save)
        self.assertNotIn("UpdateMediaLotStatusFromReleaseInTransaction", code)
        self.assertNotIn("UpdateMediaLotStatusFromRelease(", code)

    def test_v213_culture_media_final_release_is_expiry_gated_by_sql_server_and_database_trigger(self):
        xaml = (ROOT / "CultureMediaPreparation.xaml").read_text(encoding="utf-8-sig")
        code = read_source_family("CultureMediaPreparation.xaml.cs")
        migration = (ROOT / "Database/Migrations/20260906_002_GMP_Final_Release_And_Preflight_Hardening.sql").read_text(encoding="utf-8-sig")
        release = code.split("private void BtnFinalizeLotRelease_Click", 1)[1].split("private bool HasActiveQualificationForLot", 1)[0]
        completion = code.split("private void BtnSaveRelease_Click", 1)[1].split("private void BtnReviewQualification_Click", 1)[0]
        self.assertIn('x:Name="DpReleaseDate" IsEnabled="False"', xaml)
        self.assertIn("CultureMediaLots WITH (UPDLOCK, HOLDLOCK)", release)
        self.assertIn("lockedLotExpiryDate.Value.Date < lockedDatabaseNow.Date", release)
        self.assertIn("ExpiryDate >= CAST(SYSDATETIME() AS date)", release)
        self.assertIn("QualificationDate = CAST(SYSDATETIME() AS date)", completion)
        self.assertNotIn("@QualificationDate", completion)
        self.assertIn("TR_CultureMediaLots_FinalReleaseExpiryGate_20260906", migration)
        self.assertIn("CONVERT(date,i.ExpiryDate)<CONVERT(date,SYSDATETIME())", migration)

    def test_v213_production_workflows_require_full_gmp_preflight(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        readiness = main.split("private async Task RefreshSystemReadinessAsync", 1)[1].split("private void ApplyRuntimeReadinessGate", 1)[0]
        # v224 closes the Development-mode fail-open: the green readiness state
        # and workflow gate are both derived from the full GMP preflight only.
        self.assertIn("SystemPreflightReport report = await service.RunAsync();", readiness)
        self.assertNotIn("RunOperationalReadinessAsync", readiness)
        self.assertNotIn("operationalOnly", readiness)
        self.assertIn("TR_CultureMediaLots_FinalReleaseExpiryGate_20260906", preflight)
        self.assertIn("TR_PRM_TimingQELegacyLinkCorrections_AppendOnly_20260906", preflight)
        self.assertIn("_runtimePreflightChecked &&\n                                 _runtimePreflightReady", main)

    def test_v213_prm_legacy_qe_false_positive_correction_is_source_scoped_and_append_only(self):
        migration = (ROOT / "Database/Migrations/20260906_002_GMP_Final_Release_And_Preflight_Hardening.sql").read_text(encoding="utf-8-sig")
        self.assertIn("PRM_TimingQELegacyLinkCorrections", migration)
        self.assertIn("UPPER(LTRIM(RTRIM(ISNULL(qear.SourceModule,N''))))=N'PRM'", migration)
        self.assertIn("CREATE TABLE #ProvenNonPrmLinks", migration)
        self.assertIn("UPPER(LTRIM(RTRIM(qear.SourceModule))) NOT IN (N'PRM',N'LEGACY-DUPLICATE')", migration)
        self.assertIn("INNER JOIN dbo.SampleTests generalTest ON generalTest.SampleTestID=qear.SampleTestID", migration)
        self.assertIn("qe.SampleID=generalSample.SampleID", migration)
        self.assertIn("Ambiguous rows remain fail-closed", migration)
        self.assertIn("matching SampleTestID-only affected row is still ambiguous", migration)
        self.assertIn("PRM_QualityEventLinkReconciliation legacyLink", migration)
        self.assertIn("OriginalSourceModule", migration)
        self.assertIn("TR_PRM_TimingQELegacyLinkCorrections_AppendOnly_20260906", migration)
        self.assertIn("HasIssuedCertificate=0", migration)
        self.assertIn("TimingReconciliationStatus=CASE WHEN EXISTS", migration)
        self.assertIn("ResultValue=NULL", migration)
        # Published v212 migration remains checksum-controlled and is corrected by v213 rather than rewritten.
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        v212 = next(item for item in manifest["migrations"] if item["versionKey"] == "20260906_001")
        v212_path = ROOT / "Database" / v212["file"]
        self.assertEqual(v212["sha256"], hashlib.sha256(v212_path.read_bytes()).hexdigest())

    def test_v213_retired_legacy_reconciliation_table_is_optional_on_fresh_install(self):
        migration = (ROOT / "Database/Migrations/20260906_002_GMP_Final_Release_And_Preflight_Hardening.sql").read_text(encoding="utf-8-sig")
        self.assertIn("CREATE TABLE #LegacyPrmLinks", migration)
        self.assertIn("IF OBJECT_ID(N'dbo.PRM_QualityEventLinkReconciliation', N'U') IS NOT NULL", migration)
        self.assertIn("EXEC(N'\nINSERT #LegacyPrmLinks", migration)
        required_block = migration.split("Required PRM timing/Quality Event governance tables are missing.", 1)[0]
        self.assertNotIn("OR OBJECT_ID(N'dbo.PRM_QualityEventLinkReconciliation', N'U') IS NULL", required_block)

    def test_v213_migration_is_manifested_preflighted_and_database_integration_rehearses_expiry(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260906_002")
        migration_path = ROOT / "Database" / entry["file"]
        self.assertEqual(hashlib.sha256(migration_path.read_bytes()).hexdigest(), entry["sha256"])
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn('"20260906_002" =>', migrator)
        self.assertIn("PRM_TimingQELegacyLinkCorrections", preflight)
        self.assertIn("VerifyV213FinalReleaseHardeningAsync", integration)
        self.assertIn("ex.Number == 54122", integration)

    def test_v212_preflight_and_database_integration_cover_timing_governance(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn('"20260906_001" =>', migrator)
        self.assertIn("TR_PRM_TimingMigrationTestEvidence_AppendOnly_20260906", migrator)
        self.assertIn("AnalysisStartProvenanceIssue", migrator)
        self.assertIn("VerifyV212TimingGovernanceUpgradeAsync", integration)
        for marker in (
            "PRM_TimingMigrationHistory",
            "PRM_TimingMigrationTestEvidence",
            "PRM_SpecificationTimingReapprovalHistory",
            "PRM_TimingGovernanceMigrationState",
            "MediaQualificationRequirementSnapshots",
            "TimingReconciliationStatus",
            "TimingConfirmedMinimumIncubationHours",
            "CK_PRM_Samples_TimingReconciliation_20260906",
            "CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906",
            "TR_PRM_TimingGovernanceMigrationState_AppendOnly_20260906",
            "AnalysisStartProvenanceIssue",
        ):
            self.assertIn(marker, preflight)
            self.assertIn(marker, integration)


    def test_v214_em_area_snapshot_repair_is_manifested_and_checksum_controlled(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260906_003")
        migration_path = ROOT / "Database" / entry["file"]
        migration = migration_path.read_text(encoding="utf-8-sig")
        self.assertEqual(hashlib.sha256(migration_path.read_bytes()).hexdigest(), entry["sha256"])
        for marker in (
            "AreaCodeSnapshot", "AreaNameSnapshot", "GradeSnapshot", "AreaSnapshotSource",
            "TRG_EM_Events_ProtectAreaSnapshot_20260830",
            "Legacy current-master reconciliation - verify historical identity",
        ):
            self.assertIn(marker, migration)

    def test_v214_development_operational_preflight_blocks_missing_em_trend_schema(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        operational = preflight.split("public async Task<SystemPreflightReport> RunOperationalReadinessAsync", 1)[1].split(
            "public async Task<SystemPreflightReport> RunAsync", 1
        )[0]
        self.assertIn("HasEmTrendSnapshotColumns", operational)
        self.assertIn("HasEmTrendSnapshotGuard", operational)
        self.assertIn("AreaCodeSnapshot", operational)
        self.assertIn("AreaNameSnapshot", operational)
        self.assertIn("TRG_EM_Events_ProtectAreaSnapshot_20260830", operational)

    def test_v214_em_trend_has_local_schema_guard_before_snapshot_query(self):
        trend = (ROOT / "EMTrendReport.xaml.cs").read_text(encoding="utf-8-sig")
        load = trend.split("private async Task LoadTrendAsync", 1)[1].split("private static async Task EnsureTrendSnapshotSchemaReadyAsync", 1)[0]
        self.assertIn("await EnsureTrendSnapshotSchemaReadyAsync();", load)
        guard = trend.split("private static async Task EnsureTrendSnapshotSchemaReadyAsync", 1)[1].split(
            "private static DataTable SliceDetails", 1
        )[0]
        self.assertIn("COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot')", guard)
        self.assertIn("COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot')", guard)
        self.assertIn("run Database Maintenance", guard)

    def test_v214_migrator_can_repair_recorded_em_snapshot_schema_drift(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        case = migrator.split('"20260906_003" => @"', 1)[1].split('_ => "SELECT 1;"', 1)[0]
        self.assertIn("AreaCodeSnapshot", case)
        self.assertIn("AreaNameSnapshot", case)
        self.assertIn("TRG_EM_Events_ProtectAreaSnapshot_20260830", case)

    def test_v215_em_snapshot_migration_prepares_columns_before_static_batch_compile(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        compatibility = migrator.split('if (versionKey.Equals("20260906_003", StringComparison.Ordinal))', 1)[1].split(
            'if (versionKey.Equals("20260722_005", StringComparison.Ordinal))', 1
        )[0]
        for marker in (
            "AreaCodeSnapshot", "AreaNameSnapshot", "GradeSnapshot", "AreaSnapshotSource",
            "EXEC(N'ALTER TABLE dbo.EM_Events ADD",
            "Existing dbo.EM_Events.AreaCodeSnapshot has an incompatible schema",
        ):
            self.assertIn(marker, compatibility)
        entry_method = migrator.split("private async Task ApplyManifestMigrationEntryAsync", 1)[1].split(
            "private async Task ApplyResumableCurrentStateMigrationAsync", 1
        )[0]
        prepare_call = "await PrepareManifestMigrationCompatibilityAsync(versionKey, connection, transaction)"
        migration_compile = "string migrationSql = System.Text.Encoding.UTF8.GetString(migrationBytes)"
        self.assertIn(prepare_call, entry_method)
        self.assertIn(migration_compile, entry_method)
        self.assertLess(entry_method.index(prepare_call), entry_method.index(migration_compile))

    def test_v215_development_schema_drift_blocks_workflows_but_keeps_repair_controls_available(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        gate = main.split("private void ApplyRuntimeReadinessGate", 1)[1].split("private static bool IsAdministrativeRole", 1)[0]
        ensure = main.split("private bool EnsureRuntimeReadyForWorkflow", 1)[1].split("private void ShowSystemPreflightWindow", 1)[0]
        self.assertIn("_runtimePreflightChecked", gate)
        self.assertIn("_runtimePreflightReady", gate)
        self.assertNotIn("AppConfig.IsDevelopment ||", gate)
        self.assertIn("BtnSystemPreflight.IsEnabled = !_maintenanceRunning && !_preflightRunning", gate)
        self.assertIn("BtnDatabaseMaintenance.IsEnabled = canManageSystem && AppConfig.IsDevelopment", gate)
        self.assertNotIn("if (AppConfig.IsDevelopment)", ensure)
        self.assertIn("is blocked until System Preflight passes", ensure)



    def test_v216_failed_em_repair_is_retired_in_favor_of_compile_safe_replacement(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        retired = next(item for item in manifest["migrations"] if item["versionKey"] == "20260906_003")
        replacement = next(item for item in manifest["migrations"] if item["versionKey"] == "20260906_004")
        self.assertEqual("20260906_004", retired.get("supersededBy"))
        retired_path = ROOT / "Database" / retired["file"]
        replacement_path = ROOT / "Database" / replacement["file"]
        self.assertEqual("f84f0f785208ae0c6a447838308fbfb9e4ba6ed88c33d26a732cf78b88d01352", hashlib.sha256(retired_path.read_bytes()).hexdigest())
        self.assertEqual(replacement["sha256"], hashlib.sha256(replacement_path.read_bytes()).hexdigest())

        migration = replacement_path.read_text(encoding="utf-8-sig")
        for marker in (
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ADD AreaCodeSnapshot",
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ADD AreaNameSnapshot",
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ADD GradeSnapshot",
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ADD AreaSnapshotSource",
            "EXEC sys.sp_executesql N'\nUPDATE eventRecord",
            "CREATE OR ALTER TRIGGER dbo.TRG_EM_Events_ProtectAreaSnapshot_20260830",
            "ENABLE TRIGGER dbo.TRG_EM_Events_ProtectAreaSnapshot_20260830",
        ):
            self.assertIn(marker, migration)

    def test_v216_migrator_and_database_integration_cover_compile_safe_em_repair(self):
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn('"20260906_004" => @"', migrator)
        self.assertIn("VerifyV216EmAreaSnapshotCompileSafeRepairAsync", integration)
        self.assertIn('FindMigration(manifestRoot, "20260906_004")', integration)
        self.assertIn("v216 compile-safe EM area snapshot repair rehearsal PASS", integration)
        self.assertIn("ex.Number == 54231", integration)


    def test_v217_running_binary_rejects_stale_database_payload_before_sql_execution(self):
        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertIn('<Content Include="Database\\MigrationManifest.json">', project)
        for payload in ("Database\\MigrationManifest.json", "Database\\Baseline\\*.sql", "Database\\Migrations\\*.sql"):
            block = project.split(f'<Content Include="{payload}">', 1)[1].split('</Content>', 1)[0]
            self.assertIn("<CopyToOutputDirectory>Always</CopyToOutputDirectory>", block)
            self.assertIn("<CopyToPublishDirectory>Always</CopyToPublishDirectory>", block)
        self.assertIn("ValidateInstalledMigrationPackage();", migrator)
        self.assertIn("Controlled database payload mismatch", migrator)
        self.assertIn("RetiredEmSnapshotRepairMigrationKey", migrator)
        self.assertIn("CurrentEmSnapshotRepairMigrationKey", migrator)
        self.assertIn("No migration was executed", migrator)
        apply = migrator.split("private async Task ApplyRequiredUpdatesCoreAsync(string? authorizedUsername)", 1)[1].split("public async Task ApplyControlledMigrationAsync", 1)[0]
        self.assertLess(apply.index("ValidateInstalledMigrationPackage();"), apply.index("SqlConnection.ClearAllPools();"))


    def test_v218_original_em_trend_historical_context_migration_is_retired_before_execution(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        legacy = next(item for item in manifest["migrations"] if item["versionKey"] == "20260830_003")
        replacement = next(item for item in manifest["migrations"] if item["versionKey"] == "20260906_004")
        self.assertEqual("20260906_004", legacy.get("supersededBy"))
        legacy_path = ROOT / "Database" / legacy["file"]
        replacement_path = ROOT / "Database" / replacement["file"]
        self.assertEqual("7b54c15cf7b40f1029bdcf940b2ae6cd0144be0b4e8df88ac00fc4067349e5b3", hashlib.sha256(legacy_path.read_bytes()).hexdigest())
        self.assertEqual(replacement["sha256"], hashlib.sha256(replacement_path.read_bytes()).hexdigest())
        replacement_sql = replacement_path.read_text(encoding="utf-8-sig")
        for marker in (
            "AreaCodeSnapshot", "AreaNameSnapshot", "GradeSnapshot", "AreaSnapshotSource",
            "Legacy current-master reconciliation - verify historical identity",
            "TRG_EM_Events_ProtectAreaSnapshot_20260830",
        ):
            self.assertIn(marker, replacement_sql)

        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn("RetiredEmTrendHistoricalContextMigrationKey", migrator)
        self.assertIn("both '{RetiredEmTrendHistoricalContextMigrationKey}' and '{RetiredEmSnapshotRepairMigrationKey}'", migrator)



    def test_v220_20260905_prerequisite_commits_schema_before_legacy_timing_batch(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        migrations = manifest["migrations"]
        keys = [item["versionKey"] for item in migrations]
        self.assertLess(keys.index("20260905_000"), keys.index("20260905_001"))

        prerequisite = next(item for item in migrations if item["versionKey"] == "20260905_000")
        prerequisite_path = ROOT / "Database" / prerequisite["file"]
        self.assertEqual(prerequisite["sha256"], hashlib.sha256(prerequisite_path.read_bytes()).hexdigest())
        prerequisite_sql = prerequisite_path.read_text(encoding="utf-8-sig")
        for marker in (
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_SpecificationTests ADD MinimumElapsedHours DECIMAL(9,2) NULL;'",
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.PRM_SampleTests ADD MinimumElapsedHours DECIMAL(9,2) NULL;'",
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD MinimumIncubationHours DECIMAL(9,2) NULL;'",
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.MediaQualifications ADD QualificationStartedAt DATETIME2(0) NULL;'",
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.MediaQualifications ADD MinimumIncubationHoursSnapshot DECIMAL(9,2) NULL;'",
            "EXEC sys.sp_executesql N'ALTER TABLE dbo.MediaQualifications ADD IncubationCompletedAt DATETIME2(0) NULL;'",
        ):
            self.assertIn(marker, prerequisite_sql)
        self.assertNotIn("UPDATE dbo.PRM_SpecificationTests", prerequisite_sql)
        self.assertNotIn("CHECK (MinimumElapsedHours", prerequisite_sql)

        original = (ROOT / "Database/Migrations/20260905_001_GMP_Microbiology_Timing_Gates.sql").read_bytes()
        original_entry = next(item for item in migrations if item["versionKey"] == "20260905_001")
        self.assertEqual("5602a6dd6bebbff0fa0aa2caec2cf76591ffbee8b195c7b35d12fdad35f9cec1", hashlib.sha256(original).hexdigest())
        self.assertEqual(original_entry["sha256"], hashlib.sha256(original).hexdigest())

        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn('TimingGatePrerequisiteMigrationKey = "20260905_000"', migrator)
        self.assertIn('TimingGateMigrationKey = "20260905_001"', migrator)
        self.assertIn("timingGatePrerequisiteIndex >= timingGateIndex", migrator)
        verification = migrator.split('if (versionKey.Equals("20260905_001", StringComparison.Ordinal))', 1)[1].split(
            'if (versionKey.Equals("20260906_001", StringComparison.Ordinal))', 1
        )[0]
        self.assertIn("Compile-safe prerequisite 20260905_000 is incomplete", verification)
        self.assertNotIn("ALTER TABLE dbo.PRM_SpecificationTests ADD MinimumElapsedHours", verification)

    def test_v220_20260906_prerequisite_commits_governance_schema_before_legacy_batch(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        migrations = manifest["migrations"]
        keys = [item["versionKey"] for item in migrations]
        self.assertLess(keys.index("20260906_000"), keys.index("20260906_001"))

        prerequisite = next(item for item in migrations if item["versionKey"] == "20260906_000")
        prerequisite_path = ROOT / "Database" / prerequisite["file"]
        self.assertEqual(prerequisite["sha256"], hashlib.sha256(prerequisite_path.read_bytes()).hexdigest())
        prerequisite_sql = prerequisite_path.read_text(encoding="utf-8-sig")
        for marker in (
            "PRM_Samples ADD TimingReconciliationStatus NVARCHAR(30)",
            "PRM_Samples ADD TimingReconciledBy NVARCHAR(120)",
            "PRM_Samples ADD TimingReconciledAt DATETIME2(0)",
            "PRM_Samples ADD TimingReconciliationReason NVARCHAR(1000)",
            "CultureMediaQualificationRequirements ADD TimingConfirmedMinimumIncubationHours DECIMAL(9,2)",
            "CultureMediaQualificationRequirements ADD TimingConfirmedBy NVARCHAR(100)",
            "CultureMediaQualificationRequirements ADD TimingConfirmedAt DATETIME2(0)",
        ):
            self.assertIn(marker, prerequisite_sql)
        self.assertNotIn("CK_PRM_Samples_TimingReconciliation_20260906 CHECK", prerequisite_sql)
        self.assertNotIn("PRM_TimingMigrationHistory\n    (", prerequisite_sql)

        original = (ROOT / "Database/Migrations/20260906_001_GMP_Timing_Governance_Hardening.sql").read_bytes()
        original_entry = next(item for item in migrations if item["versionKey"] == "20260906_001")
        self.assertEqual("8794c450468642a32f2c59f46c5a8140549b5165b7acea32a7eaecf1619238ac", hashlib.sha256(original).hexdigest())
        self.assertEqual(original_entry["sha256"], hashlib.sha256(original).hexdigest())

        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn('TimingGovernancePrerequisiteMigrationKey = "20260906_000"', migrator)
        self.assertIn('TimingGovernanceDispositionWidthPrerequisiteMigrationKey = "20260906_000A"', migrator)
        self.assertIn('TimingGovernanceMigrationKey = "20260906_001"', migrator)
        self.assertIn("timingGovernancePrerequisiteIndex >= timingGovernanceDispositionWidthPrerequisiteIndex", migrator)
        self.assertIn("timingGovernanceDispositionWidthPrerequisiteIndex >= timingGovernanceDispositionWidthContractIndex", migrator)
        self.assertIn("timingGovernanceDispositionWidthContractIndex >= timingGovernanceIndex", migrator)
        verification = migrator.split('if (versionKey.Equals("20260906_001", StringComparison.Ordinal))', 1)[1].split(
            '// 20260906_003 repairs EM area snapshot columns', 1
        )[0]
        self.assertIn("Compile-safe prerequisite 20260906_000 is incomplete", verification)
        self.assertNotIn("ALTER TABLE dbo.PRM_Samples ADD TimingReconciliationStatus", verification)


    def test_v221_timing_schema_contract_hardening_is_manifested_without_historical_checksum_drift(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        migrations = manifest["migrations"]
        keys = [item["versionKey"] for item in migrations]
        self.assertLess(keys.index("20260906_004"), keys.index("20260906_005"))

        historical = {
            "20260905_000": "4a3695f00343c436b586aace2189eabb4b5a7ebed2a7c0db05f33fd956941bfa",
            "20260905_001": "5602a6dd6bebbff0fa0aa2caec2cf76591ffbee8b195c7b35d12fdad35f9cec1",
            "20260906_000": "5cdcb618f1cadc9bbe808d7c064f38a6bf35156d1037edd947e566c27a89dac3",
            "20260906_001": "8794c450468642a32f2c59f46c5a8140549b5165b7acea32a7eaecf1619238ac",
        }
        for version_key, expected_hash in historical.items():
            entry = next(item for item in migrations if item["versionKey"] == version_key)
            migration_path = ROOT / "Database" / entry["file"]
            actual_hash = hashlib.sha256(migration_path.read_bytes()).hexdigest()
            self.assertEqual(expected_hash, actual_hash)
            self.assertEqual(expected_hash, entry["sha256"])

        entry = next(item for item in migrations if item["versionKey"] == "20260906_005")
        migration_path = ROOT / "Database" / entry["file"]
        migration = migration_path.read_text(encoding="utf-8-sig")
        self.assertEqual(hashlib.sha256(migration_path.read_bytes()).hexdigest(), entry["sha256"])
        for marker in (
            "DF_PRM_Samples_TimingReconciliationStatus_20260906_005",
            "DEFAULT N'Not Required' FOR TimingReconciliationStatus",
            "TimingReconciledBy must be NVARCHAR(120) NULL",
            "MinimumElapsedHours must be DECIMAL(9,2) NULL",
            "HasControlledQualityEventEvidence has a conflicting default",
        ):
            self.assertIn(marker, migration)

        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn('"20260906_005" => @"', migrator)
        self.assertIn("TimingReconciliationStatus", migrator)
        self.assertIn("NOTREQUIRED", migrator)

    def test_v221_production_runtime_configuration_and_prm_registration_fail_closed(self):
        app_config = (ROOT / "AppConfig.cs").read_text(encoding="utf-8-sig")
        self.assertIn("if (IsProduction && !File.Exists(settingsPath))", app_config)
        self.assertIn("Production runtime configuration is missing", app_config)

        project = (ROOT / "PharmaLIMS.csproj").read_text(encoding="utf-8-sig")
        self.assertIn('<None Update="appsettings.Production.json"', project)
        self.assertIn("<TargetPath>appsettings.json</TargetPath>", project)

        prm = (ROOT / "ProductionRawMaterialSamples.xaml.cs").read_text(encoding="utf-8-sig")
        registration = prm.split("INSERT INTO dbo.PRM_Samples", 1)[1].split("SELECT CAST(SCOPE_IDENTITY() AS INT);", 1)[0]
        self.assertIn("TimingReconciliationStatus", registration)
        self.assertIn("N'Not Required'", registration)

    def test_v221_async_transaction_rollback_preserves_original_exception(self):
        source = (ROOT / "Infrastructure/DatabaseConnection.cs").read_text(encoding="utf-8-sig")
        async_method = source.split(
            "public async Task ExecuteInTransactionAsync", 1
        )[1]
        self.assertIn("catch (Exception rollbackException)", async_method)
        self.assertIn("The original failure is preserved.", async_method)
        self.assertIn("ApplicationLogger.Warning(", async_method)
        self.assertIsNotNone(re.search(r"catch \(Exception\).*?catch \(Exception rollbackException\).*?throw;", async_method, flags=re.S))

    def test_v221_culture_media_signoff_uses_one_sql_server_utc_timestamp_per_action(self):
        source = read_source_family("CultureMediaPreparation.xaml.cs")
        self.assertIn('new SqlCommand("SELECT SYSUTCDATETIME();", conn, tx)', source)
        for marker in (
            "VisualCheckedAt = @CheckedAtUtc",
            "UpdatedAt = @CheckedAtUtc",
            '"CheckedAtUtc", checkedAtUtc.ToString("O", CultureInfo.InvariantCulture)',
            "SterilityReviewedAt = @ReviewedAtUtc",
            '"SterilityReviewedAtUtc", reviewedAtUtc.ToString("O", CultureInfo.InvariantCulture)',
            "ReleasedAt = @ReleasedAtUtc",
            '"ReleasedAtUtc", releasedAtUtc.ToString("O", CultureInfo.InvariantCulture)',
        ):
            self.assertIn(marker, source)
        for obsolete in (
            '"CheckedAtUtc", DateTime.UtcNow',
            '"SterilityReviewedAtUtc", DateTime.UtcNow',
            '"ReleasedAtUtc", DateTime.UtcNow',
        ):
            self.assertNotIn(obsolete, source)

    def test_v221_preflight_and_database_integration_cover_timing_contract_default_repair(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        for marker in (
            "PRM_Samples.TimingReconciliationStatus schema contract",
            "PRM_Samples.TimingReconciliationStatus default Not Required",
            "PRM_SpecificationTests.MinimumElapsedHours schema contract",
            "MediaQualifications.IncubationCompletedAt schema contract",
            "CultureMediaQualificationRequirements.TimingConfirmedAt schema contract",
        ):
            self.assertIn(marker, preflight)

        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("VerifyV221TimingSchemaContractHardeningAsync", integration)
        self.assertIn('FindMigration(manifestRoot, "20260906_005")', integration)
        self.assertIn("INSERT dbo.PRM_Samples DEFAULT VALUES", integration)
        self.assertIn("v221 timing schema contract hardening rehearsal PASS", integration)


    def test_v222_sql_2628_prm_timing_disposition_width_repair_precedes_001(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        migrations = manifest["migrations"]
        keys = [item["versionKey"] for item in migrations]
        self.assertLess(keys.index("20260906_000"), keys.index("20260906_000A"))
        self.assertLess(keys.index("20260906_000A"), keys.index("20260906_001"))

        repair_entry = next(item for item in migrations if item["versionKey"] == "20260906_000A")
        repair_path = ROOT / "Database" / repair_entry["file"]
        repair_sql = repair_path.read_text(encoding="utf-8-sig")
        self.assertEqual(hashlib.sha256(repair_path.read_bytes()).hexdigest(), repair_entry["sha256"])
        self.assertIn("ReconciliationDisposition NVARCHAR(60) NOT NULL", repair_sql)
        self.assertIn("ALTER COLUMN ReconciliationDisposition NVARCHAR(60) NOT NULL", repair_sql)
        self.assertIn("Historical Closed - Quality Event Evidence", repair_sql)
        self.assertIn("max_length<120", repair_sql)
        self.assertNotRegex(repair_sql, r"(?i)\bDELETE\s+FROM\b|\bTRUNCATE\s+TABLE\b")

        historical_path = ROOT / "Database/Migrations/20260906_001_GMP_Timing_Governance_Hardening.sql"
        self.assertEqual(
            "8794c450468642a32f2c59f46c5a8140549b5165b7acea32a7eaecf1619238ac",
            hashlib.sha256(historical_path.read_bytes()).hexdigest(),
        )

        historical_sql = historical_path.read_text(encoding="utf-8-sig")
        controlled_literal = "Historical Closed - Quality Event Evidence"
        self.assertEqual(42, len(controlled_literal))
        self.assertIn("ReconciliationDisposition NVARCHAR(40) NOT NULL", historical_sql)
        self.assertIn("THEN N'Historical Closed - Quality Event Evidence'", historical_sql)

        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn('TimingGovernanceDispositionWidthPrerequisiteMigrationKey = "20260906_000A"', migrator)
        self.assertIn('"20260906_000A" => @"', migrator)
        self.assertIn("Disposition-width prerequisite 20260906_000B is incomplete", migrator)

        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("PRM_TimingMigrationHistory.ReconciliationDisposition >= NVARCHAR(80)", preflight)

        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("VerifyV222TimingDispositionWidthRepairAsync", integration)
        self.assertIn('FindMigration(manifestRoot, "20260906_000A")', integration)
        self.assertIn("Historical Closed - Quality Event Evidence", integration)
        self.assertIn("v222 SQL 2628 timing-disposition regression rehearsal PASS", integration)
        self.assertIn("v223 SQL 2628 historical 20260906_001 Quality Event disposition PASS", integration)


    def test_v223_prm_timing_upgrade_contract_closes_2628_reportstatus_preflight_and_diagnostics_gaps(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        migrations = manifest["migrations"]
        keys = [item["versionKey"] for item in migrations]
        for key in ("20260906_000", "20260906_000A", "20260906_000B", "20260906_001",
                    "20260906_002", "20260906_002A"):
            self.assertIn(key, keys)
        self.assertLess(keys.index("20260906_000"), keys.index("20260906_000A"))
        self.assertLess(keys.index("20260906_000A"), keys.index("20260906_000B"))
        self.assertLess(keys.index("20260906_000B"), keys.index("20260906_001"))
        self.assertLess(keys.index("20260906_001"), keys.index("20260906_002"))
        self.assertLess(keys.index("20260906_002"), keys.index("20260906_002A"))

        # Historical bytes stay immutable so databases that already recorded them do
        # not encounter ledger checksum drift.
        historical = {
            "20260906_000A": "f7e09df2a7a25cef46d54f460cc658b7ffd559b146cfc124f921cd7382378b2a",
            "20260906_001": "8794c450468642a32f2c59f46c5a8140549b5165b7acea32a7eaecf1619238ac",
            "20260906_002": "77310fa63f120c33a04718e40866d573bb19b905d4de46d8d8c59841e879014e",
            "20260906_005": "413963812f94f4b6d93a09ffe86d555c2a2308c17929c5d8430406b59e2d3c45",
        }
        for version_key, expected in historical.items():
            entry = next(item for item in migrations if item["versionKey"] == version_key)
            path = ROOT / "Database" / entry["file"]
            self.assertEqual(expected, hashlib.sha256(path.read_bytes()).hexdigest())
            self.assertEqual(expected, entry["sha256"])

        width_entry = next(item for item in migrations if item["versionKey"] == "20260906_000B")
        width_path = ROOT / "Database" / width_entry["file"]
        width_sql = width_path.read_text(encoding="utf-8-sig")
        self.assertEqual(width_entry["sha256"], hashlib.sha256(width_path.read_bytes()).hexdigest())
        self.assertIn("ALTER COLUMN ReconciliationDisposition NVARCHAR(80) NOT NULL", width_sql)
        self.assertIn("max_length<160", width_sql)
        self.assertIn("at least 80 Unicode characters before 20260906_001", width_sql)
        self.assertNotRegex(width_sql, r"(?i)\bDELETE\s+FROM\b|\bTRUNCATE\s+TABLE\b")

        report_entry = next(item for item in migrations if item["versionKey"] == "20260906_002A")
        report_path = ROOT / "Database" / report_entry["file"]
        report_sql = report_path.read_text(encoding="utf-8-sig")
        self.assertEqual(report_entry["sha256"], hashlib.sha256(report_path.read_bytes()).hexdigest())
        self.assertIn("ReportStatus=N'Not Issued'", report_sql)
        self.assertIn("SampleStatus,N''))))=N'IN PROGRESS'", report_sql)
        self.assertIn("ReportStatus,N''))))=N'RESULTS ENTERED'", report_sql)
        self.assertIn("PRM_TimingQELegacyLinkCorrections", report_sql)
        self.assertIn("NOT EXISTS", report_sql)
        self.assertNotRegex(report_sql, r"(?i)\bDELETE\s+FROM\b|\bTRUNCATE\s+TABLE\b")

        historical_002 = (ROOT / "Database/Migrations/20260906_002_GMP_Final_Release_And_Preflight_Hardening.sql").read_text(encoding="utf-8-sig")
        self.assertIn("ReportStatus=N'Results Entered'", historical_002)

        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("PRM_TimingMigrationHistory.ReconciliationDisposition >= NVARCHAR(80)", preflight)
        self.assertIn("(max_length=-1 OR max_length>=160)", preflight)
        self.assertIn("PRM corrected timing sample ReportStatus contract", preflight)

        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        self.assertIn('TimingGovernanceDispositionWidthContractMigrationKey = "20260906_000B"', migrator)
        self.assertIn('PrmReportStatusContractRepairMigrationKey = "20260906_002A"', migrator)
        self.assertIn("timingGovernanceDispositionWidthPrerequisiteIndex >= timingGovernanceDispositionWidthContractIndex", migrator)
        self.assertIn("timingGovernanceDispositionWidthContractIndex >= timingGovernanceIndex", migrator)
        self.assertIn("prmFinalReleaseMigrationIndex >= prmReportStatusContractRepairIndex", migrator)
        self.assertIn("exception.Number is 102 or 156 or 207 or 208 or 245 or 515 or 547 or 8114 or 8152 or 2601 or 2627 or 2628", migrator)
        self.assertIn("Truncated value redacted.", migrator)
        self.assertIn("Regex.Match(", migrator)

        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn('FindMigration(manifestRoot, "20260906_000B")', integration)
        self.assertIn("v223 pre-001 NVARCHAR(80) timing-disposition contract PASS", integration)
        self.assertIn("Exact v223 SQL 2628 regression path", integration)
        self.assertIn("v223 SQL 2628 historical 20260906_001 Quality Event disposition PASS", integration)
        self.assertIn("VerifyV223PrmReportStatusContractRepairAsync", integration)
        self.assertIn('FindMigration(manifestRoot, "20260906_002A")', integration)
        self.assertIn("v223 PRM ReportStatus Not Issued correction rehearsal PASS", integration)

    def test_v224_water_legacy_schema_backfill_and_runtime_gate_are_fail_closed(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        migrations = manifest["migrations"]
        keys = [item["versionKey"] for item in migrations]
        self.assertIn("20260906_006", keys)
        self.assertLess(keys.index("20260906_005"), keys.index("20260906_006"))

        entry = next(item for item in migrations if item["versionKey"] == "20260906_006")
        path = ROOT / "Database" / entry["file"]
        sql = path.read_text(encoding="utf-8-sig")
        self.assertEqual(entry["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())
        for marker in (
            "CREATE TABLE dbo.WaterTestProfiles",
            "CREATE TABLE dbo.WaterTestProfileTests",
            "CREATE TABLE dbo.WaterSpecifications",
            "FK_WaterTestProfileTests_Profile",
            "FK_WaterTestProfileTests_Test",
            "FK_WaterSpecifications_Test",
            "20260906_006",
        ):
            self.assertIn(marker, sql)
        self.assertNotRegex(sql, r"(?i)\bDELETE\s+(?:FROM\s+)?dbo\.|\bTRUNCATE\s+TABLE\b")

        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("operationalOnly", main)
        self.assertNotIn("RunOperationalReadinessAsync", main)
        self.assertIn("SystemPreflightReport report = await service.RunAsync();", main)
        self.assertIn("_runtimePreflightReady = report.BlockerCount == 0;", main)

        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        for marker in (
            "HasWaterTestProfiles",
            "HasWaterTestProfileTests",
            "HasWaterSpecifications",
            "WaterTestProfiles.ProfileCode NVARCHAR(20) NOT NULL",
            "WaterTestProfileTests trusted FK to WaterTestProfiles",
            "Water controlled test profiles",
        ):
            self.assertIn(marker, preflight)

        registration = (ROOT / "NewSampleDialog.xaml.cs").read_text(encoding="utf-8-sig")
        allowed = registration.split("private HashSet<int> GetAllowedWaterTestIds()", 1)[1].split(
            "private bool IsAllowedCurrentWaterTest", 1
        )[0]
        self.assertIn("DatabaseHelper.GetActiveWaterTestIdsForProfile(profileCode)", allowed)
        self.assertNotIn("ids.Clear()", allowed)
        self.assertIn("ids.Count == 0", allowed)

        is_allowed = registration.split("private bool IsAllowedCurrentWaterTest", 1)[1].split(
            "private IEnumerable<DataRow> GetCurrentVisibleTestRows", 1
        )[0]
        self.assertNotIn("return true", is_allowed)
        self.assertIn("return allowedIds.Contains(testId);", is_allowed)

        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("VerifyV224WaterControlledMasterSchemaBackfillAsync", integration)
        self.assertIn('FindMigration(manifestRoot, "20260906_006")', integration)
        self.assertIn("v224 legacy Water controlled-master schema backfill rehearsal PASS", integration)



    def test_v225_legacy_tests_active_contract_closes_water_preflight_sql207(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        migrations = manifest["migrations"]
        keys = [item["versionKey"] for item in migrations]
        self.assertIn("20260906_007", keys)
        self.assertLess(keys.index("20260906_006"), keys.index("20260906_007"))

        entry = next(item for item in migrations if item["versionKey"] == "20260906_007")
        path = ROOT / "Database" / entry["file"]
        sql = path.read_text(encoding="utf-8-sig")
        self.assertEqual(entry["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())
        self.assertIn("COL_LENGTH(N'dbo.Tests', N'IsActive') IS NULL", sql)
        self.assertIn("ADD IsActive BIT NOT NULL", sql)
        self.assertIn("DEFAULT (1) WITH VALUES", sql)
        self.assertNotRegex(sql, r"(?i)\bDELETE\s+(?:FROM\s+)?dbo\.|\bTRUNCATE\s+TABLE\b")

        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("(N'Tests',N'IsActive')", preflight)
        self.assertIn("AND ISNULL(test.IsActive,0)=1", preflight)

        helper = read_database_helper_source()
        water_profile = helper.split("public static DataTable GetActiveWaterTestIdsForProfile", 1)[1].split(
            "public static DataTable GetEffectiveWaterTestSpecification", 1
        )[0]
        self.assertIn("AND ISNULL(t.IsActive, 0) = 1", water_profile)

        registration = (ROOT / "NewSampleDialog.xaml.cs").read_text(encoding="utf-8-sig")
        load_tests = registration.split("private void LoadTests()", 1)[1].split(
            "private void RenderTests", 1
        )[0]
        self.assertNotIn("IsWaterTest", load_tests)
        self.assertIn("FROM dbo.Tests", load_tests)

        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("VerifyV225LegacyTestsActiveContractAsync", integration)
        self.assertIn('FindMigration(manifestRoot, "20260906_007")', integration)
        self.assertIn("v225 legacy dbo.Tests.IsActive + Water preflight SQL 207 regression rehearsal PASS", integration)


    def test_v226_preflight_distinguishes_legacy_certificate_evidence_from_post_control_defects(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])

        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        prm = preflight.split("private async Task CheckPrmCertificateIntegrityAsync", 1)[1].split(
            "private async Task CheckWaterCertificateIntegrityAsync", 1
        )[0]
        water = preflight.split("private async Task CheckWaterCertificateIntegrityAsync", 1)[1].split(
            "private async Task CheckWorkflowIntegrityAsync", 1
        )[0]
        em = preflight.split("private async Task CheckHistoricalEmSnapshotIntegrityAsync", 1)[1].split(
            "private async Task CheckIdentityIntegrityAsync", 1
        )[0]

        for block in (prm, water):
            self.assertIn("VersionKey = N'20260722_003'", block)
            self.assertIn("@SnapshotCutover", block)
            self.assertIn("IssueDate >= @SnapshotCutover", block)
            self.assertIn("N'BLOCKER' ELSE N'WARNING'", block)
            self.assertIn("predates immutable-snapshot control", block)

        self.assertIn("Legacy PRM certificate evidence", prm)
        self.assertIn("do not fabricate retrospective snapshots or hashes", prm.lower())
        self.assertNotIn("INSERT dbo.PRM_CertificateSnapshots", prm)

        self.assertIn("Legacy certificate snapshot evidence", water)
        self.assertIn("do not fabricate retrospective snapshots", water.lower())
        self.assertNotIn("INSERT dbo.CertificateDocumentSnapshots", water)

        self.assertIn("Affected active event(s):", em)
        self.assertIn("EM Results Entry > Reconcile Historical Snapshot", em)
        self.assertIn("EvidenceReference", (ROOT / "EMLegacySnapshotReconciliation.xaml.cs").read_text(encoding="utf-8-sig"))

        preflight_xaml = (ROOT / "SystemPreflight.xaml").read_text(encoding="utf-8-sig")
        preflight_ui = (ROOT / "SystemPreflight.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('x:Name="BtnEmReconcile"', preflight_xaml)
        self.assertIn('Content="Reconcile Historical EM"', preflight_xaml)
        self.assertIn("BtnEmReconcile_Click", preflight_ui)
        self.assertIn("LoadHistoricalEmReconciliationCandidates", preflight_ui)
        self.assertIn("new EMLegacySnapshotReconciliation", preflight_ui)
        self.assertIn("DatabaseHelper.CanApproveResults", preflight_ui)
        self.assertIn("reconciliation.WasSaved", preflight_ui)
        self.assertNotIn("INSERT dbo.EM_LimitSnapshotReconciliations", preflight_ui)

        ai_review = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("Legacy active certificates predate the recorded immutable-snapshot control activation", ai_review)
        self.assertIn("Legacy PRM documents predate the recorded immutable-snapshot control activation", ai_review)


    def test_v227_trigger_safe_output_identity_capture_for_insert_trigger_targets(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])

        em_runtime = (ROOT / "EMLegacySnapshotReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        em_save = em_runtime.split("using SqlCommand insert = new SqlCommand(@\"", 1)[1].split(
            "insert.Parameters.Add(\"@Plate\"", 1
        )[0]
        self.assertIn("DECLARE @InsertedReconciliation TABLE(ReconciliationID INT NOT NULL)", em_save)
        self.assertIn(
            "OUTPUT INSERTED.ReconciliationID INTO @InsertedReconciliation(ReconciliationID)",
            em_save,
        )
        self.assertIn("SELECT TOP(1) ReconciliationID", em_save)
        self.assertNotRegex(
            em_save,
            r"OUTPUT\s+INSERTED\.ReconciliationID\s*(?:\r?\n|\s)+VALUES",
        )

        em_migration = (
            ROOT / "Database/Migrations/20260828_003_EM_Limit_Snapshot_Reconciliation_And_Strict_Creation.sql"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("ON dbo.EM_LimitSnapshotReconciliations", em_migration)
        self.assertRegex(em_migration, r"(?is)AFTER\s+INSERT\s*,\s*UPDATE\s*,\s*DELETE")

        culture = read_source_family("CultureMediaPreparation.xaml.cs")
        receipt = culture.split('const string insertSql = @"', 1)[1].split('";', 1)[0]
        self.assertIn("DECLARE @InsertedLot TABLE(MediaLotID INT NOT NULL)", receipt)
        self.assertIn(
            "OUTPUT INSERTED.MediaLotID INTO @InsertedLot(MediaLotID)",
            receipt,
        )
        self.assertIn("SELECT TOP(1) MediaLotID", receipt)
        self.assertNotRegex(
            receipt,
            r"OUTPUT\s+INSERTED\.MediaLotID\s*(?:\r?\n|\s)+VALUES",
        )

        culture_migration = (
            ROOT / "Database/Migrations/20260906_002_GMP_Final_Release_And_Preflight_Hardening.sql"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("ON dbo.CultureMediaLots", culture_migration)
        self.assertRegex(culture_migration, r"(?is)AFTER\s+INSERT\s*,\s*UPDATE")

        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn(
            "OUTPUT INSERTED.ReconciliationID INTO @InsertedReconciliation(ReconciliationID)",
            integration,
        )
        self.assertIn(
            "OUTPUT INSERTED.MediaLotID,INSERTED.LotNumber INTO @InsertedLot(MediaLotID,LotNumber)",
            integration,
        )


    def test_v228_historical_em_batch_reconciliation_is_atomic_scoped_and_auditable(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))

        xaml = (ROOT / "EMLegacySnapshotReconciliation.xaml").read_text(encoding="utf-8-sig")
        runtime = (ROOT / "EMLegacySnapshotReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        preflight_ui = (ROOT / "SystemPreflight.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn('SelectionMode="Extended"', xaml)
        self.assertIn('x:Name="BtnSelectMatching"', xaml)
        self.assertIn('Content="Select all matching unreconciled"', xaml)
        self.assertIn('Binding="{Binding EventNo}"', xaml)

        self.assertIn("eventId == 0", runtime)
        self.assertIn("All unresolved historical events (active and closed)", runtime)
        self.assertIn("(@Event=0 OR P.EventId=@Event)", runtime)
        self.assertIn("Select all matching unreconciled", runtime)
        self.assertIn("SameText(row.Grade, anchor.Grade)", runtime)
        self.assertIn("SameText(row.Method, anchor.Method)", runtime)
        self.assertIn("!row.LatestReconciliationID.HasValue", runtime)
        self.assertIn("SelectedRowsAreCompatible(selected)", runtime)
        self.assertIn("Batch reconciliation cannot include a plate that already has signed evidence", runtime)
        self.assertIn("Guid.NewGuid().ToString(\"N\").ToUpperInvariant()", runtime)
        self.assertIn("Batch reconciliation ID:", runtime)
        self.assertIn("foreach (LegacyPlateRow row in selected.OrderBy", runtime)
        self.assertIn("DatabaseHelper.ExecuteInTransaction((connection, transaction) =>", runtime)
        self.assertIn("WITH(UPDLOCK,HOLDLOCK)", runtime)
        self.assertIn("OUTPUT INSERTED.ReconciliationID INTO @InsertedReconciliation(ReconciliationID)", runtime)
        self.assertIn("DatabaseHelper.AddAuditTrailAdvanced(", runtime)
        self.assertIn("row.EventNo", runtime)

        # The batch helper never mutates the historical EM_EventPlates source.
        save_block = runtime.split("private void SaveSelectedReconciliations()", 1)[1].split(
            "private List<LegacyPlateRow> GetSelectedPlates()", 1
        )[0]
        self.assertNotRegex(save_block, r"(?i)\bUPDATE\s+dbo\.EM_EventPlates\b")
        self.assertNotRegex(save_block, r"(?i)\bDELETE\s+(?:FROM\s+)?dbo\.EM_LimitSnapshotReconciliations\b")

        self.assertIn('EventId = 0', preflight_ui)
        self.assertIn('EventNo = "ALL UNRESOLVED HISTORICAL EVENTS (batch mode)"', preflight_ui)
        self.assertIn("eventCandidates.Sum(candidate => candidate.UnresolvedPlateCount)", preflight_ui)
        self.assertIn("same Grade + Method", preflight_ui)


    def test_v229_em_batch_reconciliation_compile_contract(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        runtime = (ROOT / "EMLegacySnapshotReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("out decimal actionLimit", runtime)
        self.assertIn("string signatureAction = selected.Count == 1", runtime)
        self.assertIn("new ElectronicSignature(signatureScope, Login.CurrentUser, signatureAction, true)", runtime)
        self.assertIn("actionParameter.Value = actionLimit;", runtime)
        self.assertIn("Action={actionLimit.ToString(CultureInfo.InvariantCulture)}", runtime)
        self.assertNotIn("out decimal action) || action < alert", runtime)
        culture = read_source_family("CultureMediaPreparation.xaml.cs")
        self.assertIn('object? invalidValue = ExecuteScalarInTransaction(conn, tx, @"', culture)



    def test_v230_typed_sql_parameters_and_current_session_log_grouping(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))

        helper = (ROOT / "Infrastructure/SqlParameterCollectionExtensions.cs").read_text(encoding="utf-8-sig")
        self.assertIn("SqlDbType sqlDbType", helper)
        self.assertIn("int size = 0", helper)
        self.assertIn("byte precision = 0", helper)
        self.assertIn("byte scale = 0", helper)
        self.assertIn("parameter.Value = value ?? DBNull.Value;", helper)

        for relative in ("NewSampleDialog.xaml.cs", "EMPlanning.xaml.cs"):
            source = (ROOT / relative).read_text(encoding="utf-8-sig")
            self.assertNotIn(".Parameters.AddWithValue(", source, relative)
            self.assertIn(".Parameters.AddExplicit(", source, relative)

        registration = (ROOT / "NewSampleDialog.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('AddExplicit("@PointID", SqlDbType.Int', registration)
        self.assertIn('AddExplicit("@SamplingDateTime", SqlDbType.DateTime2', registration)
        self.assertIn('AddExplicit("@Alert", SqlDbType.Decimal', registration)
        self.assertIn("precision: 18, scale: 6", registration)

        planning = read_source_family("EMPlanning.xaml.cs")
        self.assertIn('AddExplicit("@Plan", SqlDbType.Int', planning)
        self.assertIn('AddExplicit("@Due", SqlDbType.Date', planning)
        self.assertIn('AddExplicit("@ActualTemp", SqlDbType.Decimal', planning)
        self.assertIn('AddExplicit("@User", SqlDbType.NVarChar', planning)

        ai_review = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('CountOccurrences(source, ".Parameters." + "AddWithValue(")', ai_review)
        self.assertIn("currentSessionErrorHeaders", ai_review)
        self.assertIn("Current-session error groups:", ai_review)
        self.assertIn("SummarizeLogHeaders", ai_review)
        self.assertIn("latest PharmaLIMS startup marker", ai_review)

        actual_runtime_occurrences = 0
        for path in ROOT.rglob("*.cs"):
            if "tests" in path.parts or "obj" in path.parts or "bin" in path.parts:
                continue
            source = path.read_text(encoding="utf-8-sig")
            actual_runtime_occurrences += source.count(".Parameters.AddWithValue(")
        self.assertEqual(0, actual_runtime_occurrences)



    def test_v232_water_controlled_profiles_are_versioned_signed_and_fail_closed(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260907_000")
        migration_path = ROOT / "Database" / entry["file"]
        self.assertTrue(migration_path.exists())
        self.assertEqual(entry["sha256"], hashlib.sha256(migration_path.read_bytes()).hexdigest())

        migration = migration_path.read_text(encoding="utf-8-sig")
        self.assertIn("ControlledReference NVARCHAR(300)", migration)
        self.assertIn("ADD ProfileID INT NULL", migration)
        self.assertIn("CREATE TABLE dbo.WaterTestProfileSignatures", migration)
        self.assertIn("TRG_WaterTestProfileSignatures_AppendOnly", migration)
        self.assertIn("AFTER UPDATE, DELETE", migration)
        self.assertIn("UX_WaterTestProfiles_OneActiveCode_20260907_000", migration)
        self.assertNotRegex(migration, r"(?i)\bINSERT\s+(?:INTO\s+)?dbo\.Tests\b")
        self.assertNotRegex(migration, r"(?i)\bINSERT\s+(?:INTO\s+)?dbo\.WaterTestProfiles\b")

        xaml = (ROOT / "WaterTestProfileManagement.xaml").read_text(encoding="utf-8-sig")
        manager = (ROOT / "WaterTestProfileManagement.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn('x:Class="PharmaLIMS.WaterTestProfileManagement"', xaml)
        self.assertIn('Content="New Draft"', xaml)
        self.assertIn('Content="Review"', xaml)
        self.assertIn('Content="Approve &amp; Activate"', xaml)
        self.assertIn("source-backed helpers", xaml)
        self.assertIn("Only the Draft creator may edit this controlled profile", manager)
        self.assertIn("The Draft author cannot perform the independent Review", manager)
        self.assertIn("Approval must be independent of both the Draft author and reviewer", manager)
        self.assertIn("Water Test Profile Review", manager)
        self.assertIn("Water Test Profile Approval", manager)
        self.assertIn("InsertProfileSignature(", manager)
        self.assertIn("ControlledReference", manager)
        self.assertIn("Specification Text is required", manager)
        self.assertNotIn("USP", migration)
        self.assertNotIn("Ph. Eur.", migration)

        database = read_database_helper_source()
        self.assertIn("WaterTestProfileSignatures", database)
        self.assertIn("No currently effective, approved controlled tests/specifications", database)
        self.assertIn("specification.ProfileID = wp.ProfileID", database)
        self.assertIn("(specification.PointCode IS NULL OR LTRIM(RTRIM(specification.PointCode))=N'')", database)
        self.assertIn("profile.ApprovalStatus = N'Approved'", database)

        registration = (ROOT / "NewSampleDialog.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("BtnManageWaterProfiles_Click", registration)
        self.assertIn("GetEffectiveWaterTestSpecification(sampleType, testId, pointCode)", registration)
        self.assertIn("@LimitDescription", registration)
        self.assertIn("snapshot.Specification", registration)
        self.assertIn("decimal? alert = null;", registration)
        self.assertIn("decimal? action = null;", registration)

        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("WaterTestProfileSignatures", preflight)
        self.assertIn("Water controlled test profiles", preflight)
        self.assertIn("PW and PTW each have one currently effective, approved controlled profile", preflight)
        self.assertIn("TRG_WaterTestProfileSignatures_AppendOnly", preflight)

        preflight_ui = (ROOT / "SystemPreflight.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("BtnWaterProfiles", preflight_ui)
        self.assertIn("new WaterTestProfileManagement", preflight_ui)

        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn("dbo.WaterTestProfileSignatures", integration)
        self.assertIn("WaterTestProfiles.ControlledReference", integration)
        self.assertIn("TRG_WaterTestProfileSignatures_AppendOnly", integration)
        self.assertIn("UX_WaterTestProfiles_OneActiveCode_20260907_000", integration)



    def test_v234_water_profiles_reuse_central_review_approval_and_dev_admin_override(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))

        manager = (ROOT / "WaterTestProfileManagement.xaml.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "WaterTestProfileManagement.xaml").read_text(encoding="utf-8-sig")
        app_config = (ROOT / "AppConfig.cs").read_text(encoding="utf-8-sig")

        # Reuse the existing central permission model rather than a Water-only role model.
        self.assertIn("DatabaseHelper.CanManageSettings(currentUser)", manager)
        self.assertIn("DatabaseHelper.CanReviewResults(currentUser)", manager)
        self.assertIn("DatabaseHelper.CanApproveResults(currentUser)", manager)
        self.assertIn('"CanManageSettings"', manager)
        self.assertIn('"CanReviewResults"', manager)
        self.assertIn('"CanApproveResults"', manager)
        self.assertIn("DatabaseHelper.EnsureUserPermissionInTransaction(", manager)

        # Normal segregation: author != reviewer; approver != author/reviewer.
        self.assertIn("The Draft author cannot perform the independent Review", manager)
        self.assertIn("Approval must be independent of both the Draft author and reviewer", manager)
        self.assertIn("currentUser.Equals(_createdBy", manager)
        self.assertIn("currentUser.Equals(_reviewedBy", manager)

        # The only same-user bypass is the existing Development Admin control.
        self.assertIn("IsDevelopmentAdminWorkflowOverrideAllowed()", manager)
        self.assertIn("IsDevelopmentAdminWorkflowOverrideAllowed(signerRole)", manager)
        self.assertIn("AppConfig.DevelopmentAdminFullPermissions", manager)
        self.assertIn('role.Equals("Admin"', manager)
        self.assertIn('role.Equals("Administrator"', manager)
        self.assertIn("@AllowSameUser=1", manager)
        self.assertIn("Development Admin override is active", manager)

        # The config property itself is compile-time Development gated.
        self.assertRegex(
            app_config,
            r"public static bool DevelopmentAdminFullPermissions\s*=>\s*"
            r"\r?\n?\s*IsDevelopment && Settings\.Value\.DevelopmentAdminFullPermissions;",
        )
        self.assertIn(
            "Runtime:DevelopmentAdminFullPermissions must be false in the Production environment.",
            app_config,
        )

        self.assertIn("Authoring uses Settings permission", xaml)
        self.assertIn("Review uses Review permission", xaml)
        self.assertIn("Approval uses Approval permission", xaml)
        self.assertIn("configured Admin override permitted only in Development", xaml)

        # v233 is application-only: the v232 Water migration remains unchanged.
        migration = ROOT / "Database/Migrations/20260907_000_Water_Controlled_Profile_Workflow.sql"
        self.assertTrue(migration.exists())
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260907_000")
        self.assertEqual(entry["sha256"], hashlib.sha256(migration.read_bytes()).hexdigest())



    def test_v236_mqc_g_0018_water_profiles_are_source_backed_and_keep_controlled_transitions(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))

        manager = (ROOT / "WaterTestProfileManagement.xaml.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "WaterTestProfileManagement.xaml").read_text(encoding="utf-8-sig")
        results = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        report = (ROOT / "ReportCertificate.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn('Content="Load MQC-G-0018"', xaml)
        self.assertIn('Click="BtnLoadDevTemplate_Click"', xaml)
        self.assertIn("ApplyMqcG0018ControlledProfile(code);", manager)
        self.assertIn('MqcWaterSopReference = "MQC-G-0018 v2.0"', manager)

        helper_start = manager.index("private void BtnLoadDevTemplate_Click")
        helper_end = manager.index("private void BtnLoadFullDevInventory_Click", helper_start)
        helper = manager[helper_start:helper_end]
        self.assertNotIn("ExecuteInTransaction", helper)
        self.assertNotIn("SaveDraftInternal", helper)
        self.assertNotIn("InsertProfileSignature", helper)

        # Explicit site-SOP limits are source-backed rather than generic development guesses.
        self.assertIn("Purified Water pH 5.0–7.0", manager)
        self.assertIn("Potable Water pH 6.5–8.5", manager)
        self.assertIn("Purified Water conductivity ≤1.3 µS/cm", manager)
        self.assertIn("Potable Water conductivity ≤500 µS/cm", manager)
        self.assertIn("Purified Water TDS ≤1 mg/L", manager)
        self.assertIn("Potable Water TDS ≤500 mg/L", manager)
        self.assertIn("Purified Water TOC ≤500 ppb; Alert 300 ppb; Action 500 ppb", manager)
        self.assertIn("Purified Water TAMC NMT 100 CFU/mL", manager)
        self.assertIn("Potable Water TAMC NMT 500 CFU/mL", manager)
        self.assertIn("Escherichia coli absent per 100 mL", manager)
        self.assertIn("Burkholderia cepacia complex must be absent when the test is applicable", manager)
        self.assertIn("BET is applicable to Purified Water as required", manager)
        self.assertIn("Record Complies/Does Not Comply", manager)
        self.assertIn("Residue on evaporation NMT 1 mg from 100 mL", manager)

        # Alert/Action microbial trend limits remain governed by the separate site SOP.
        self.assertIn("MQC-G-0011", manager)
        self.assertNotIn("Operational Purified Water microbial action level", manager)

        # Generic qualitative compendial-style chemistry can be entered/reported without
        # storing text in the controlled DECIMAL ResultValue column.
        self.assertIn("IsComplianceQualitativeTest", results)
        self.assertIn('"Complies"', results)
        self.assertIn('"Does Not Comply"', results)
        self.assertIn("IsComplianceQualitativeTest", report)
        self.assertIn('"Complies"', report)

        # Legacy v235 placeholders remain fail-closed if such a draft still exists.
        self.assertIn(
            'FullDevInventoryPlaceholderPrefix = "DEVELOPMENT FULL TEST INVENTORY PLACEHOLDER"',
            manager,
        )
        self.assertIn("Review/Approval is blocked because", manager)
        self.assertGreaterEqual(
            manager.count("NOT LIKE N'DEVELOPMENT FULL TEST INVENTORY PLACEHOLDER%'"),
            2,
        )


    def test_v236_water_test_catalog_and_optional_ctg_11_02_ptw_supplement_are_controlled(self):
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))

        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260908_000")
        migration_path = ROOT / "Database" / entry["file"]
        self.assertTrue(migration_path.exists())
        self.assertEqual(entry["sha256"], hashlib.sha256(migration_path.read_bytes()).hexdigest())

        migration = migration_path.read_text(encoding="utf-8-sig")
        manager = (ROOT / "WaterTestProfileManagement.xaml.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "WaterTestProfileManagement.xaml").read_text(encoding="utf-8-sig")

        # Catalog migration is additive only: it can represent source-defined tests
        # but does not create, approve, or activate an operational profile.
        self.assertIn("WTR-TAMC", migration)
        self.assertIn("WTR-BCC", migration)
        self.assertIn("WTR-BET", migration)
        self.assertIn("WTR-RES-EVAP", migration)
        self.assertIn("PTW-AS", migration)
        self.assertIn("PTW-RAD-AB", migration)
        self.assertNotRegex(migration, r"(?i)\bINSERT\s+(?:INTO\s+)?dbo\.WaterTestProfiles\b")
        self.assertNotRegex(migration, r"(?i)\bINSERT\s+(?:INTO\s+)?dbo\.WaterSpecifications\b")
        self.assertNotIn("ApprovalStatus=N'Approved'", migration)

        self.assertIn('Content="Add CTG-11-02 PTW"', xaml)
        self.assertIn('Click="BtnLoadFullDevInventory_Click"', xaml)
        self.assertIn("ApplyCtg1102PotableSupplement();", manager)
        self.assertIn('CtgWaterGuidelineReference = "CTG-11-02 v02"', manager)
        self.assertIn("Arsenic maximum contaminant level 0.05 ppm", manager)
        self.assertIn("Nitrates (as N) maximum contaminant level 10 ppm", manager)
        self.assertIn("Nitrites (as N) maximum contaminant level 1 ppm", manager)
        self.assertIn("Turbidity maximum contaminant level 1 NTU", manager)
        self.assertIn("Gross alpha and gross beta activity total maximum 10 pCi/L", manager)
        self.assertIn("Ra-226 and Ra-228 total maximum 5 pCi/L", manager)
        self.assertIn("Total coliforms <2", manager)

        helper_start = manager.index("private void BtnLoadFullDevInventory_Click")
        helper_end = manager.index("private void ApplyMqcG0018ControlledProfile", helper_start)
        helper = manager[helper_start:helper_end]
        self.assertNotIn("ExecuteInTransaction", helper)
        self.assertNotIn("SaveDraftInternal", helper)
        self.assertNotIn("InsertProfileSignature", helper)
        self.assertIn("only to the PTW Draft", helper)

        # Business validation messages are shown directly, rather than hiding
        # controlled authoring errors behind an opaque safe-message reference.
        self.assertIn("Water Test Profile Review was blocked by controlled validation.", manager)
        self.assertIn("Water Test Profile Approval was blocked by controlled validation.", manager)





    def test_v237_database_helper_is_split_into_bounded_partial_files(self):
        helper_files = [
            ROOT / "DatabaseHelper.cs",
            ROOT / "DatabaseHelper.QualityEvents.cs",
            ROOT / "DatabaseHelper.SecurityAudit.cs",
            ROOT / "DatabaseHelper.EnvironmentalMonitoring.cs",
            ROOT / "DatabaseHelper.Certificates.cs",
        ]

        for path in helper_files:
            self.assertTrue(path.is_file(), path.name)
            self.assertLess(
                path.stat().st_size,
                150_000,
                f"{path.name} must remain below the AI Review large-file threshold.",
            )
            source = path.read_text(encoding="utf-8-sig")
            self.assertIn("public static partial class DatabaseHelper", source)

        # Central API/gates remain represented across the split rather than
        # being replaced with a parallel helper implementation.
        combined = "\n".join(path.read_text(encoding="utf-8-sig") for path in helper_files)
        for symbol in (
            "ExecuteInTransaction",
            "CanReviewResults",
            "CanApproveResults",
            "AddElectronicSignature",
            "ApproveEMEvent",
            "IssueCertificateAtomic",
            "CancelCertificateControlled",
        ):
            self.assertIn(symbol, combined)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


    def test_v238_dashboard_pending_samples_isolated_and_transient_retry_is_bounded(self):
        source = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("PendingSamples = LoadPendingSamplesMetric()", source)
        self.assertIn('"Pending Samples / Samples"', source)
        self.assertIn('"Pending Samples / PRM"', source)
        self.assertIn("Status IS NULL OR Status NOT IN", source)
        self.assertIn("SampleStatus IS NULL OR SampleStatus NOT IN", source)
        self.assertIn("commandTimeoutSeconds: 10", source)
        self.assertIn("System.Threading.Thread.Sleep(250)", source)
        self.assertIn("IsTransientDashboardSqlError", source)
        self.assertIn("Dashboard metric query failed after retry:", source)
        self.assertIn("Dashboard metric query failed:", source)

        # Persistent failures must remain visibly unavailable rather than being
        # silently converted into a misleading zero.
        metric_method = source.split(
            "private static int? GetDashboardCount(string metricName, string query)", 1
        )[1].split("private static int? ExecuteDashboardCount", 1)[0]
        self.assertIn("return null;", metric_method)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


    def test_v239_sign_in_cancellation_evicts_pool_and_deadlines_are_layered(self):
        login = (ROOT / "Login.xaml.cs").read_text(encoding="utf-8-sig")
        database = (ROOT / "Infrastructure/DatabaseConnection.cs").read_text(encoding="utf-8-sig")
        repository = (ROOT / "Repositories/UserRepository.cs").read_text(encoding="utf-8-sig")

        self.assertIn(
            "TimeSpan.FromSeconds(Math.Max(40, AppConfig.CommandTimeoutSeconds + 15))",
            login,
        )
        self.assertIn("commandTimeoutSeconds: 10, cancellationToken: cancellationToken", repository)

        self.assertIn("SqlConnection.ClearPool(connection)", database)
        self.assertIn("cancellationToken.IsCancellationRequested", database)
        self.assertIn("throw new OperationCanceledException(", database)
        self.assertIn("Operation cancelled by user", database)
        self.assertIn("A severe error occurred on the current command", database)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))



    def test_v240_legacy_evidence_reconciliation_is_append_only_and_does_not_backfill_snapshots(self):
        migration_path = ROOT / "Database/Migrations/20260908_001_Legacy_Certificate_Evidence_Reconciliation.sql"
        migration = migration_path.read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        preflight_ui = (ROOT / "SystemPreflight.xaml.cs").read_text(encoding="utf-8-sig")
        em_reconciliation = (ROOT / "EMLegacySnapshotReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        certificate_reconciliation = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn("LegacyCertificateEvidenceReconciliations", migration)
        self.assertIn("TRG_LegacyCertificateEvidenceReconciliations_AppendOnly_20260908", migration)
        self.assertIn("TRG_LegacyCertificateEvidenceReconciliations_ValidateInsert_20260908", migration)
        self.assertIn("UX_LegacyCertificateEvidenceReconciliations_Root", migration)
        self.assertIn("UX_LegacyCertificateEvidenceReconciliations_Supersedes", migration)
        self.assertIn("LEGACY_HISTORICAL_RECORD_RETAINED", migration)
        self.assertIn("CONTROLLED_REISSUE_REQUIRED", migration)
        self.assertIn("c.IssueDate>=@SnapshotCutover", migration)
        self.assertNotIn("UPDATE DBO.PRM_CERTIFICATES", migration.upper())
        self.assertNotIn("UPDATE DBO.CERTIFICATES", migration.upper())
        self.assertNotIn("INSERT DBO.PRM_CERTIFICATESNAPSHOTS", migration.upper())
        self.assertNotIn("INSERT DBO.CERTIFICATEDOCUMENTSNAPSHOTS", migration.upper())

        # Closed/historical EM warnings are actionable from preflight, not only active blockers.
        self.assertIn('string.Equals(check.Status, "WARNING"', preflight_ui)
        self.assertIn("ALL UNRESOLVED HISTORICAL EVENTS", preflight_ui)
        self.assertIn("Approved/Completed/Closed", preflight_ui)
        self.assertNotIn("@Event<>0 OR UPPER(LTRIM(RTRIM(ISNULL(E.WorkflowStatus", em_reconciliation)

        # Only latest signed Retain disposition suppresses a pre-cutover warning.
        self.assertIn("#ResolvedLegacyCertificates", preflight)
        self.assertIn("LEGACY_HISTORICAL_RECORD_RETAINED", preflight)
        self.assertIn("Reconcile Legacy Certificates", preflight)
        self.assertIn("LegacyCertificateEvidenceReconciliation", preflight_ui)
        self.assertIn("SupersedesReconciliationID", certificate_reconciliation)
        self.assertIn("EnsureUserPermissionInTransaction", certificate_reconciliation)
        self.assertIn("ElectronicSignature", certificate_reconciliation)
        self.assertNotIn("UPDATE dbo.PRM_Certificates", certificate_reconciliation)
        self.assertNotIn("UPDATE dbo.Certificates", certificate_reconciliation)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260908_001")
        self.assertEqual(
            hashlib.sha256(migration_path.read_bytes()).hexdigest(),
            entry["sha256"],
        )

    def test_v241_legacy_certificate_validation_normalizes_legacy_storage_without_relaxing_controls(self):
        migration_path = ROOT / "Database/Migrations/20260908_002_Legacy_Certificate_Evidence_Validation_Normalization.sql"
        migration = migration_path.read_text(encoding="utf-8-sig")
        reconciliation = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")

        self.assertIn("CREATE OR ALTER TRIGGER dbo.TRG_LegacyCertificateEvidenceReconciliations_ValidateInsert_20260908", migration)
        self.assertIn("CONVERT(datetime2(0),c.IssueDate)", migration)
        self.assertIn("NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N''''", migration)
        self.assertIn("certificate now has a native issue snapshot", migration)
        self.assertIn("c.IssueDate>=@SnapshotCutover", migration)
        self.assertIn("A new certificate reconciliation must explicitly supersede the latest prior reconciliation", migration)
        self.assertNotIn("UPDATE dbo.Certificates", migration)
        self.assertNotIn("UPDATE dbo.PRM_Certificates", migration)
        self.assertNotIn("INSERT dbo.CertificateDocumentSnapshots", migration)
        self.assertNotIn("INSERT dbo.PRM_CertificateSnapshots", migration)

        self.assertIn("NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N'')", reconciliation)
        self.assertIn("NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N'')", preflight)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260908_002")
        self.assertEqual(
            hashlib.sha256(migration_path.read_bytes()).hexdigest(),
            entry["sha256"],
        )

    def test_v242_legacy_retain_requires_meaningful_controlled_evidence_and_preflight_fails_closed(self):
        migration_path = ROOT / "Database/Migrations/20260909_000_Legacy_Certificate_Evidence_Quality_Gate.sql"
        migration = migration_path.read_text(encoding="utf-8-sig")
        reconciliation = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml").read_text(encoding="utf-8-sig")

        self.assertIn("fn_LegacyCertificateEvidenceIsPlaceholder_20260909", migration)
        self.assertIn("LEGACY_HISTORICAL_RECORD_RETAINED", migration)
        self.assertIn("CONTROLLED_REISSUE_REQUIRED", migration)
        self.assertIn("THROW 54852", migration)
        self.assertIn("Batch reconciliation ID: TEST", migration)
        self.assertIn("Legacy certificate evidence placeholder normalization self-test failed", migration)
        for placeholder in (
            "N''NA''",
            "N''NOTFOUNDRESULT''",
            "N''NOEVIDENCE''",
            "N''MISSING''",
            "N''UNKNOWN''",
        ):
            self.assertIn(placeholder, migration)
        self.assertNotIn("UPDATE dbo.Certificates", migration)
        self.assertNotIn("UPDATE dbo.PRM_Certificates", migration)
        self.assertNotIn("INSERT dbo.CertificateDocumentSnapshots", migration)
        self.assertNotIn("INSERT dbo.PRM_CertificateSnapshots", migration)

        self.assertIn("ValidateReconciliationEvidenceQuality", reconciliation)
        self.assertIn("IsPlaceholderEvidenceText", reconciliation)
        self.assertIn("VersionKey=N'20260909_000'", reconciliation)
        self.assertIn("Retain evidence invalid; supersede", reconciliation)
        self.assertIn("placeholder text such as NA/not found is rejected for both dispositions", xaml)

        # Fail closed: a legacy Retain row suppresses a warning only when its latest
        # signed Evidence Reference, Summary and Reason all pass the DB evidence-quality gate.
        self.assertGreaterEqual(preflight.count("fn_LegacyCertificateEvidenceIsPlaceholder_20260909"), 7)
        self.assertIn("legacy-certificate evidence/quality controls", preflight)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260909_000")
        self.assertEqual(
            hashlib.sha256(migration_path.read_bytes()).hexdigest(),
            entry["sha256"],
        )



    def test_v243_reissue_evidence_quality_is_fail_closed_and_ui_prepares_clean_draft(self):
        migration_path = ROOT / "Database/Migrations/20260909_001_Legacy_Certificate_Reissue_Evidence_Quality.sql"
        migration = migration_path.read_text(encoding="utf-8-sig")
        reconciliation = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml").read_text(encoding="utf-8-sig")

        self.assertIn("TRG_LegacyCertificateEvidenceReconciliations_EvidenceQuality_20260909", migration)
        self.assertIn("Every signed Retain or Reissue disposition", migration)
        self.assertIn("THROW 54862", migration)
        self.assertNotIn("UPDATE dbo.Certificates", migration)
        self.assertNotIn("UPDATE dbo.PRM_Certificates", migration)
        self.assertNotIn("INSERT dbo.CertificateDocumentSnapshots", migration)
        self.assertNotIn("INSERT dbo.PRM_CertificateSnapshots", migration)

        self.assertIn("ValidateReconciliationEvidenceQuality", reconciliation)
        self.assertIn("PrepareControlledReissueDraft", reconciliation)
        self.assertIn("HasReissueEvidenceQualityIssue", reconciliation)
        self.assertIn("Reissue evidence incomplete; supersede", reconciliation)
        self.assertIn("VersionKey=N'20260909_001'", reconciliation)
        self.assertIn("PharmaLIMS certificate record", reconciliation)
        self.assertIn("does not create or imply a retrospective issue snapshot or report hash", reconciliation)

        # Fail-closed UI: Reissue is first/default; Retain requires deliberate selection.
        reissue_pos = xaml.index('Tag="CONTROLLED_REISSUE_REQUIRED"')
        retain_pos = xaml.index('Tag="LEGACY_HISTORICAL_RECORD_RETAINED"')
        self.assertLess(reissue_pos, retain_pos)
        self.assertIn("Prepare Controlled Reissue Draft", xaml)
        self.assertIn("placeholder text such as NA/not found is rejected for both dispositions", xaml)

        # Runtime preflight explains that a reissue disposition documents the QA decision
        # but cannot clear the warning while the legacy active certificate still exists.
        self.assertIn("intentionally does not clear this warning while the legacy certificate remains active", preflight)
        self.assertIn("TRG_LegacyCertificateEvidenceReconciliations_EvidenceQuality_20260909", preflight)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))
        entry = next(item for item in manifest["migrations"] if item["versionKey"] == "20260909_001")
        self.assertEqual(
            hashlib.sha256(migration_path.read_bytes()).hexdigest(),
            entry["sha256"],
        )



    def test_v244_legacy_reissue_routes_to_source_workflow_and_preflight_detects_stranded_cancellation(self):
        reconciliation = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        preflight_ui = (ROOT / "SystemPreflight.xaml.cs").read_text(encoding="utf-8-sig")
        water_results = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        prm_results = read_source_family("ProductionRawMaterialResults.xaml.cs")

        self.assertIn("Open Controlled Reissue Workflow", xaml)
        self.assertIn('x:Name="BtnOpenSourceWorkflow"', xaml)
        self.assertIn("BtnOpenSourceWorkflow_Click", reconciliation)
        self.assertIn("CreateSourceReissueWorkflow", reconciliation)
        self.assertIn("ReadSourceReissueState", reconciliation)
        self.assertIn("RequiresPreflightRefresh", reconciliation)
        self.assertIn("waterResults.LoadSample(row.SampleID)", reconciliation)
        self.assertIn("new ProductionRawMaterialResults(", reconciliation)
        self.assertIn("ReissuedFromCertificateID=oldc.CertificateID", reconciliation)
        self.assertIn("ReplacementHasNativeSnapshot", reconciliation)
        self.assertIn("ReplacementHasValidHash", reconciliation)

        # The routed Water and PRM windows remain the existing controlled certificate workflows.
        self.assertIn("public void LoadSample(int sampleId)", water_results)
        self.assertIn("CancelCertificateControlled", water_results)
        self.assertIn("BtnCancelCOA.IsEnabled = certificateStateVerified && CanCancelCertificate() && hasActiveCertificate", water_results)
        self.assertIn("IssueCertificateAtomic", (ROOT / "DatabaseHelper.Certificates.cs").read_text(encoding="utf-8-sig"))
        self.assertIn("BtnReissueCertificate_Click", prm_results)
        self.assertIn("ReissuedFromCertificateID", prm_results)

        # Preflight fails closed when cancellation happened but the linked compliant replacement was never issued.
        self.assertIn("CheckLegacyCertificateReissueLifecycleAsync", preflight)
        self.assertIn("Legacy controlled reissue lifecycle", preflight)
        self.assertIn("no active linked replacement", preflight)
        self.assertIn("ReissuedFromCertificateID", preflight)
        self.assertIn("(N'Certificates',N'ReissuedFromCertificateID')", preflight)
        self.assertIn("PRM_CertificateSnapshots", preflight)
        self.assertIn("CertificateDocumentSnapshots", preflight)
        self.assertIn("reconciliation.WasSaved || reconciliation.RequiresPreflightRefresh", preflight_ui)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))



    def test_v245_signed_reconciliation_requires_explicit_superseding_mode_and_ai_review_avoids_false_delivery_error(self):
        reconciliation = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        xaml = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml").read_text(encoding="utf-8-sig")
        ai_review = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")

        self.assertIn('x:Name="BtnStartSuperseding"', xaml)
        self.assertIn("Correct Signed QA Evidence", xaml)
        self.assertIn("DisplaySignedReconciliation(row)", reconciliation)
        self.assertIn("txtReason.Text = row.ReconciliationReason", reconciliation)
        self.assertIn("SetEntryEditMode(false, showSupersedingAction: true)", reconciliation)
        self.assertIn("_isSupersedingCorrectionDraft", reconciliation)
        self.assertIn("TryValidateSaveRequest", reconciliation)
        self.assertIn("Use Start Superseding Correction before entering or signing replacement evidence", reconciliation)
        self.assertNotIn('ApplicationLogger.Error("Unable to save legacy certificate evidence reconciliation."', reconciliation)
        self.assertIn("UserFacingError performs the single diagnostic log write", reconciliation)

        self.assertIn('if (AppConfig.IsDevelopment)', ai_review)
        self.assertIn('"Info", "Development Workspace"', ai_review)
        self.assertIn("not evidence that they are included in the controlled delivery ZIP", ai_review)
        self.assertIn("signed Legacy Certificate Evidence Reconciliation workflow", ai_review)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


    def test_v246_prm_legacy_reissue_route_prefills_signed_reason_and_disables_standalone_cancel(self):
        reconciliation = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        prm_results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        prm_xaml = (ROOT / "ProductionRawMaterialResults.xaml").read_text(encoding="utf-8-sig")

        self.assertIn("row.CertificateID", reconciliation)
        self.assertIn("row.LatestReconciliationID ?? 0", reconciliation)
        self.assertIn("row.CertificateNumber", reconciliation)
        self.assertIn("row.ReconciliationReason", reconciliation)
        self.assertIn("signed QA reconciliation reason prefilled", reconciliation)
        self.assertIn("standalone cancellation is disabled", reconciliation)

        self.assertIn("_initialLegacyCertificateId", prm_results)
        self.assertIn("_initialLegacyReconciliationId", prm_results)
        self.assertIn("_initialControlledReissueReason", prm_results)
        self.assertIn("BuildControlledReissueReason", prm_results)
        self.assertIn("Legacy Certificate Evidence Reconciliation #", prm_results)
        self.assertIn("TxtCertificateReason.Text = _initialControlledReissueReason", prm_results)
        self.assertIn('BtnReissueCertificate.Content = "Reissue Legacy Certificate"', prm_results)
        self.assertIn("Standalone cancellation is disabled", prm_results)
        self.assertIn("cancellation and linked replacement issuance occur atomically", prm_results)
        self.assertIn("_controlledLegacyReissueCompleted = true", prm_results)
        self.assertIn('MaxLength="500"', prm_xaml)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


    def test_v247_water_legacy_reissue_route_prefills_reason_locks_target_and_links_replacement(self):
        reconciliation = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        reconciliation_xaml = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml").read_text(encoding="utf-8-sig")
        water_results = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        report = (ROOT / "ReportCertificate.xaml.cs").read_text(encoding="utf-8-sig")
        certificates = (ROOT / "DatabaseHelper.Certificates.cs").read_text(encoding="utf-8-sig")

        self.assertIn("ConfigureLegacyCertificateReissueContext", reconciliation)
        self.assertIn("row.ReconciliationReason", reconciliation)
        self.assertIn("Discard Unsaved Superseding Draft", reconciliation)
        self.assertIn('Content="Correct Signed QA Evidence"', reconciliation_xaml)

        self.assertIn("_legacyCertificateReissueRouteActive", water_results)
        self.assertIn("BuildLegacyReissueReason", water_results)
        self.assertIn("Legacy Certificate Evidence Reconciliation #", water_results)
        self.assertIn('Cancel Legacy {documentName} (Step 1)', water_results)
        self.assertIn('Issue Replacement {documentName} (Step 2)', water_results)
        self.assertIn("PromptForCancellationReason(", water_results)
        self.assertIn("_legacyReissueReason", water_results)
        self.assertIn("_legacyReissueCertificateId", water_results)
        self.assertIn("ConfigureControlledLegacyReissue", water_results)

        self.assertIn("ConfigureControlledLegacyReissue", report)
        self.assertIn("Issue Replacement COA", report)
        self.assertIn("Issue Replacement Report", report)
        self.assertIn("_controlledReissueFromCertificateId", report)
        self.assertIn("Controlled Legacy COA Reissue", report)
        self.assertIn("Controlled legacy COA reissue", report)

        self.assertIn("expectedReissuedFromCertificateId", certificates)
        self.assertIn("@ExpectedReissuedFromCertificateID", certificates)
        self.assertIn("expectedCertificateId", certificates)
        self.assertIn("@ExpectedCertificateID", certificates)
        self.assertIn("Replacement issuance was stopped to preserve the reissue link", certificates)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


    def test_v248_large_codebehind_files_are_split_into_controlled_partial_classes(self):
        split_families = (
            "CultureMediaPreparation.xaml.cs",
            "ProductionRawMaterialResults.xaml.cs",
            "QualityEventInvestigation.xaml.cs",
            "ResultsEntry.xaml.cs",
            "EMResultsEntry.xaml.cs",
            "ReportsTrends.xaml.cs",
            "EMPlanning.xaml.cs",
            "Infrastructure/StartupDatabaseMigrator.cs",
        )
        for relative in split_families:
            path = ROOT / relative
            family = [path, *sorted(path.parent.glob(path.stem + ".Part*.cs"))]
            self.assertGreaterEqual(len(family), 2, relative)
            for source in family:
                self.assertLess(source.stat().st_size, 150_000, source.name)

        ai_review = (ROOT / "AISystemReview.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("new FileInfo(path).Length >= 150_000", ai_review)
        validator = (ROOT / "scripts/validate_project.py").read_text(encoding="utf-8-sig")
        self.assertIn("def source_family(path: Path) -> str:", validator)
        self.assertIn("code = source_family(codebehind)", validator)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


    def test_v249_water_workflow_reauthorizes_and_rechecks_approval_gates_in_transaction(self):
        registration = (ROOT / "NewSampleDialog.xaml.cs").read_text(encoding="utf-8-sig")
        results = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        security = (ROOT / "DatabaseHelper.SecurityAudit.cs").read_text(encoding="utf-8-sig")
        quality = (ROOT / "DatabaseHelper.QualityEvents.cs").read_text(encoding="utf-8-sig")

        self.assertIn('registrationSignature.SignedBy, "CanRegisterSamples", "register water samples"', registration)

        for permission_contract in (
            'signatureWindow.SignedBy, "CanEnterResults", "save water results"',
            'signatureWindow.SignedBy, "CanEnterResults", "start water analysis"',
            'signatureWindow.SignedBy, "CanEnterResults", "submit water results for review"',
            'signatureWindow.SignedBy, "CanReviewResults", "review water results"',
            'signatureWindow.SignedBy, "approve water results"',
        ):
            self.assertIn(permission_contract, results)

        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", results)
        self.assertIn("EnsureSampleWorkflowSeparationInTransaction", security)
        self.assertIn("FROM dbo.ElectronicSignatures WITH (UPDLOCK, HOLDLOCK)", security)
        self.assertIn('signerRole, "Review"', results)
        self.assertIn('signerRole, "Approval"', results)
        self.assertIn('currentSampleId, currentUser, "Review"', results)
        self.assertIn('currentSampleId, currentUser, "Approval"', results)

        self.assertIn("EnsureSampleApprovalQualityGatesInTransaction", quality)
        self.assertIn("FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)", quality)
        self.assertIn("FROM dbo.SampleTests st WITH (UPDLOCK, HOLDLOCK)", quality)
        self.assertIn("EnsureSampleApprovalQualityGatesInTransaction(\n                        con, tran, currentSampleId);", results)

        self.assertIn("string signedBy,\n            string signerRole", results)
        self.assertIn("new SqlParameter(\"@signedBy\", signedBy.Trim())", results)
        self.assertNotIn("new SqlParameter(\"@role\", string.IsNullOrWhiteSpace(currentRole)", results)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


    def test_v251_water_registration_numbering_and_v249_transaction_gates_are_preserved(self):
        registration = (ROOT / "NewSampleDialog.xaml.cs").read_text(encoding="utf-8-sig")
        results = read_source_family("ResultsEntry.xaml.cs")
        helper = (ROOT / "DatabaseHelper.cs").read_text(encoding="utf-8-sig")
        quality = read_source_family("QualityEventInvestigation.xaml.cs")
        prm = (ROOT / "PRMQualityEventInvestigation.xaml.cs").read_text(encoding="utf-8-sig")

        # v249 authorization / SoD / approval gates must survive the database-clock merge.
        self.assertIn('registrationSignature.SignedBy, "CanRegisterSamples", "register water samples"', registration)
        for permission_contract in (
            'signatureWindow.SignedBy, "CanEnterResults", "save water results"',
            'signatureWindow.SignedBy, "CanEnterResults", "start water analysis"',
            'signatureWindow.SignedBy, "CanEnterResults", "submit water results for review"',
            'signatureWindow.SignedBy, "CanReviewResults", "review water results"',
            'signatureWindow.SignedBy, "approve water results"',
        ):
            self.assertIn(permission_contract, results)
        self.assertIn("EnsureQaApprovalAuthorizationInTransaction", results)
        self.assertIn("EnsureSampleWorkflowSeparationInTransaction", results)
        self.assertIn("EnsureSampleApprovalQualityGatesInTransaction", results)
        self.assertIn("string signedBy,\n            string signerRole", results)

        # v250 database-clock hardening is merged without replacing authorization.
        self.assertIn("ValidateWaterManualTimestampsAgainstDatabaseTime", registration)
        self.assertIn('new SqlCommand("SELECT SYSDATETIME();", con, tran)', registration)
        self.assertIn("DatabaseHelper.GetAuthoritativeDatabaseTime()", results)
        self.assertIn("ValidateWaterAnalysisStartInTransaction", results)
        self.assertIn("SELECT SamplingDateTime, ReceivedDateTime, SYSDATETIME() AS ServerNow", results)

        # Water number generation self-heals a stale ledger against existing Development rows.
        self.assertIn("DECLARE @ExistingMax INT = 0", helper)
        self.assertIn("MAX(TRY_CONVERT(INT, SUBSTRING(SampleNumber", helper)
        self.assertIn("@CurrentLast < @ExistingMax", helper)
        self.assertIn("LastNumber = @ExistingMax", helper)
        self.assertIn("IsolationLevel.Serializable", helper)

        # Failed registration is fail-closed and identifies the controlled stage without exposing SQL text.
        for stage in (
            'registrationStage = "authorization"',
            'registrationStage = "authoritative timestamp validation"',
            'registrationStage = "controlled Water Plan validation"',
            'registrationStage = "water sample insert"',
            'registrationStage = "controlled test snapshot insert"',
            'registrationStage = "Water Plan sample link"',
            'registrationStage = "electronic signature persistence"',
            'registrationStage = "registration audit trail"',
        ):
            self.assertIn(stage, registration)
        self.assertIn("No data were committed", registration)
        self.assertIn("sqlException.Number == 2601 || sqlException.Number == 2627", registration)

        # Controlled investigation report identity is based on persisted event creation date.
        self.assertIn("loadedEventCreatedDate", quality)
        self.assertIn("GenerateInvestigationReportNumber()", quality)
        self.assertIn('"QEIR-" + loadedEventCreatedDate.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)', quality)
        self.assertNotIn('"QEIR-" + DateTime.Now.ToString("yyyyMMdd")', quality)
        self.assertIn("loadedEventCreatedDate", prm)
        self.assertIn('"PRM-QEIR-" + loadedEventCreatedDate.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)', prm)
        self.assertNotIn('"PRM-QEIR-" + DateTime.Now.ToString("yyyyMMdd")', prm)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


    def test_v257_preflight_does_not_own_session_application_lock(self):
        database = (ROOT / "Infrastructure/DatabaseConnection.cs").read_text(encoding="utf-8-sig")
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")

        # Database Maintenance keeps the non-pooled exclusive lease, but the read-only
        # System Preflight must never own a session-level application lock.
        self.assertIn("CreateUnpooledConnection", database)
        self.assertGreaterEqual(migrator.count("_database.CreateUnpooledConnection()"), 2)
        self.assertIn("@LockMode=N'Exclusive'", migrator)
        self.assertIn("sys.sp_getapplock", migrator)

        self.assertIn("APPLOCK_TEST(N'public', N'PharmaLIMS.SchemaMigration', N'Shared', N'Session')", preflight)
        self.assertIn("IsDatabaseMaintenanceActiveAsync", preflight)
        self.assertNotIn("_database.CreateUnpooledConnection()", preflight)
        self.assertNotIn("sys.sp_getapplock", preflight)
        self.assertNotIn("sys.sp_releaseapplock", preflight)
        self.assertNotIn("ReleasePreflightLeaseAsync", preflight)
        self.assertNotIn("AsyncLocal<SqlConnection", preflight)

        self.assertIn("_maintenanceBlockedByDatabaseActivity", main)
        self.assertIn("!_maintenanceBlockedByDatabaseActivity", main)

    def test_v252_database_maintenance_signature_uses_authenticated_di_session_and_clear_reason(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        signature = (ROOT / "ElectronicSignature.xaml.cs").read_text(encoding="utf-8-sig")
        signature_xaml = (ROOT / "ElectronicSignature.xaml").read_text(encoding="utf-8-sig")

        self.assertIn("ElectronicSignature signature = GetService<ElectronicSignature>();", main)
        self.assertIn('signature.Configure("SYSTEM-DB", Login.CurrentUser, "Development Database Maintenance", true);', main)
        self.assertIn("GetSignatureDisplayUserName", signature)
        self.assertIn("IsDatabaseMaintenanceAction", signature)
        self.assertIn('SelectComboText(cboReason, "Database maintenance")', signature)
        self.assertIn("Invalid password for account", signature)
        self.assertIn("authenticated session is no longer available", signature)
        self.assertIn('x:Name="lblPasswordHelp"', signature_xaml)
        self.assertIn('Content="Database maintenance"', signature_xaml)

    def test_v257_system_preflight_uses_normal_database_helpers_and_reports_stage(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("private readonly SemaphoreSlim _runGate = new(1, 1);", preflight)
        self.assertIn("_database.ExecuteQueryAsync(", preflight)
        self.assertIn("_database.ExecuteScalarAsync(", preflight)
        self.assertNotIn("ExecutePreflightQueryAsync", preflight)
        self.assertNotIn("ExecutePreflightScalarAsync", preflight)
        self.assertNotIn("_activePreflightConnection", preflight)
        self.assertIn('string verificationStage = "Required operational objects"', preflight)
        self.assertIn('"Stage: " + verificationStage', preflight)
        self.assertIn('verificationStage = "Protected compliance records"', preflight)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))

    def test_v257_preflight_serializes_runs_without_reusing_a_lock_owning_session(self):
        source = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        ui = (ROOT / "SystemPreflight.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("private readonly SemaphoreSlim _runGate = new(1, 1);", source)
        self.assertGreaterEqual(source.count("await _runGate.WaitAsync"), 2)
        self.assertGreaterEqual(source.count("_runGate.Release();"), 2)
        self.assertNotIn("AsyncLocal<SqlConnection", source)
        self.assertNotIn("_activePreflightConnection", source)
        self.assertIn("APPLOCK_TEST", source)
        self.assertIn("Build: ", ui)
        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))

    def test_v255_superseding_reconciliation_binds_to_expected_signed_row(self):
        source = (ROOT / "LegacyCertificateEvidenceReconciliation.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("_expectedSupersededReconciliationId", source)
        self.assertIn("_expectedSupersededCertificateId", source)
        self.assertIn("_expectedSupersededModule", source)
        self.assertIn("row.LatestReconciliationID is not int reconciliationId", source)
        self.assertIn("_expectedSupersededReconciliationId = reconciliationId", source)
        self.assertIn("latestId.Value != expectedSupersededReconciliationId.Value", source)
        self.assertIn("was superseded by another action before this correction was signed", source)
        self.assertIn("@Supersedes", source)

    def test_v255_coa_cancellation_persists_complete_esignature_atomically(self):
        helper = (ROOT / "DatabaseHelper.Certificates.cs").read_text(encoding="utf-8-sig")
        results = (ROOT / "ResultsEntry.xaml.cs").read_text(encoding="utf-8-sig")
        signature = (ROOT / "ElectronicSignature.xaml.cs").read_text(encoding="utf-8-sig")
        signature_xaml = (ROOT / "ElectronicSignature.xaml").read_text(encoding="utf-8-sig")

        self.assertIn("meaningOfSignature", helper)
        self.assertIn("signatureReason", helper)
        self.assertIn("INSERT dbo.ElectronicSignatures", helper)
        self.assertIn("N'COA Cancellation'", helper)
        self.assertIn("The COA cancellation electronic signature could not be stored. The cancellation was rolled back.", helper)
        self.assertIn("signatureWindow.Meaning", results)
        self.assertIn("signatureWindow.Reason", results)
        self.assertIn("IsCertificateCancellationAction", signature)
        self.assertIn('SelectComboText(cboReason, "Certificate cancellation")', signature)
        self.assertIn('Content="Certificate cancellation"', signature_xaml)

    def test_v255_preflight_temp_tables_are_reentrant_and_failures_are_classified(self):
        source = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertGreaterEqual(source.count("DROP TABLE #ResolvedLegacyCertificates"), 4)
        self.assertIn("IsPreflightTimeoutOrLock(ex)", source)
        self.assertIn('"Preflight verification failure"', source)
        self.assertIn('"Preflight database responsiveness"', source)
        self.assertIn("SQL error ", source)

    def test_v255_mainwindow_reuses_window_preflight_report_without_second_full_run(self):
        main = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        ui = (ROOT / "SystemPreflight.xaml.cs").read_text(encoding="utf-8-sig")
        self.assertIn("public SystemPreflightReport? LastReport => _lastReport;", ui)
        self.assertIn("SystemPreflightReport? report = null;", main)
        self.assertIn("report = ShowSystemPreflightWindow();", main)
        self.assertIn("ApplySystemReadinessReport(report);", main)
        button = main.split("private void BtnSystemPreflight_Click", 1)[1].split("private async void BtnDatabaseMaintenance_Click", 1)[0]
        self.assertNotIn("RefreshSystemReadinessAsync", button)



    def test_v257_preflight_maintenance_coordination_is_read_only_and_nonblocking(self):
        source = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        method = source.split("private async Task<bool> IsDatabaseMaintenanceActiveAsync", 1)[1].split(
            "private static bool IsPreflightTimeoutOrLock", 1
        )[0]
        self.assertIn("APPLOCK_TEST", method)
        self.assertIn("N'PharmaLIMS.SchemaMigration'", method)
        self.assertIn("N'Shared'", method)
        self.assertIn("N'Session'", method)
        self.assertIn("compatible == 1", method)
        self.assertIn("compatible == 0", method)
        self.assertNotIn("sp_getapplock", method)
        self.assertNotIn("sp_releaseapplock", method)
        self.assertNotIn("CreateUnpooledConnection", method)

    def test_v257_preflight_integration_contract_detects_exclusive_maintenance_without_shared_lease(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        migrator = read_source_family("Infrastructure/StartupDatabaseMigrator.cs")

        self.assertIn("APPLOCK_TEST", preflight)
        self.assertIn("APPLOCK_TEST", integration)
        self.assertIn("Exclusive maintenance lifecycle lease", integration)
        self.assertIn("@LockMode=N'Exclusive'", integration)
        self.assertIn("@LockMode=N'Exclusive'", migrator)
        self.assertNotIn("PreflightLifecycleException", preflight)
        self.assertIn("FindPreflightSqlException", preflight)
        self.assertIn("exception.InnerException != null && IsPreflightTimeoutOrLock(exception.InnerException)", preflight)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))


    def test_v258_prm_numeric_interpretation_is_strict_and_shared_with_runtime_integration(self):
        evaluator = (ROOT / "Services/PrmNumericSpecificationEvaluator.cs").read_text(encoding="utf-8-sig")
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        integration_project = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj").read_text(encoding="utf-8-sig")

        self.assertIn("PrmNumericSpecificationEvaluator.TryParseControlledDecimal", prm)
        self.assertIn("PrmNumericSpecificationEvaluator.Evaluate", prm)
        self.assertIn("ValidatePrmNumericResultFormatsBeforeSignature();", prm)
        self.assertIn("private void ValidatePrmNumericResultFormatsBeforeSignature()", prm)
        self.assertIn("Commas and thousands separators are not accepted because they are ambiguous", prm)
        self.assertLess(
            prm.index("ValidatePrmNumericResultFormatsBeforeSignature();"),
            prm.index('RequestPrmSignature("PRM Result Entry")'),
            "Ambiguous numeric PRM result text must be rejected before e-signature."
        )
        self.assertNotIn("Replace(',', '.')", evaluator)
        self.assertIn("normalized.Contains(',')", evaluator)
        self.assertIn("RuleKind.LessThan => value < limit", evaluator)
        self.assertIn("RuleKind.LessOrEqual => value <= limit", evaluator)
        self.assertIn("RuleKind.Range", evaluator)
        self.assertIn("NMT 100 CFU/g; incubation 30-35 C", integration)
        self.assertIn('AssertPrmInterpretation(90m, "95-105", null, "Does Not Conform")', integration)
        self.assertIn('AssertPrmInterpretation(100m, "95-105", null, "Conforms")', integration)
        self.assertIn('AssertPrmInterpretation(100m, "LESS THAN 100", null, "Does Not Conform")', integration)
        self.assertIn('AssertPrmInterpretation(100m, "NMT 1,000", null, "Check Required")', integration)
        self.assertIn('TryParseControlledDecimal("1,000", out _)', integration)
        self.assertIn("../../Services/PrmNumericSpecificationEvaluator.cs", integration_project)

    def test_v258_prm_result_save_rejects_stale_windows_and_audits_database_values(self):
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        contract = (ROOT / "Services/PrmResultPersistenceContract.cs").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        integration_project = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj").read_text(encoding="utf-8-sig")

        self.assertIn("PrmResultPersistenceContract.GuardedUpdateSql", results)
        self.assertIn("@ExpectedResultValue", contract)
        self.assertIn("@ExpectedInterpretation", contract)
        self.assertIn("@ExpectedRemarks", contract)
        self.assertIn("@ExpectedEnteredBy", contract)
        self.assertIn("@ExpectedEnteredDate", contract)
        self.assertIn("OUTPUT\n    deleted.ResultValue", contract)
        self.assertIn("CONVERT(VARBINARY(MAX), ResultValue)", contract)
        self.assertIn("DBConcurrencyException", results)
        self.assertIn("no stale result was overwritten", results)
        self.assertIn("BuildPrmResultAuditValue(persistedOldResult", results)
        self.assertIn("BuildPrmResultAuditValue(persistedNewResult", results)
        self.assertIn("VerifyPrmResultOptimisticConcurrencyAsync", integration)
        self.assertIn("staleRows != 0", integration)
        self.assertIn("../../Services/PrmResultPersistenceContract.cs", integration_project)

    def test_v258_prm_remarks_only_edit_preserves_result_attribution(self):
        results = read_source_family("ProductionRawMaterialResults.xaml.cs")
        contract = (ROOT / "Services/PrmResultPersistenceContract.cs").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")

        self.assertIn("EnteredDate,\n    CONVERT(NVARCHAR(20), EnteredDate, 120) AS EnteredDateText", results)
        self.assertIn("WHEN @ResultChanged = 1", contract)
        self.assertIn("ELSE EnteredBy", contract)
        self.assertIn("ELSE EnteredDate", contract)
        self.assertIn('"PRM Result Remarks Update"', results)
        self.assertIn('auditField = remarksChanged && !resultChanged && !interpretationChanged', results)
        self.assertIn("Remarks-only PRM update changed result attribution or entry time", integration)

    def test_v258_preflight_uses_read_only_applock_test_not_sp_getapplock(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("APPLOCK_TEST", preflight)
        self.assertNotIn("sys.sp_getapplock", preflight)
        self.assertNotIn("sys.sp_releaseapplock", preflight)
        self.assertIn("compatible == 1", preflight)
        self.assertIn("compatible == 0", preflight)
        self.assertIn("unexpected APPLOCK_TEST result", preflight)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))



    def test_v259_prm_numeric_evaluator_handles_compound_rules_and_ignores_context_ranges(self):
        evaluator = (ROOT / "Services/PrmNumericSpecificationEvaluator.cs").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        integration_project = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj").read_text(encoding="utf-8-sig")

        self.assertIn("while (remaining.Length > 0)", evaluator)
        self.assertIn("if (!op.Success || op.Index != 0)", evaluator)
        self.assertIn("if (!connector.Success) return ClauseParseResult.Invalid", evaluator)
        self.assertIn("IsPureContextClause(clause)", evaluator)
        self.assertNotIn(".Where(ContainsKnownRuleSyntax)", evaluator)
        self.assertNotIn("OperatorRegex.Matches(clause)", evaluator)
        self.assertIn("TryParseSpecificationLimitToken", evaluator)
        self.assertIn("NumberStyles.AllowExponent", evaluator)
        self.assertIn('@"^10\\s*\\^\\s*(?<exponent>[+-]?\\d+)$"', evaluator)
        self.assertIn("foreach (RuleConstraint constraint in parsed.Constraints)", evaluator)
        self.assertIn('AssertPrmInterpretation(106m, "NLT 95 AND NMT 105", null, "Does Not Conform")', integration)
        self.assertIn('AssertPrmInterpretation(106m, ">=95 AND <=105", null, "Does Not Conform")', integration)
        self.assertIn('AssertPrmInterpretation(32m, "NMT 10 CFU/g; incubation RANGE 30-35 C", null, "Does Not Conform")', integration)
        self.assertIn('AssertPrmInterpretation(50m, "NMT 100 CFU/g; incubation RANGE 30-35 C", null, "Conforms")', integration)
        self.assertIn('AssertPrmInterpretation(0.5m, "NMT 1e-3 CFU/g", null, "Does Not Conform")', integration)
        self.assertIn('NMT 10^3 CFU/g (1000 CFU/g)', integration)
        self.assertIn("../../Services/PrmResultInterpretationEvaluator.cs", integration_project)

    def test_v259_prm_save_and_submit_use_complete_locked_snapshot_and_authoritative_aggregate(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")
        state = (ROOT / "Services/PrmSampleResultStateService.cs").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        integration_project = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj").read_text(encoding="utf-8-sig")

        self.assertIn("WITH (UPDLOCK, HOLDLOCK)", state)
        self.assertIn("DataRowVersion.Original", state)
        self.assertIn("SnapshotColumns", state)
        self.assertIn("LockAndValidateLoadedSnapshot", state)
        self.assertIn("ReadAuthoritativeStateForUpdate", state)
        self.assertIn("PrmResultInterpretationEvaluator.Evaluate", state)
        self.assertIn("PersistedInterpretationsMatchEvidence", state)

        save = prm.split("private void BtnSaveResults_Click", 1)[1].split("private void BtnSubmitReview_Click", 1)[0]
        self.assertLess(save.index("PrmSampleResultStateService.LockAndValidateLoadedSnapshot"), save.index("SaveResultsFromGrid"))
        self.assertLess(save.index("SaveResultsFromGrid"), save.index("PrmSampleResultStateService.ReadAuthoritativeStateForUpdate"))
        self.assertIn('newStatus = authoritative.AllRequiredResultsEntered ? "Results Entered" : "In Progress"', save)
        self.assertIn("overall = authoritative.OverallInterpretation", save)

        submit = prm.split("private void BtnSubmitReview_Click", 1)[1].split("private void BtnReview_Click", 1)[0]
        self.assertIn("PrmSampleResultStateService.LockAndValidateLoadedSnapshot", submit)
        self.assertIn("PrmSampleResultStateService.DeriveAuthoritativeState", submit)
        self.assertIn("authoritative.AllRequiredResultsEntered", submit)
        self.assertIn("authoritative.OverallInterpretation", submit)

        self.assertIn("VerifyPrmSampleWideStateConcurrencyAsync", integration)
        self.assertIn("A stale second PRM window was not rejected", integration)
        self.assertIn("Remarks-only save changed the authoritative sample-wide PRM interpretation", integration)
        self.assertIn("Partial PRM required-result state did not remain In Progress", integration)
        self.assertIn("../../Services/PrmSampleResultStateService.cs", integration_project)

    def test_v259_prm_review_approval_and_certificate_rederive_authoritative_result_state(self):
        prm = read_source_family("ProductionRawMaterialResults.xaml.cs")

        review = prm.split("private void BtnReview_Click", 1)[1].split("private async void BtnApprove_Click", 1)[0]
        self.assertIn("PrmSampleResultStateService.LockAndValidateLoadedSnapshot", review)
        self.assertIn("PrmSampleResultStateService.DeriveAuthoritativeState", review)
        self.assertIn("ResultInterpretation = @ResultInterpretation", review)
        self.assertIn("authoritative.OverallInterpretation", review)

        approve = prm.split("private async void BtnApprove_Click", 1)[1].split("private void BtnIssueCertificate_Click", 1)[0]
        self.assertIn("PrmSampleResultStateService.LockAndValidateLoadedSnapshot", approve)
        self.assertIn("PrmSampleResultStateService.DeriveAuthoritativeState", approve)
        self.assertIn("string persistedInterpretation = authoritative.OverallInterpretation", approve)

        issue = prm.split("private string IssueCertificate(", 1)[1].split("private void CancelCertificate(", 1)[0]
        self.assertIn("PrmSampleResultStateService.DeriveAuthoritativeState(results)", issue)
        self.assertIn("approved sample summary no longer matches the authoritative PRM test evidence", issue)
        self.assertIn("authoritative.OverallInterpretation", issue)

    def test_v259_full_preflight_holds_transaction_owned_shared_maintenance_lease(self):
        preflight = (ROOT / "Infrastructure/SystemPreflightService.cs").read_text(encoding="utf-8-sig")
        contract = (ROOT / "Infrastructure/DatabaseLifecycleCoordinationContract.cs").read_text(encoding="utf-8-sig")
        integration = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs").read_text(encoding="utf-8-sig")
        integration_project = (ROOT / "tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj").read_text(encoding="utf-8-sig")

        self.assertIn("TryAcquireFullPreflightMaintenanceLeaseAsync", preflight)
        self.assertIn("maintenanceLease?.Dispose();", preflight)
        self.assertIn("connection.BeginTransaction(IsolationLevel.ReadCommitted)", preflight)
        self.assertIn("AcquirePreflightSharedTransactionLockSql", preflight)
        self.assertNotIn("@LockOwner=N'Session'", contract)
        self.assertIn("@LockMode=N'Shared'", contract)
        self.assertIn("@LockOwner=N'Transaction'", contract)
        self.assertIn("@LockTimeout=0", contract)
        self.assertIn("-1 =>", contract)
        self.assertIn("-2 =>", contract)
        self.assertIn("-3 =>", contract)
        self.assertIn("-999 =>", contract)
        self.assertIn("Transaction-owned shared preflight lifecycle lease", integration)
        self.assertIn("incorrectly granted while full preflight held its Shared Transaction lease", integration)
        self.assertIn("Full preflight should be denied while Database Maintenance holds the Exclusive lease", integration)
        self.assertIn("../../Infrastructure/DatabaseLifecycleCoordinationContract.cs", integration_project)

        manifest = json.loads((ROOT / "Database/MigrationManifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("2026.9.23.295", manifest["applicationVersion"])
        self.assertEqual(85, len(manifest["migrations"]))



if __name__ == "__main__":
    unittest.main()
