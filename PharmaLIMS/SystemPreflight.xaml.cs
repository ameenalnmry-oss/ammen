using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace PharmaLIMS
{
    public partial class SystemPreflight : Window
    {
        private readonly SystemPreflightService _preflightService;
        private SystemPreflightReport? _lastReport;
        public SystemPreflightReport? LastReport => _lastReport;

        public SystemPreflight(SystemPreflightService preflightService)
        {
            InitializeComponent();
            _preflightService = preflightService ?? throw new ArgumentNullException(nameof(preflightService));

            string buildVersion = typeof(SystemPreflight).Assembly.GetName().Version?.ToString() ?? "unknown";
            lblEnvironment.Text = "Environment: " + AppConfig.EnvironmentName + " | Build: " + buildVersion;
            lblTarget.Text = AppConfig.SqlServerName + " / " + AppConfig.DatabaseName;
            Loaded += SystemPreflight_Loaded;
        }

        private async void SystemPreflight_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= SystemPreflight_Loaded;
            await RunPreflightAsync();
        }

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            await RunPreflightAsync();
        }

        private async System.Threading.Tasks.Task RunPreflightAsync()
        {
            BtnRun.IsEnabled = false;
            BtnRun.Content = "Running...";
            lblOverall.Text = "Checking the configured runtime and SQL Server database...";
            lblExecutedAt.Text = string.Empty;

            try
            {
                _lastReport = await _preflightService.RunAsync();
                gridChecks.ItemsSource = _lastReport.Checks;
                lblBlockers.Text = _lastReport.BlockerCount.ToString();
                lblWarnings.Text = _lastReport.WarningCount.ToString();
                lblPassed.Text = _lastReport.PassCount.ToString();
                lblExecutedAt.Text = $"Executed {_lastReport.ExecutedAt:yyyy-MM-dd HH:mm:ss} | {_lastReport.Server} / {_lastReport.Database}";
                UpdateRemediationActions();

                if (_lastReport.BlockerCount > 0)
                {
                    lblOverall.Text = "BLOCKED — correct the blocker(s) before regulated workflow use.";
                    lblOverall.Foreground = System.Windows.Media.Brushes.Firebrick;
                }
                else if (_lastReport.WarningCount > 0)
                {
                    lblOverall.Text = "PASS WITH WARNINGS — no release blocker detected by these checks.";
                    lblOverall.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
                }
                else
                {
                    lblOverall.Text = "PASS — no blocker or warning detected by the runtime preflight.";
                    lblOverall.Foreground = System.Windows.Media.Brushes.DarkGreen;
                }
            }
            catch (Exception ex)
            {
                BtnWaterProfiles.Visibility = Visibility.Collapsed;
                BtnWaterProfiles.IsEnabled = false;
                BtnEmReconcile.Visibility = Visibility.Collapsed;
                BtnEmReconcile.IsEnabled = false;
                BtnCertificateReconcile.Visibility = Visibility.Collapsed;
                BtnCertificateReconcile.IsEnabled = false;
                ApplicationLogger.Error("Runtime system preflight failed unexpectedly.", ex);
                lblOverall.Text = "Preflight failed before completion.";
                lblOverall.Foreground = System.Windows.Media.Brushes.Firebrick;
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Runtime system preflight"),
                    "System Preflight",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                BtnRun.IsEnabled = true;
                BtnRun.Content = "Run Preflight";
            }
        }

        private void UpdateRemediationActions()
        {
            bool hasWaterProfileFinding = _lastReport?.Checks.Any(check =>
                (string.Equals(check.Status, "WARNING", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(check.Status, "BLOCKER", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(check.Area, "Master Data", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(check.Check, "Water controlled test profiles", StringComparison.OrdinalIgnoreCase)) == true;

            BtnWaterProfiles.Visibility = hasWaterProfileFinding ? Visibility.Visible : Visibility.Collapsed;
            BtnWaterProfiles.IsEnabled = hasWaterProfileFinding &&
                !string.IsNullOrWhiteSpace(Login.CurrentUser) &&
                DatabaseHelper.CanManageSettings(Login.CurrentUser);

            bool hasHistoricalEmFinding = _lastReport?.Checks.Any(check =>
                (string.Equals(check.Status, "WARNING", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(check.Status, "BLOCKER", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(check.Area, "Environmental Monitoring", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(check.Check, "Historical EM limit evidence", StringComparison.OrdinalIgnoreCase)) == true;

            BtnEmReconcile.Visibility = hasHistoricalEmFinding ? Visibility.Visible : Visibility.Collapsed;
            BtnEmReconcile.IsEnabled = hasHistoricalEmFinding &&
                !string.IsNullOrWhiteSpace(Login.CurrentUser) &&
                DatabaseHelper.CanApproveResults(Login.CurrentUser);

            bool hasLegacyCertificateFinding = _lastReport?.Checks.Any(check =>
                (string.Equals(check.Status, "WARNING", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(check.Status, "BLOCKER", StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(check.Check, "Legacy PRM certificate evidence", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(check.Check, "Legacy certificate snapshot evidence", StringComparison.OrdinalIgnoreCase))) == true;

            BtnCertificateReconcile.Visibility = hasLegacyCertificateFinding ? Visibility.Visible : Visibility.Collapsed;
            BtnCertificateReconcile.IsEnabled = hasLegacyCertificateFinding &&
                !string.IsNullOrWhiteSpace(Login.CurrentUser) &&
                DatabaseHelper.CanApproveResults(Login.CurrentUser);
        }

        private async void BtnWaterProfiles_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Login.CurrentUser) ||
                    !DatabaseHelper.CanManageSettings(Login.CurrentUser))
                {
                    throw new UnauthorizedAccessException(
                        "Settings permission is required to manage controlled Water Test Profiles.");
                }

                var manager = new WaterTestProfileManagement
                {
                    Owner = this
                };
                manager.ShowDialog();

                if (manager.WasChanged)
                    await RunPreflightAsync();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open controlled Water Test Profile management from System Preflight.", ex);
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Water Test Profile management"),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnEmReconcile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_lastReport == null ||
                    !_lastReport.Checks.Any(check =>
                        (string.Equals(check.Status, "WARNING", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(check.Status, "BLOCKER", StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(check.Area, "Environmental Monitoring", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(check.Check, "Historical EM limit evidence", StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(
                        "No unresolved historical EM limit-evidence finding is present in the current preflight report.",
                        "EM Reconciliation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (string.IsNullOrWhiteSpace(Login.CurrentUser) ||
                    !DatabaseHelper.CanApproveResults(Login.CurrentUser))
                {
                    throw new UnauthorizedAccessException(
                        "QA approval permission is required to reconcile historical EM limit evidence.");
                }

                List<EmReconciliationCandidate> candidates = LoadHistoricalEmReconciliationCandidates();
                if (candidates.Count == 0)
                {
                    MessageBox.Show(
                        "No unresolved historical EM events were found. Run System Preflight again to refresh the report.",
                        "EM Reconciliation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    await RunPreflightAsync();
                    return;
                }

                EmReconciliationCandidate? selected = SelectHistoricalEmEvent(candidates);
                if (selected == null)
                    return;

                var reconciliation = new EMLegacySnapshotReconciliation(selected.EventId, selected.EventNo)
                {
                    Owner = this
                };
                reconciliation.ShowDialog();

                if (reconciliation.WasSaved)
                    await RunPreflightAsync();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open controlled historical EM reconciliation from System Preflight.", ex);
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Historical EM snapshot reconciliation"),
                    "EM Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnCertificateReconcile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool hasFinding = _lastReport?.Checks.Any(check =>
                    (string.Equals(check.Status, "WARNING", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(check.Status, "BLOCKER", StringComparison.OrdinalIgnoreCase)) &&
                    (string.Equals(check.Check, "Legacy PRM certificate evidence", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(check.Check, "Legacy certificate snapshot evidence", StringComparison.OrdinalIgnoreCase))) == true;

                if (!hasFinding)
                {
                    MessageBox.Show(
                        "No unresolved legacy certificate evidence finding is present in the current preflight report.",
                        "Certificate Reconciliation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (string.IsNullOrWhiteSpace(Login.CurrentUser) ||
                    !DatabaseHelper.CanApproveResults(Login.CurrentUser))
                {
                    throw new UnauthorizedAccessException(
                        "QA approval permission is required to reconcile legacy certificate evidence.");
                }

                var reconciliation = new LegacyCertificateEvidenceReconciliation
                {
                    Owner = this
                };
                reconciliation.ShowDialog();

                if (reconciliation.WasSaved || reconciliation.RequiresPreflightRefresh)
                    await RunPreflightAsync();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open controlled legacy certificate reconciliation from System Preflight.", ex);
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Legacy certificate evidence reconciliation"),
                    "Certificate Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static List<EmReconciliationCandidate> LoadHistoricalEmReconciliationCandidates()
        {
            DataTable table = DatabaseHelper.ExecuteQuery(@"
;WITH EffectiveEvidence AS
(
    SELECT P.Id,
           P.EventId,
           CASE WHEN P.AlertLimitSnapshot IS NOT NULL AND P.ActionLimitSnapshot IS NOT NULL
                      AND NULLIF(LTRIM(RTRIM(ISNULL(P.ResultUnitSnapshot,N''))),N'') IS NOT NULL
                      AND (UPPER(LTRIM(RTRIM(P.Method)))<>N'ACTIVE AIR SAMPLING' OR ISNULL(P.AirVolumeLitersSnapshot,0)>0)
                THEN 1
                WHEN R.ReconciliationID IS NOT NULL THEN 1
                ELSE 0 END AS EvidenceComplete
    FROM dbo.EM_EventPlates P
    OUTER APPLY
    (
        SELECT TOP(1) X.ReconciliationID
        FROM dbo.EM_LimitSnapshotReconciliations X
        WHERE X.PlateID=P.Id
        ORDER BY X.ReconciliationID DESC
    ) R
)
SELECT E.Id AS EventID,
       ISNULL(NULLIF(LTRIM(RTRIM(E.EventNo)),N''),N'EventID ' + CONVERT(nvarchar(20),E.Id)) AS EventNo,
       ISNULL(NULLIF(LTRIM(RTRIM(E.WorkflowStatus)),N''),N'Unknown') AS WorkflowStatus,
       COUNT_BIG(*) AS UnresolvedPlateCount
FROM EffectiveEvidence X
INNER JOIN dbo.EM_Events E ON E.Id=X.EventId
WHERE X.EvidenceComplete=0
GROUP BY E.Id,E.EventNo,E.WorkflowStatus
ORDER BY E.EventNo,E.Id;");

            List<EmReconciliationCandidate> eventCandidates = table.Rows.Cast<DataRow>()
                .Select(row => new EmReconciliationCandidate
                {
                    EventId = Convert.ToInt32(row["EventID"], CultureInfo.InvariantCulture),
                    EventNo = Convert.ToString(row["EventNo"], CultureInfo.InvariantCulture) ?? string.Empty,
                    WorkflowStatus = Convert.ToString(row["WorkflowStatus"], CultureInfo.InvariantCulture) ?? string.Empty,
                    UnresolvedPlateCount = Convert.ToInt64(row["UnresolvedPlateCount"], CultureInfo.InvariantCulture)
                })
                .ToList();

            if (eventCandidates.Count > 1)
            {
                eventCandidates.Insert(0, new EmReconciliationCandidate
                {
                    EventId = 0,
                    EventNo = "ALL UNRESOLVED HISTORICAL EVENTS (batch mode)",
                    WorkflowStatus = "Mixed statuses",
                    UnresolvedPlateCount = eventCandidates.Sum(candidate => candidate.UnresolvedPlateCount)
                });
            }

            return eventCandidates;
        }

        private EmReconciliationCandidate? SelectHistoricalEmEvent(IReadOnlyList<EmReconciliationCandidate> candidates)
        {
            var dialog = new Window
            {
                Title = "Select Historical EM Event",
                Owner = this,
                Width = 620,
                Height = 520,
                MinWidth = 520,
                MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize
            };

            var layout = new Grid { Margin = new Thickness(16) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var instruction = new TextBlock
            {
                Text = "Select any unresolved historical EM event, including Approved/Completed/Closed records, or choose ALL UNRESOLVED HISTORICAL EVENTS for controlled batch reconciliation. " +
                       "Only controlled historical evidence may be entered. Batch mode groups plates with the same Grade + Method; every plate still receives an independent append-only signed reconciliation record and the original EM records are never rewritten.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(instruction, 0);
            layout.Children.Add(instruction);

            var list = new ListBox
            {
                ItemsSource = candidates,
                DisplayMemberPath = nameof(EmReconciliationCandidate.Display),
                MinHeight = 260
            };
            if (candidates.Count > 0)
                list.SelectedIndex = 0;
            Grid.SetRow(list, 1);
            layout.Children.Add(list);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var open = new Button
            {
                Content = "Open Reconciliation",
                Width = 160,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancel = new Button
            {
                Content = "Cancel",
                Width = 90,
                Height = 34,
                IsCancel = true
            };

            open.Click += (_, _) =>
            {
                if (list.SelectedItem is not EmReconciliationCandidate)
                {
                    MessageBox.Show(
                        "Select an EM event first.",
                        "EM Reconciliation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                dialog.DialogResult = true;
            };
            actions.Children.Add(open);
            actions.Children.Add(cancel);
            Grid.SetRow(actions, 2);
            layout.Children.Add(actions);

            dialog.Content = layout;
            return dialog.ShowDialog() == true
                ? list.SelectedItem as EmReconciliationCandidate
                : null;
        }

        private sealed class EmReconciliationCandidate
        {
            public int EventId { get; init; }
            public string EventNo { get; init; } = string.Empty;
            public string WorkflowStatus { get; init; } = string.Empty;
            public long UnresolvedPlateCount { get; init; }
            public string Display => $"{EventNo} — {WorkflowStatus} — {UnresolvedPlateCount} unresolved plate(s)";
        }

        private void BtnCopyReleaseChecklist_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildReleaseAcceptanceChecklist(_lastReport));
                MessageBox.Show(
                    "Release/UAT checklist copied. Complete it with real Windows and SQL Server evidence; copying the checklist does not mark any item as passed.",
                    "Release Acceptance Checklist",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to copy the release acceptance checklist.", ex);
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Release acceptance checklist"),
                    "Release Acceptance Checklist",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string BuildReleaseAcceptanceChecklist(SystemPreflightReport? report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PharmaLIMS RELEASE / UAT ACCEPTANCE CHECKLIST");
            sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("Environment: " + AppConfig.EnvironmentName);
            sb.AppendLine("Target: " + AppConfig.SqlServerName + " / " + AppConfig.DatabaseName);
            if (report == null)
            {
                sb.AppendLine("Current System Preflight: NOT RUN IN THIS WINDOW");
            }
            else
            {
                sb.AppendLine("Current System Preflight: " +
                    (report.BlockerCount == 0 ? "PASS" : "BLOCKED") +
                    " | Blockers=" + report.BlockerCount.ToString(CultureInfo.InvariantCulture) +
                    " | Warnings=" + report.WarningCount.ToString(CultureInfo.InvariantCulture));
            }

            sb.AppendLine();
            sb.AppendLine("CONTROL NOTICE");
            sb.AppendLine("This is a manual acceptance record. Static/source tests and System Preflight do not prove Windows WPF build, SQL migration execution, concurrency, printing, or end-to-end UAT.");
            sb.AppendLine();
            sb.AppendLine("DATABASE READINESS");
            sb.AppendLine("[ ] All checksum-controlled migrations were applied to the intended SQL Server database.");
            sb.AppendLine("[ ] System Preflight completed with 0 BLOCKER findings.");
            sb.AppendLine("[ ] Users.AuthenticationRowVersion is ROWVERSION NOT NULL.");
            sb.AppendLine("[ ] Users.MustChangePassword is BIT NOT NULL.");
            sb.AppendLine("[ ] Users.PasswordChangedAt is DATETIME2(0) NULL.");
            sb.AppendLine("[ ] EM_Events.ResultRowVersion is ROWVERSION NOT NULL.");
            sb.AppendLine("[ ] EM_EventPlates.ResultRowVersion is ROWVERSION NOT NULL.");
            sb.AppendLine("[ ] SampleTests.ResultRowVersion is ROWVERSION NOT NULL.");
            sb.AppendLine();
            sb.AppendLine("WINDOWS / SQL SERVER VALIDATION");
            sb.AppendLine("[ ] Clean Release build succeeds on the intended Windows .NET 8 WPF environment.");
            sb.AppendLine("[ ] DatabaseIntegration / ReviewRegression executables pass against a representative SQL Server/LocalDB test database.");
            sb.AppendLine("[ ] Login, legacy-credential transition, lockout, unlock, temporary-password change, and electronic signature scenarios pass.");
            sb.AppendLine("[ ] User Management create/update/permissions/reset/unlock/concurrency and separation-of-duties scenarios pass.");
            sb.AppendLine("[ ] Water registration/results/review/approval/Quality Event/certificate workflows pass.");
            sb.AppendLine("[ ] EM planning/collection/results/review/approval/reporting and concurrent-edit scenarios pass.");
            sb.AppendLine("[ ] PRM registration/results/review/approval/Quality Event/certificate/reissue workflows pass.");
            sb.AppendLine("[ ] Audit Trail evidence is attributable and complete for the tested critical actions.");
            sb.AppendLine("[ ] COA / PRM / EM / Trend reports render and print correctly on A4 at normal Windows scaling/printer settings.");
            sb.AppendLine("[ ] UI load/responsiveness evidence completed with the site-approved representative data volume (including Sample Management, Results Entry, EM, and Reports); no unhandled exception, data loss, or unacceptable UI freeze.");
            sb.AppendLine("[ ] Concurrent-user load evidence completed against representative SQL Server infrastructure and reviewed against the approved performance acceptance criteria.");
            sb.AppendLine("[ ] QA reviewed the executed evidence and approved release/deployment.");
            sb.AppendLine();
            sb.AppendLine("Executed By: ____________________    Date: __________");
            sb.AppendLine("Reviewed By: ____________________    Date: __________");
            sb.AppendLine("QA Approval: ____________________    Date: __________");
            return sb.ToString();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_lastReport == null)
            {
                MessageBox.Show("Run the preflight first.", "System Preflight", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Clipboard.SetText(BuildTextReport(_lastReport));
                MessageBox.Show("Preflight report copied to the clipboard.", "System Preflight", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Copying the system preflight report failed.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Copy preflight report"), "System Preflight", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string BuildTextReport(SystemPreflightReport report)
        {
            var text = new StringBuilder();
            text.AppendLine("PharmaLIMS Runtime System Preflight");
            text.AppendLine($"Executed: {report.ExecutedAt:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"Build: {typeof(SystemPreflight).Assembly.GetName().Version?.ToString() ?? "unknown"}");
            text.AppendLine($"Target: {report.Server} / {report.Database}");
            text.AppendLine($"Blockers: {report.BlockerCount}; Warnings: {report.WarningCount}; Passed: {report.PassCount}");
            text.AppendLine(new string('-', 88));

            foreach (SystemPreflightCheck check in report.Checks)
                text.AppendLine($"[{check.Status}] {check.Area} | {check.Check} | {check.Details}");

            return text.ToString();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
