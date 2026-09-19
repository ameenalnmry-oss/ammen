using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Services;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;

namespace PharmaLIMS
{
    [SupportedOSPlatform("windows7.0")]
    public partial class MainWindow : Window
    {
        private readonly IAuthService? _authService;
        private readonly IServiceProvider? _serviceProvider;
        private bool _runtimePreflightReady;
        private bool _runtimePreflightChecked;
        private bool _maintenanceRunning;
        private bool _preflightRunning;
        private bool _dashboardRefreshRunning;
        private bool _maintenanceBlockedByDatabaseActivity;
        private readonly HashSet<Window> _activeWorkflowWindows = new();

        public MainWindow()
        {
            InitializeComponent();
            LoadUserContext();
            ApplyRolePermissions();
            ApplyRuntimeReadinessGate();
            StartClock();
        }

        public MainWindow(IAuthService authService, IServiceProvider serviceProvider) : this()
        {
            _authService = authService;
            _serviceProvider = serviceProvider;
        }

        private sealed class DashboardSnapshot
        {
            public int? PendingSamples { get; set; }
            public int? PendingReview { get; set; }
            public int? PendingApproval { get; set; }
            public int? OpenQualityEvents { get; set; }
            public int? ReleasedToday { get; set; }
            public int? TodayEmEvents { get; set; }
            public int? MediaNearExpiry { get; set; }
            public int? OverdueSamples { get; set; }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Always enter the controlled workspace full-screen even when the process
            // is launched by Visual Studio or Windows has remembered a restored size.
            if (WindowState != WindowState.Maximized)
                WindowState = WindowState.Maximized;

            Activate();

            // Workflow authorization always comes from the full read-only System Preflight.
            // The bounded operational probe is diagnostic-only and must never unlock regulated
            // workflows from a subset of schema checks.
            await RefreshSystemReadinessAsync(showBlockedWindow: false);
            if (_runtimePreflightChecked && _runtimePreflightReady)
            {
                await RefreshDashboardAsync();
            }
            else
            {
                lblDashboardUpdated.Text = "Dashboard blocked until System Preflight passes.";
            }
        }

        private async void BtnRefreshDashboard_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDashboardAsync();
        }

        private async Task RefreshDashboardAsync()
        {
            if (BtnRefreshDashboard == null || _dashboardRefreshRunning || _maintenanceRunning)
                return;

            if (!_runtimePreflightChecked || !_runtimePreflightReady)
            {
                lblDashboardUpdated.Text = "Dashboard blocked until System Preflight passes.";
                ApplyRuntimeReadinessGate();
                return;
            }

            _dashboardRefreshRunning = true;
            ApplyRuntimeReadinessGate();
            BtnRefreshDashboard.IsEnabled = false;
            BtnRefreshDashboard.Content = "Refreshing...";
            lblDashboardUpdated.Text = "Loading current laboratory workload...";

            try
            {
                DashboardSnapshot snapshot = await Task.Run(LoadDashboardSnapshot);

                lblPendingSamples.Text = FormatMetric(snapshot.PendingSamples);
                lblPendingReview.Text = FormatMetric(snapshot.PendingReview);
                lblPendingApproval.Text = FormatMetric(snapshot.PendingApproval);
                lblOpenQualityEvents.Text = FormatMetric(snapshot.OpenQualityEvents);
                lblReleasedToday.Text = FormatMetric(snapshot.ReleasedToday);
                lblTodayEM.Text = FormatMetric(snapshot.TodayEmEvents);
                lblMediaNearExpiry.Text = FormatMetric(snapshot.MediaNearExpiry);
                lblOverdueSamples.Text = FormatMetric(snapshot.OverdueSamples);
                lblDashboardUpdated.Text = "Updated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Dashboard refresh failed.", ex);
                SetDashboardUnavailable();
                lblDashboardUpdated.Text = "Dashboard data is currently unavailable. Details were written to the application log.";
            }
            finally
            {
                _dashboardRefreshRunning = false;
                BtnRefreshDashboard.Content = "Refresh Dashboard";
                ApplyRolePermissions();
                ApplyRuntimeReadinessGate();
            }
        }

        private DashboardSnapshot LoadDashboardSnapshot()
        {
            return new DashboardSnapshot
            {
                PendingSamples = LoadPendingSamplesMetric(),

                PendingReview = GetDashboardCount("Pending Review", @"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Samples', N'Status') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.Samples WHERE Status IN (''Results Entered'', ''Under Review'');', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.EM_Events', N'WorkflowStatus') IS NOT NULL
BEGIN DECLARE @Em INT = 0; EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.EM_Events WHERE WorkflowStatus = ''Under Review'';', N'@Value INT OUTPUT', @Value = @Em OUTPUT; SET @Result += @Em; END
IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.MediaQualifications', N'QualificationStatus') IS NOT NULL
BEGIN DECLARE @Media INT = 0; EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.MediaQualifications WHERE QualificationStatus = ''Pending Review'';', N'@Value INT OUTPUT', @Value = @Media OUTPUT; SET @Result += @Media; END
IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.PRM_Samples', N'SampleStatus') IS NOT NULL
BEGIN DECLARE @Prm INT = 0; EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.PRM_Samples WHERE SampleStatus IN (''Results Entered'', ''Under Review'');', N'@Value INT OUTPUT', @Value = @Prm OUTPUT; SET @Result += @Prm; END
SELECT @Result;"),

                PendingApproval = GetDashboardCount("Pending Approval", @"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Samples', N'Status') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.Samples WHERE Status = ''Reviewed'';', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.EM_Events', N'WorkflowStatus') IS NOT NULL
BEGIN DECLARE @Em INT = 0; EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.EM_Events WHERE WorkflowStatus = ''Reviewed'';', N'@Value INT OUTPUT', @Value = @Em OUTPUT; SET @Result += @Em; END
IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.MediaQualifications', N'QualificationStatus') IS NOT NULL
BEGIN DECLARE @Media INT = 0; EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.MediaQualifications WHERE QualificationStatus = ''Qualified'' AND ReleaseDate IS NULL;', N'@Value INT OUTPUT', @Value = @Media OUTPUT; SET @Result += @Media; END
IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.PRM_Samples', N'SampleStatus') IS NOT NULL
BEGIN DECLARE @Prm INT = 0; EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.PRM_Samples WHERE SampleStatus = ''Reviewed'';', N'@Value INT OUTPUT', @Value = @Prm OUTPUT; SET @Result += @Prm; END
SELECT @Result;"),

                OpenQualityEvents = GetDashboardCount("Open Quality Events", @"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.QualityEvents', N'CurrentStatus') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.QualityEvents WHERE ISNULL(CurrentStatus, ''Open'') NOT IN (''Closed'', ''QA Closed'', ''Cancelled'', ''Rejected Closed'');', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
    ELSE IF COL_LENGTH(N'dbo.QualityEvents', N'Status') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.QualityEvents WHERE ISNULL(Status, ''Open'') NOT IN (''Closed'', ''QA Closed'', ''Cancelled'', ''Rejected Closed'');', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
END
SELECT @Result;"),

                ReleasedToday = GetDashboardCount("Released Today", @"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.MediaQualifications', N'ReleaseDate') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.MediaQualifications WHERE CAST(ReleaseDate AS DATE) = CAST(GETDATE() AS DATE);', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
IF OBJECT_ID(N'dbo.Certificates', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Certificates', N'IssueDate') IS NOT NULL
BEGIN DECLARE @Cert INT = 0; EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.Certificates WHERE CAST(IssueDate AS DATE) = CAST(GETDATE() AS DATE);', N'@Value INT OUTPUT', @Value = @Cert OUTPUT; SET @Result += @Cert; END
IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NOT NULL
BEGIN
    DECLARE @Prm INT = 0;
    IF COL_LENGTH(N'dbo.PRM_Certificates', N'IssuedDate') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.PRM_Certificates WHERE CAST(IssuedDate AS DATE) = CAST(GETDATE() AS DATE);', N'@Value INT OUTPUT', @Value = @Prm OUTPUT;
    ELSE IF COL_LENGTH(N'dbo.PRM_Certificates', N'IssueDate') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.PRM_Certificates WHERE CAST(IssueDate AS DATE) = CAST(GETDATE() AS DATE);', N'@Value INT OUTPUT', @Value = @Prm OUTPUT;
    SET @Result += @Prm;
END
SELECT @Result;"),

                TodayEmEvents = GetDashboardCount("Today EM", @"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.EM_Events', N'EventDate') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.EM_Events WHERE CAST(EventDate AS DATE) = CAST(GETDATE() AS DATE);', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
SELECT @Result;"),

                MediaNearExpiry = GetDashboardCount("Media Near Expiry", @"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.CultureMediaLots', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CultureMediaLots', N'ExpiryDate') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.CultureMediaLots', N'ReceiptStatus') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.CultureMediaLots WHERE ExpiryDate >= CAST(GETDATE() AS DATE) AND ExpiryDate < DATEADD(DAY, 31, CAST(GETDATE() AS DATE)) AND ISNULL(ReceiptStatus, '''') NOT IN (''Rejected'', ''Expired'');', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
    ELSE
        EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.CultureMediaLots WHERE ExpiryDate >= CAST(GETDATE() AS DATE) AND ExpiryDate < DATEADD(DAY, 31, CAST(GETDATE() AS DATE));', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
END
SELECT @Result;"),

                OverdueSamples = GetDashboardCount("Overdue Samples", @"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Samples', N'RegistrationDate') IS NOT NULL AND COL_LENGTH(N'dbo.Samples', N'Status') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.Samples WHERE RegistrationDate < DATEADD(DAY, -7, GETDATE()) AND ISNULL(Status, '''') NOT IN (''Approved'', ''COA Issued'', ''Certificate Issued'', ''Cancelled'', ''Rejected'');', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.PRM_Samples', N'CreatedDate') IS NOT NULL AND COL_LENGTH(N'dbo.PRM_Samples', N'SampleStatus') IS NOT NULL
BEGIN DECLARE @Prm INT = 0; EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.PRM_Samples WHERE CreatedDate < DATEADD(DAY, -7, GETDATE()) AND ISNULL(SampleStatus, '''') NOT IN (''Approved'', ''COA Issued'', ''Certificate Issued'', ''Cancelled'', ''Rejected'');', N'@Value INT OUTPUT', @Value = @Prm OUTPUT; SET @Result += @Prm; END
SELECT @Result;")
            };
        }

        private static int? LoadPendingSamplesMetric()
        {
            int? coreSamples = GetDashboardCount(
                "Pending Samples / Samples",
                @"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Samples', N'Status') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.Samples WHERE Status IS NULL OR Status NOT IN (''Approved'', ''COA Issued'', ''Certificate Issued'', ''Cancelled'', ''Rejected'');', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
SELECT @Result;");

            int? prmSamples = GetDashboardCount(
                "Pending Samples / PRM",
                @"
DECLARE @Result INT = 0;
IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.PRM_Samples', N'SampleStatus') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @Value = COUNT(1) FROM dbo.PRM_Samples WHERE SampleStatus IS NULL OR SampleStatus NOT IN (''Approved'', ''COA Issued'', ''Certificate Issued'', ''Cancelled'', ''Rejected'');', N'@Value INT OUTPUT', @Value = @Result OUTPUT;
SELECT @Result;");

            if (!coreSamples.HasValue || !prmSamples.HasValue)
                return null;

            return checked(coreSamples.Value + prmSamples.Value);
        }

        private static int? GetDashboardCount(string metricName, string query)
        {
            try
            {
                return ExecuteDashboardCount(query);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (IsTransientDashboardSqlError(ex))
            {
                // Dashboard metrics are read-only. A single bounded retry avoids a false N/A when
                // SQL Express is briefly busy while preserving fail-visible behavior for persistent errors.
                System.Threading.Thread.Sleep(250);
                try
                {
                    int? retried = ExecuteDashboardCount(query);
                    ApplicationLogger.Information($"Dashboard metric recovered after one transient SQL retry: {metricName}.");
                    return retried;
                }
                catch (Exception retryEx)
                {
                    ApplicationLogger.Error($"Dashboard metric query failed after retry: {metricName}.", retryEx);
                    return null;
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Dashboard metric query failed: {metricName}.", ex);
                return null;
            }
        }

        private static int? ExecuteDashboardCount(string query)
        {
            object value = DatabaseHelper.ExecuteScalar(query, commandTimeoutSeconds: 10);
            if (value == null || value == DBNull.Value)
                return 0;

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static bool IsTransientDashboardSqlError(Microsoft.Data.SqlClient.SqlException exception)
        {
            return exception.Number is -2 or 1205 or 233 or 10053 or 10054 or 10060;
        }

        private static string FormatMetric(int? value)
        {
            return value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : "N/A";
        }

        private void SetDashboardUnavailable()
        {
            lblPendingSamples.Text = "N/A";
            lblPendingReview.Text = "N/A";
            lblPendingApproval.Text = "N/A";
            lblOpenQualityEvents.Text = "N/A";
            lblReleasedToday.Text = "N/A";
            lblTodayEM.Text = "N/A";
            lblMediaNearExpiry.Text = "N/A";
            lblOverdueSamples.Text = "N/A";
        }

        private void LoadUserContext()
        {
            string displayName = "";

            if (!string.IsNullOrWhiteSpace(Login.CurrentUserFullName))
                displayName = Login.CurrentUserFullName;
            else if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                displayName = Login.CurrentUser;
            else
                displayName = "Not signed in";

            lblUser.Text = "User: " + displayName;

            string role = "";

            if (!string.IsNullOrWhiteSpace(Login.CurrentUserRole))
                role = Login.CurrentUserRole;
            else
                role = "Unknown";

            lblRole.Text = "Role: " + role;
        }

        private void ApplyRolePermissions()
        {
            BtnSampleManagement.IsEnabled = Login.CanRegisterSamples ||
                                            Login.CanEnterResults ||
                                            Login.CanReviewResults ||
                                            Login.CanAccessReports;

            BtnNewSample.IsEnabled = Login.CanRegisterSamples;

            BtnResultsEntry.IsEnabled = Login.CanEnterResults ||
                                        Login.CanReviewResults;

            BtnEMResults.IsEnabled = Login.CanAccessEM;
            BtnEMPlanning.IsEnabled = Login.CanAccessEM && Login.CanRegisterSamples;

            BtnCultureMedia.IsEnabled = Login.CanEnterResults ||
                                        Login.CanReviewResults ||
                                        Login.CanApproveResults;

            BtnPRMSamples.IsEnabled = Login.CanRegisterSamples ||
                                      Login.CanEnterResults ||
                                      Login.CanReviewResults ||
                                      Login.CanAccessReports;

            BtnPRMResults.IsEnabled = Login.CanEnterResults ||
                                      Login.CanReviewResults ||
                                      Login.CanApproveResults ||
                                      Login.CanIssueCOA ||
                                      Login.CanCancelCOA;

            BtnReports.IsEnabled = Login.CanAccessReports;
            BtnUserManagement.IsEnabled = Login.CanManageUsers;
            BtnAIReview.IsEnabled = Login.CanManageSettings;
            ApplyRuntimeReadinessGate();
        }

        private void ApplySystemReadinessReport(SystemPreflightReport report)
        {
            _runtimePreflightChecked = true;
            _runtimePreflightReady = report.BlockerCount == 0;
            _maintenanceBlockedByDatabaseActivity = report.Checks.Any(item =>
                item.Status == "BLOCKER" &&
                item.Area == "Database" &&
                (item.Check.Contains("lifecycle", StringComparison.OrdinalIgnoreCase) ||
                 item.Check.Contains("responsiveness", StringComparison.OrdinalIgnoreCase) ||
                 item.Details.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
                 item.Details.Contains("lock", StringComparison.OrdinalIgnoreCase)));

            ApplyRolePermissions();
            ApplyRuntimeReadinessGate();

            if (AppConfig.IsDevelopment)
            {
                if (_runtimePreflightReady)
                {
                    lblSystemContext.Text = report.WarningCount > 0
                        ? $"Development readiness PASS ({report.WarningCount} warning{(report.WarningCount == 1 ? string.Empty : "s")})"
                        : "Development readiness PASS";
                    lblSystemContext.Foreground = report.WarningCount > 0
                        ? System.Windows.Media.Brushes.DarkGoldenrod
                        : System.Windows.Media.Brushes.DarkGreen;
                }
                else
                {
                    lblSystemContext.Text = _maintenanceBlockedByDatabaseActivity
                        ? $"Development readiness BLOCKED ({report.BlockerCount}) - preflight database query timed out/was blocked; review System Preflight details, then retry"
                        : $"Development readiness BLOCKED ({report.BlockerCount}) - review System Preflight; use Database Maintenance only for schema/deployment blockers";
                    lblSystemContext.Foreground = System.Windows.Media.Brushes.Firebrick;
                }
            }
            else if (_runtimePreflightReady)
            {
                lblSystemContext.Text = report.WarningCount > 0
                    ? $"Preflight PASS ({report.WarningCount} warning{(report.WarningCount == 1 ? string.Empty : "s")})"
                    : "System Preflight PASS";
                lblSystemContext.Foreground = report.WarningCount > 0
                    ? System.Windows.Media.Brushes.DarkGoldenrod
                    : System.Windows.Media.Brushes.DarkGreen;
            }
            else
            {
                lblSystemContext.Text = $"Preflight BLOCKED ({report.BlockerCount})";
                lblSystemContext.Foreground = System.Windows.Media.Brushes.Firebrick;
            }
        }

        private async Task RefreshSystemReadinessAsync(
            bool showBlockedWindow,
            bool maintenanceVerification = false)
        {
            if (_preflightRunning && !maintenanceVerification)
                return;

            if (_maintenanceRunning && !maintenanceVerification)
                return;

            bool ownsPreflightFlag = !_preflightRunning;
            if (ownsPreflightFlag)
            {
                _preflightRunning = true;
                ApplyRuntimeReadinessGate();
            }

            try
            {
                lblSystemContext.Text = maintenanceVerification
                    ? "Verifying database after controlled maintenance..."
                    : "Running system preflight...";
                lblSystemContext.Foreground = System.Windows.Media.Brushes.DarkSlateBlue;

                SystemPreflightService service = GetService<SystemPreflightService>();
                // Runtime workflow authorization always uses the complete read-only preflight.
                // The bounded operational probe is diagnostic-only and is never a readiness gate.
                SystemPreflightReport report = await service.RunAsync();
                ApplySystemReadinessReport(report);

                if (!_runtimePreflightReady && showBlockedWindow && !AppConfig.IsDevelopment && !maintenanceVerification)
                    ShowSystemPreflightWindow();
            }
            catch (Exception ex)
            {
                _runtimePreflightChecked = true;
                _runtimePreflightReady = false;
                _maintenanceBlockedByDatabaseActivity = true;
                ApplyRolePermissions();
                ApplyRuntimeReadinessGate();
                lblSystemContext.Text = AppConfig.IsDevelopment
                    ? "Development readiness unavailable - workflows blocked"
                    : "Preflight unavailable - workflows blocked";
                lblSystemContext.Foreground = System.Windows.Media.Brushes.Firebrick;
                ApplicationLogger.Error("Runtime system preflight failed from MainWindow.", ex);
                if (showBlockedWindow && !AppConfig.IsDevelopment && !maintenanceVerification)
                {
                    MessageBox.Show(
                        UserFacingError.SafeMessage(ex, "System preflight"),
                        "System Preflight",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                if (ownsPreflightFlag)
                {
                    _preflightRunning = false;
                    ApplyRolePermissions();
                    ApplyRuntimeReadinessGate();
                }
            }
        }

        private void ApplyRuntimeReadinessGate()
        {
            // Every workflow remains fail-closed until the current database passes
            // readiness. Development keeps only System Preflight and signed Database
            // Maintenance available so schema drift can be repaired without letting a
            // workflow execute against an incompatible schema.
            bool workflowReady = !_maintenanceRunning &&
                                 _runtimePreflightChecked &&
                                 _runtimePreflightReady;

            if (!workflowReady)
            {
                BtnSampleManagement.IsEnabled = false;
                BtnNewSample.IsEnabled = false;
                BtnResultsEntry.IsEnabled = false;
                BtnEMResults.IsEnabled = false;
                BtnEMPlanning.IsEnabled = false;
                BtnCultureMedia.IsEnabled = false;
                BtnPRMSamples.IsEnabled = false;
                BtnPRMResults.IsEnabled = false;
                BtnReports.IsEnabled = false;
                BtnAIReview.IsEnabled = false;
            }

            bool canManageSystem = IsAdministrativeRole(Login.CurrentUserRole) || Login.CanManageSettings;
            // System Preflight is read-only and available to every authenticated user so a
            // blocked workflow always has an inspectable reason. Schema maintenance remains administrative.
            BtnSystemPreflight.IsEnabled = !_maintenanceRunning && !_preflightRunning;
            BtnDatabaseMaintenance.IsEnabled = canManageSystem && AppConfig.IsDevelopment &&
                                               !_maintenanceBlockedByDatabaseActivity &&
                                               !_maintenanceRunning && !_preflightRunning && !_dashboardRefreshRunning;
            if (BtnRefreshDashboard != null)
                BtnRefreshDashboard.IsEnabled = !_maintenanceRunning && !_dashboardRefreshRunning &&
                                                _runtimePreflightChecked && _runtimePreflightReady;
            if (_maintenanceRunning)
            {
                BtnReports.ToolTip = "Unavailable while controlled database maintenance is running.";
                BtnAIReview.ToolTip = "Unavailable while controlled database maintenance is running.";
            }
            BtnDatabaseMaintenance.ToolTip = !AppConfig.IsDevelopment
                ? "Database schema changes in Production must use the approved deployment process."
                : _maintenanceBlockedByDatabaseActivity
                    ? "Database Maintenance is disabled while System Preflight detects active database blocking. Close other PharmaLIMS/debug sessions or open SSMS transactions, then rerun System Preflight."
                    : "Explicit, signed Development database maintenance for schema/deployment blockers. Login may verify only the checksum-controlled authentication compatibility backfill; no broader migration runs from login or workflow buttons.";
        }

        private static bool IsAdministrativeRole(string? role)
        {
            return !string.IsNullOrWhiteSpace(role) &&
                   (role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
        }

        private bool EnsureRuntimeReadyForWorkflow(string workflowName)
        {
            if (_maintenanceRunning)
            {
                MessageBox.Show(
                    "Database Maintenance is in progress. Wait for that controlled action to finish before opening a workflow.",
                    "Database Maintenance",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }

            if (_runtimePreflightChecked && _runtimePreflightReady)
                return true;

            MessageBox.Show(
                workflowName + " is blocked until System Preflight passes.\n\n" +
                "Use System Preflight to review blockers. In Development, an authorized administrator can run Database Maintenance explicitly; normal workflow buttons never change database schema.",
                "System Preflight Required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        private SystemPreflightReport? ShowSystemPreflightWindow()
        {
            try
            {
                SystemPreflight window = GetService<SystemPreflight>();
                window.Owner = this;
                window.ShowDialog();
                return window.LastReport;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("System Preflight window failed to open.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "System Preflight"), "System Preflight",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        private void BtnSystemPreflight_Click(object sender, RoutedEventArgs e)
        {
            if (_maintenanceRunning)
            {
                MessageBox.Show(
                    "Database Maintenance is in progress. System Preflight will be available after maintenance finishes.",
                    "Database Maintenance", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_preflightRunning)
                return;

            _preflightRunning = true;
            ApplyRolePermissions();
            ApplyRuntimeReadinessGate();
            SystemPreflightReport? report = null;
            try
            {
                report = ShowSystemPreflightWindow();
            }
            finally
            {
                _preflightRunning = false;
            }

            if (report != null)
                ApplySystemReadinessReport(report);
            else
            {
                ApplyRolePermissions();
                ApplyRuntimeReadinessGate();
            }
        }

        private async void BtnDatabaseMaintenance_Click(object sender, RoutedEventArgs e)
        {
            if (_maintenanceRunning)
                return;

            if (_preflightRunning)
            {
                MessageBox.Show(
                    "System Preflight is still running. Wait for it to finish before starting Database Maintenance.",
                    "System Preflight", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_dashboardRefreshRunning)
            {
                MessageBox.Show(
                    "Dashboard database queries are still finishing. Wait a moment and start Database Maintenance again.",
                    "Database Maintenance", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_maintenanceBlockedByDatabaseActivity)
            {
                MessageBox.Show(
                    "System Preflight detected active database blocking. Database Maintenance is intentionally disabled because starting migrations while another transaction or maintenance session owns database locks is unsafe.\n\n" +
                    "Close other PharmaLIMS / Visual Studio debug instances and any open SSMS transaction, then rerun System Preflight. Start Database Maintenance only if the resulting blocker is a schema/deployment item.",
                    "Database Busy / Locked", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!AppConfig.IsDevelopment)
            {
                MessageBox.Show(
                    "Database Maintenance is disabled inside the application outside Development. Use the approved controlled deployment process.",
                    "Database Maintenance",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!(IsAdministrativeRole(Login.CurrentUserRole) || Login.CanManageSettings))
            {
                MessageBox.Show("Database Maintenance requires administrator/system-settings permission.",
                    "Permission Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Window? openWorkflowWindow = GetActiveWorkflowWindow();
            if (openWorkflowWindow != null)
            {
                string workflowName = string.IsNullOrWhiteSpace(openWorkflowWindow.Title)
                    ? openWorkflowWindow.GetType().Name
                    : openWorkflowWindow.Title.Trim();
                MessageBox.Show(
                    $"Close the open PharmaLIMS workflow window '{workflowName}' before Database Maintenance. " +
                    "Controlled schema maintenance runs only after this application instance has no active workflow windows.",
                    "Database Maintenance", MessageBoxButton.OK, MessageBoxImage.Information);
                openWorkflowWindow.Activate();
                return;
            }

            ElectronicSignature signature = GetService<ElectronicSignature>();
            signature.Configure("SYSTEM-DB", Login.CurrentUser, "Development Database Maintenance", true);
            signature.Owner = this;
            signature.ShowDialog();
            if (!signature.IsConfirmed)
                return;

            MessageBoxResult confirmation = MessageBox.Show(
                "Apply all pending checksum-controlled database migrations now?\n\n" +
                "This is an explicit Development maintenance action. Workflow screens remain closed while maintenance runs. Login may run only the checksum-controlled authentication compatibility backfill; no broader migration is executed from login, result entry, approval, investigation, or reporting buttons.",
                "Development Database Maintenance",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
                return;

            _maintenanceRunning = true;
            _runtimePreflightReady = false;
            _runtimePreflightChecked = true;
            ApplyRolePermissions();
            ApplyRuntimeReadinessGate();
            BtnDatabaseMaintenance.Content = "Applying updates...";
            lblSystemContext.Text = "Controlled database maintenance in progress...";
            lblSystemContext.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;

            bool migrationsCompleted = false;
            bool maintenanceAuditRecorded = false;
            try
            {
                StartupDatabaseMigrator migrator = GetService<StartupDatabaseMigrator>();
                Action<string> progressHandler = progress => Dispatcher.Invoke(() =>
                {
                    lblSystemContext.Text = progress;
                    lblSystemContext.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
                });

                migrator.ProgressChanged += progressHandler;
                try
                {
                    await migrator.ApplyRequiredUpdatesAsUserAsync(Login.CurrentUser);
                    migrationsCompleted = true;
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Error("Development Database Maintenance migration phase failed.", ex);
                    lblSystemContext.Text = ex is DatabaseMigrationException migrationFailure
                        ? $"Database maintenance stopped at {migrationFailure.VersionKey}; the failing migration was not recorded."
                        : "Database maintenance stopped before the controlled migration chain completed.";
                    lblSystemContext.Foreground = System.Windows.Media.Brushes.Firebrick;
                    MessageBox.Show(UserFacingError.SafeMessage(ex, "Database migration"),
                        "Database Maintenance", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                finally
                {
                    migrator.ProgressChanged -= progressHandler;
                }

                lblSystemContext.Text = "Controlled database migrations completed. Recording maintenance audit...";
                lblSystemContext.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;

                try
                {
                    DatabaseHelper.AddAuditTrailAdvanced(
                        "LIMS_SchemaVersions",
                        0,
                        "Development Database Maintenance",
                        string.Empty,
                        "Checksum-controlled database migrations completed",
                        signature.Reason,
                        signature.SignedBy,
                        "ApplicationVersion",
                        null,
                        null,
                        "System Maintenance");
                    maintenanceAuditRecorded = true;
                }
                catch (Exception auditException)
                {
                    ApplicationLogger.Error(
                        "Database migrations completed, but the Development maintenance audit record failed.",
                        auditException);
                    lblSystemContext.Text = "Database migrations completed; maintenance audit evidence failed.";
                    lblSystemContext.Foreground = System.Windows.Media.Brushes.Firebrick;
                    MessageBox.Show(
                        "The database migrations completed, but the required maintenance audit record could not be stored. " +
                        "Do not rerun migrations just to clear this message. Review the audit failure first.\n\n" +
                        UserFacingError.SafeMessage(auditException, "Maintenance audit"),
                        "Database Maintenance",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                lblSystemContext.Text = "Database migrations completed. Running read-only post-maintenance verification...";
                lblSystemContext.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;

                try
                {
                    await RefreshSystemReadinessAsync(
                        showBlockedWindow: false,
                        maintenanceVerification: true);
                }
                catch (Exception verificationException)
                {
                    ApplicationLogger.Error(
                        "Database migrations completed, but post-maintenance System Preflight failed to execute.",
                        verificationException);
                    lblSystemContext.Text = "Database migrations completed; post-maintenance verification could not run.";
                    lblSystemContext.Foreground = System.Windows.Media.Brushes.Firebrick;
                    MessageBox.Show(
                        "The controlled database migrations completed, but the read-only post-maintenance verification could not run. " +
                        "The schema update is not reported as failed.\n\n" +
                        UserFacingError.SafeMessage(verificationException, "Post-maintenance verification"),
                        "Database Maintenance",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                if (_runtimePreflightReady && maintenanceAuditRecorded)
                {
                    MessageBox.Show("Database maintenance completed, audit evidence was stored, and System Preflight passed.",
                        "Database Maintenance", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (!_runtimePreflightReady)
                {
                    MessageBox.Show(
                        "The controlled database migrations completed, but the read-only verification still reports blocker(s). " +
                        "Run System Preflight to see the exact items. Do not assume the migration itself failed.",
                        "Database Maintenance", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(
                        "The controlled database migrations and System Preflight completed, but the maintenance audit record failed. " +
                        "Resolve the audit evidence issue before treating this maintenance run as complete.",
                        "Database Maintenance", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unexpected Development Database Maintenance failure.", ex);
                lblSystemContext.Text = migrationsCompleted
                    ? "Database migrations completed; an unexpected maintenance follow-up failed."
                    : "Database maintenance stopped before the controlled migration chain completed.";
                lblSystemContext.Foreground = System.Windows.Media.Brushes.Firebrick;
                MessageBox.Show(UserFacingError.SafeMessage(ex, migrationsCompleted ? "Maintenance follow-up" : "Database maintenance"),
                    "Database Maintenance", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _maintenanceRunning = false;
                BtnDatabaseMaintenance.Content = "Database Maintenance";
                ApplyRolePermissions();
                ApplyRuntimeReadinessGate();
            }
        }

        internal void RequestDevelopmentDatabaseMaintenanceFromWorkflow()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RequestDevelopmentDatabaseMaintenanceFromWorkflow);
                return;
            }

            BtnDatabaseMaintenance_Click(BtnDatabaseMaintenance, new RoutedEventArgs());
        }

        private void StartClock()
        {
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, e) =>
            {
                lblDateTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };
            timer.Start();
        }

        private T GetService<T>() where T : class
        {
            if (_serviceProvider != null)
                return _serviceProvider.GetRequiredService<T>();

            try
            {
                return Activator.CreateInstance<T>();
            }
            catch
            {
                if (App.ServiceProvider != null)
                    return App.ServiceProvider.GetRequiredService<T>();

                throw new InvalidOperationException($"Cannot create instance of {typeof(T).Name}");
            }
        }

        private Window? GetActiveWorkflowWindow()
        {
            return _activeWorkflowWindows.FirstOrDefault(window => window.IsVisible);
        }

        private void TrackWorkflowWindow(Window window)
        {
            if (!_activeWorkflowWindows.Add(window))
                return;

            window.Closed += OnTrackedWorkflowWindowClosed;
        }

        private void OnTrackedWorkflowWindowClosed(object? sender, EventArgs e)
        {
            if (sender is not Window window)
                return;

            window.Closed -= OnTrackedWorkflowWindowClosed;
            _activeWorkflowWindows.Remove(window);
        }

        private void ShowWorkspaceWindow<T>(Func<T> createWindow) where T : Window
        {
            T? existingWindow = Application.Current.Windows
                .OfType<T>()
                .FirstOrDefault(window => window.IsVisible);

            if (existingWindow != null)
            {
                TrackWorkflowWindow(existingWindow);

                if (existingWindow.WindowState == WindowState.Minimized)
                    existingWindow.WindowState = WindowState.Normal;

                existingWindow.Activate();
                existingWindow.Focus();
                return;
            }

            T window = createWindow();
            window.Owner = this;
            TrackWorkflowWindow(window);
            window.Show();
        }

        private void BtnChangePassword_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.ServiceProvider == null)
                    throw new InvalidOperationException("Application services are unavailable.");

                ChangePassword window = App.ServiceProvider.GetRequiredService<ChangePassword>();
                window.Owner = this;
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open Change Password.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Change Password"), "Change Password",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnUserManagement_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnUserManagement.IsEnabled || !DatabaseHelper.CanManageUsers(Login.CurrentUser))
            {
                MessageBox.Show(
                    "Access denied. You do not have current permission to manage user accounts.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!EnsureRuntimeReadyForWorkflow("User Management"))
                return;

            try
            {
                if (App.ServiceProvider == null)
                    throw new InvalidOperationException("Application services are unavailable.");

                ShowWorkspaceWindow(() => App.ServiceProvider.GetRequiredService<UserManagement>());
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open User Management.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "User Management"), "User Management",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSampleManagement_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnSampleManagement.IsEnabled)
            {
                MessageBox.Show(
                    "Access denied. You do not have permission to access sample management.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!EnsureRuntimeReadyForWorkflow("Sample Management"))
                return;

            try
            {
                ShowWorkspaceWindow(() => new SampleManagement());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Sample Management: {Infrastructure.UserFacingError.SafeMessage(ex)}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnNewSample_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnNewSample.IsEnabled)
            {
                MessageBox.Show(
                    "Access denied. You do not have permission to register samples.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!EnsureRuntimeReadyForWorkflow("Sample Registration"))
                return;

            try
            {
                ShowWorkspaceWindow(() => GetService<NewSampleDialog>());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening New Sample dialog: {Infrastructure.UserFacingError.SafeMessage(ex)}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnResultsEntry_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnResultsEntry.IsEnabled)
            {
                MessageBox.Show(
                    "Access denied. You do not have permission to access water results.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!EnsureRuntimeReadyForWorkflow("Water Results"))
                return;

            try
            {
                ShowWorkspaceWindow(() => GetService<ResultsEntry>());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Results Entry: {Infrastructure.UserFacingError.SafeMessage(ex)}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEMResults_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnEMResults.IsEnabled)
            {
                MessageBox.Show(
                    "Access denied. You do not have permission to access environmental monitoring.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!EnsureRuntimeReadyForWorkflow("Environmental Monitoring Results"))
                return;

            try
            {
                ShowWorkspaceWindow(() => GetService<EMResultsEntry>());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening EM Results Entry: {Infrastructure.UserFacingError.SafeMessage(ex)}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnEMPlanning_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnEMPlanning.IsEnabled)
            {
                MessageBox.Show("Access denied. EM planning requires EM and sample registration permissions.",
                    "Permission Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!EnsureRuntimeReadyForWorkflow("Environmental Monitoring Planning"))
                return;

            try
            {
                ShowWorkspaceWindow(() => GetService<EMPlanning>());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening EM Planning: {Infrastructure.UserFacingError.SafeMessage(ex)}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCultureMedia_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnCultureMedia.IsEnabled)
            {
                MessageBox.Show(
                    "Access denied. You do not have permission to access culture media preparation.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!EnsureRuntimeReadyForWorkflow("Culture Media"))
                return;

            try
            {
                ShowWorkspaceWindow(() => new CultureMediaPreparation());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Culture Media Preparation: {Infrastructure.UserFacingError.SafeMessage(ex)}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnPRMSamples_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnPRMSamples.IsEnabled)
            {
                MessageBox.Show(
                    "Access denied. You do not have permission to access production and raw material samples.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!EnsureRuntimeReadyForWorkflow("Production / Raw Material / Stability Samples"))
                return;

            try
            {
                // IMPORTANT: Open directly, not via GetService, because this window is not registered in DI.
                ShowWorkspaceWindow(() => new ProductionRawMaterialSamples());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Production & Raw Material Samples: {Infrastructure.UserFacingError.SafeMessage(ex)}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private async void BtnPRMResults_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnPRMResults.IsEnabled)
            {
                MessageBox.Show(
                    "Access denied. You do not have permission to access production / raw material results.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!EnsureRuntimeReadyForWorkflow("Production / Raw Material / Stability Results"))
                return;

            try
            {
                // v186: do not let an upgraded Development database enter PRM Results
                // and discover the missing investigation schema only after a sample is
                // selected.  Probe the complete PRM Quality Event contract first.
                PrmSchemaReadinessResult qualityEventReadiness =
                    await PrmSchemaReadinessService.EnsureQualityEventReadyAsync();
                if (!qualityEventReadiness.IsReady)
                {
                    bool canRunDevelopmentMaintenance =
                        AppConfig.IsDevelopment &&
                        (IsAdministrativeRole(Login.CurrentUserRole) || Login.CanManageSettings);

                    if (canRunDevelopmentMaintenance)
                    {
                        MessageBoxResult updateNow = MessageBox.Show(
                            "The PRM database schema must be updated before Results / Investigation can be opened.\n\n" +
                            "Apply the pending checksum-controlled database migrations now? " +
                            "The normal electronic signature and confirmation will still be required.",
                            "PRM Database Update Required",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (updateNow == MessageBoxResult.Yes)
                            RequestDevelopmentDatabaseMaintenanceFromWorkflow();
                    }
                    else
                    {
                        MessageBox.Show(qualityEventReadiness.Message,
                            "PRM Database Update Required",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }

                    return;
                }

                ShowWorkspaceWindow(() => new ProductionRawMaterialResults());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Production / Raw Material Results Entry: {Infrastructure.UserFacingError.SafeMessage(ex)}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReports_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnReports.IsEnabled)
            {
                MessageBox.Show(
                    "Access denied. You do not have permission to access reports.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                ShowWorkspaceWindow(() => GetService<ReportsTrends>());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Reports: {Infrastructure.UserFacingError.SafeMessage(ex)}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAIReview_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnAIReview.IsEnabled)
            {
                MessageBox.Show(
                    "Access denied. AI System Review requires administrator or system-settings permission.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                ShowWorkspaceWindow(() => GetService<AISystemReview>());
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("AI System Review window failed to open.", ex);
                MessageBox.Show(
                    "AI System Review could not be opened. Details were written to the application log.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
