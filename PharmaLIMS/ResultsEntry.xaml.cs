#nullable disable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PharmaLIMS.Infrastructure;

namespace PharmaLIMS
{
    public partial class ResultsEntry : Window
    {
        private readonly IAuthService _authService;
        private readonly IServiceProvider _serviceProvider;

        private int currentSampleId = 0;
        private string currentUser = "";
        private string currentSampleType = "";
        private string currentPointCode = "";
        private string currentPointLocation = "";
        private readonly List<ResultItem> resultItems = new List<ResultItem>();
        private bool _isSavingResults = false;
        private bool _isWorkflowBusy = false;
        private bool _legacyCertificateReissueRouteActive = false;
        private int _legacyReissueSampleId = 0;
        private int _legacyReissueCertificateId = 0;
        private int _legacyReconciliationId = 0;
        private string _legacyCertificateNumber = "";
        private string _legacyReissueReason = "";

        public class ResultItem
        {
            public int SampleTestID { get; set; }
            public int TestID { get; set; }
            public string TestName { get; set; } = "";
            public string Unit { get; set; } = "";
            public decimal? AlertLimit { get; set; }
            public decimal? ActionLimit { get; set; }
            public string ResultValue { get; set; } = "";
            public string PassFail { get; set; } = "";
            public string Remarks { get; set; } = "";
        }

        private sealed class WaterTimestampSnapshot
        {
            public DateTime? SamplingDateTime { get; set; }
            public DateTime? ReceivedDateTime { get; set; }
            public DateTime? IncubationStartedDateTime { get; set; }
            public DateTime? IncubationEndDate { get; set; }
            public DateTime? AnalysisStartedDateTime { get; set; }
            public DateTime? AnalysisCompletedDateTime { get; set; }
        }

        public ResultsEntry(IAuthService authService, IServiceProvider serviceProvider)
        {
            InitializeComponent();
            _authService = authService;
            _serviceProvider = serviceProvider;

            currentUser = GetCurrentUserName();
            dgResults.CellEditEnding += dgResults_CellEditEnding;
            SetSampleStatus("");
            UpdateWorkflowButtons();
            lblStatus.Text = "Ready";
        }

        public void ConfigureLegacyCertificateReissueContext(
            int sampleId,
            int certificateId,
            int reconciliationId,
            string certificateNumber,
            string reconciliationReason)
        {
            if (sampleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleId));
            if (certificateId <= 0)
                throw new ArgumentOutOfRangeException(nameof(certificateId));
            if (reconciliationId <= 0)
                throw new ArgumentOutOfRangeException(nameof(reconciliationId));
            if (string.IsNullOrWhiteSpace(certificateNumber))
                throw new ArgumentException("Legacy certificate number is required.", nameof(certificateNumber));
            if (string.IsNullOrWhiteSpace(reconciliationReason))
                throw new ArgumentException("Signed reconciliation reason is required.", nameof(reconciliationReason));

            _legacyCertificateReissueRouteActive = true;
            _legacyReissueSampleId = sampleId;
            _legacyReissueCertificateId = certificateId;
            _legacyReconciliationId = reconciliationId;
            _legacyCertificateNumber = certificateNumber.Trim();
            _legacyReissueReason = BuildLegacyReissueReason(reconciliationReason);
        }

        private string BuildLegacyReissueReason(string reconciliationReason)
        {
            string baseReason = reconciliationReason == null ? string.Empty : reconciliationReason.Trim();
            string trace = $"Legacy Certificate Evidence Reconciliation #{_legacyReconciliationId}; legacy certificate {_legacyCertificateNumber}.";
            return string.IsNullOrWhiteSpace(baseReason) ? trace : baseReason + " " + trace;
        }

        private (bool TargetActive, bool TargetCancelled, bool ReplacementComplete) GetLegacyReissueRouteState()
        {
            if (!_legacyCertificateReissueRouteActive || currentSampleId <= 0)
                return (false, false, false);

            SqlParameter[] parameters =
            {
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = currentSampleId },
                new SqlParameter("@CertificateID", SqlDbType.Int) { Value = _legacyReissueCertificateId }
            };

            DataTable state = DatabaseHelper.ExecuteQuery(@"
SELECT
    CASE WHEN EXISTS
    (
        SELECT 1 FROM dbo.Certificates c
        WHERE c.CertificateID=@CertificateID AND c.SampleID=@SampleID
          AND ISNULL(c.IsCancelled,0)=0
          AND UPPER(ISNULL(c.CertificateStatus,ISNULL(c.Status,N''))) IN(N'ACTIVE',N'ISSUED')
    ) THEN 1 ELSE 0 END AS TargetActive,
    CASE WHEN EXISTS
    (
        SELECT 1 FROM dbo.Certificates c
        WHERE c.CertificateID=@CertificateID AND c.SampleID=@SampleID
          AND (ISNULL(c.IsCancelled,0)=1 OR UPPER(ISNULL(c.CertificateStatus,ISNULL(c.Status,N'')))=N'CANCELLED')
    ) THEN 1 ELSE 0 END AS TargetCancelled,
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM dbo.Certificates r
        INNER JOIN dbo.CertificateDocumentSnapshots s ON s.CertificateID=r.CertificateID
        WHERE r.SampleID=@SampleID
          AND r.ReissuedFromCertificateID=@CertificateID
          AND ISNULL(r.IsCancelled,0)=0
          AND UPPER(ISNULL(r.CertificateStatus,ISNULL(r.Status,N''))) IN(N'ACTIVE',N'ISSUED')
          AND LEN(ISNULL(r.ReportHash,N''))=64
          AND LEN(ISNULL(s.SnapshotHash,N''))=64
    ) THEN 1 ELSE 0 END AS ReplacementComplete;", parameters);

            if (state.Rows.Count != 1)
                throw new InvalidOperationException("The legacy certificate reissue state could not be verified.");

            DataRow row = state.Rows[0];
            return (
                Convert.ToInt32(row["TargetActive"], CultureInfo.InvariantCulture) == 1,
                Convert.ToInt32(row["TargetCancelled"], CultureInfo.InvariantCulture) == 1,
                Convert.ToInt32(row["ReplacementComplete"], CultureInfo.InvariantCulture) == 1);
        }

        private void ApplyLegacyReissueRouteUi(string status, bool canCertificate, bool hasOpenQualityEvent)
        {
            if (!_legacyCertificateReissueRouteActive || currentSampleId != _legacyReissueSampleId)
                return;

            var routeState = GetLegacyReissueRouteState();
            string documentName = IsPotableWaterSample() ? "Report" : "COA";

            if (routeState.ReplacementComplete)
            {
                BtnCancelCOA.Content = $"Legacy {documentName} Reissued";
                BtnCancelCOA.IsEnabled = false;
                BtnCertificate.Content = $"Open Replacement {documentName}";
                BtnCertificate.IsEnabled = canCertificate;
                return;
            }

            if (routeState.TargetActive)
            {
                BtnCancelCOA.Content = $"Cancel Legacy {documentName} (Step 1)";
                BtnCancelCOA.IsEnabled = CanCancelCertificate();
                BtnCertificate.Content = $"Issue Replacement {documentName} (Step 2)";
                BtnCertificate.IsEnabled = false;
                return;
            }

            if (routeState.TargetCancelled)
            {
                BtnCancelCOA.Content = $"Legacy {documentName} Cancelled";
                BtnCancelCOA.IsEnabled = false;
                BtnCertificate.Content = $"Issue Replacement {documentName} (Step 2)";
                BtnCertificate.IsEnabled = canCertificate && !hasOpenQualityEvent && status == "COA Cancelled";
                return;
            }

            BtnCancelCOA.Content = "Legacy Reissue Blocked";
            BtnCancelCOA.IsEnabled = false;
            BtnCertificate.Content = "Legacy Reissue Blocked";
            BtnCertificate.IsEnabled = false;
            lblStatus.Text = "Controlled legacy reissue is blocked because the signed target certificate state no longer matches the routed workflow.";
        }

        private string GetCurrentUserName()
        {
            var user = _authService.GetCurrentUser();
            if (user != null && !string.IsNullOrWhiteSpace(user.Username))
                return user.Username.Trim();
            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                return Login.CurrentUser.Trim();

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required to enter, review, or approve results.");
        }

        private string GetSignatureRecordNumber()
        {
            string sampleNumber = lblSampleNumber?.Text?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(sampleNumber)
                ? currentSampleId.ToString(CultureInfo.InvariantCulture)
                : sampleNumber;
        }

        // Critical workflow authorization is read fresh from the database. Role labels and
        // the process-global Login flags are display/navigation hints, not an authorization
        // boundary. The only administrative override is the explicitly development-only
        // override enforced inside DatabaseHelper.
        private bool CanEnterResults() => DatabaseHelper.CanEditResults(currentUser);
        private bool CanStartAnalysis() => DatabaseHelper.CanEditResults(currentUser);
        private bool CanCorrectSampleTimes() => DatabaseHelper.CanRegisterSamples(currentUser);
        private bool CanSubmitForReview() => DatabaseHelper.CanSubmitForReview(currentUser);
        private bool CanReviewSample() => DatabaseHelper.CanReviewResults(currentUser);
        private bool CanApproveSample() => DatabaseHelper.CanApproveResults(currentUser);
        private bool CanOpenOrIssueCertificate() => DatabaseHelper.CanIssueCertificate(currentUser);
        private bool CanCancelCertificate() => DatabaseHelper.CanCancelCertificate(currentUser);

        private void EnsureGlobalWaterDateColumns()
        {
            object count = DatabaseHelper.ExecuteScalar(@"
SELECT COUNT(1)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = N'dbo'
  AND TABLE_NAME = N'Samples'
  AND COLUMN_NAME IN
  (
      N'ReceivedDateTime', N'AnalysisStartedDateTime', N'AnalysisCompletedDateTime',
      N'IncubationStartedDateTime', N'IncubationCompletedDateTime',
      N'ReviewedDateTime', N'ApprovedDateTime'
  );");

            if (Convert.ToInt32(count, CultureInfo.InvariantCulture) != 7)
            {
                throw new InvalidOperationException(
                    "Required water workflow timestamp columns are missing. Apply Database/Migrations/20260722_003_Resolve_Project_Complexities.sql.");
            }
        }

        private bool HasManualDate(string columnName)
        {
            if (currentSampleId <= 0)
                return false;

            EnsureGlobalWaterDateColumns();

            try
            {
                SqlParameter[] pars =
                {
                    new SqlParameter("@SampleID", currentSampleId)
                };

                object result = DatabaseHelper.ExecuteScalar(
                    "SELECT " + columnName + " FROM dbo.Samples WHERE SampleID = @SampleID", pars);

                return result != null && result != DBNull.Value;
            }
            catch
            {
                return false;
            }
        }

        private void ValidateManualWaterDatesBeforeAnalysis()
        {
            if (!HasManualDate("SamplingDateTime"))
                throw new InvalidOperationException("Sampling Date / Time is not recorded. Enter the actual sampling time in sample registration.");

            if (!HasManualDate("ReceivedDateTime"))
                throw new InvalidOperationException("Received in Lab Date / Time is not recorded. Enter the actual lab receipt time in sample registration.");

            if (!HasManualDate("AnalysisStartedDateTime"))
                throw new InvalidOperationException("Analysis Started Date / Time is not recorded. Use Start Analysis before entering results.");
        }

        private bool ValidateWaterIncubationCompleteBeforeResultsInTransaction(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            List<int> enteredTestIds = resultItems
                .Where(item => !string.IsNullOrWhiteSpace(item.ResultValue))
                .Select(item => item.TestID)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (enteredTestIds.Count == 0) return false;

            object microCount = ExecuteScalarInTransaction(connection, transaction, @"
SELECT COUNT(1)
FROM dbo.SampleTests st LEFT JOIN dbo.Tests t ON t.TestID=st.TestID
WHERE st.SampleID=@SampleID
  AND st.TestID IN (" + string.Join(",", enteredTestIds) + @")
  AND UPPER(ISNULL(CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.TestCategorySnapshot ELSE t.TestCategory END,N'')) LIKE N'%MICRO%';",
                new[] { new SqlParameter("@SampleID", currentSampleId) });

            if (Convert.ToInt32(microCount, CultureInfo.InvariantCulture) <= 0)
                return false;

            object endValue = ExecuteScalarInTransaction(connection, transaction,
                "SELECT IncubationEndDate FROM dbo.Samples WHERE SampleID=@SampleID",
                new[] { new SqlParameter("@SampleID", currentSampleId) });

            if (endValue == null || endValue == DBNull.Value)
                throw new InvalidOperationException("Incubation End Date / Time is not recorded for this microbiological water sample.");

            DateTime incubationEnd = Convert.ToDateTime(endValue, CultureInfo.InvariantCulture);
            DateTime serverNow = Convert.ToDateTime(
                ExecuteScalarInTransaction(connection, transaction, "SELECT SYSDATETIME();"),
                CultureInfo.InvariantCulture);
            if (serverNow < incubationEnd)
            {
                if (AppConfig.AllowEarlyMicrobiologyResults)
                {
                    ApplicationLogger.Warning(
                        "Development-only early microbiology result entry was used for water sample " +
                        GetSignatureRecordNumber() + ". Recorded incubation end: " +
                        incubationEnd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");
                    return true;
                }

                throw new InvalidOperationException(
                    "Microbiological incubation is not complete. Results cannot be entered before " +
                    incubationEnd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");

            }

            return false;
        }

        private DateTime? GetSampleDateTime(string columnName)
        {
            if (currentSampleId <= 0)
                return null;

            EnsureGlobalWaterDateColumns();
            SqlParameter[] pars = { new SqlParameter("@SampleID", currentSampleId) };
            object value = DatabaseHelper.ExecuteScalar(
                "SELECT " + columnName + " FROM dbo.Samples WHERE SampleID = @SampleID", pars);

            return value == null || value == DBNull.Value
                ? null
                : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        }

        private bool TryPromptForAnalysisStart(out DateTime analysisStartedAt)
        {
            analysisStartedAt = default;
            DateTime now = DatabaseHelper.GetAuthoritativeDatabaseTime();
            DateTime? samplingDateTime = GetSampleDateTime("SamplingDateTime");
            DateTime? receivedDateTime = GetSampleDateTime("ReceivedDateTime");

            if (!samplingDateTime.HasValue)
                throw new InvalidOperationException("Sampling Date / Time is not recorded for this sample.");
            if (!receivedDateTime.HasValue)
                throw new InvalidOperationException("Received in Lab Date / Time is not recorded for this sample.");

            Window dialog = new Window
            {
                Title = "Record Analysis Start",
                Owner = this,
                Width = 440,
                Height = 245,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            Grid layout = new Grid { Margin = new Thickness(18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock instruction = new TextBlock
            {
                Text = "Enter the actual date and time when analysis/work started.",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(instruction, 0);
            Grid.SetColumnSpan(instruction, 2);
            layout.Children.Add(instruction);

            TextBlock dateLabel = new TextBlock { Text = "Start Date", VerticalAlignment = VerticalAlignment.Center };
            DatePicker datePicker = new DatePicker { SelectedDate = now.Date, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(dateLabel, 1);
            Grid.SetColumn(dateLabel, 0);
            Grid.SetRow(datePicker, 1);
            Grid.SetColumn(datePicker, 1);
            layout.Children.Add(dateLabel);
            layout.Children.Add(datePicker);

            TextBlock timeLabel = new TextBlock { Text = "Start Time (HH:mm)", VerticalAlignment = VerticalAlignment.Center };
            TextBox timeBox = new TextBox { Text = now.ToString("HH:mm", CultureInfo.InvariantCulture), Height = 30 };
            Grid.SetRow(timeLabel, 2);
            Grid.SetColumn(timeLabel, 0);
            Grid.SetRow(timeBox, 2);
            Grid.SetColumn(timeBox, 1);
            layout.Children.Add(timeLabel);
            layout.Children.Add(timeBox);

            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            Button confirmButton = new Button { Content = "Continue", Width = 100, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            Button cancelButton = new Button { Content = "Cancel", Width = 90, Height = 32, IsCancel = true };
            actions.Children.Add(confirmButton);
            actions.Children.Add(cancelButton);
            Grid.SetRow(actions, 3);
            Grid.SetColumnSpan(actions, 2);
            layout.Children.Add(actions);

            DateTime selectedStart = default;
            confirmButton.Click += (buttonSender, buttonArgs) =>
            {
                if (!datePicker.SelectedDate.HasValue ||
                    !DateTime.TryParseExact(
                        timeBox.Text.Trim(),
                        new[] { "H:mm", "HH:mm" },
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime parsedTime))
                {
                    MessageBox.Show("Enter a valid date and time using HH:mm format.",
                        "Analysis Start", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                selectedStart = datePicker.SelectedDate.Value.Date.Add(parsedTime.TimeOfDay);

                if (selectedStart < samplingDateTime.Value || selectedStart < receivedDateTime.Value)
                {
                    MessageBox.Show("Analysis Start cannot be earlier than Sampling or Received in Lab time.",
                        "Analysis Start", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DateTime databaseNow = DatabaseHelper.GetAuthoritativeDatabaseTime();
                if (selectedStart > databaseNow.AddMinutes(1))
                {
                    MessageBox.Show("Analysis Start cannot be in the future according to the authoritative database clock.",
                        "Analysis Start", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                dialog.DialogResult = true;
            };

            dialog.Content = layout;
            if (dialog.ShowDialog() != true)
                return false;

            analysisStartedAt = selectedStart;
            return true;
        }

        private void MarkAnalysisCompletedNow()
        {
            if (currentSampleId <= 0)
                return;

            EnsureGlobalWaterDateColumns();

            try
            {
                SqlParameter[] pars =
                {
                    new SqlParameter("@SampleID", currentSampleId)
                };

                DatabaseHelper.ExecuteNonQuery(@"
                    UPDATE dbo.Samples
                    SET AnalysisCompletedDateTime = GETDATE()
                    WHERE SampleID = @SampleID", pars);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to update AnalysisCompletedDateTime for the current water sample.", ex);
            }
        }


        private void MarkReviewedNow()
        {
            if (currentSampleId <= 0)
                return;

            EnsureGlobalWaterDateColumns();

            SqlParameter[] pars =
            {
                new SqlParameter("@SampleID", currentSampleId)
            };

            DatabaseHelper.ExecuteNonQuery(@"
                UPDATE dbo.Samples
                SET ReviewedDateTime = GETDATE()
                WHERE SampleID = @SampleID", pars);
        }

        private void MarkApprovedNow()
        {
            if (currentSampleId <= 0)
                return;

            EnsureGlobalWaterDateColumns();

            SqlParameter[] pars =
            {
                new SqlParameter("@SampleID", currentSampleId)
            };

            DatabaseHelper.ExecuteNonQuery(@"
                UPDATE dbo.Samples
                SET ApprovedDateTime = GETDATE()
                WHERE SampleID = @SampleID", pars);
        }

        private void AddSampleElectronicSignature(string actionType, string meaningOfSignature, string reason)
        {
            if (currentSampleId <= 0)
                return;

            DatabaseHelper.AddElectronicSignature(
                currentSampleId,
                actionType,
                currentUser,
                meaningOfSignature,
                string.IsNullOrWhiteSpace(reason) ? actionType : reason);
        }


        private void ExecuteInLocalTransaction(Action<SqlConnection, SqlTransaction> action)
        {
            using (SqlConnection con = new SqlConnection(AppConfig.ConnectionString))
            {
                con.Open();

                using (SqlTransaction tran = con.BeginTransaction())
                {
                    try
                    {
                        action(con, tran);
                        tran.Commit();
                    }
                    catch
                    {
                        tran.Rollback();
                        throw;
                    }
                }
            }
        }

        private int ExecuteNonQueryInTransaction(SqlConnection con, SqlTransaction tran, string sql, params SqlParameter[] parameters)
        {
            using (SqlCommand cmd = new SqlCommand(sql, con, tran))
            {
                cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;

                if (parameters != null && parameters.Length > 0)
                    cmd.Parameters.AddRange(parameters);

                return cmd.ExecuteNonQuery();
            }
        }



        private void AddSampleElectronicSignatureInTransaction(
            SqlConnection con,
            SqlTransaction tran,
            string actionType,
            string meaningOfSignature,
            string reason,
            string signedBy,
            string signerRole)
        {
            if (currentSampleId <= 0)
                return;

            if (string.IsNullOrWhiteSpace(signedBy))
                throw new UnauthorizedAccessException("An authenticated signer is required for the water workflow electronic signature.");

            ExecuteNonQueryInTransaction(con, tran, @"
                INSERT INTO dbo.ElectronicSignatures
                (
                    SampleID,
                    ActionType,
                    ActionReason,
                    SignedBy,
                    MeaningOfSignature,
                    UserRole,
                    SignedAt
                )
                VALUES
                (
                    @sampleId,
                    @actionType,
                    @reason,
                    @signedBy,
                    @meaning,
                    @role,
                    GETDATE()
                )",
                new SqlParameter("@sampleId", currentSampleId),
                new SqlParameter("@actionType", string.IsNullOrWhiteSpace(actionType) ? (object)DBNull.Value : actionType),
                new SqlParameter("@reason", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),
                new SqlParameter("@signedBy", signedBy.Trim()),
                new SqlParameter("@meaning", string.IsNullOrWhiteSpace(meaningOfSignature) ? (object)DBNull.Value : meaningOfSignature),
                new SqlParameter("@role", string.IsNullOrWhiteSpace(signerRole) ? (object)DBNull.Value : signerRole.Trim()));
        }

        private string LockAndValidateSampleStatusInTransaction(
            SqlConnection con,
            SqlTransaction tran,
            string expectedStatus,
            string operationName)
        {
            object currentValue = ExecuteScalarInTransaction(con, tran, @"
                SELECT ISNULL(LTRIM(RTRIM(Status)), N'')
                FROM dbo.Samples WITH (UPDLOCK, HOLDLOCK)
                WHERE SampleID = @sampleId",
                new SqlParameter("@sampleId", currentSampleId));

            if (currentValue == null || currentValue == DBNull.Value)
            {
                throw new InvalidOperationException(
                    "The sample no longer exists. " + operationName + " was cancelled.");
            }

            string currentStatus = Convert.ToString(currentValue, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string normalizedExpected = (expectedStatus ?? string.Empty).Trim();

            if (!currentStatus.Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The sample status changed while the electronic signature was being completed. " +
                    operationName + " was cancelled to protect data integrity. Expected: " +
                    normalizedExpected + "; Current: " + currentStatus + ". Reload the sample and try again.");
            }

            return currentStatus;
        }

        private void UpdateSampleStatusInTransaction(
            SqlConnection con,
            SqlTransaction tran,
            string status,
            string expectedStatus,
            string operationName)
        {
            int affected = ExecuteNonQueryInTransaction(con, tran, @"
                UPDATE dbo.Samples
                SET Status = @status
                WHERE SampleID = @sampleId
                  AND ISNULL(LTRIM(RTRIM(Status)), N'') = @expectedStatus",
                new SqlParameter("@sampleId", currentSampleId),
                new SqlParameter("@status", status),
                new SqlParameter("@expectedStatus", (expectedStatus ?? string.Empty).Trim()));

            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "The sample status changed during " + operationName +
                    ". No workflow change was committed. Reload the sample and try again.");
            }
        }

        private void MarkAnalysisCompletedNowInTransaction(SqlConnection con, SqlTransaction tran)
        {
            EnsureGlobalWaterDateColumns();

            ExecuteNonQueryInTransaction(con, tran, @"
                UPDATE dbo.Samples
                SET AnalysisCompletedDateTime = GETDATE()
                WHERE SampleID = @sampleId AND AnalysisCompletedDateTime IS NULL",
                new SqlParameter("@sampleId", currentSampleId));
        }

        private void MarkReviewedNowInTransaction(SqlConnection con, SqlTransaction tran)
        {
            EnsureGlobalWaterDateColumns();

            ExecuteNonQueryInTransaction(con, tran, @"
                UPDATE dbo.Samples
                SET ReviewedDateTime = GETDATE()
                WHERE SampleID = @sampleId",
                new SqlParameter("@sampleId", currentSampleId));
        }

        private void MarkApprovedNowInTransaction(SqlConnection con, SqlTransaction tran)
        {
            EnsureGlobalWaterDateColumns();

            ExecuteNonQueryInTransaction(con, tran, @"
                UPDATE dbo.Samples
                SET ApprovedDateTime = GETDATE()
                WHERE SampleID = @sampleId",
                new SqlParameter("@sampleId", currentSampleId));
        }

        private int GetPendingResultsCountInTransaction(SqlConnection con, SqlTransaction tran)
        {
            object result = ExecuteScalarInTransaction(con, tran, @"
                SELECT COUNT(*)
                FROM dbo.SampleTests
                WHERE SampleID = @sampleId
                  AND ResultValue IS NULL",
                new SqlParameter("@sampleId", currentSampleId));

            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        private void SetButtonBusy(Button button, bool busy, string busyText, string normalText)
        {
            if (button == null)
                return;

            button.IsEnabled = !busy;
            button.Content = busy ? busyText : normalText;
        }

        private bool IsWaterSampleType(string sampleType)
        {
            sampleType = sampleType == null ? "" : sampleType.Trim().ToLowerInvariant();
            return sampleType == "purified water" ||
                   sampleType == "potable water" ||
                   sampleType == "purified" ||
                   sampleType == "potable" ||
                   sampleType.Contains("pws") ||
                   sampleType.Contains("ptws") ||
                   sampleType.Contains("water");
        }

        private bool IsEnvironmentalSampleType(string sampleType)
        {
            sampleType = sampleType == null ? "" : sampleType.Trim().ToLowerInvariant();
            return sampleType.Contains("environment") ||
                   sampleType.Contains("em") ||
                   sampleType.Contains("settle") ||
                   sampleType.Contains("active air");
        }

        private bool IsPurifiedWaterSample()
        {
            string sampleType = currentSampleType == null ? "" : currentSampleType.Trim().ToLowerInvariant();
            return sampleType == "purified water" ||
                   sampleType == "purified" ||
                   sampleType.Contains("pws") ||
                   sampleType.Contains("purified");
        }

        private bool IsPotableWaterSample()
        {
            string sampleType = currentSampleType == null ? "" : currentSampleType.Trim().ToLowerInvariant();
            return sampleType == "potable water" ||
                   sampleType == "potable" ||
                   sampleType.Contains("ptws") ||
                   sampleType.Contains("potable") ||
                   sampleType.Contains("drinking");
        }

        private int ExtractPointNumber(string pointCode)
        {
            pointCode = pointCode == null ? "" : pointCode.Trim();
            if (string.IsNullOrWhiteSpace(pointCode))
                return 0;

            Match match = Regex.Match(pointCode, @"(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int pointNumber))
                return pointNumber;

            return 0;
        }

        private bool IsSoftWaterPoint()
        {
            if (!IsPotableWaterSample())
                return false;
            int pointNumber = ExtractPointNumber(currentPointCode);
            return pointNumber >= 5;
        }

        private bool IsConductivityTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName.Contains("conductivity");
        }

        private bool IsHardnessTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName.Contains("hardness") ||
                   (testName.Contains("calcium") && testName.Contains("magnesium"));
        }

        private bool IsResidualChlorineTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName.Contains("residual chlorine") ||
                   testName.Contains("free chlorine") ||
                   testName == "chlorine";
        }

        private bool IsRemovedTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName == "taste" ||
                   testName == "odor" ||
                   testName == "odour" ||
                   testName == "taste & odor" ||
                   testName == "taste and odor" ||
                   testName == "taste & odour" ||
                   testName == "taste and odour";
        }

        private bool IsAppearanceTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName.Contains("appearance") ||
                   testName.Contains("color") ||
                   testName.Contains("colour") ||
                   testName.Contains("clarity");
        }

        private bool IsPhTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName == "ph" ||
                   testName == "p.h" ||
                   testName.Contains("ph value") ||
                   testName.Contains("ph test") ||
                   testName.StartsWith("ph ") ||
                   testName.EndsWith(" ph");
        }

        private bool IsAbsencePresenceTest(string unit, string testName)
        {
            unit = unit == null ? "" : unit.Trim().ToLowerInvariant();
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            if (unit == "absence")
                return true;

            return testName.Contains("e. coli") ||
                   testName.Contains("salmonella") ||
                   testName.Contains("staphylococcus") ||
                   testName.Contains("pseudomonas") ||
                   testName.Contains("burkholderia") ||
                   testName.Contains("candida") ||
                   testName.Contains("clostridia") ||
                   testName.Contains("bile tolerant") ||
                   testName.Contains("gram-negative") ||
                   testName.Contains("aureus");
        }

        private bool IsComplianceQualitativeTest(string testName)
        {
            string normalized = (testName ?? string.Empty).Trim().ToLowerInvariant();

            if (normalized.Contains("residual chlorine") ||
                normalized.Contains("free chlorine") ||
                normalized.Contains("nitrates (as n)") ||
                normalized.Contains("nitrate (as n)"))
            {
                return false;
            }

            return normalized == "acidity" ||
                   normalized == "ammonium" ||
                   normalized == "ammonia" ||
                   normalized == "chloride" ||
                   normalized == "chlorides" ||
                   normalized == "nitrate" ||
                   normalized == "nitrates" ||
                   normalized == "sulphate" ||
                   normalized == "sulphates" ||
                   normalized == "sulfate" ||
                   normalized == "sulfates" ||
                   normalized.Contains("heavy metals") ||
                   normalized.Contains("oxidisable substances") ||
                   normalized.Contains("oxidizable substances");
        }


        private void ApplyEffectiveSpecification(ResultItem item)
        {
            if (item == null)
                return;

            if (IsPhTest(item.TestName))
            {
                if (IsPurifiedWaterSample())
                {
                    item.AlertLimit = 5.00m;
                    item.ActionLimit = 7.00m;
                    return;
                }

                if (IsPotableWaterSample())
                {
                    item.AlertLimit = 6.50m;
                    item.ActionLimit = 8.50m;
                    return;
                }
            }

            if (IsConductivityTest(item.TestName))
            {
                if (IsPurifiedWaterSample())
                {
                    item.AlertLimit = null;
                    item.ActionLimit = 2.00m;
                    return;
                }

                if (IsPotableWaterSample())
                {
                    item.AlertLimit = null;
                    item.ActionLimit = 500.00m;
                    return;
                }
            }

            if (IsHardnessTest(item.TestName) && IsPotableWaterSample())
            {
                item.AlertLimit = null;
                item.ActionLimit = IsSoftWaterPoint() ? 5.00m : 300.00m;
                return;
            }
        }

        private bool TryNormalizeResultForSave(ResultItem item, out decimal result)
        {
            result = 0;

            if (item == null)
                return false;

            string raw = item.ResultValue == null ? "" : item.ResultValue.Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return false;

            raw = raw.Replace(",", ".");

            if (IsAppearanceTest(item.TestName))
            {
                if (raw.Equals("Clear", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Clear and Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conforms", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    result = 0;
                    return true;
                }

                if (raw.Equals("Not Clear / Colored", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Turbid", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Colored", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Coloured", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not Clear", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Non-Conform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not OK", StringComparison.OrdinalIgnoreCase))
                {
                    result = 1;
                    return true;
                }
            }

            if (IsComplianceQualitativeTest(item.TestName))
            {
                if (raw.Equals("Complies", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Comply", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conforms", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Pass", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Acceptable", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    result = 0;
                    return true;
                }

                if (raw.Equals("Does Not Comply", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Does not comply", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Non-Conform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Nonconform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Fail", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not Acceptable", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not OK", StringComparison.OrdinalIgnoreCase))
                {
                    result = 1;
                    return true;
                }

                if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal qualitativeValue) &&
                    (qualitativeValue == 0m || qualitativeValue == 1m))
                {
                    result = qualitativeValue;
                    return true;
                }

                return false;
            }

            if (IsAbsencePresenceTest(item.Unit, item.TestName))
            {
                if (raw.Equals("Absence", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Absent", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Negative", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("No", StringComparison.OrdinalIgnoreCase))
                {
                    result = 0;
                    return true;
                }

                if (raw.Equals("Presence", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Present", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Positive", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                {
                    result = 1;
                    return true;
                }

                if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal qualitativeValue))
                {
                    result = qualitativeValue <= 0 ? 0 : 1;
                    return true;
                }

                return false;
            }

            return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
        }

        private decimal GetPersistedResultValue(ResultItem item, decimal normalizedResult)
        {
            // The write parameter remains decimal(18,4), including databases whose
            // baseline ResultValue column is text. Reject excess precision/range
            // instead of letting the parameter silently change a measurement.
            if (!WaterResultValueContract.IsExactlyRepresentable(normalizedResult))
                throw new InvalidOperationException("Result for " + item.TestName +
                    " cannot be stored exactly by the current decimal(18,4) write contract. " +
                    "No results were committed. Use the approved reporting precision; " +
                    "do not round a measurement merely to make it pass.");
            return normalizedResult;
        }

        private string FormatResultForDisplay(ResultItem item)
        {
            if (item == null)
                return "";

            string raw = item.ResultValue == null ? "" : item.ResultValue.Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "";

            if (IsAppearanceTest(item.TestName))
            {
                if (decimal.TryParse(raw.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal value))
                    return value <= 0 ? "Clear and Colorless" : "Not Clear / Colored";

                return raw;
            }

            if (IsComplianceQualitativeTest(item.TestName))
            {
                if (decimal.TryParse(raw.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal value))
                    return value <= 0 ? "Complies" : "Does Not Comply";

                return raw;
            }

            if (IsAbsencePresenceTest(item.Unit, item.TestName))
            {
                if (decimal.TryParse(raw.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal value))
                    return value <= 0 ? "Absence" : "Presence";

                return raw;
            }

            if (decimal.TryParse(raw.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal numeric))
                return WaterResultValueContract.FormatNumeric(numeric);

            return raw;
        }

        private string CalculatePassFail(ResultItem item)
        {
            if (item == null)
                return "Pending";

            string raw = item.ResultValue == null ? "" : item.ResultValue.Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "Pending";

            if (!TryNormalizeResultForSave(item, out decimal resultValue))
                return "Invalid";

            if (IsAppearanceTest(item.TestName))
                return resultValue <= 0 ? "PASS" : "OOS";

            if (IsComplianceQualitativeTest(item.TestName))
                return resultValue <= 0 ? "PASS" : "OOS";

            if (IsAbsencePresenceTest(item.Unit, item.TestName))
                return resultValue <= 0 ? "PASS" : "OOS";

            if (IsPhTest(item.TestName))
            {
                if (item.AlertLimit.HasValue && resultValue < item.AlertLimit.Value)
                    return "OOS";

                if (item.ActionLimit.HasValue && resultValue > item.ActionLimit.Value)
                    return "OOS";

                return "PASS";
            }

            if (IsResidualChlorineTest(item.TestName))
            {
                if (item.AlertLimit.HasValue && resultValue < item.AlertLimit.Value)
                    return "OOS";

                if (item.ActionLimit.HasValue && resultValue > item.ActionLimit.Value)
                    return "OOS";

                return "PASS";
            }

            if (item.ActionLimit.HasValue && resultValue > item.ActionLimit.Value)
                return "OOS";

            if (item.AlertLimit.HasValue && resultValue > item.AlertLimit.Value)
                return "ALERT";

            return "PASS";
        }

        private bool IsLockedStatus(string status)
        {
            status = status == null ? "" : status.Trim();
            return status.Equals("Under Review", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Reviewed", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Approved", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("COA Issued", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Released After Investigation", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshCalculatedStatuses()
        {
            foreach (ResultItem item in resultItems)
                item.PassFail = CalculatePassFail(item);

            dgResults.Items.Refresh();
        }

        private void dgResults_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshCalculatedStatuses();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void SetSampleStatus(string status)
        {
            string normalizedStatus = (status ?? "").Trim();

            if (string.IsNullOrWhiteSpace(normalizedStatus))
                normalizedStatus = "";

            lblStatusText.Text = normalizedStatus;

            string color = "#94A3B8";

            switch (normalizedStatus)
            {
                case "Approved":
                case "Completed":
                case "Results Entered":
                case "COA Issued":
                case "Released After Investigation":
                    color = "#10B981";
                    break;

                case "Reviewed":
                    color = "#0D9488";
                    break;

                case "Alert":
                case "Partially Entered":
                    color = "#F59E0B";
                    break;

                case "OOS":
                case "Failed":
                    color = "#EF4444";
                    break;

                case "Under Review":
                case "In Analysis":
                case "In Progress":
                    color = "#3B82F6";
                    break;

                case "Registered":
                    color = "#64748B";
                    break;
            }

            statusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        private string GetSampleStatus()
        {
            if (currentSampleId == 0)
                return "";

            string query = "SELECT Status FROM Samples WHERE SampleID = @sid";
            SqlParameter[] pars = { new SqlParameter("@sid", currentSampleId) };
            object result = DatabaseHelper.ExecuteScalar(query, pars);
            return result == null || result == DBNull.Value ? "" : result.ToString();
        }

        private bool HasCurrentAlertOrOosResult()
        {
            foreach (ResultItem item in resultItems)
            {
                string passFail = item.PassFail == null ? "" : item.PassFail.Trim();
                if (passFail.Equals("ALERT", StringComparison.OrdinalIgnoreCase) ||
                    passFail.Equals("OOS", StringComparison.OrdinalIgnoreCase) ||
                    passFail.Equals("ACTION", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool HasCurrentOosResult()
        {
            foreach (ResultItem item in resultItems)
            {
                string passFail = item.PassFail == null ? "" : item.PassFail.Trim();
                if (passFail.Equals("OOS", StringComparison.OrdinalIgnoreCase) ||
                    passFail.Equals("ACTION", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private void UpdateWorkflowButtons()
        {
            if (BtnStartAnalysis == null ||
                BtnCorrectTimes == null ||
                BtnSubmitReview == null ||
                BtnReview == null ||
                BtnApprove == null ||
                BtnCertificate == null ||
                BtnCancelCOA == null ||
                BtnPrintReport == null ||
                BtnSaveResults == null)
                return;

            if (currentSampleId == 0)
            {
                BtnStartAnalysis.Visibility = Visibility.Collapsed;
                BtnCorrectTimes.IsEnabled = false;
                BtnSubmitReview.Visibility = Visibility.Collapsed;
                BtnReview.Visibility = Visibility.Collapsed;
                BtnApprove.Visibility = Visibility.Collapsed;

                BtnSaveResults.IsEnabled = false;
                BtnCertificate.IsEnabled = false;
                BtnCancelCOA.IsEnabled = false;
                BtnPrintReport.IsEnabled = false;
                if (BtnQualityEvent != null)
                    BtnQualityEvent.IsEnabled = false;

                if (dgResults != null)
                    dgResults.IsReadOnly = true;

                return;
            }

            string status = GetSampleStatus();
            bool locked = IsLockedStatus(status);

            bool canEnterResults = CanEnterResults();
            bool canStartAnalysis = CanStartAnalysis();
            bool canSubmitReview = CanSubmitForReview();
            bool canReview = CanReviewSample();
            bool canApprove = CanApproveSample();
            bool canCertificate = CanOpenOrIssueCertificate();
            bool resultEntryStatus =
                status == "In Analysis" ||
                status == "Partially Entered" ||
                status == "Results Entered" ||
                status == "Alert" ||
                status == "OOS" ||
                status == "Released After Investigation";

            BtnSaveResults.IsEnabled = canEnterResults && !locked && resultEntryStatus;

            if (dgResults != null)
                dgResults.IsReadOnly = !canEnterResults || locked || !resultEntryStatus;

            BtnStartAnalysis.Visibility =
                canStartAnalysis && (status == "Registered" || status == "Incubation")
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            BtnSubmitReview.Visibility =
                canSubmitReview &&
                (status == "Results Entered" ||
                 status == "Alert" ||
                 status == "Released After Investigation")
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            BtnReview.Visibility =
                canReview && status == "Under Review"
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            BtnApprove.Visibility =
                canApprove && status == "Reviewed"
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            bool certificateStateVerified = true;
            bool hasActiveCertificate = false;
            try
            {
                hasActiveCertificate = DatabaseHelper.HasCertificate(currentSampleId);
            }
            catch (Exception ex)
            {
                certificateStateVerified = false;
                hasActiveCertificate = false;
                ApplicationLogger.Error(
                    $"Workflow button refresh could not verify certificate state for SampleID {currentSampleId}.", ex);
            }

            BtnCorrectTimes.IsEnabled =
                certificateStateVerified &&
                CanCorrectSampleTimes() &&
                IsWaterSampleType(currentSampleType) &&
                !locked &&
                !hasActiveCertificate;

            bool qualityEventStateVerified = true;
            bool hasOpenQualityEvent = false;
            try
            {
                hasOpenQualityEvent = DatabaseHelper.HasOpenQualityEvent(currentSampleId);
            }
            catch (Exception ex)
            {
                qualityEventStateVerified = false;
                hasOpenQualityEvent = true; // Fail closed: unknown QE state must block controlled progression.
                ApplicationLogger.Error(
                    $"Workflow button refresh could not verify open Quality Event state for SampleID {currentSampleId}.", ex);
            }

            bool hasAnyQualityEvent = hasOpenQualityEvent;
            if (qualityEventStateVerified)
            {
                try
                {
                    hasAnyQualityEvent = hasAnyQualityEvent || DatabaseHelper.HasAnyQualityEvent(currentSampleId);
                }
                catch (Exception ex)
                {
                    qualityEventStateVerified = false;
                    hasOpenQualityEvent = true;
                    hasAnyQualityEvent = true;
                    ApplicationLogger.Error(
                        $"Workflow button refresh could not verify Quality Event history for SampleID {currentSampleId}.", ex);
                }
            }

            bool hasCurrentDeviation = HasCurrentAlertOrOosResult();
            const string verificationUnavailableMessage =
                "Compliance verification unavailable. Controlled review, approval, and certificate actions are disabled. Run System Preflight / Database Maintenance, then reload the sample.";

            if (BtnQualityEvent != null)
            {
                if (!qualityEventStateVerified)
                {
                    BtnQualityEvent.IsEnabled = false;
                    BtnQualityEvent.Content = "Quality Event Verification Unavailable";
                    BtnQualityEvent.ToolTip = verificationUnavailableMessage;
                }
                else
                {
                    BtnQualityEvent.IsEnabled = hasAnyQualityEvent || hasCurrentDeviation || status == "Alert" || status == "Under Investigation";
                    BtnQualityEvent.Content = hasAnyQualityEvent ? "Open Quality Event" : "Create Quality Event";
                    BtnQualityEvent.ToolTip = null;
                }
            }

            if (!qualityEventStateVerified || hasOpenQualityEvent)
            {
                BtnSubmitReview.Visibility = Visibility.Collapsed;
                BtnReview.Visibility = Visibility.Collapsed;
                BtnApprove.Visibility = Visibility.Collapsed;
            }

            BtnCertificate.IsEnabled =
                certificateStateVerified &&
                qualityEventStateVerified &&
                canCertificate &&
                !hasOpenQualityEvent &&
                (status == "Approved" ||
                 status == "COA Issued" ||
                 status == "COA Cancelled" ||
                 hasActiveCertificate);

            BtnCancelCOA.IsEnabled = certificateStateVerified && CanCancelCertificate() && hasActiveCertificate;
            BtnPrintReport.IsEnabled = currentSampleId > 0;

            if (!_legacyCertificateReissueRouteActive)
            {
                BtnCancelCOA.Content = "Cancel COA";
                BtnCertificate.Content = "Certificate";
            }
            else
            {
                ApplyLegacyReissueRouteUi(status, canCertificate, hasOpenQualityEvent);
            }

            if (!qualityEventStateVerified || !certificateStateVerified)
            {
                BtnSubmitReview.Visibility = Visibility.Collapsed;
                BtnReview.Visibility = Visibility.Collapsed;
                BtnApprove.Visibility = Visibility.Collapsed;
                BtnCertificate.IsEnabled = false;
                BtnCancelCOA.IsEnabled = false;
                BtnCertificate.ToolTip = verificationUnavailableMessage;
                BtnApprove.ToolTip = verificationUnavailableMessage;
            }
            else
            {
                BtnCertificate.ToolTip = null;
                BtnApprove.ToolTip = null;
            }
        }

        private void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            string sampleNumber = txtSearchSample.Text.Trim();

            if (string.IsNullOrWhiteSpace(sampleNumber))
            {
                MessageBox.Show("Please enter sample number.", "Search", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LoadSampleFromUi(sampleNumber);
        }


        public void LoadSample(int sampleId)
        {
            if (sampleId <= 0)
                return;

            try
            {
                SqlParameter[] pars =
                {
                    new SqlParameter("@sampleId", sampleId)
                };

                object sampleNumberObj = DatabaseHelper.ExecuteScalar(
                    "SELECT TOP 1 SampleNumber FROM Samples WHERE SampleID = @sampleId",
                    pars);

                string sampleNumber = sampleNumberObj == null || sampleNumberObj == DBNull.Value
                    ? ""
                    : sampleNumberObj.ToString();

                if (string.IsNullOrWhiteSpace(sampleNumber))
                {
                    MessageBox.Show(
                        "Sample was not found for SampleID: " + sampleId.ToString(CultureInfo.InvariantCulture),
                        "Sample Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                txtSearchSample.Text = sampleNumber;
                LoadSampleFromUi(sampleNumber);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error loading selected sample:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Results Entry",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnLoadPending_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string query = @"
                    SELECT TOP 30
                        s.SampleNumber,
                        s.SampleType,
                        s.Status,
                        s.SamplingDateTime
                    FROM Samples s
                    WHERE ISNULL(s.SampleType, '') <> 'Environment'
                      AND ISNULL(s.SampleType, '') NOT LIKE '%Environmental%'
                      AND ISNULL(s.Status, '') IN
                      (
                          'Registered',
                          'Incubation',
                          'In Analysis',
                          'Partially Entered',
                          'Results Entered',
                          'Under Review',
                          'Reviewed',
                          'Alert',
                          'OOS',
                          'Under Investigation',
                          'Released After Investigation'
                      )
                    ORDER BY s.SampleID DESC";

                DataTable dt = DatabaseHelper.ExecuteQuery(query);

                if (dt.Rows.Count == 0)
                {
                    MessageBox.Show("No open water samples found.", "Open Water Samples",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Window picker = new Window
                {
                    Title = "Open Water Samples",
                    Owner = this,
                    Width = 760,
                    Height = 480,
                    MinWidth = 620,
                    MinHeight = 360,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Grid layout = new Grid { Margin = new Thickness(12) };
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                TextBlock instruction = new TextBlock
                {
                    Text = "Select an open Water sample, then double-click it or press Open Sample.",
                    Margin = new Thickness(0, 0, 0, 10),
                    FontWeight = FontWeights.SemiBold
                };
                Grid.SetRow(instruction, 0);
                layout.Children.Add(instruction);

                DataGrid samplesGrid = new DataGrid
                {
                    ItemsSource = dt.DefaultView,
                    AutoGenerateColumns = false,
                    IsReadOnly = true,
                    SelectionMode = DataGridSelectionMode.Single,
                    SelectionUnit = DataGridSelectionUnit.FullRow,
                    CanUserAddRows = false,
                    HeadersVisibility = DataGridHeadersVisibility.Column
                };
                samplesGrid.Columns.Add(new DataGridTextColumn { Header = "Sample No.", Binding = new Binding("SampleNumber"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
                samplesGrid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding("SampleType"), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
                samplesGrid.Columns.Add(new DataGridTextColumn { Header = "Sampling Date", Binding = new Binding("SamplingDateTime") { StringFormat = "yyyy-MM-dd HH:mm" }, Width = new DataGridLength(1.5, DataGridLengthUnitType.Star) });
                samplesGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
                Grid.SetRow(samplesGrid, 1);
                layout.Children.Add(samplesGrid);

                StackPanel actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                Button openButton = new Button { Content = "Open Sample", Width = 120, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
                Button cancelButton = new Button { Content = "Cancel", Width = 90, Height = 34, IsCancel = true };
                actions.Children.Add(openButton);
                actions.Children.Add(cancelButton);
                Grid.SetRow(actions, 2);
                layout.Children.Add(actions);
                picker.Content = layout;

                Action openSelected = () =>
                {
                    if (samplesGrid.SelectedItem is DataRowView)
                        picker.DialogResult = true;
                };
                openButton.Click += (buttonSender, buttonArgs) => openSelected();
                samplesGrid.MouseDoubleClick += (gridSender, gridArgs) => openSelected();

                if (picker.ShowDialog() == true && samplesGrid.SelectedItem is DataRowView selected)
                {
                    string selectedSampleNumber = selected["SampleNumber"]?.ToString() ?? "";
                    txtSearchSample.Text = selectedSampleNumber;
                    LoadSampleFromUi(selectedSampleNumber);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading pending samples: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadSampleFromUi(string sampleNumber)
        {
            _ = LoadSampleAsync(sampleNumber);
        }

        private async Task LoadSampleAsync(string sampleNumber)
        {
            try
            {
                sampleNumber = sampleNumber == null ? "" : sampleNumber.Trim();

                if (string.IsNullOrWhiteSpace(sampleNumber))
                {
                    MessageBox.Show("Please enter sample number.", "Search", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ClearSampleDisplay();
                    return;
                }

                string query = @"
                    SELECT TOP 1
                        s.SampleID,
                        s.SampleNumber,
                        ISNULL(s.SampleType, '') AS SampleType,
                        s.SamplingDateTime,
                        ISNULL(s.SampledBy, '') AS SampledBy,
                        ISNULL(s.Status, '') AS Status,
                        COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode, p.PointCode, '') AS PointCode,
                        COALESCE(NULLIF(s.PointLocationSnapshot,N''),wp.Location, p.Location, '') AS Location
                    FROM Samples s
                    LEFT JOIN WaterSamplingPoints wp ON s.PointID = wp.Id
                    LEFT JOIN SamplingPoints p ON s.PointID = p.PointID
                    WHERE s.SampleNumber = @sampleNumber";

                SqlParameter[] pars =
                {
                    new SqlParameter("@sampleNumber", sampleNumber)
                };

                DataTable dt = DatabaseHelper.ExecuteQuery(query, pars);

                if (dt.Rows.Count == 0)
                {
                    MessageBox.Show(
                        "Sample " + sampleNumber + " not found.",
                        "Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    ClearSampleDisplay();
                    return;
                }

                DataRow row = dt.Rows[0];
                string sampleType = row["SampleType"] == DBNull.Value ? "" : row["SampleType"].ToString();

                if (IsEnvironmentalSampleType(sampleType) && !IsWaterSampleType(sampleType))
                {
                    MessageBox.Show(
                        "This sample is Environmental Monitoring.\n\nUse EM Results Entry screen for EM events.",
                        "Wrong Screen",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    ClearSampleDisplay();
                    return;
                }

                if (!IsWaterSampleType(sampleType))
                {
                    MessageBox.Show(
                        "This screen is for water samples only.\n\nSample Type: " + sampleType,
                        "Wrong Sample Type",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    ClearSampleDisplay();
                    return;
                }

                int loadedSampleId = Convert.ToInt32(row["SampleID"]);
                if (_legacyCertificateReissueRouteActive &&
                    _legacyReissueSampleId > 0 &&
                    loadedSampleId != _legacyReissueSampleId)
                {
                    throw new InvalidOperationException(
                        $"This controlled legacy reissue window is locked to SampleID {_legacyReissueSampleId}. Close it before loading another sample.");
                }

                currentSampleId = loadedSampleId;
                currentSampleType = sampleType;
                currentPointCode = row["PointCode"] == DBNull.Value ? "" : row["PointCode"].ToString();
                currentPointLocation = row["Location"] == DBNull.Value ? "" : row["Location"].ToString();

                lblSampleNumber.Text = row["SampleNumber"] == DBNull.Value ? sampleNumber : row["SampleNumber"].ToString();
                lblSampleType.Text = sampleType;

                DateTime samplingDate = DateTime.MinValue;
                if (row["SamplingDateTime"] != DBNull.Value)
                    DateTime.TryParse(row["SamplingDateTime"].ToString(), out samplingDate);

                lblSamplingDate.Text = samplingDate == DateTime.MinValue
                    ? ""
                    : samplingDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

                string pointCode = string.IsNullOrWhiteSpace(currentPointCode) ? "N/A" : currentPointCode;
                string location = currentPointLocation ?? "";

                lblSamplingPoint.Text = pointCode + " - " + location;

                if (lblSampledBy != null)
                    lblSampledBy.Text = row["SampledBy"] == DBNull.Value ? "" : row["SampledBy"].ToString();

                string status = row["Status"] == DBNull.Value ? "" : row["Status"].ToString();
                SetSampleStatus(status);
                await LoadTestsForSample(currentSampleId);
                UpdateWorkflowButtons();

                lblStatus.Text = "Water sample loaded successfully.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading sample: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string BuildDeviationType(ResultItem item, string status)
        {
            if (item == null || string.IsNullOrWhiteSpace(status))
                return "";

            if (status.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                return "";

            if (IsAbsencePresenceTest(item.Unit, item.TestName))
                return "Microbiological OOS";

            if (item.TestName != null &&
                (item.TestName.Contains("TAMC", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("TYMC", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("Microbial", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("Aerobic", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("Yeast", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("Mold", StringComparison.OrdinalIgnoreCase)))
            {
                return status.Equals("ALERT", StringComparison.OrdinalIgnoreCase)
                    ? "Microbiological Alert"
                    : "Microbiological OOS";
            }

            if (status.Equals("ALERT", StringComparison.OrdinalIgnoreCase))
                return "Alert Limit Exceeded";

            if (status.Equals("OOS", StringComparison.OrdinalIgnoreCase))
                return "Action Limit / Specification Exceeded";

            return status;
        }

        private string BuildLimitDescription(ResultItem item)
        {
            if (item == null)
                return "";

            if (IsPhTest(item.TestName))
            {
                if (item.AlertLimit.HasValue && item.ActionLimit.HasValue)
                {
                    return "Specification Range: " + item.AlertLimit.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                           " - " + item.ActionLimit.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                           (string.IsNullOrWhiteSpace(item.Unit) ? "" : " " + item.Unit);
                }

                return "";
            }

            if (item.AlertLimit.HasValue && item.ActionLimit.HasValue)
            {
                return "Alert Limit: " + item.AlertLimit.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                       (string.IsNullOrWhiteSpace(item.Unit) ? "" : " " + item.Unit) +
                       "; Action Limit: " + item.ActionLimit.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                       (string.IsNullOrWhiteSpace(item.Unit) ? "" : " " + item.Unit);
            }

            if (item.AlertLimit.HasValue)
            {
                return "Alert Limit: " + item.AlertLimit.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                       (string.IsNullOrWhiteSpace(item.Unit) ? "" : " " + item.Unit);
            }

            if (item.ActionLimit.HasValue)
            {
                return "Action Limit: " + item.ActionLimit.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                       (string.IsNullOrWhiteSpace(item.Unit) ? "" : " " + item.Unit);
            }

            return "";
        }

        private Task LoadTestsForSample(int sampleId)
        {
            try
            {
                resultItems.Clear();

                _loadedWaterSnapshot = null;
                _loadedWaterDisplayValues.Clear();
                string query = WaterResultSnapshotSql.Build(false);

                SqlParameter[] pars =
                {
                    new SqlParameter("@sampleId", sampleId)
                };

                DataTable dt = DatabaseHelper.ExecuteQuery(query, pars);

                foreach (DataRow row in dt.Rows)
                {
                    string testName = row["TestName"] == DBNull.Value ? "" : row["TestName"].ToString();

                    if (IsRemovedTest(testName))
                        continue;

                    ResultItem item = WaterItemFromEvidence(row);
                    item.ResultValue = FormatResultForDisplay(item);
                    item.PassFail = CalculatePassFail(item);

                    resultItems.Add(item);
                    _loadedWaterDisplayValues[item.SampleTestID] = item.ResultValue ?? "";
                }

                _loadedWaterSnapshot = dt.Copy();
                _loadedWaterSnapshot.AcceptChanges();
                dgResults.ItemsSource = null;
                dgResults.ItemsSource = resultItems;
                dgResults.Items.Refresh();

                lblStatus.Text = "Loaded " + resultItems.Count + " test(s).";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading tests: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return Task.CompletedTask;
        }

        private void ClearSampleDisplay()
        {
            _loadedWaterSnapshot = null;
            _loadedWaterDisplayValues.Clear();
            currentSampleId = 0;
            currentSampleType = "";
            currentPointCode = "";
            currentPointLocation = "";

            lblSampleNumber.Text = "";
            lblSampleType.Text = "";
            lblSamplingDate.Text = "";
            lblSamplingPoint.Text = "";

            if (lblSampledBy != null)
                lblSampledBy.Text = "";

            SetSampleStatus("");

            resultItems.Clear();
            dgResults.ItemsSource = null;

            lblStatus.Text = "Ready";
            UpdateWorkflowButtons();
        }

        private async void BtnSaveResults_Click(object sender, RoutedEventArgs e)
        {
            if (_isSavingResults)
                return;

            if (currentSampleId == 0)
            {
                MessageBox.Show("No sample loaded.", "Save Results", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string statusBeforeSave = GetSampleStatus();

            if (IsLockedStatus(statusBeforeSave))
            {
                MessageBox.Show("Results cannot be edited after submission for review or approval.",
                    "Locked Record", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!CanEnterResults())
            {
                MessageBox.Show("You do not have permission to enter or edit results.",
                    "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            dgResults.CommitEdit(DataGridEditingUnit.Cell, true);
            dgResults.CommitEdit(DataGridEditingUnit.Row, true);
            RefreshCalculatedStatuses();

            bool hasAnyResult = false;

            foreach (ResultItem item in resultItems)
            {
                if (!string.IsNullOrWhiteSpace(item.ResultValue))
                {
                    hasAnyResult = true;
                    break;
                }
            }

            if (!hasAnyResult)
            {
                MessageBox.Show("No results entered.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (ResultItem item in resultItems)
            {
                if (string.IsNullOrWhiteSpace(item.ResultValue))
                    continue;

                item.PassFail = CalculatePassFail(item);

                if (item.PassFail == "Invalid")
                {
                    MessageBox.Show(
                        "Invalid result value for test: " + item.TestName +
                        "\n\nFor qualitative tests use Absence / Presence or Clear / Not Clear.\nFor numeric tests use a number.",
                        "Invalid Result",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }

                bool inputEdited = !_loadedWaterDisplayValues.TryGetValue(item.SampleTestID, out string originalDisplay) ||
                    !string.Equals(originalDisplay, item.ResultValue ?? "", StringComparison.Ordinal);
                if (inputEdited && TryNormalizeResultForSave(item, out decimal checkedValue) &&
                    !WaterResultValueContract.IsExactlyRepresentable(checkedValue))
                {
                    MessageBox.Show("Result for " + item.TestName +
                        " exceeds the current write contract (14 integer digits, 4 decimal places). " +
                        "Review the approved reporting precision before signing. No value has been rounded or saved.",
                        "Result Precision", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if ((item.PassFail == "ALERT" || item.PassFail == "OOS") &&
                    string.IsNullOrWhiteSpace(item.Remarks))
                {
                    MessageBox.Show(
                        "Remarks are required for " + item.PassFail + " result.\n\nTest: " + item.TestName,
                        "Validation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }
            }

            ElectronicSignature signatureWindow =
                _serviceProvider.GetRequiredService<ElectronicSignature>();
            signatureWindow.Configure(GetSignatureRecordNumber(), currentUser, "Result Entry", true);
            signatureWindow.Owner = this;

            if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                return;

            _isSavingResults = true;
            SetButtonBusy(BtnSaveResults, true, "Saving...", "Save Results");

            try
            {
                int completedTests = 0;
                int passedTests = 0;
                int alertTests = 0;
                int oosTests = 0;
                int pendingCount = 0;
                string newStatus = "";
                string qualityEventSummary = "";
                bool developmentTimingOverrideUsed = false;

                ValidateManualWaterDatesBeforeAnalysis();
                DatabaseHelper.EnsureSampleTestStatusColumns();
                EnsureGlobalWaterDateColumns();

                ExecuteInLocalTransaction((con, tran) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        con, tran, signatureWindow.SignedBy, "CanEnterResults", "save water results");

                    string lockedStatus = LockAndValidateSampleStatusInTransaction(
                        con, tran, statusBeforeSave, "Result save");

                    if (IsLockedStatus(lockedStatus))
                    {
                        throw new InvalidOperationException(
                            "Results are locked because the sample has already entered review or approval.");
                    }

                    developmentTimingOverrideUsed =
                        ValidateWaterIncubationCompleteBeforeResultsInTransaction(con, tran);

                    bool resultEvidenceChanged = PersistWaterEdits(con, tran, signatureWindow);
                    (completedTests, passedTests, alertTests, oosTests, pendingCount) = ReadWaterSummary(con, tran);

                    if (developmentTimingOverrideUsed)
                    {
                        DatabaseHelper.AddAuditTrailAdvanced(
                            con,
                            tran,
                            "Samples",
                            currentSampleId,
                            "Development Incubation Timing Override",
                            "Incubation completion required",
                            "Early microbiology result entry permitted in Development only",
                            signatureWindow.Reason,
                            signatureWindow.SignedBy,
                            "IncubationEndDate",
                            null,
                            GetSignatureRecordNumber(),
                            "Water");
                    }

                    if (oosTests > 0)
                        newStatus = "Under Investigation";
                    else if (pendingCount > 0)
                        newStatus = "Partially Entered";
                    else if (alertTests > 0)
                        newStatus = "Alert";
                    else
                        newStatus = "Results Entered";

                    if (pendingCount == 0 && resultEvidenceChanged)
                        MarkAnalysisCompletedNowInTransaction(con, tran);
                    AddSampleElectronicSignatureInTransaction(
                        con, tran, "Result Entry", "Result Entry", signatureWindow.Reason,
                        signatureWindow.SignedBy, signerRole);
                    UpdateSampleStatusInTransaction(con, tran, newStatus, lockedStatus, "Result save");

                    if (oosTests > 0)
                    {
                        qualityEventSummary = DatabaseHelper.CreateOrUpdateQualityEventForOOS(
                            con,
                            tran,
                            currentSampleId,
                            signatureWindow.SignedBy,
                            "OOS/ACTION result detected during result entry for sample " + lblSampleNumber.Text + ".");
                    }
                });

                if (alertTests > 0 && oosTests == 0)
                {
                    qualityEventSummary = "ALERT follow-up/trending required. No mandatory Quality Event was opened.";
                }

                SetSampleStatus(newStatus);

                MessageBox.Show(
                    "Results saved successfully!\n\n" +
                    "Completed: " + completedTests + "\n" +
                    "Passed: " + passedTests + "\n" +
                    "Alerts: " + alertTests + "\n" +
                    "OOS: " + oosTests + "\n" +
                    "Pending: " + pendingCount + "\n" +
                    "Status: " + newStatus +
                    (string.IsNullOrWhiteSpace(qualityEventSummary) ? "" : "\nFollow-up: " + qualityEventSummary),
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                await LoadTestsForSample(currentSampleId);
                UpdateWorkflowButtons();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving results: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isSavingResults = false;
                SetButtonBusy(BtnSaveResults, false, "Saving...", "Save Results");
            }
        }

        private static DateTime? ReadNullableDateTime(DataRow row, string columnName)
        {
            return row == null || row.IsNull(columnName)
                ? (DateTime?)null
                : Convert.ToDateTime(row[columnName], CultureInfo.InvariantCulture);
        }

        private WaterTimestampSnapshot LoadWaterTimestampSnapshot()
        {
            DataTable rows = DatabaseHelper.ExecuteQuery(@"
SELECT SamplingDateTime,
       ReceivedDateTime,
       IncubationStartedDateTime,
       IncubationEndDate,
       AnalysisStartedDateTime,
       AnalysisCompletedDateTime
FROM dbo.Samples
WHERE SampleID=@SampleID;",
                new[] { new SqlParameter("@SampleID", currentSampleId) });

            if (rows.Rows.Count != 1)
                throw new InvalidOperationException("The water sample could not be loaded for timestamp correction.");

            DataRow row = rows.Rows[0];
            return new WaterTimestampSnapshot
            {
                SamplingDateTime = ReadNullableDateTime(row, "SamplingDateTime"),
                ReceivedDateTime = ReadNullableDateTime(row, "ReceivedDateTime"),
                IncubationStartedDateTime = ReadNullableDateTime(row, "IncubationStartedDateTime"),
                IncubationEndDate = ReadNullableDateTime(row, "IncubationEndDate"),
                AnalysisStartedDateTime = ReadNullableDateTime(row, "AnalysisStartedDateTime"),
                AnalysisCompletedDateTime = ReadNullableDateTime(row, "AnalysisCompletedDateTime")
            };
        }

        private WaterTimestampSnapshot LoadWaterTimestampSnapshotInTransaction(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT SamplingDateTime,
       ReceivedDateTime,
       IncubationStartedDateTime,
       IncubationEndDate,
       AnalysisStartedDateTime,
       AnalysisCompletedDateTime
FROM dbo.Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID=@SampleID;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = currentSampleId;

            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The water sample no longer exists.");

            WaterTimestampSnapshot snapshot = new WaterTimestampSnapshot
            {
                SamplingDateTime = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0),
                ReceivedDateTime = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
                IncubationStartedDateTime = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                IncubationEndDate = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                AnalysisStartedDateTime = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4),
                AnalysisCompletedDateTime = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5)
            };

            if (reader.Read())
                throw new InvalidOperationException("Duplicate sample identity was detected. Timestamp correction was blocked.");

            return snapshot;
        }

        private static bool WaterTimestampsEqual(WaterTimestampSnapshot left, WaterTimestampSnapshot right)
        {
            return left != null && right != null &&
                   Nullable.Equals(left.SamplingDateTime, right.SamplingDateTime) &&
                   Nullable.Equals(left.ReceivedDateTime, right.ReceivedDateTime) &&
                   Nullable.Equals(left.IncubationStartedDateTime, right.IncubationStartedDateTime) &&
                   Nullable.Equals(left.IncubationEndDate, right.IncubationEndDate) &&
                   Nullable.Equals(left.AnalysisStartedDateTime, right.AnalysisStartedDateTime) &&
                   Nullable.Equals(left.AnalysisCompletedDateTime, right.AnalysisCompletedDateTime);
        }

        private static string FormatWaterTimestampAuditValue(WaterTimestampSnapshot value)
        {
            string Format(DateTime? timestamp) => timestamp.HasValue
                ? timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "NULL";

            return "Sampling=" + Format(value.SamplingDateTime) +
                   "; Received=" + Format(value.ReceivedDateTime) +
                   "; IncubationStart=" + Format(value.IncubationStartedDateTime) +
                   "; IncubationEnd=" + Format(value.IncubationEndDate) +
                   "; AnalysisStart=" + Format(value.AnalysisStartedDateTime) +
                   "; AnalysisCompleted=" + Format(value.AnalysisCompletedDateTime);
        }

        private static void ValidateWaterTimestampCorrection(
            WaterTimestampSnapshot value,
            DateTime latestAllowedEntry)
        {
            if (!value.SamplingDateTime.HasValue || !value.ReceivedDateTime.HasValue)
                throw new InvalidOperationException("Sampling and Received in Lab timestamps are required.");
            if (value.ReceivedDateTime.Value < value.SamplingDateTime.Value)
                throw new InvalidOperationException("Received in Lab cannot be earlier than Sampling.");
            if (value.IncubationStartedDateTime.HasValue &&
                value.IncubationStartedDateTime.Value < value.ReceivedDateTime.Value)
                throw new InvalidOperationException("Incubation Start cannot be earlier than Received in Lab.");
            if (value.IncubationStartedDateTime.HasValue && value.IncubationEndDate.HasValue &&
                value.IncubationEndDate.Value <= value.IncubationStartedDateTime.Value)
                throw new InvalidOperationException("Incubation End must be later than Incubation Start.");
            if (value.AnalysisStartedDateTime.HasValue &&
                (value.AnalysisStartedDateTime.Value < value.SamplingDateTime.Value ||
                 value.AnalysisStartedDateTime.Value < value.ReceivedDateTime.Value))
                throw new InvalidOperationException("Analysis Start cannot be earlier than Sampling or Received in Lab.");
            if (value.AnalysisCompletedDateTime.HasValue && !value.AnalysisStartedDateTime.HasValue)
                throw new InvalidOperationException("Analysis Start is required because Analysis Completed is already recorded.");
            if (value.AnalysisCompletedDateTime.HasValue && value.AnalysisStartedDateTime.HasValue &&
                value.AnalysisStartedDateTime.Value > value.AnalysisCompletedDateTime.Value)
                throw new InvalidOperationException("Analysis Start cannot be later than the recorded Analysis Completed time.");

            DateTime?[] manuallyEntered =
            {
                value.SamplingDateTime,
                value.ReceivedDateTime,
                value.IncubationStartedDateTime,
                value.AnalysisStartedDateTime
            };
            if (manuallyEntered.Any(timestamp => timestamp.HasValue && timestamp.Value > latestAllowedEntry))
                throw new InvalidOperationException("Corrected timestamps cannot be in the future.");
        }

        private bool TryPromptForWaterTimestampCorrection(
            WaterTimestampSnapshot original,
            out WaterTimestampSnapshot corrected)
        {
            corrected = null;
            if (original.IncubationStartedDateTime.HasValue != original.IncubationEndDate.HasValue)
            {
                throw new InvalidOperationException(
                    "The existing incubation start/end pair is incomplete. Correct the underlying data through an approved remediation before changing timestamps.");
            }

            Window dialog = new Window
            {
                Title = "Correct Water Sample Times",
                Width = 590,
                Height = 510,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = this
            };

            StackPanel layout = new StackPanel { Margin = new Thickness(20) };
            layout.Children.Add(new TextBlock
            {
                Text = "Correct only documented source-data errors. Saving requires an electronic signature and creates an immutable audit-trail entry.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9A3412")),
                Margin = new Thickness(0, 0, 0, 14)
            });

            Grid fields = new Grid();
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            Dictionary<string, (DatePicker Picker, TextBox Time)> inputs =
                new Dictionary<string, (DatePicker Picker, TextBox Time)>();

            void AddRow(string key, string label, DateTime? value, bool required)
            {
                int row = fields.RowDefinitions.Count;
                fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TextBlock caption = new TextBlock
                {
                    Text = label + (required ? " *" : string.Empty),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 5, 8, 5)
                };
                DatePicker picker = new DatePicker
                {
                    SelectedDate = value?.Date,
                    Margin = new Thickness(0, 5, 8, 5)
                };
                TextBox time = new TextBox
                {
                    Text = value?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                    Margin = new Thickness(0, 5, 0, 5),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(caption, row); Grid.SetColumn(caption, 0);
                Grid.SetRow(picker, row); Grid.SetColumn(picker, 1);
                Grid.SetRow(time, row); Grid.SetColumn(time, 2);
                fields.Children.Add(caption);
                fields.Children.Add(picker);
                fields.Children.Add(time);
                inputs[key] = (picker, time);
            }

            AddRow("sampling", "Sampling", original.SamplingDateTime, true);
            AddRow("received", "Received in Lab", original.ReceivedDateTime, true);
            AddRow("incubation", "Incubation Start", original.IncubationStartedDateTime, false);
            AddRow("analysis", "Analysis Start", original.AnalysisStartedDateTime, false);
            layout.Children.Add(fields);
            layout.Children.Add(new TextBlock
            {
                Text = "Time format: HH:mm or HH:mm:ss. The incubation end is recalculated by preserving the originally approved duration.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 10, 0, 16)
            });

            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Button cancel = new Button { Content = "Cancel", Width = 95, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            Button save = new Button { Content = "Continue to Signature", Width = 170, IsDefault = true };
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            layout.Children.Add(actions);

            WaterTimestampSnapshot selected = null;
            bool unchanged = false;
            save.Click += (_, __) =>
            {
                try
                {
                    DateTime? ReadInput(string key, bool required, DateTime? previous)
                    {
                        (DatePicker Picker, TextBox Time) input = inputs[key];
                        bool dateMissing = !input.Picker.SelectedDate.HasValue;
                        bool timeMissing = string.IsNullOrWhiteSpace(input.Time.Text);
                        if (dateMissing && timeMissing)
                        {
                            if (required || previous.HasValue)
                                throw new InvalidOperationException("A previously recorded timestamp cannot be cleared.");
                            return null;
                        }
                        if (dateMissing || timeMissing ||
                            !DateTime.TryParseExact(input.Time.Text.Trim(), new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" },
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedTime))
                            throw new InvalidOperationException("Enter a complete date and a valid HH:mm time for every populated row.");
                        return input.Picker.SelectedDate.Value.Date.Add(parsedTime.TimeOfDay);
                    }

                    DateTime? incubationStart = ReadInput("incubation", false, original.IncubationStartedDateTime);
                    DateTime? recalculatedEnd = original.IncubationEndDate;
                    if (original.IncubationStartedDateTime.HasValue && original.IncubationEndDate.HasValue && incubationStart.HasValue)
                    {
                        TimeSpan approvedDuration = original.IncubationEndDate.Value - original.IncubationStartedDateTime.Value;
                        if (approvedDuration <= TimeSpan.Zero)
                            throw new InvalidOperationException("The existing incubation duration is invalid and cannot be preserved.");
                        recalculatedEnd = incubationStart.Value.Add(approvedDuration);
                    }

                    selected = new WaterTimestampSnapshot
                    {
                        SamplingDateTime = ReadInput("sampling", true, original.SamplingDateTime),
                        ReceivedDateTime = ReadInput("received", true, original.ReceivedDateTime),
                        IncubationStartedDateTime = incubationStart,
                        IncubationEndDate = recalculatedEnd,
                        AnalysisStartedDateTime = ReadInput("analysis", false, original.AnalysisStartedDateTime),
                        AnalysisCompletedDateTime = original.AnalysisCompletedDateTime
                    };
                    ValidateWaterTimestampCorrection(selected, DatabaseHelper.GetAuthoritativeDatabaseTime().AddMinutes(1));

                    if (WaterTimestampsEqual(original, selected))
                    {
                        unchanged = true;
                        MessageBox.Show("No timestamp changes were entered.", "Timestamp Correction",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        dialog.DialogResult = false;
                        return;
                    }

                    dialog.DialogResult = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex), "Timestamp Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            dialog.Content = layout;
            if (dialog.ShowDialog() != true || unchanged || selected == null)
                return false;

            corrected = selected;
            return true;
        }

        private void BtnCorrectTimes_Click(object sender, RoutedEventArgs e)
        {
            if (_isWorkflowBusy || currentSampleId <= 0)
                return;
            if (!IsWaterSampleType(currentSampleType))
            {
                MessageBox.Show("Controlled timestamp correction is available only for water samples.",
                    "Timestamp Correction", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!CanCorrectSampleTimes())
            {
                MessageBox.Show("You do not have permission to correct registered sample timestamps.",
                    "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string statusBefore = GetSampleStatus();
            if (IsLockedStatus(statusBefore) || DatabaseHelper.HasCertificate(currentSampleId))
            {
                MessageBox.Show("Timestamps are locked after review or while an active certificate exists.",
                    "Timestamp Correction", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                EnsureGlobalWaterDateColumns();
                WaterTimestampSnapshot original = LoadWaterTimestampSnapshot();
                if (!TryPromptForWaterTimestampCorrection(original, out WaterTimestampSnapshot corrected))
                    return;

                ElectronicSignature signatureWindow =
                    _serviceProvider.GetRequiredService<ElectronicSignature>();
                signatureWindow.Configure(GetSignatureRecordNumber(), currentUser, "Water Timestamp Correction", true);
                signatureWindow.Owner = this;
                if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                    return;

                string sampleNumber = GetSignatureRecordNumber();
                _isWorkflowBusy = true;
                SetButtonBusy(BtnCorrectTimes, true, "Correcting...", "Correct Times");
                ExecuteInLocalTransaction((connection, transaction) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        connection, transaction, signatureWindow.SignedBy, "CanRegisterSamples", "correct water sample timestamps");
                    string lockedStatus = LockAndValidateSampleStatusInTransaction(
                        connection, transaction, statusBefore, "Water timestamp correction");
                    if (IsLockedStatus(lockedStatus))
                        throw new InvalidOperationException("Timestamps are locked because the sample has entered review or approval.");

                    int activeCertificates = Convert.ToInt32(ExecuteScalarInTransaction(connection, transaction, @"
SELECT COUNT(1)
FROM dbo.Certificates WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID=@SampleID
  AND ISNULL(IsCancelled,0)=0
  AND ISNULL(CertificateStatus,ISNULL(Status,N'Active')) IN (N'Active',N'Issued');",
                        new SqlParameter("@SampleID", currentSampleId)), CultureInfo.InvariantCulture);
                    if (activeCertificates > 0)
                        throw new InvalidOperationException("An active certificate now exists. Timestamp correction was cancelled.");

                    WaterTimestampSnapshot current = LoadWaterTimestampSnapshotInTransaction(connection, transaction);
                    if (!WaterTimestampsEqual(original, current))
                        throw new InvalidOperationException("The sample timestamps changed while the signature was being completed. Reload and retry.");

                    DateTime serverNow = Convert.ToDateTime(
                        ExecuteScalarInTransaction(connection, transaction, "SELECT SYSDATETIME();"),
                        CultureInfo.InvariantCulture);
                    ValidateWaterTimestampCorrection(corrected, serverNow.AddMinutes(1));

                    SqlParameter DateParameter(string name, DateTime? value) => new SqlParameter(name, SqlDbType.DateTime2)
                    {
                        Value = value.HasValue ? (object)value.Value : DBNull.Value
                    };

                    int affected = ExecuteNonQueryInTransaction(connection, transaction, @"
UPDATE dbo.Samples
SET SamplingDateTime=@Sampling,
    ReceivedDateTime=@Received,
    IncubationStartedDateTime=@IncubationStart,
    IncubationEndDate=@IncubationEnd,
    AnalysisStartedDateTime=@AnalysisStart
WHERE SampleID=@SampleID
  AND ISNULL(LTRIM(RTRIM(Status)),N'')=@ExpectedStatus;",
                        DateParameter("@Sampling", corrected.SamplingDateTime),
                        DateParameter("@Received", corrected.ReceivedDateTime),
                        DateParameter("@IncubationStart", corrected.IncubationStartedDateTime),
                        DateParameter("@IncubationEnd", corrected.IncubationEndDate),
                        DateParameter("@AnalysisStart", corrected.AnalysisStartedDateTime),
                        new SqlParameter("@SampleID", currentSampleId),
                        new SqlParameter("@ExpectedStatus", lockedStatus));
                    if (affected != 1)
                        throw new DBConcurrencyException("The sample changed during timestamp correction. No changes were committed.");

                    AddSampleElectronicSignatureInTransaction(
                        connection, transaction, "Water Timestamp Correction", "Controlled Timestamp Correction", signatureWindow.Reason,
                        signatureWindow.SignedBy, signerRole);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection,
                        transaction,
                        "Samples",
                        currentSampleId,
                        "Water Timestamp Correction",
                        FormatWaterTimestampAuditValue(original),
                        FormatWaterTimestampAuditValue(corrected),
                        signatureWindow.Reason,
                        signatureWindow.SignedBy,
                        "SamplingDateTime / ReceivedDateTime / IncubationStartedDateTime / IncubationEndDate / AnalysisStartedDateTime",
                        null,
                        sampleNumber,
                        "Water");
                });

                LoadSampleFromUi(sampleNumber);
                lblStatus.Text = "Water timestamps corrected with electronic signature and audit trail.";
                MessageBox.Show("Water timestamps were corrected successfully and the change was audited.",
                    "Timestamp Correction", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Timestamp correction failed: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Timestamp Correction", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isWorkflowBusy = false;
                SetButtonBusy(BtnCorrectTimes, false, "Correcting...", "Correct Times");
                UpdateWorkflowButtons();
            }
        }

        private void BtnStartAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (_isWorkflowBusy)
                return;

            if (currentSampleId == 0)
                return;

            if (!CanStartAnalysis())
            {
                MessageBox.Show("You do not have permission to start analysis.",
                    "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string status = GetSampleStatus();

            if (status != "Registered" && status != "Incubation")
            {
                MessageBox.Show("Only Registered or Incubation samples can be started for analysis.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DateTime analysisStartedAt;
            try
            {
                DateTime? recordedStart = GetSampleDateTime("AnalysisStartedDateTime");
                if (recordedStart.HasValue)
                {
                    analysisStartedAt = recordedStart.Value;
                }
                else if (!TryPromptForAnalysisStart(out analysisStartedAt))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to record analysis start: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Analysis Start", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ElectronicSignature signatureWindow =
                _serviceProvider.GetRequiredService<ElectronicSignature>();
            signatureWindow.Configure(GetSignatureRecordNumber(), currentUser, "Analysis Start", true);
            signatureWindow.Owner = this;

            if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                return;

            _isWorkflowBusy = true;
            SetButtonBusy(BtnStartAnalysis, true, "Starting...", "Start Analysis");

            try
            {
                ExecuteInLocalTransaction((con, tran) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        con, tran, signatureWindow.SignedBy, "CanEnterResults", "start water analysis");

                    string lockedStatus = LockAndValidateSampleStatusInTransaction(
                        con, tran, status, "Analysis start");
                    ValidateWaterAnalysisStartInTransaction(con, tran, analysisStartedAt);

                    ExecuteNonQueryInTransaction(con, tran, @"
                        UPDATE dbo.Samples
                        SET AnalysisStartedDateTime = COALESCE(AnalysisStartedDateTime, @analysisStartedAt),
                            IncubationCompletedDateTime = CASE
                                WHEN IncubationEndDate IS NOT NULL AND IncubationEndDate <= @analysisStartedAt
                                    THEN COALESCE(IncubationCompletedDateTime, IncubationEndDate)
                                ELSE IncubationCompletedDateTime
                            END
                        WHERE SampleID = @sampleId",
                        new SqlParameter("@analysisStartedAt", SqlDbType.DateTime2) { Value = analysisStartedAt },
                        new SqlParameter("@sampleId", SqlDbType.Int) { Value = currentSampleId });
                    AddSampleElectronicSignatureInTransaction(
                        con, tran, "Analysis Start", "Analysis Started", signatureWindow.Reason,
                        signatureWindow.SignedBy, signerRole);
                    UpdateSampleStatusInTransaction(con, tran, "In Analysis", lockedStatus, "Analysis start");
                });

                SetSampleStatus("In Analysis");
                UpdateWorkflowButtons();

                MessageBox.Show("Analysis started successfully.",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error starting analysis: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isWorkflowBusy = false;
                SetButtonBusy(BtnStartAnalysis, false, "Starting...", "Start Analysis");
            }
        }

        private void BtnSubmitReview_Click(object sender, RoutedEventArgs e)
        {
            if (_isWorkflowBusy)
                return;

            if (currentSampleId == 0)
                return;

            string status = GetSampleStatus();

            if (!CanSubmitForReview())
            {
                MessageBox.Show("You do not have permission to submit results for review.",
                    "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (status != "Results Entered" &&
                status != "Alert" &&
                status != "Released After Investigation")
            {
                MessageBox.Show("Only samples with Results Entered, Alert, or Released After Investigation status can be submitted for review.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int pendingCount = DatabaseHelper.GetPendingResultsCount(currentSampleId);

            if (pendingCount > 0)
            {
                MessageBox.Show(
                    "Cannot submit for review. There are still pending test results: " + pendingCount,
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            ElectronicSignature signatureWindow =
                _serviceProvider.GetRequiredService<ElectronicSignature>();
            signatureWindow.Configure(GetSignatureRecordNumber(), currentUser, "Submit for Review", true);
            signatureWindow.Owner = this;

            if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                return;

            _isWorkflowBusy = true;
            SetButtonBusy(BtnSubmitReview, true, "Submitting...", "Submit Review");

            try
            {
                ExecuteInLocalTransaction((con, tran) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        con, tran, signatureWindow.SignedBy, "CanEnterResults", "submit water results for review");

                    string lockedStatus = LockAndValidateSampleStatusInTransaction(
                        con, tran, status, "Submit for review");

                    int currentPendingCount = GetPendingResultsCountInTransaction(con, tran);
                    if (currentPendingCount > 0)
                    {
                        throw new InvalidOperationException(
                            "Pending results were detected while the electronic signature was being completed. " +
                            "Submission was cancelled.");
                    }

                    AddSampleElectronicSignatureInTransaction(
                        con, tran, "Submit Review", "Submit for Review", signatureWindow.Reason,
                        signatureWindow.SignedBy, signerRole);
                    UpdateSampleStatusInTransaction(con, tran, "Under Review", lockedStatus, "Submit for review");
                });

                SetSampleStatus("Under Review");
                UpdateWorkflowButtons();

                MessageBox.Show("Sample submitted for review successfully.",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error submitting review: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isWorkflowBusy = false;
                SetButtonBusy(BtnSubmitReview, false, "Submitting...", "Submit Review");
            }
        }

        private void BtnReview_Click(object sender, RoutedEventArgs e)
        {
            if (_isWorkflowBusy)
                return;

            if (currentSampleId == 0)
                return;

            if (!CanReviewSample())
            {
                MessageBox.Show("You do not have permission to review this sample.",
                    "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string status = GetSampleStatus();

            if (status != "Under Review")
            {
                MessageBox.Show("Only Under Review samples can be reviewed.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int pendingCount = DatabaseHelper.GetPendingResultsCount(currentSampleId);

            if (pendingCount > 0)
            {
                MessageBox.Show(
                    "Cannot review sample. There are still pending test results: " + pendingCount,
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            bool hasInvalid = false;

            foreach (ResultItem item in resultItems)
            {
                item.PassFail = CalculatePassFail(item);

                if (string.Equals(item.PassFail, "Invalid", StringComparison.OrdinalIgnoreCase))
                {
                    hasInvalid = true;
                    break;
                }
            }

            if (hasInvalid)
            {
                MessageBox.Show(
                    "Cannot review sample. One or more results are invalid.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            if (!DatabaseHelper.ValidateSampleWorkflowSeparation(
                    currentSampleId, currentUser, "Review", out string reviewSeparationMessage))
            {
                MessageBox.Show(reviewSeparationMessage,
                    "Workflow Separation Block", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ElectronicSignature signatureWindow =
                _serviceProvider.GetRequiredService<ElectronicSignature>();
            signatureWindow.Configure(GetSignatureRecordNumber(), currentUser, "Technical Review", true);
            signatureWindow.Owner = this;

            if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                return;

            _isWorkflowBusy = true;
            SetButtonBusy(BtnReview, true, "Reviewing...", "Review");

            try
            {
                EnsureGlobalWaterDateColumns();

                ExecuteInLocalTransaction((con, tran) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        con, tran, signatureWindow.SignedBy, "CanReviewResults", "review water results");

                    string lockedStatus = LockAndValidateSampleStatusInTransaction(
                        con, tran, status, "Technical review");

                    DatabaseHelper.EnsureSampleWorkflowSeparationInTransaction(
                        con, tran, currentSampleId, signatureWindow.SignedBy, signerRole, "Review");

                    int currentPendingCount = GetPendingResultsCountInTransaction(con, tran);
                    if (currentPendingCount > 0)
                    {
                        throw new InvalidOperationException(
                            "Pending results were detected while the electronic signature was being completed. " +
                            "Review was cancelled.");
                    }

                    AddSampleElectronicSignatureInTransaction(
                        con, tran, "Review", "Technical Review", signatureWindow.Reason,
                        signatureWindow.SignedBy, signerRole);
                    MarkReviewedNowInTransaction(con, tran);
                    UpdateSampleStatusInTransaction(con, tran, "Reviewed", lockedStatus, "Technical review");
                });

                SetSampleStatus("Reviewed");
                UpdateWorkflowButtons();

                MessageBox.Show(
                    "Sample reviewed successfully.\n\nThe sample is now ready for QA approval.",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error reviewing sample: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isWorkflowBusy = false;
                SetButtonBusy(BtnReview, false, "Reviewing...", "Review");
            }
        }

        private void BtnApprove_Click(object sender, RoutedEventArgs e)
        {
            if (_isWorkflowBusy)
                return;

            if (currentSampleId == 0)
                return;

            string status = GetSampleStatus();

            if (!CanApproveSample())
            {
                MessageBox.Show("You do not have permission to approve results.",
                    "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (status != "Reviewed")
            {
                MessageBox.Show("Only Reviewed samples can be approved.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!DatabaseHelper.CanApproveSampleRecord(currentSampleId, out string qualityEventMessage))
            {
                MessageBox.Show(
                    qualityEventMessage,
                    "Quality Event Block",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            int pendingCount = DatabaseHelper.GetPendingResultsCount(currentSampleId);

            if (pendingCount > 0)
            {
                MessageBox.Show(
                    "Cannot approve sample. There are still pending test results: " + pendingCount,
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            if (!DatabaseHelper.ValidateSampleWorkflowSeparation(
                    currentSampleId, currentUser, "Approval", out string approvalSeparationMessage))
            {
                MessageBox.Show(approvalSeparationMessage,
                    "Workflow Separation Block", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ElectronicSignature signatureWindow =
                _serviceProvider.GetRequiredService<ElectronicSignature>();
            signatureWindow.Configure(GetSignatureRecordNumber(), currentUser, "QA Approval", true);
            signatureWindow.Owner = this;

            if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                return;

            _isWorkflowBusy = true;
            SetButtonBusy(BtnApprove, true, "Approving...", "Approve");

            try
            {
                EnsureGlobalWaterDateColumns();

                ExecuteInLocalTransaction((con, tran) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        con, tran, signatureWindow.SignedBy, "CanApproveResults", "approve water results");

                    string lockedStatus = LockAndValidateSampleStatusInTransaction(
                        con, tran, status, "QA approval");

                    DatabaseHelper.EnsureSampleWorkflowSeparationInTransaction(
                        con, tran, currentSampleId, signatureWindow.SignedBy, signerRole, "Approval");
                    DatabaseHelper.EnsureSampleApprovalQualityGatesInTransaction(
                        con, tran, currentSampleId);

                    int currentPendingCount = GetPendingResultsCountInTransaction(con, tran);
                    if (currentPendingCount > 0)
                    {
                        throw new InvalidOperationException(
                            "Pending results were detected while the electronic signature was being completed. " +
                            "Approval was cancelled.");
                    }

                    AddSampleElectronicSignatureInTransaction(
                        con, tran, "Approval", "QA Approval", signatureWindow.Reason,
                        signatureWindow.SignedBy, signerRole);
                    MarkApprovedNowInTransaction(con, tran);
                    UpdateSampleStatusInTransaction(con, tran, "Approved", lockedStatus, "QA approval");
                });

                SetSampleStatus("Approved");
                DatabaseHelper.SyncWaterPlanStatusForSample(currentSampleId);
                UpdateWorkflowButtons();

                MessageBox.Show(
                    "Sample approved successfully.\n\nYou can now issue a certificate.",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error approving sample: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isWorkflowBusy = false;
                SetButtonBusy(BtnApprove, false, "Approving...", "Approve");
            }
        }

        private void BtnPrintReport_Click(object sender, RoutedEventArgs e)
        {
            if (currentSampleId == 0)
            {
                MessageBox.Show("No sample loaded.", "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                if (!DatabaseHelper.CanAccessReports(currentUser))
                {
                    MessageBox.Show("You do not have permission to print controlled reports.",
                        "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var reportWindow = new ReportCertificate(currentSampleId);
                reportWindow.Owner = this;
                reportWindow.Show();

                reportWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    reportWindow.PrintCertificate();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening print report:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCertificate_Click(object sender, RoutedEventArgs e)
        {
            if (currentSampleId == 0)
            {
                MessageBox.Show("No sample loaded.", "Certificate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string status = GetSampleStatus();

                if (!CanOpenOrIssueCertificate())
                {
                    MessageBox.Show("You do not have permission to open or issue certificates.",
                        "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!DatabaseHelper.CanIssueCertificateForSample(currentSampleId, out string qualityEventMessage))
                {
                    MessageBox.Show(
                        qualityEventMessage,
                        "Quality Event Block",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                bool hasActiveCertificate = false;
                try
                {
                    hasActiveCertificate = DatabaseHelper.HasCertificate(currentSampleId);
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Error(
                        $"Unable to verify active certificate state for SampleID {currentSampleId}.", ex);
                    throw new InvalidOperationException(
                        "Certificate state could not be verified. The operation was stopped to protect data integrity.", ex);
                }

                if (status != "Approved" &&
                    status != "COA Issued" &&
                    status != "COA Cancelled" &&
                    !hasActiveCertificate)
                {
                    MessageBox.Show(
                        "Cannot open final certificate. Sample must be approved first, cancelled for reissue, or have an active COA.",
                        "Certificate",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }

                if (_legacyCertificateReissueRouteActive)
                {
                    var routeState = GetLegacyReissueRouteState();
                    if (routeState.TargetActive)
                    {
                        MessageBox.Show(
                            "Step 1 is not complete. Cancel the signed legacy COA first, then issue the replacement certificate.",
                            "Legacy COA Reissue",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    if (!routeState.TargetCancelled && !routeState.ReplacementComplete)
                        throw new InvalidOperationException("The legacy certificate state no longer permits controlled replacement issuance.");
                }

                var certificate = new ReportCertificate(currentSampleId);
                if (_legacyCertificateReissueRouteActive)
                {
                    certificate.ConfigureControlledLegacyReissue(
                        _legacyReissueCertificateId,
                        _legacyReconciliationId,
                        _legacyCertificateNumber,
                        _legacyReissueReason);
                }

                certificate.Owner = this;
                certificate.ShowDialog();

                if (DatabaseHelper.HasCertificate(currentSampleId))
                {
                    string latestStatus = GetSampleStatus();
                    if (latestStatus == "COA Cancelled" || latestStatus == "Approved")
                    {
                        DatabaseHelper.UpdateSampleStatus(currentSampleId, "COA Issued");
                        SetSampleStatus("COA Issued");
                    }
                    else
                    {
                        SetSampleStatus(latestStatus);
                    }
                }
                else
                {
                    SetSampleStatus(GetSampleStatus());
                }

                UpdateWorkflowButtons();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening certificate:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Certificate Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnQualityEvent_Click(object sender, RoutedEventArgs e)
        {
            if (currentSampleId == 0)
            {
                MessageBox.Show("No sample loaded.",
                    "Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                int qualityEventId = DatabaseHelper.GetOpenQualityEventId(currentSampleId);

                if (qualityEventId <= 0)
                    qualityEventId = DatabaseHelper.GetLatestQualityEventId(currentSampleId);

                if (qualityEventId <= 0)
                {
                    bool hasOos = HasCurrentOosResult() || GetSampleStatus().Equals("Under Investigation", StringComparison.OrdinalIgnoreCase);
                    bool hasDeviation = hasOos || HasCurrentAlertOrOosResult() || GetSampleStatus().Equals("Alert", StringComparison.OrdinalIgnoreCase);

                    if (!hasDeviation)
                    {
                        MessageBox.Show("No ALERT/OOS deviation was found for this sample.",
                            "Quality Event",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);

                        UpdateWorkflowButtons();
                        return;
                    }

                    string summary = hasOos
                        ? DatabaseHelper.CreateOrUpdateQualityEventForOOS(
                            currentSampleId,
                            currentUser,
                            "Quality Event opened manually from Results Entry for OOS/ACTION result(s).")
                        : DatabaseHelper.CreateOrUpdateQualityEventForAlert(
                            currentSampleId,
                            currentUser,
                            "Quality Event opened manually from Results Entry for ALERT result(s).");

                    qualityEventId = DatabaseHelper.GetOpenQualityEventId(currentSampleId);

                    if (qualityEventId <= 0)
                    {
                        MessageBox.Show("Unable to create Quality Event. " + summary,
                            "Quality Event",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        UpdateWorkflowButtons();
                        return;
                    }
                }

                var investigationWindow = new QualityEventInvestigation(qualityEventId);
                investigationWindow.Owner = this;
                investigationWindow.ShowDialog();

                string loadedSampleNumber = lblSampleNumber.Text == null ? "" : lblSampleNumber.Text.Trim();

                if (!string.IsNullOrWhiteSpace(loadedSampleNumber))
                    LoadSampleFromUi(loadedSampleNumber);
                else
                    SetSampleStatus(GetSampleStatus());

                UpdateWorkflowButtons();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening Quality Event investigation:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private string PromptForCancellationReason(string initialReason = "")
        {
            Window dialog = new Window
            {
                Title = "Cancel COA",
                Width = 460,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = this
            };

            Grid grid = new Grid
            {
                Margin = new Thickness(16)
            };

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock label = new TextBlock
            {
                Text = "Enter cancellation reason. This reason will be saved in the certificate lifecycle audit.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                FontWeight = FontWeights.SemiBold
            };

            TextBox reasonBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 90,
                Text = initialReason ?? string.Empty
            };

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Button okButton = new Button
            {
                Content = "OK",
                Width = 85,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };

            Button cancelButton = new Button
            {
                Content = "Cancel",
                Width = 85,
                Height = 32,
                IsCancel = true
            };

            okButton.Click += (s, args) =>
            {
                if (string.IsNullOrWhiteSpace(reasonBox.Text))
                {
                    MessageBox.Show("Cancellation reason is required.", "Cancel COA", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                dialog.DialogResult = true;
                dialog.Close();
            };

            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);

            Grid.SetRow(label, 0);
            Grid.SetRow(reasonBox, 1);
            Grid.SetRow(buttons, 2);

            grid.Children.Add(label);
            grid.Children.Add(reasonBox);
            grid.Children.Add(buttons);

            dialog.Content = grid;
            reasonBox.Focus();

            bool? result = dialog.ShowDialog();
            return result == true ? reasonBox.Text.Trim() : "";
        }



        private void BtnCancelCOA_Click(object sender, RoutedEventArgs e)
        {
            if (currentSampleId == 0)
            {
                MessageBox.Show("No sample loaded.", "Cancel COA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                if (!CanCancelCertificate())
                {
                    MessageBox.Show("You do not have permission to cancel certificates.",
                        "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!DatabaseHelper.HasCertificate(currentSampleId))
                {
                    MessageBox.Show("No active certificate was found for this sample.",
                        "Cancel COA", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (_legacyCertificateReissueRouteActive)
                {
                    var routeState = GetLegacyReissueRouteState();
                    if (!routeState.TargetActive)
                    {
                        MessageBox.Show(
                            "The signed legacy certificate is no longer the active certificate for this sample. Refresh the reconciliation workflow before continuing.",
                            "Legacy COA Reissue",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        UpdateWorkflowButtons();
                        return;
                    }
                }

                string reason = PromptForCancellationReason(
                    _legacyCertificateReissueRouteActive ? _legacyReissueReason : string.Empty);
                if (string.IsNullOrWhiteSpace(reason))
                    return;

                MessageBoxResult confirmation = MessageBox.Show(
                    "This will cancel the active COA for this sample. The action cannot be undone. Continue?",
                    "Confirm COA Cancellation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirmation != MessageBoxResult.Yes)
                    return;

                ElectronicSignature signatureWindow =
                    _serviceProvider.GetRequiredService<ElectronicSignature>();
                signatureWindow.Configure(GetSignatureRecordNumber(), currentUser, "COA Cancellation", true);
                signatureWindow.Owner = this;
                signatureWindow.ShowDialog();

                if (!signatureWindow.IsConfirmed)
                    return;

                string resultMessage = _legacyCertificateReissueRouteActive
                    ? DatabaseHelper.CancelCertificateControlled(
                        currentSampleId,
                        _legacyReissueCertificateId,
                        signatureWindow.SignedBy,
                        reason,
                        signatureWindow.Meaning,
                        signatureWindow.Reason)
                    : DatabaseHelper.CancelCertificateControlled(
                        currentSampleId,
                        signatureWindow.SignedBy,
                        reason,
                        signatureWindow.Meaning,
                        signatureWindow.Reason);

                if (!resultMessage.Contains("successfully", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(resultMessage, "Cancel COA", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SetSampleStatus("COA Cancelled");
                UpdateWorkflowButtons();

                MessageBox.Show(
                    _legacyCertificateReissueRouteActive
                        ? $"Legacy {(IsPotableWaterSample() ? "Report" : "COA")} cancellation completed (Step 1). Now use Issue Replacement {(IsPotableWaterSample() ? "Report" : "COA")} (Step 2) so the replacement is linked to the signed legacy certificate and receives a native immutable snapshot."
                        : "COA cancelled successfully.",
                    _legacyCertificateReissueRouteActive ? "Legacy COA Reissue" : "Cancel COA",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error cancelling COA:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Cancel COA Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
