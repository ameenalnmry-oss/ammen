using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PharmaLIMS
{
    public partial class AISystemReview : Window
    {
        private readonly ObservableCollection<ReviewFinding> _findings = new();
        private ICollectionView? _findingsView;
        private DateTime? _lastReviewAt;

        public AISystemReview()
        {
            InitializeComponent();
            gridFindings.ItemsSource = _findings;
            _findingsView = CollectionViewSource.GetDefaultView(_findings);
            lblReviewContext.Text = $"{AppConfig.EnvironmentName} | {AppConfig.DatabaseName} | Read-only";
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!HasReviewPermission())
            {
                MessageBox.Show(
                    "AI System Review is restricted to administrators and system-settings managers.",
                    "Access Denied",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Close();
                return;
            }

            await RunReviewAsync();
        }

        private async void BtnRunReview_Click(object sender, RoutedEventArgs e)
        {
            await RunReviewAsync();
        }

        private async Task RunReviewAsync()
        {
            btnRunReview.IsEnabled = false;
            btnCopyPlan.IsEnabled = false;
            btnExport.IsEnabled = false;
            btnRunReview.Content = "Reviewing...";
            lblStatus.Text = "Analyzing configuration, database controls, operational state, logs, and source package...";
            _findings.Clear();
            UpdateSummary();

            try
            {
                IReadOnlyList<ReviewFinding> findings = await Task.Run(AnalyzeSystem);

                foreach (ReviewFinding finding in findings
                    .OrderBy(item => SeverityRank(item.Severity))
                    .ThenBy(item => item.Area, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Finding, StringComparer.OrdinalIgnoreCase))
                {
                    _findings.Add(finding);
                }

                _lastReviewAt = DateTime.Now;
                lblLastReview.Text = "Last review: " + _lastReviewAt.Value.ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture);
                lblStatus.Text = $"Review completed. {_findings.Count} finding(s) and observation(s) were generated.";
                btnCopyPlan.IsEnabled = _findings.Count > 0;
                btnExport.IsEnabled = _findings.Count > 0;
                UpdateSummary();
                _findingsView?.Refresh();

                ApplicationLogger.Information(
                    $"AI System Review completed by '{Login.CurrentUser}'. Findings={_findings.Count}, " +
                    $"Critical={CountSeverity("Critical")}, High={CountSeverity("High")}, Medium={CountSeverity("Medium")}.");
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("AI System Review failed.", ex);
                lblStatus.Text = "The review could not be completed. Technical details were written to the application log.";
                MessageBox.Show(
                    "AI System Review could not complete. Check the application log for details.",
                    "Review Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                btnRunReview.Content = "Run AI Review";
                btnRunReview.IsEnabled = true;
            }
        }

        private static IReadOnlyList<ReviewFinding> AnalyzeSystem()
        {
            var results = new List<ReviewFinding>();

            AnalyzeConfiguration(results);
            AnalyzeDatabase(results);
            AnalyzeOperationalState(results);
            AnalyzeApplicationLogs(results);
            AnalyzeSourcePackage(results);

            if (!results.Any(item => item.Severity != "Info"))
            {
                Add(results, "Info", "Overall",
                    "No blocking condition was detected by the local review rules.",
                    "Configuration, database connectivity, protected records, operational gates, logs, and available source controls were checked.",
                    "Complete the controlled Windows build, database verification, and end-to-end user acceptance tests before release.");
            }

            return results;
        }

        private static void AnalyzeConfiguration(List<ReviewFinding> results)
        {
            SqlConnectionStringBuilder builder;

            try
            {
                builder = new SqlConnectionStringBuilder(AppConfig.ConnectionString);
            }
            catch (Exception ex)
            {
                Add(results, "Critical", "Configuration",
                    "The effective SQL Server connection string is invalid.",
                    Infrastructure.UserFacingError.SafeMessage(ex, "Configuration validation"),
                    "Correct the controlled environment configuration before the application is used.");
                return;
            }

            string encrypt = builder.ContainsKey("Encrypt")
                ? Convert.ToString(builder["Encrypt"], CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;

            bool encryptionEnabled =
                encrypt.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                encrypt.Equals("Mandatory", StringComparison.OrdinalIgnoreCase) ||
                encrypt.Equals("Strict", StringComparison.OrdinalIgnoreCase);

            if (!encryptionEnabled)
            {
                Add(results, AppConfig.IsProduction ? "Critical" : "High", "Configuration",
                    "Database transport encryption is not enabled.",
                    $"Environment={AppConfig.EnvironmentName}; Encrypt={encrypt}.",
                    "Enable mandatory SQL Server encryption in the effective environment configuration.");
            }

            if (AppConfig.IsProduction && builder.TrustServerCertificate)
            {
                Add(results, "High", "Configuration",
                    "Production trusts the SQL Server certificate without validation.",
                    "TrustServerCertificate=True in the effective connection string.",
                    "Install a trusted SQL Server certificate and set TrustServerCertificate=False.");
            }

            if (AppConfig.SessionTimeoutMinutes <= 0)
            {
                Add(results, "High", "Identity",
                    "Automatic session timeout is disabled.",
                    "Runtime SessionTimeoutMinutes is zero or invalid.",
                    "Configure a positive inactivity timeout and require re-authentication after expiry.");
            }

            if (AppConfig.DevelopmentAdminFullPermissions)
            {
                Add(results, "Info", "Identity",
                    "Development Admin full permissions are enabled.",
                    "The override is active only because the effective environment is not Production.",
                    "Keep this setting disabled in every Production configuration and validate workflows with separate Analyst, Reviewer, and QA accounts.");
            }

            if (AppConfig.ApplyStartupDatabaseUpdates)
            {
                Add(results, AppConfig.IsProduction ? "Critical" : "Medium", "Deployment",
                    "Database updates are configured to run automatically at startup.",
                    $"Environment={AppConfig.EnvironmentName}.",
                    "Use a reviewed migration package and controlled deployment record instead of startup schema changes.");
            }
        }

        private static void AnalyzeDatabase(List<ReviewFinding> results)
        {
            try
            {
                string databaseName = Convert.ToString(
                    DatabaseHelper.ExecuteScalar("SELECT DB_NAME();"),
                    CultureInfo.InvariantCulture) ?? string.Empty;

                if (!databaseName.Equals(AppConfig.DatabaseName, StringComparison.OrdinalIgnoreCase))
                {
                    Add(results, "Critical", "Database",
                        "The connected database does not match the configured PharmaLIMS database.",
                        $"Configured={AppConfig.DatabaseName}; Connected={databaseName}.",
                        "Stop testing and correct the connection configuration before entering or approving data.");
                }
                else
                {
                    Add(results, "Info", "Database",
                        "Database connectivity check passed.",
                        $"Connected to {AppConfig.SqlServerName} / {databaseName}.",
                        "Continue monitoring connection failures and SQL Server availability.");
                }
            }
            catch (Exception ex)
            {
                Add(results, "Critical", "Database",
                    "The review could not connect to the configured PharmaLIMS database.",
                    ex.GetBaseException().Message,
                    "Verify SQL Server service, network access, credentials, encryption, and database name.");
                return;
            }

            string missingCoreTables = ScalarText(@"
SELECT STRING_AGG(required.TableName, ', ')
FROM (VALUES
    (N'Users'), (N'Samples'), (N'SampleTests'), (N'Tests'),
    (N'WaterSamplingPoints'), (N'EM_Events'), (N'EM_EventPlates'),
    (N'MediaPreparations'), (N'MediaQualifications'),
    (N'PRM_Samples'), (N'PRM_SampleTests'), (N'Certificates')
) required(TableName)
WHERE OBJECT_ID(N'dbo.' + required.TableName, N'U') IS NULL;");

            if (!string.IsNullOrWhiteSpace(missingCoreTables))
            {
                Add(results, "Critical", "Database Schema",
                    "Required operational tables are missing.",
                    missingCoreTables,
                    "Apply the controlled migrations in manifest order and verify the database before startup.");
            }

            string missingComplianceTables = ScalarText(@"
SELECT STRING_AGG(required.TableName, ', ')
FROM (VALUES
    (N'AuditTrail'), (N'ElectronicSignatures'), (N'QualityEvents'),
    (N'CertificateDocumentSnapshots'), (N'CertificateLifecycleAudit'),
    (N'EM_EventSignatures'), (N'PRM_ElectronicSignatures'),
    (N'PRM_CertificateSnapshots'), (N'CultureMediaSignatures')
) required(TableName)
WHERE OBJECT_ID(N'dbo.' + required.TableName, N'U') IS NULL;");

            if (!string.IsNullOrWhiteSpace(missingComplianceTables))
            {
                Add(results, "Critical", "Compliance",
                    "Required audit, signature, investigation, or snapshot tables are missing.",
                    missingComplianceTables,
                    "Do not approve results or issue reports until the controlled compliance schema is installed and verified.");
            }

            int externalTrendSchemaState = ScalarInt(@"
SELECT
    CASE
        WHEN OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') IS NULL
         AND OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NULL THEN 0
        WHEN OBJECT_ID(N'dbo.ExternalTrendImportBatches', N'U') IS NOT NULL
         AND OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NOT NULL
         AND COL_LENGTH(N'dbo.ExternalTrendImportRows', N'AreaClassification') IS NOT NULL
         AND OBJECT_ID(N'dbo.TR_ExternalTrendImportRows_Immutable', N'TR') IS NOT NULL
         AND OBJECT_ID(N'dbo.TR_ExternalTrendImportBatches_NoDelete', N'TR') IS NOT NULL
         AND OBJECT_ID(N'dbo.TR_ExternalTrendImportBatches_ProtectSource', N'TR') IS NOT NULL THEN 2
        ELSE 1
    END;");

            if (externalTrendSchemaState == 0)
            {
                Add(results, "Info", "External Trends",
                    "Controlled external trend import is not installed in the connected database.",
                    "Migrations 20260810_001 and 20260811_001 have not been applied.",
                    "Apply the controlled migration before using Import External Data; native LIMS reporting is unaffected.");
            }
            else if (externalTrendSchemaState == 1)
            {
                Add(results, "Critical", "External Trends",
                    "The external trend import schema is incomplete or lacks immutable-record protection.",
                    "One or more import tables or protection triggers are missing.",
                    "Stop external imports and apply/verify migrations 20260810_001 and 20260811_001 under change control.");
            }
            else
            {
                int pendingExternalImports = ScalarInt(@"
SELECT COUNT(*)
FROM dbo.ExternalTrendImportBatches
WHERE Status = N'Pending Approval';");
                if (pendingExternalImports > 0)
                {
                    Add(results, "Info", "External Trends",
                        "Controlled external trend imports are awaiting independent approval.",
                        $"Pending approval batches={pendingExternalImports}.",
                        "An authorized user other than the importer should review the source, validation findings, and electronic signature action.");
                }
            }

            DataTable protection = DatabaseHelper.ExecuteQuery(ComplianceRecordProtectionContract.QuerySql);
            var protectionFailures = protection.Rows.Cast<DataRow>()
                .Where(row =>
                    Convert.ToInt32(row["TableExists"], CultureInfo.InvariantCulture) != 1 ||
                    Convert.ToInt32(row["HasExpectedEnabledTrigger"], CultureInfo.InvariantCulture) != 1 ||
                    Convert.ToInt32(row["ProtectsUpdate"], CultureInfo.InvariantCulture) != 1 ||
                    Convert.ToInt32(row["ProtectsDelete"], CultureInfo.InvariantCulture) != 1 ||
                    Convert.ToInt32(row["HasCanonicalAppendOnlyBody"], CultureInfo.InvariantCulture) != 1)
                .Select(row => Convert.ToString(row["TableName"], CultureInfo.InvariantCulture) ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();

            if (protectionFailures.Length > 0)
            {
                Add(results, "High", "Data Integrity",
                    "One or more controlled compliance tables or append-only protections fail the authoritative contract.",
                    "Failed contract entries: " + string.Join(", ", protectionFailures),
                    "Run Full System Preflight and restore the controlled schema/trigger through Database Maintenance/change control before operational use.");
            }

            int duplicateSampleNumbers = ScalarInt(@"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Samples', N'SampleNumber') IS NOT NULL
    SELECT @Result = COUNT(*) FROM (SELECT SampleNumber FROM dbo.Samples WHERE SampleNumber IS NOT NULL GROUP BY SampleNumber HAVING COUNT(*) > 1) duplicate_numbers;
SELECT @Result;");

            if (duplicateSampleNumbers > 0)
            {
                Add(results, "Critical", "Numbering",
                    "Duplicate water/general sample numbers were detected.",
                    $"Duplicate number groups={duplicateSampleNumbers}.",
                    "Quarantine affected records, reconcile sequence state, and enforce a unique index after QA assessment.");
            }

            int missingWaterSnapshots = ScalarInt(@"
DECLARE @Result INT = -1;
IF OBJECT_ID(N'dbo.Certificates', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.CertificateDocumentSnapshots', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Certificates', N'CertificateID') IS NOT NULL
   AND COL_LENGTH(N'dbo.CertificateDocumentSnapshots', N'CertificateID') IS NOT NULL
BEGIN
    DECLARE @Sql NVARCHAR(MAX) = N'
        SELECT @Value = COUNT(*)
        FROM dbo.Certificates certificate
        LEFT JOIN dbo.CertificateDocumentSnapshots snapshot ON snapshot.CertificateID = certificate.CertificateID
        WHERE snapshot.CertificateID IS NULL';
    IF COL_LENGTH(N'dbo.Certificates', N'IsCancelled') IS NOT NULL
        SET @Sql += N' AND ISNULL(certificate.IsCancelled, 0) = 0';
    EXEC sys.sp_executesql @Sql, N'@Value INT OUTPUT', @Value = @Result OUTPUT;
END
SELECT @Result;");

            int missingWaterSnapshotsAfterControl = ScalarInt(@"
DECLARE @Result INT = -1;
DECLARE @Cutover DATETIME2(0) = NULL;
IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.LIMS_SchemaVersions', N'AppliedAt') IS NOT NULL
    SELECT TOP (1) @Cutover = AppliedAt FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260722_003' ORDER BY AppliedAt;
IF @Cutover IS NOT NULL
   AND OBJECT_ID(N'dbo.Certificates', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.CertificateDocumentSnapshots', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Certificates', N'CertificateID') IS NOT NULL
   AND COL_LENGTH(N'dbo.Certificates', N'IssueDate') IS NOT NULL
   AND COL_LENGTH(N'dbo.CertificateDocumentSnapshots', N'CertificateID') IS NOT NULL
BEGIN
    DECLARE @Sql NVARCHAR(MAX) = N'
        SELECT @Value = COUNT(*)
        FROM dbo.Certificates certificate
        LEFT JOIN dbo.CertificateDocumentSnapshots snapshot ON snapshot.CertificateID = certificate.CertificateID
        WHERE snapshot.CertificateID IS NULL
          AND certificate.IssueDate >= @SnapshotCutover';
    IF COL_LENGTH(N'dbo.Certificates', N'IsCancelled') IS NOT NULL
        SET @Sql += N' AND ISNULL(certificate.IsCancelled, 0) = 0';
    EXEC sys.sp_executesql @Sql, N'@SnapshotCutover DATETIME2(0), @Value INT OUTPUT', @SnapshotCutover=@Cutover, @Value=@Result OUTPUT;
END
SELECT @Result;");

            if (missingWaterSnapshots > 0)
            {
                int legacyWaterSnapshots = missingWaterSnapshotsAfterControl >= 0
                    ? Math.Max(0, missingWaterSnapshots - missingWaterSnapshotsAfterControl)
                    : -1;

                if (missingWaterSnapshotsAfterControl > 0)
                {
                    Add(results, "High", "Certificates",
                        "Certificates issued after snapshot control activation are missing immutable document snapshots.",
                        $"Post-control certificates without snapshots={missingWaterSnapshotsAfterControl}; total active snapshot gaps={missingWaterSnapshots}.",
                        "Treat this as a current document-control defect: investigate issuance history, block uncontrolled originals, and use controlled cancellation/reissue where a compliant electronic original is required.");
                }

                if (legacyWaterSnapshots > 0)
                {
                    string legacySeverity = AppConfig.IsDevelopment ? "Info" : "Medium";
                    string legacyFinding = AppConfig.IsDevelopment
                        ? "Legacy Development certificate records predate immutable-snapshot control; they are not evidence of a current issuance defect."
                        : "Legacy active certificates predate the recorded immutable-snapshot control activation.";
                    string legacyRecommendation = AppConfig.IsDevelopment
                        ? "If these records are disposable Development test data, do not fabricate or reconcile historical snapshots; reset or replace the Development test dataset when convenient and do not migrate these records into Production. If regulated historical records were intentionally loaded into Development, use the signed Legacy Certificate Evidence Reconciliation workflow before relying on them as controlled originals."
                        : "For legacy records, do not fabricate snapshots retrospectively. Use the signed Legacy Certificate Evidence Reconciliation workflow: retain only when QA has documented meaningful controlled historical evidence; when the signed disposition is CONTROLLED_REISSUE_REQUIRED, complete the linked cancellation/reissue lifecycle until an active replacement with native immutable evidence exists.";

                    Add(results, legacySeverity, "Certificates",
                        legacyFinding,
                        $"Legacy certificates without snapshots={legacyWaterSnapshots}; post-control certificates without snapshots={Math.Max(0, missingWaterSnapshotsAfterControl)}; total active snapshot gaps={missingWaterSnapshots}.",
                        legacyRecommendation);

                    if (AppConfig.IsDevelopment && missingWaterSnapshotsAfterControl == 0)
                    {
                        Add(results, "Info", "Certificates",
                            "Current certificate snapshot control shows no post-control gap in this Development database.",
                            $"Post-control certificates without snapshots=0; legacy pre-control records={legacyWaterSnapshots}.",
                            "Continue UAT with newly issued Development records and verify each new certificate has its native immutable snapshot. Legacy test records do not need retrospective snapshot fabrication.");
                    }
                }
                else if (missingWaterSnapshotsAfterControl < 0)
                {
                    Add(results, "High", "Certificates",
                        "Active water/general certificates without immutable document snapshots were detected, but the snapshot-control cutover could not be resolved from the migration ledger.",
                        $"Certificates without snapshots={missingWaterSnapshots}.",
                        "Do not treat the affected certificates as controlled electronic originals; verify migration 20260722_003 evidence and investigate/reissue through the approved workflow where justified.");
                }
            }

            int missingPrmSnapshots = ScalarInt(@"
DECLARE @Result INT = -1;
IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.PRM_CertificateSnapshots', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PRM_Certificates', N'CertificateID') IS NOT NULL
   AND COL_LENGTH(N'dbo.PRM_CertificateSnapshots', N'CertificateID') IS NOT NULL
BEGIN
    DECLARE @Sql NVARCHAR(MAX) = N'
        SELECT @Value = COUNT(*)
        FROM dbo.PRM_Certificates certificate
        LEFT JOIN dbo.PRM_CertificateSnapshots snapshot ON snapshot.CertificateID = certificate.CertificateID
        WHERE snapshot.CertificateID IS NULL';
    IF COL_LENGTH(N'dbo.PRM_Certificates', N'IsCancelled') IS NOT NULL
        SET @Sql += N' AND ISNULL(certificate.IsCancelled, 0) = 0';
    EXEC sys.sp_executesql @Sql, N'@Value INT OUTPUT', @Value = @Result OUTPUT;
END
SELECT @Result;");

            int missingPrmSnapshotsAfterControl = ScalarInt(@"
DECLARE @Result INT = -1;
DECLARE @Cutover DATETIME2(0) = NULL;
IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.LIMS_SchemaVersions', N'AppliedAt') IS NOT NULL
    SELECT TOP (1) @Cutover = AppliedAt FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260722_003' ORDER BY AppliedAt;
IF @Cutover IS NOT NULL
   AND OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.PRM_CertificateSnapshots', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PRM_Certificates', N'CertificateID') IS NOT NULL
   AND COL_LENGTH(N'dbo.PRM_Certificates', N'IssueDate') IS NOT NULL
   AND COL_LENGTH(N'dbo.PRM_CertificateSnapshots', N'CertificateID') IS NOT NULL
BEGIN
    DECLARE @Sql NVARCHAR(MAX) = N'
        SELECT @Value = COUNT(*)
        FROM dbo.PRM_Certificates certificate
        LEFT JOIN dbo.PRM_CertificateSnapshots snapshot ON snapshot.CertificateID = certificate.CertificateID
        WHERE snapshot.CertificateID IS NULL
          AND certificate.IssueDate >= @SnapshotCutover';
    IF COL_LENGTH(N'dbo.PRM_Certificates', N'IsCancelled') IS NOT NULL
        SET @Sql += N' AND ISNULL(certificate.IsCancelled, 0) = 0';
    EXEC sys.sp_executesql @Sql, N'@SnapshotCutover DATETIME2(0), @Value INT OUTPUT', @SnapshotCutover=@Cutover, @Value=@Result OUTPUT;
END
SELECT @Result;");

            if (missingPrmSnapshots > 0)
            {
                int legacyPrmSnapshots = missingPrmSnapshotsAfterControl >= 0
                    ? Math.Max(0, missingPrmSnapshots - missingPrmSnapshotsAfterControl)
                    : -1;

                if (missingPrmSnapshotsAfterControl > 0)
                {
                    Add(results, "High", "PRM Reports",
                        "PRM certificates/reports issued after snapshot control activation are missing immutable snapshots.",
                        $"Post-control PRM documents without snapshots={missingPrmSnapshotsAfterControl}; total active snapshot gaps={missingPrmSnapshots}.",
                        "Investigate the current issuance path and use controlled cancellation/reissue when a compliant electronic original is required. Do not synthesize retrospective snapshots.");
                }

                if (legacyPrmSnapshots > 0)
                {
                    string legacySeverity = AppConfig.IsDevelopment ? "Info" : "Medium";
                    string legacyFinding = AppConfig.IsDevelopment
                        ? "Legacy Development PRM documents predate immutable-snapshot control; they are not evidence of a current issuance defect."
                        : "Legacy PRM documents predate the recorded immutable-snapshot control activation.";
                    string legacyRecommendation = AppConfig.IsDevelopment
                        ? "If these records are disposable Development test data, do not fabricate or reconcile retrospective snapshots; reset or replace the Development test dataset when convenient and do not migrate these records into Production. If regulated historical PRM records were intentionally loaded into Development, use signed legacy-evidence reconciliation and controlled reissue where required."
                        : "For legacy records, do not fabricate snapshots retrospectively. Use the signed Legacy Certificate Evidence Reconciliation workflow: retain only when QA has documented meaningful controlled historical evidence; when the signed disposition is CONTROLLED_REISSUE_REQUIRED, complete PRM reissue until the linked active replacement has a valid report hash and native immutable snapshot.";

                    Add(results, legacySeverity, "PRM Reports",
                        legacyFinding,
                        $"Legacy PRM documents without snapshots={legacyPrmSnapshots}; post-control PRM documents without snapshots={Math.Max(0, missingPrmSnapshotsAfterControl)}; total active snapshot gaps={missingPrmSnapshots}.",
                        legacyRecommendation);

                    if (AppConfig.IsDevelopment && missingPrmSnapshotsAfterControl == 0)
                    {
                        Add(results, "Info", "PRM Reports",
                            "Current PRM immutable-snapshot control shows no post-control gap in this Development database.",
                            $"Post-control PRM documents without snapshots=0; legacy pre-control records={legacyPrmSnapshots}.",
                            "Continue UAT with newly issued PRM documents and verify each new controlled report/certificate has its native immutable snapshot and valid report hash. Legacy test records do not need retrospective evidence fabrication.");
                    }
                }
                else if (missingPrmSnapshotsAfterControl < 0)
                {
                    Add(results, "High", "PRM Reports",
                        "Active PRM certificates/reports without immutable snapshots were detected, but the snapshot-control cutover could not be resolved from the migration ledger.",
                        $"PRM documents without snapshots={missingPrmSnapshots}.",
                        "Verify migration 20260722_003 evidence and investigate legacy/current records before relying on them as controlled electronic originals.");
                }
            }
        }

        private static void AnalyzeOperationalState(List<ReviewFinding> results)
        {
            int openQualityEvents = ScalarInt(@"
DECLARE @Result INT = -1;
IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.QualityEvents', N'CurrentStatus') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT @Value = COUNT(*) FROM dbo.QualityEvents WHERE ISNULL(CurrentStatus, ''Open'') NOT IN (''Closed'', ''QA Closed'', ''Cancelled'', ''Rejected Closed'');', N'@Value INT OUTPUT', @Value=@Result OUTPUT;
    ELSE IF COL_LENGTH(N'dbo.QualityEvents', N'Status') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT @Value = COUNT(*) FROM dbo.QualityEvents WHERE ISNULL(Status, ''Open'') NOT IN (''Closed'', ''QA Closed'', ''Cancelled'', ''Rejected Closed'');', N'@Value INT OUTPUT', @Value=@Result OUTPUT;
END
SELECT @Result;");

            if (openQualityEvents > 0)
            {
                Add(results, "Info", "Quality Events",
                    "Open Quality Events require operational follow-up.",
                    $"Open events={openQualityEvents}.",
                    "Review due dates, investigation completeness, root cause, CAPA, QA disposition, and certificate blocks.");
            }

            int pendingReview = ScalarInt(@"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Samples', N'Status') IS NOT NULL
    SELECT @Result += COUNT(*) FROM dbo.Samples WHERE Status IN (N'Results Entered', N'Under Review');
IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.EM_Events', N'WorkflowStatus') IS NOT NULL
    SELECT @Result += COUNT(*) FROM dbo.EM_Events WHERE WorkflowStatus = N'Under Review';
IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.PRM_Samples', N'SampleStatus') IS NOT NULL
    SELECT @Result += COUNT(*) FROM dbo.PRM_Samples WHERE SampleStatus IN (N'Results Entered', N'Under Review');
SELECT @Result;");

            if (pendingReview > 0)
            {
                Add(results, "Info", "Workload",
                    "Records are awaiting technical review.",
                    $"Pending review records={pendingReview}.",
                    "Assign independent reviewers and monitor laboratory turnaround time.");
            }

            int pendingApproval = ScalarInt(@"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Samples', N'Status') IS NOT NULL
    SELECT @Result += COUNT(*) FROM dbo.Samples WHERE Status = N'Reviewed';
IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.EM_Events', N'WorkflowStatus') IS NOT NULL
    SELECT @Result += COUNT(*) FROM dbo.EM_Events WHERE WorkflowStatus = N'Reviewed';
IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.PRM_Samples', N'SampleStatus') IS NOT NULL
    SELECT @Result += COUNT(*) FROM dbo.PRM_Samples WHERE SampleStatus = N'Reviewed';
SELECT @Result;");

            if (pendingApproval > 0)
            {
                Add(results, "Info", "Workload",
                    "Reviewed records are awaiting QA approval.",
                    $"Pending approval records={pendingApproval}.",
                    "Confirm Quality Event and OOS disposition before QA approval and document issuance.");
            }
        }

        private static void AnalyzeApplicationLogs(List<ReviewFinding> results)
        {
            try
            {
                if (!Directory.Exists(ApplicationLogger.LogDirectory))
                    return;

                DateTime threshold = DateTime.Now.AddDays(-7);
                List<string> recentLogFiles = Directory
                    .EnumerateFiles(ApplicationLogger.LogDirectory, "PharmaLIMS-*.log")
                    .Select(path => new FileInfo(path))
                    .Where(file => file.LastWriteTime >= threshold)
                    .OrderBy(file => file.LastWriteTime)
                    .Select(file => file.FullName)
                    .ToList();

                int historicalErrors = 0;
                int historicalWarnings = 0;
                foreach (string path in recentLogFiles)
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        if (line.Contains("[ERROR]", StringComparison.Ordinal))
                            historicalErrors++;
                        else if (line.Contains("[WARN]", StringComparison.Ordinal))
                            historicalWarnings++;
                    }
                }

                int currentSessionErrors = 0;
                int currentSessionWarnings = 0;
                var currentSessionErrorHeaders = new List<string>();

                if (recentLogFiles.Count > 0)
                {
                    string[] latestLines = File.ReadAllLines(recentLogFiles[^1]);
                    int sessionStart = Array.FindLastIndex(
                        latestLines,
                        line => line.Contains("[INFO] Starting PharmaLIMS.", StringComparison.Ordinal));
                    if (sessionStart < 0)
                        sessionStart = 0;

                    for (int index = sessionStart; index < latestLines.Length; index++)
                    {
                        string line = latestLines[index];
                        if (line.Contains("[ERROR]", StringComparison.Ordinal))
                        {
                            currentSessionErrors++;
                            string header = ExtractLogHeader(line, "[ERROR]");
                            if (!string.IsNullOrWhiteSpace(header))
                                currentSessionErrorHeaders.Add(header);
                        }
                        else if (line.Contains("[WARN]", StringComparison.Ordinal))
                        {
                            currentSessionWarnings++;
                        }
                    }
                }

                if (currentSessionErrors > 0)
                {
                    string groupedErrors = SummarizeLogHeaders(currentSessionErrorHeaders);
                    string groupedEvidence = string.IsNullOrWhiteSpace(groupedErrors)
                        ? string.Empty
                        : " Current-session error groups: " + groupedErrors + ".";

                    Add(results, currentSessionErrors >= 5 ? "High" : "Medium", "Application Logs",
                        "The current PharmaLIMS session contains application errors that require review.",
                        $"Current session: errors={currentSessionErrors}, warnings={currentSessionWarnings}. " +
                        $"Historical 7-day total: errors={historicalErrors}, warnings={historicalWarnings}, log files={recentLogFiles.Count}." +
                        groupedEvidence,
                        "Use the listed current-session references/operations to group repeated root causes, reproduce safely, and verify each correction before release. Historical errors from earlier builds should not be treated as current-build failures unless they recur after the latest startup marker.");
                }
                else if (historicalErrors > 0)
                {
                    Add(results, "Info", "Application Logs",
                        "Historical application errors exist, but none were recorded after the latest PharmaLIMS startup marker.",
                        $"Current session: errors=0, warnings={currentSessionWarnings}. " +
                        $"Historical 7-day total: errors={historicalErrors}, warnings={historicalWarnings}, log files={recentLogFiles.Count}.",
                        "Retain the historical logs as development evidence and focus corrective work on errors that recur in the current build/session.");
                }
                else if (currentSessionWarnings > 0 || historicalWarnings > 0)
                {
                    Add(results, "Low", "Application Logs",
                        "Warnings were recorded without current-session application errors.",
                        $"Current session: warnings={currentSessionWarnings}. Historical 7-day warnings={historicalWarnings}.",
                        "Review repeated warnings and confirm they do not indicate schema drift, failed auditing, or unavailable dependencies.");
                }
            }
            catch (Exception ex)
            {
                Add(results, "Low", "Application Logs",
                    "The review could not read all recent application logs.",
                    Infrastructure.UserFacingError.SafeMessage(ex, "Application-log review"),
                    "Verify read access to the PharmaLIMS local log directory.");
            }
        }

        private static string ExtractLogHeader(string line, string levelMarker)
        {
            int marker = line.IndexOf(levelMarker, StringComparison.Ordinal);
            if (marker < 0)
                return string.Empty;

            string header = line[(marker + levelMarker.Length)..].Trim();
            if (header.Length > 220)
                header = header[..220] + "...";
            return header;
        }

        private static string SummarizeLogHeaders(IEnumerable<string> headers)
        {
            return string.Join(" | ", headers
                .Where(header => !string.IsNullOrWhiteSpace(header))
                .GroupBy(header => header, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(group => $"{group.Count()}x {group.Key}"));
        }


        private static void AnalyzeSourcePackage(List<ReviewFinding> results)
        {
            string? projectRoot = FindProjectRoot();
            if (projectRoot == null)
            {
                Add(results, "Info", "Source Review",
                    "Source-code checks were skipped because the running installation does not include the project source.",
                    "Runtime database, configuration, operational, and log checks were still completed.",
                    "Run the same review from a Development build located under the source project when code-maintainability checks are required.");
                return;
            }

            string legacyXaml = Path.Combine(projectRoot, "SampleRegistration.xaml");
            string legacyCode = Path.Combine(projectRoot, "SampleRegistration.xaml.cs");
            if (File.Exists(legacyXaml) || File.Exists(legacyCode))
            {
                Add(results, "Critical", "Workflow",
                    "The obsolete SampleRegistration workflow is present in the source package.",
                    "The legacy screen can bypass the controlled result-review-approval workflow.",
                    "Remove the obsolete XAML and code-behind files and keep registration in NewSampleDialog with results in the controlled result-entry modules.");
            }

            string[] generatedDirectories = { ".vs", "bin", "obj" };
            string presentGeneratedDirectories = string.Join(", ", generatedDirectories
                .Where(name => Directory.Exists(Path.Combine(projectRoot, name))));
            if (!string.IsNullOrWhiteSpace(presentGeneratedDirectories))
            {
                if (AppConfig.IsDevelopment)
                {
                    Add(results, "Info", "Development Workspace",
                        "Local IDE/build-output folders are present in the active Development workspace.",
                        presentGeneratedDirectories + ". Their presence beside the project during local build/debug is not evidence that they are included in the controlled delivery ZIP.",
                        "Keep .vs, bin and obj excluded from controlled delivery packages and source manifests; no release defect should be raised solely because these folders exist in the local Development workspace.");
                }
                else
                {
                    Add(results, "Low", "Delivery Package",
                        "Local IDE or build-output folders are present beside the source in a non-Development runtime.",
                        presentGeneratedDirectories,
                        "Exclude these folders from consultant, release, and GitHub delivery packages and verify the controlled source manifest before release.");
                }
            }

            string[] largeCodeFiles = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsGeneratedPath(path, projectRoot))
                .Where(path => new FileInfo(path).Length >= 150_000)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

            if (largeCodeFiles.Length > 0)
            {
                Add(results, "Medium", "Maintainability",
                    "Very large C# files increase validation and maintenance risk.",
                    string.Join(", ", largeCodeFiles),
                    "Refactor gradually by extracting database, validation, reporting, and workflow services without changing stable module behavior.");
            }

            int addWithValueCount = 0;
            foreach (string path in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsGeneratedPath(path, projectRoot)))
            {
                string source = File.ReadAllText(path);
                addWithValueCount += CountOccurrences(source, ".Parameters." + "AddWithValue(");
            }

            if (addWithValueCount > 0)
            {
                Add(results, "Medium", "Data Access",
                    "SQL parameters still rely heavily on AddWithValue.",
                    $"Occurrences={addWithValueCount}.",
                    "Replace critical date, decimal, identifier, result, signature, and numbering parameters with explicitly typed SqlParameter definitions.");
            }

            ValidateMigrationManifest(results, projectRoot);

            if (!File.Exists(Path.Combine(projectRoot, ".github", "workflows", "ci.yml")))
            {
                Add(results, "High", "GitHub / CI",
                    "The source package has no controlled GitHub build workflow.",
                    ".github/workflows/ci.yml was not found.",
                    "Add Windows .NET restore, validation, vulnerability scan, build, and controlled publish checks.");
            }
        }

        private static void ValidateMigrationManifest(List<ReviewFinding> results, string projectRoot)
        {
            string manifestPath = Path.Combine(projectRoot, "Database", "MigrationManifest.json");
            string migrationDirectory = Path.Combine(projectRoot, "Database", "Migrations");

            if (!File.Exists(manifestPath))
            {
                Add(results, "Critical", "Database Deployment",
                    "The controlled migration manifest is missing.",
                    manifestPath,
                    "Restore the reviewed manifest before any database deployment.");
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var listedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int checksumFailures = 0;
                int missingFiles = 0;

                foreach (JsonElement migration in document.RootElement.GetProperty("migrations").EnumerateArray())
                {
                    string relative = migration.GetProperty("file").GetString() ?? string.Empty;
                    string expectedHash = migration.GetProperty("sha256").GetString() ?? string.Empty;
                    string fileName = Path.GetFileName(relative);
                    listedFiles.Add(fileName);
                    string fullPath = Path.Combine(projectRoot, "Database", relative.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(fullPath))
                    {
                        missingFiles++;
                        continue;
                    }

                    string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
                    if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                        checksumFailures++;
                }

                int orphanFiles = Directory.Exists(migrationDirectory)
                    ? Directory.EnumerateFiles(migrationDirectory, "*.sql")
                        .Count(path => !listedFiles.Contains(Path.GetFileName(path)))
                    : 0;

                if (missingFiles > 0 || checksumFailures > 0 || orphanFiles > 0)
                {
                    Add(results, "Critical", "Database Deployment",
                        "The controlled SQL migration package is inconsistent.",
                        $"Missing files={missingFiles}; checksum failures={checksumFailures}; unmanifested files={orphanFiles}.",
                        "Stop deployment, restore the approved files, regenerate controlled checksums, and review the change package.");
                }
            }
            catch (Exception ex)
            {
                Add(results, "High", "Database Deployment",
                    "The migration manifest could not be validated.",
                    Infrastructure.UserFacingError.SafeMessage(ex, "Migration-manifest validation"),
                    "Correct the manifest JSON structure and verify every SQL checksum before deployment.");
            }
        }

        private static string? FindProjectRoot()
        {
            var starts = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };

            foreach (string start in starts)
            {
                DirectoryInfo? directory = new DirectoryInfo(start);
                for (int level = 0; directory != null && level < 8; level++, directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "PharmaLIMS.csproj")))
                        return directory.FullName;
                }
            }

            return null;
        }

        private static bool IsGeneratedPath(string path, string projectRoot)
        {
            string relative = Path.GetRelativePath(projectRoot, path);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Any(part => part.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                                     part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                     part.Equals("obj", StringComparison.OrdinalIgnoreCase));
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        private static int ScalarInt(string sql)
        {
            try
            {
                object value = DatabaseHelper.ExecuteScalar(sql);
                return value == null || value == DBNull.Value
                    ? -1
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("AI System Review database rule could not execute.", ex);
                return -1;
            }
        }

        private static string ScalarText(string sql)
        {
            try
            {
                object value = DatabaseHelper.ExecuteScalar(sql);
                return value == null || value == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("AI System Review schema rule could not execute.", ex);
                return string.Empty;
            }
        }

        private static void Add(
            ICollection<ReviewFinding> results,
            string severity,
            string area,
            string finding,
            string evidence,
            string recommendation)
        {
            results.Add(new ReviewFinding
            {
                Severity = severity,
                Area = area,
                Finding = finding,
                Evidence = evidence,
                Recommendation = recommendation
            });
        }

        private static bool HasReviewPermission()
        {
            return Login.CanManageSettings;
        }

        private void UpdateSummary()
        {
            lblCritical.Text = CountSeverity("Critical").ToString(CultureInfo.InvariantCulture);
            lblHigh.Text = CountSeverity("High").ToString(CultureInfo.InvariantCulture);
            lblMedium.Text = CountSeverity("Medium").ToString(CultureInfo.InvariantCulture);
            lblLow.Text = CountSeverity("Low").ToString(CultureInfo.InvariantCulture);

            lblOverall.Text = CountSeverity("Critical") > 0 ? "Action required" :
                              CountSeverity("High") > 0 ? "High risk" :
                              CountSeverity("Medium") > 0 ? "Improvements required" :
                              _findings.Count > 0 ? "No blocking finding" : "Not reviewed";
        }

        private int CountSeverity(string severity)
        {
            return _findings.Count(item => item.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase));
        }

        private static int SeverityRank(string severity)
        {
            return severity switch
            {
                "Critical" => 0,
                "High" => 1,
                "Medium" => 2,
                "Low" => 3,
                _ => 4
            };
        }

        private void CboSeverityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_findingsView == null || cboSeverityFilter.SelectedItem is not ComboBoxItem item)
                return;

            string selected = Convert.ToString(item.Tag, CultureInfo.InvariantCulture) ?? "All";
            _findingsView.Filter = selected == "All"
                ? null
                : candidate => candidate is ReviewFinding finding &&
                               finding.Severity.Equals(selected, StringComparison.OrdinalIgnoreCase);
            _findingsView.Refresh();
        }

        private void BtnCopyPlan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildMarkdownReport(includeInformation: false));
                lblStatus.Text = "The prioritized correction plan was copied to the clipboard.";
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("AI System Review could not copy the correction plan.", ex);
                MessageBox.Show("The correction plan could not be copied.", "Copy Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export AI System Review",
                FileName = $"PharmaLIMS_AI_System_Review_{DateTime.Now:yyyyMMdd_HHmm}.html",
                Filter = "HTML report (*.html)|*.html|Text report (*.txt)|*.txt",
                AddExtension = true,
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                string content = Path.GetExtension(dialog.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    ? BuildMarkdownReport(includeInformation: true)
                    : BuildHtmlReport();
                File.WriteAllText(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                lblStatus.Text = "Review report exported: " + dialog.FileName;
                ApplicationLogger.Information($"AI System Review report exported by '{Login.CurrentUser}'.");
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("AI System Review report export failed.", ex);
                MessageBox.Show("The review report could not be exported.", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string BuildMarkdownReport(bool includeInformation)
        {
            var builder = new StringBuilder();
            builder.AppendLine("PharmaLIMS AI System Review");
            builder.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Environment: {AppConfig.EnvironmentName}");
            builder.AppendLine($"Server / Database: {AppConfig.SqlServerName} / {AppConfig.DatabaseName}");
            builder.AppendLine($"Generated By: {Login.CurrentUser}");
            builder.AppendLine();

            foreach (ReviewFinding finding in _findings
                .Where(item => includeInformation || item.Severity != "Info")
                .OrderBy(item => SeverityRank(item.Severity)))
            {
                builder.AppendLine($"[{finding.Severity}] {finding.Area} - {finding.Finding}");
                builder.AppendLine("Evidence: " + finding.Evidence);
                builder.AppendLine("Recommended Action: " + finding.Recommendation);
                builder.AppendLine();
            }

            builder.AppendLine("CONTROL NOTICE");
            builder.AppendLine("This report is advisory and read-only. Corrections require human review, build validation, testing, approval, and controlled deployment.");
            return builder.ToString();
        }

        private string BuildHtmlReport()
        {
            string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
            var rows = new StringBuilder();

            foreach (ReviewFinding finding in _findings.OrderBy(item => SeverityRank(item.Severity)))
            {
                string cssClass = finding.Severity.ToLowerInvariant();
                rows.AppendLine($"<tr class=\"{cssClass}\"><td>{Encode(finding.Severity)}</td><td>{Encode(finding.Area)}</td><td>{Encode(finding.Finding)}</td><td>{Encode(finding.Evidence)}</td><td>{Encode(finding.Recommendation)}</td></tr>");
            }

            return $@"<!doctype html>
<html><head><meta charset=""utf-8""><title>PharmaLIMS AI System Review</title>
<style>
@page {{ size: A4 landscape; margin: 10mm; }}
body {{ font-family: Arial, sans-serif; color:#10243a; margin:24px; }}
h1 {{ color:#123f5e; margin-bottom:4px; }} .meta {{ color:#5e7186; margin-bottom:18px; }}
.summary {{ display:flex; gap:12px; margin:16px 0; }} .card {{ border:1px solid #d6e1e8; border-radius:8px; padding:10px 16px; }}
table {{ width:100%; border-collapse:collapse; font-size:11px; }} th {{ background:#123f5e; color:white; padding:8px; text-align:left; }} td {{ border:1px solid #d6e1e8; padding:8px; vertical-align:top; }}
tr.critical {{ background:#fde8ea; }} tr.high {{ background:#fff0e4; }} tr.medium {{ background:#fff8dd; }} tr.low {{ background:#f0f6fa; }}
.notice {{ margin-top:16px; padding:12px; background:#eaf4f8; border-radius:8px; }}
</style></head><body>
<h1>PharmaLIMS AI System Review</h1>
<div class=""meta"">Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {Encode(AppConfig.EnvironmentName)} | {Encode(AppConfig.SqlServerName)} / {Encode(AppConfig.DatabaseName)} | By {Encode(Login.CurrentUser)}</div>
<div class=""summary""><div class=""card"">Critical: {CountSeverity("Critical")}</div><div class=""card"">High: {CountSeverity("High")}</div><div class=""card"">Medium: {CountSeverity("Medium")}</div><div class=""card"">Low: {CountSeverity("Low")}</div></div>
<table><thead><tr><th>Severity</th><th>Area</th><th>Finding</th><th>Evidence</th><th>Recommended Action</th></tr></thead><tbody>{rows}</tbody></table>
<div class=""notice""><b>Control notice:</b> This report is advisory and read-only. Corrections require human review, build validation, testing, approval, and controlled deployment.</div>
</body></html>";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public sealed class ReviewFinding
        {
            public string Severity { get; set; } = string.Empty;
            public string Area { get; set; } = string.Empty;
            public string Finding { get; set; } = string.Empty;
            public string Evidence { get; set; } = string.Empty;
            public string Recommendation { get; set; } = string.Empty;
        }
    }
}
