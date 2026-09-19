"""Source contracts for the merge. These do not compile/execute C#, SQL or WPF."""
import json
import re
import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def source(n): return (ROOT/n).read_text(encoding='utf-8-sig')
def section(text,start,end): return text.split(start,1)[1].split(end,1)[0]
def failure(): return source('Services/AuthenticationCommitContract.cs').split('internal const string FailureSql',1)[1]
class Merge262Contracts(unittest.TestCase):
    def test_quality_management_rechecks_effective_lock_in_transaction(self):
        s=section(source('DatabaseHelper.SecurityAudit.cs'),'internal static string EnsureQualityEventManagementAuthorizationInTransaction','internal static string EnsureActiveUserInTransaction')
        for token in ['UPDLOCK,HOLDLOCK','LockedUntil>SYSDATETIME()','AS IsLockedNow','reader["IsLockedNow"]','!isActive || isLockedNow ||']: self.assertIn(token,s)
    def test_general_active_guard_rechecks_effective_lock_in_transaction(self):
        s=section(source('DatabaseHelper.SecurityAudit.cs'),'internal static string EnsureActiveUserInTransaction','public static bool CanCloseQualityEvent')
        for token in ['UPDLOCK, HOLDLOCK','LockedUntil>SYSDATETIME()','reader["IsLockedNow"]','if (!isActive || isLockedNow || mustChangePassword)']: self.assertIn(token,s)
    def test_failure_has_checked_identity_not_username_alone(self):
        s=section(source('Repositories/UserRepository.cs'),'public async Task<int> RegisterAuthenticationFailureAsync','public async Task<string> GetRoleAsync')
        for token in ['User verifiedUser','ThrowIfNull','verifiedUser.UserId','verifiedUser.Username','AuthenticationCommitContract.FailureSql']: self.assertIn(token,s)
        self.assertIn('UserID=@VerifiedUserID AND Username=@Username',failure())
    def test_failure_compares_all_credentials_as_binary(self):
        s=failure()
        for name,parameter in [('PasswordHash','VerifiedLegacyHash'),('PasswordHashNew','VerifiedHash'),('PasswordSalt','VerifiedSalt')]:
            self.assertIn(f"CONVERT(varbinary(max),ISNULL({name},N''))=CONVERT(varbinary(max),@{parameter})",s)
    def test_failure_keeps_real_concurrent_increments(self):
        s=failure(); predicate=s.split('WHERE',1)[1]
        self.assertIn('ISNULL(FailedLoginAttempts,0)+1',s)
        self.assertNotIn('AuthenticationRowVersion',predicate)
        self.assertNotIn('FailedLoginAttempts',predicate)
    def test_failure_keeps_expired_lock_reset_and_active_lock_guard(self):
        s=failure()
        self.assertIn('LockedUntil<=SYSDATETIME() THEN 1',s)
        self.assertIn('ISNULL(IsActive,1)=1',s)
        self.assertIn('AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))',s)
        self.assertNotIn('SET PasswordHash',s)
    def test_both_failure_callers_supply_the_checked_account(self):
        s=source('Services/AuthService.cs')
        self.assertIn('RegisterAuthenticationFailureAsync(user, cancellationToken)',s)
        self.assertIn('RegisterAuthenticationFailureAsync(freshUser)',s)
        self.assertNotIn('RegisterAuthenticationFailureAsync(username',s)
    def test_failed_signature_clears_missing_inactive_or_locked_session(self):
        s=source('Services/AuthService.cs')
        self.assertIn('updatedUser == null || !updatedUser.IsActive || IsLockedNow(updatedUser)',s)
    def test_failure_cancellation_reaches_write(self):
        s=section(source('Repositories/UserRepository.cs'),'public async Task<int> RegisterAuthenticationFailureAsync','public async Task<string> GetRoleAsync')
        self.assertIn('cancellationToken: cancellationToken',s)
    def test_success_still_one_atomic_versioned_update(self):
        s=section(source('Services/AuthenticationCommitContract.cs'),'internal const string Sql','";')
        self.assertEqual(1,s.count('UPDATE dbo.Users'))
        for token in ['AuthenticationRowVersion=@ExpectedVersion','PasswordHashNew = CASE WHEN @Upgrade=1','PasswordSalt = CASE WHEN @Upgrade=1']: self.assertIn(token,s)
    def test_water_display_preserves_full_decimal_precision(self):
        s=section(source('ResultsEntry.xaml.cs'),'private string FormatResultForDisplay','private string CalculatePassFail')
        self.assertIn('WaterResultValueContract.FormatNumeric(numeric)',s)
        self.assertNotIn('ToString("0.####"',s)
        self.assertIn('"0.'+'#'*28+'"',source('Services/WaterResultValueContract.cs'))
    def test_water_precision_checked_before_signature_and_at_write(self):
        s=section(source('ResultsEntry.xaml.cs'),'private async void BtnSaveResults_Click','private async void BtnSubmitForReview_Click')
        self.assertLess(s.index('WaterResultValueContract.IsExactlyRepresentable'),s.index('signatureWindow.ShowDialog'))
        w=section(source('ResultsEntry.xaml.cs'),'private decimal GetPersistedResultValue','private string FormatResultForDisplay')
        self.assertIn('WaterResultValueContract.IsExactlyRepresentable(normalizedResult)',w)
        self.assertIn('GetPersistedResultValue(authoritative, normalized)',source('ResultsEntry.xaml.Part3.cs'))
    def test_notes_only_does_not_encode_old_value_as_decimal_parameter(self):
        s=source('ResultsEntry.xaml.Part3.cs')
        self.assertIn('Value = resultChanged ? resultValue : DBNull.Value',s)
        self.assertIn(': "Remarks=@remarks"',s)
    def test_water_roundtrip_compares_every_written_value(self):
        s=source('ResultsEntry.xaml.Part3.cs')
        self.assertIn('expectedWrittenValues.Add(edited.SampleTestID, expectedValue)',s)
        self.assertIn('foreach (KeyValuePair<int, decimal> expected in expectedWrittenValues)',s)
        self.assertIn('WaterResultValueContract.StoredValueMatches(expected.Value, row["ResultValue"])',s)
        self.assertLess(s.index('StoredValueMatches'),s.index('return anyResultChanged'))
    def test_water_stored_comparator_rejects_null_and_numeric_difference(self):
        s=source('Services/WaterResultValueContract.cs')
        self.assertIn('stored != null && stored != DBNull.Value',s)
        self.assertIn('actual == expected',s)
        self.assertIn('NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint',s)
    def test_water_summary_uses_raw_saved_evidence(self):
        s=source('ResultsEntry.xaml.Part3.cs').split('private (int Completed',1)[1]
        self.assertIn('CalculatePassFail(WaterItemFromEvidence(row))',s)
        self.assertNotIn('FormatResultForDisplay',s)
        self.assertIn('if (IsRemovedTest(',s)
    def test_appearance_canonical_positive_still_roundtrips(self):
        s=source('ResultsEntry.xaml.cs')
        self.assertIn('raw.Equals("Not Clear / Colored", StringComparison.OrdinalIgnoreCase)',s)
        self.assertIn('"Clear and Colorless" : "Not Clear / Colored"',s)
    def test_em_storage_and_decision_verified_before_parent_completion(self):
        s=source('EMResultsEntry.xaml.Part3.cs')
        self.assertIn('row["ResultCFU"], row["Status"], row["ResultCalculationVersion"]',s)
        self.assertLess(s.index('StoredEvidenceMatches'),s.index('UPDATE dbo.EM_Events'))
    def test_em_precision_contract_still_twelve_places(self):
        s=source('Services/EmResultCalculator.cs')
        self.assertIn('const byte Precision = 28',s)
        self.assertIn('const byte Scale = 12',s)
        self.assertIn('numerator > action.Value * denominator',s)
        self.assertIn('actualVersion == CalculationVersion',s)
    def test_no_unused_legacy_em_service_or_old_probe_project(self):
        self.assertFalse((ROOT/'Services/EmResultEvidenceService.cs').exists())
        self.assertFalse((ROOT/'tests/PharmaLIMS.ResultIntegrityProbes').exists())
    def test_ported_behavior_suite_is_executed_by_existing_harness(self):
        s=source('tests/PharmaLIMS.ReviewRegression/Program.cs')
        self.assertIn('RunMergedRegressionCases();',s)
        s=source('tests/PharmaLIMS.ReviewRegression/MergedRegressionCases.cs')
        for token in ['WATER_DISPLAY_PRESERVES_LEGACY_PRECISION','WRONG_TEXT_NOT_REPLACED_BY_NAME','STRICT_STRUCTURED_CONFLICT','StoredEvidenceMatches']: self.assertIn(token,s)
    def test_sql_failure_tests_use_real_contract_and_disposable_guard(self):
        s=source('tests/PharmaLIMS.DatabaseIntegration/Merge262Integration.cs')
        for token in ['AuthenticationCommitContract.FailureSql','StartsWith("PharmaLIMS_Integration_"','Task.WhenAll','new SqlConnection(connectionString)','WaterResultValueContract.StoredValueMatches']: self.assertIn(token,s)
        self.assertIn('await Merge262Integration.VerifyAsync(connectionString)',source('tests/PharmaLIMS.DatabaseIntegration/ReviewRemediationIntegration.cs'))
    def test_both_test_projects_link_active_water_value_contract(self):
        for name in ['PharmaLIMS.ReviewRegression','PharmaLIMS.DatabaseIntegration']:
            self.assertIn('../../Services/WaterResultValueContract.cs',source(f'tests/{name}/{name}.csproj'))
    def test_closeout_build_and_ci_gates_are_wired(self):
        integration = source('tests/PharmaLIMS.DatabaseIntegration/Program.cs')
        self.assertIn('using PharmaLIMS.Infrastructure;', integration)
        solution = source('pharmaLIMS.slnx')
        self.assertIn('tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj', solution)
        ci = source('.github/workflows/ci.yml')
        runner=(ROOT/'scripts/Invoke-ReleaseValidation.ps1').read_text(encoding='utf-8-sig')
        self.assertIn('dotnet restore tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj', runner)
        self.assertIn('Invoke-ReleaseValidation.ps1 -RunDatabaseIntegration -RunRuntimeSmoke', ci)
        self.assertIn('dotnet build tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj --configuration Release --no-restore', runner)
        self.assertIn('dotnet run --project tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj --configuration Release --no-build --no-restore', runner)
    def test_closeout_controlled_time_paths_do_not_silently_use_workstation_time(self):
        trend = section(source('ReportsTrends.xaml.cs'),'private static DateTime GetAuthoritativeTrendTime','private string GenerateReportNumber')
        self.assertNotIn('DateTime.UtcNow', trend)
        self.assertIn('controlled trend report/export was not generated', trend)
        ext = section(source('Services/ExternalTrendImportService.cs'),'private static DateTimeOffset GetAuthoritativeDatabaseTimestamp','private static void ValidateModule')
        self.assertNotIn('DateTimeOffset.UtcNow', ext)
        self.assertIn('controlled external-trend action was not recorded', ext)
        new_sample = section(source('NewSampleDialog.xaml.cs'),'private void CalculateIncubationEnd','private')
        self.assertNotIn('DateTime.Today', new_sample)
        self.assertIn('Enter actual sampling date/time', new_sample)
    def test_closeout_prm_quality_event_action_row_is_responsive(self):
        xaml = source('PRMQualityEventInvestigation.xaml')
        self.assertNotIn('MinWidth="1250"', xaml)
        self.assertNotIn('Width="1040"', xaml)
        self.assertIn('<ColumnDefinition Width="*"/>', xaml)
    def test_merged_version_identity_agrees(self):
        version=re.search(r'<Version>([^<]+)</Version>',source('PharmaLIMS.csproj')).group(1)
        self.assertEqual('2026.9.18.294',version)
        self.assertIn(f'AssemblyFileVersion("{version}")',source('AssemblyInfo.cs'))
        self.assertEqual(version,json.loads(source(f'SBOM_{version}.cdx.json'))['metadata']['component']['version'])
if __name__=='__main__': unittest.main()
