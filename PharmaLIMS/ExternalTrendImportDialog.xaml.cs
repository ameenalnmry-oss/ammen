#nullable disable
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PharmaLIMS
{
    public partial class ExternalTrendImportDialog : Window
    {
        private readonly ExternalTrendImportService importService = new();
        private TrendImportPreview currentPreview;
        private int historySelectionRequest;
        private CancellationTokenSource? historySelectionCancellation;
        private CancellationTokenSource? parameterSelectionCancellation;
        private readonly string preferredModule;

        public int? SelectedApprovedBatchId { get; private set; }
        public string SelectedParameter { get; private set; } = string.Empty;

        public ExternalTrendImportDialog() : this("Water")
        {
        }

        public ExternalTrendImportDialog(string preferredModule)
        {
            this.preferredModule = string.Equals(preferredModule, "Environmental Monitoring", StringComparison.OrdinalIgnoreCase)
                ? "Environmental Monitoring"
                : "Water";
            InitializeComponent();
            Loaded += ExternalTrendImportDialog_Loaded;
        }

        private async void ExternalTrendImportDialog_Loaded(object sender, RoutedEventArgs e)
        {
            txtSourceSystem.Text = "External Laboratory / Instrument";
            cboModule.SelectedIndex = preferredModule == "Environmental Monitoring" ? 1 : 0;
            await RefreshHistoryAsync();
        }

        private string SelectedModule()
        {
            return (cboModule.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Water";
        }

        private void ModuleSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
                return;

            // A preview is controlled by the selected module. Never leave a Water-parsed
            // preview visible after the operator changes the selector to EM (or vice versa).
            if (currentPreview != null && !string.IsNullOrWhiteSpace(txtFilePath.Text) && File.Exists(txtFilePath.Text))
            {
                try
                {
                    currentPreview = importService.ParseFile(txtFilePath.Text, SelectedModule());
                    dgPreview.ItemsSource = importService.BuildPreviewTable(currentPreview).DefaultView;
                    lstIssues.ItemsSource = currentPreview.Issues;
                    lblPreviewSummary.Text = $"Module: {SelectedModule()} | File: {currentPreview.FileName} | Rows accepted: {currentPreview.Rows.Count:N0} | Findings: {currentPreview.Issues.Count:N0} | SHA-256: {currentPreview.FileHashSha256}";
                }
                catch (Exception ex)
                {
                    currentPreview = null;
                    dgPreview.ItemsSource = null;
                    lstIssues.ItemsSource = null;
                    lblPreviewSummary.Text = "The selected file must be revalidated for the new module. " + Infrastructure.UserFacingError.SafeMessage(ex);
                }
            }
            else
            {
                lblPreviewSummary.Text = SelectedModule() == "Environmental Monitoring"
                    ? "Select a CSV or XLSX file. EM requires AreaClassification and Method for every row."
                    : "Select a CSV or XLSX file for controlled Water trend staging.";
            }
            UpdateStageAvailability();
        }

        private static string CurrentUser()
        {
            return string.IsNullOrWhiteSpace(Login.CurrentUser) ? string.Empty : Login.CurrentUser.Trim();
        }

        private static bool IsAdministrativeRole()
        {
            if (!AppConfig.DevelopmentAdminFullPermissions)
                return false;

            string role = DatabaseHelper.GetUserRole(CurrentUser());
            return role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                   role.Equals("Administrator", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanStageImport()
        {
            return IsAdministrativeRole() ||
                   DatabaseHelper.CanEditResults(CurrentUser());
        }

        private static bool CanApproveImport()
        {
            return IsAdministrativeRole() ||
                   DatabaseHelper.CanApproveResults(CurrentUser());
        }

        private void SelectFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select Controlled External Trend Data",
                    Filter = "Supported files (*.csv;*.xlsx)|*.csv;*.xlsx|CSV files (*.csv)|*.csv|Excel workbooks (*.xlsx)|*.xlsx",
                    Multiselect = false,
                    CheckFileExists = true
                };

                if (dialog.ShowDialog(this) != true)
                    return;

                currentPreview = importService.ParseFile(dialog.FileName, SelectedModule());
                txtFilePath.Text = dialog.FileName;
                dgPreview.ItemsSource = importService.BuildPreviewTable(currentPreview).DefaultView;
                lstIssues.ItemsSource = currentPreview.Issues;
                lblPreviewSummary.Text = $"File: {currentPreview.FileName} | Rows accepted: {currentPreview.Rows.Count:N0} | " +
                                         $"Findings: {currentPreview.Issues.Count:N0} | SHA-256: {currentPreview.FileHashSha256}";
                UpdateStageAvailability();
            }
            catch (Exception ex)
            {
                currentPreview = null;
                UpdateStageAvailability();
                ShowError("The selected file could not be validated.\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private void DownloadTemplate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save Controlled Trend Import Template",
                    FileName = SelectedModule() == "Water" ? "PharmaLIMS_Water_Trend_Template.csv" : "PharmaLIMS_EM_Trend_Template.csv",
                    Filter = "CSV file (*.csv)|*.csv",
                    AddExtension = true,
                    DefaultExt = ".csv"
                };
                if (dialog.ShowDialog(this) != true)
                    return;

                File.WriteAllText(dialog.FileName, importService.BuildTemplate(SelectedModule()), new UTF8Encoding(true));
                MessageBox.Show(this, "The controlled template was saved successfully.", "Template",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowError("The template could not be saved.\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private async void StageImport_Click(object sender, RoutedEventArgs e)
        {
            bool stagedSuccessfully = false;
            object originalButtonContent = btnStage.Content;
            string originalSummary = lblPreviewSummary.Text;

            try
            {
                if (!CanStageImport())
                    throw new InvalidOperationException("You do not have permission to stage external trend data.");
                if (currentPreview == null)
                    throw new InvalidOperationException("Select and validate a source file first.");
                if (string.IsNullOrWhiteSpace(txtImportReason.Text) || txtImportReason.Text.Trim().Length < 10)
                {
                    MessageBox.Show(this,
                        "Enter an import reason of at least 10 characters before staging the file.",
                        "Import Reason Required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    txtImportReason.Focus();
                    return;
                }

                TrendImportPreview previewToStage = currentPreview;
                string sourceSystem = txtSourceSystem.Text;
                string importReason = txtImportReason.Text;
                string importedBy = CurrentUser();

                btnStage.IsEnabled = false;
                btnStage.Content = "Staging...";
                lblPreviewSummary.Text = $"Staging {previewToStage.Rows.Count:N0} validated rows. Please wait; do not close this window.";
                Mouse.OverrideCursor = Cursors.Wait;

                TrendImportStageResult stageResult = await Task.Run(() => importService.StageImport(
                    previewToStage,
                    sourceSystem,
                    importReason,
                    importedBy));

                stagedSuccessfully = true;

                string message = stageResult.AlreadyExists
                    ? $"This exact source file is already controlled as {stageResult.ImportNumber} ({stageResult.Status}). No duplicate batch was created; the existing batch has been selected."
                    : $"Import batch {stageResult.BatchId} was staged successfully and requires approval by another authorized user.";
                MessageBox.Show(this, message, stageResult.AlreadyExists ? "Existing Import Opened" : "Import Staged",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                ResetNewImport();
                await RefreshHistoryAsync();
                tabImport.SelectedIndex = 1;
                SelectHistoryBatch(stageResult.BatchId);
            }
            catch (Exception ex)
            {
                lblPreviewSummary.Text = originalSummary;
                ShowDatabaseAwareError("The import could not be staged.", ex);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                btnStage.Content = originalButtonContent;
                if (!stagedSuccessfully)
                    UpdateStageAvailability();
            }
        }

        private void ImportReason_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateStageAvailability();
        }

        private void UpdateStageAvailability()
        {
            btnStage.IsEnabled = currentPreview != null &&
                                 !currentPreview.HasErrors &&
                                 currentPreview.Rows.Count > 0 &&
                                 CanStageImport() &&
                                 !string.IsNullOrWhiteSpace(txtImportReason.Text) &&
                                 txtImportReason.Text.Trim().Length >= 10;
        }

        private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
        {
            await RefreshHistoryAsync();
        }

        private async Task RefreshHistoryAsync()
        {
            try
            {
                lblHistorySelection.Text = "Loading controlled import history...";
                dgHistory.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;
                DataTable history = await importService.GetImportHistoryAsync();
                dgHistory.ItemsSource = history.DefaultView;
                cboMethod.ItemsSource = null;
                cboParameter.ItemsSource = null;
                lblHistorySelection.Text = "Select an import batch.";
            }
            catch (Exception ex)
            {
                if (IsTransientDatabaseBusy(ex))
                {
                    ApplicationLogger.Warning("External Trend import history remained busy after bounded read retries.", ex);
                    lblHistorySelection.Text = "Database activity is in progress. Import history was not refreshed; use Refresh after the current database operation completes.";
                }
                else
                {
                    ShowDatabaseAwareError("Import history could not be loaded.", ex);
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
                dgHistory.IsEnabled = true;
            }
        }

        private void SelectHistoryBatch(int batchId)
        {
            foreach (object item in dgHistory.Items)
            {
                if (item is DataRowView row && Convert.ToInt32(row["ImportBatchID"]) == batchId)
                {
                    dgHistory.SelectedItem = row;
                    dgHistory.ScrollIntoView(row);
                    return;
                }
            }
        }

        private DataRowView SelectedHistoryRow()
        {
            return dgHistory.SelectedItem as DataRowView;
        }

        private async void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int request = ++historySelectionRequest;
            historySelectionCancellation?.Cancel();
            historySelectionCancellation?.Dispose();
            historySelectionCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = historySelectionCancellation.Token;

            try
            {
                DataRowView row = SelectedHistoryRow();
                if (row == null)
                    return;

                int batchId = Convert.ToInt32(row["ImportBatchID"]);
                lblHistorySelection.Text = $"{row["ImportNumber"]} | {row["Module"]} | {row["Status"]} | " +
                                           $"Imported by {row["ImportedBy"]} | Loading methods...";

                DataTable methods = await importService.GetBatchMethodsAsync(batchId, cancellationToken);
                if (request != historySelectionRequest || SelectedHistoryRow() != row)
                    return;

                cboMethod.ItemsSource = methods.DefaultView;
                cboParameter.ItemsSource = null;
                if (cboMethod.Items.Count > 0)
                    cboMethod.SelectedIndex = 0;

                lblHistorySelection.Text = $"{row["ImportNumber"]} | {row["Module"]} | {row["Status"]} | " +
                                           $"Imported by {row["ImportedBy"]}";
            }
            catch (OperationCanceledException)
            {
                // A newer batch selection superseded this read.
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Import methods could not be loaded without blocking batch review.", ex);
                if (request == historySelectionRequest)
                {
                    cboMethod.ItemsSource = null;
                    cboParameter.ItemsSource = null;
                    lblHistorySelection.Text = "Batch selected. Approval remains available; methods will load after database activity completes.";
                }
            }
        }

        private async void HistoryMethodSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DataRowView? row = SelectedHistoryRow();
            if (row == null || cboMethod.SelectedValue is not string methodName)
                return;

            parameterSelectionCancellation?.Cancel();
            parameterSelectionCancellation?.Dispose();
            parameterSelectionCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = parameterSelectionCancellation.Token;

            try
            {
                int batchId = Convert.ToInt32(row["ImportBatchID"]);
                cboParameter.ItemsSource = null;
                IReadOnlyList<string> parameters = await importService.GetBatchParametersAsync(batchId, methodName, cancellationToken);
                if (SelectedHistoryRow() != row || !string.Equals(cboMethod.SelectedValue?.ToString(), methodName, StringComparison.Ordinal))
                    return;
                cboParameter.ItemsSource = parameters;
                if (cboParameter.Items.Count > 0)
                    cboParameter.SelectedIndex = 0;
            }
            catch (OperationCanceledException)
            {
                // A newer method selection superseded this read.
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Import parameters could not be loaded for the selected method.", ex);
                cboParameter.ItemsSource = null;
            }
        }

        private async void ApproveImport_Click(object sender, RoutedEventArgs e)
        {
            await ChangeImportStatusAsync(true);
        }

        private async void RejectImport_Click(object sender, RoutedEventArgs e)
        {
            await ChangeImportStatusAsync(false);
        }

        private async Task ChangeImportStatusAsync(bool approve)
        {
            try
            {
                if (!CanApproveImport())
                    throw new InvalidOperationException("Only an authorized QA/approver may approve or reject an external import.");

                DataRowView row = SelectedHistoryRow();
                if (row == null)
                    throw new InvalidOperationException("Select an import batch first.");
                if (!string.Equals(row["Status"]?.ToString(), "Pending Approval", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only a Pending Approval import can be processed.");

                int batchId = Convert.ToInt32(row["ImportBatchID"]);
                string importNumber = row["ImportNumber"]?.ToString() ?? batchId.ToString();
                string action = approve ? "Approve External Trend Import" : "Reject External Trend Import";
                var signature = new ElectronicSignature(importNumber, CurrentUser(), action, true) { Owner = this };
                signature.ShowDialog();
                if (!signature.IsConfirmed)
                {
                    MessageBox.Show(this,
                        "No approval was recorded because the electronic signature was not confirmed. " +
                        "The batch remains Pending Approval. Complete the signature confirmation or use the displayed password-validation message to resolve the database connection.",
                        "Electronic Signature Not Completed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                lblHistorySelection.Text = approve ? "Recording controlled approval..." : "Recording controlled rejection...";
                dgHistory.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;

                await Task.Run(() =>
                {
                    if (approve)
                        importService.ApproveImport(batchId, signature.SignedBy, signature.Meaning, signature.Reason);
                    else
                        importService.RejectImport(batchId, signature.SignedBy, signature.Meaning, signature.Reason);
                });

                MessageBox.Show(this, approve ? "The import was approved." : "The import was rejected.",
                    "External Trend Import", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshHistoryAsync();
            }
            catch (Exception ex)
            {
                ShowDatabaseAwareError("The approval action could not be completed.", ex);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                dgHistory.IsEnabled = true;
            }
        }

        private void AnalyzeImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DataRowView row = SelectedHistoryRow();
                if (row == null)
                    throw new InvalidOperationException("Select an import batch first.");
                if (!string.Equals(row["Status"]?.ToString(), "Approved", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only approved external data can be analyzed.");
                int batchId = Convert.ToInt32(row["ImportBatchID"]);
                string module = row["Module"]?.ToString() ?? string.Empty;
                if (module.Equals("Environmental Monitoring", StringComparison.OrdinalIgnoreCase))
                {
                    new ExternalTrendThreeCycleReviewWindow { Owner = this }.ShowDialog();
                }
                else if (module.Equals("Water", StringComparison.OrdinalIgnoreCase))
                {
                    string parameter = cboParameter.SelectedValue?.ToString() ?? cboParameter.SelectedItem?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(parameter))
                        throw new InvalidOperationException("Select a Water parameter to analyze.");
                    new ReportsTrends(batchId, parameter) { Owner = this }.ShowDialog();
                }
                else
                {
                    throw new InvalidOperationException("The selected external import module is not supported for analysis.");
                }
            }
            catch (Exception ex)
            {
                ShowError(Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private void ResetNewImport()
        {
            currentPreview = null;
            txtFilePath.Clear();
            txtImportReason.Clear();
            dgPreview.ItemsSource = null;
            lstIssues.ItemsSource = null;
            btnStage.IsEnabled = false;
            lblPreviewSummary.Text = "Select a CSV or XLSX file. EM also requires AreaClassification (Classified / Grade D / ISO 8, or Unclassified).";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static bool IsTransientDatabaseBusy(Exception ex) =>
            ex is SqlException sql && (sql.Number == -2 || sql.Number == 1222);

        private void ShowDatabaseAwareError(string prefix, Exception ex)
        {
            ApplicationLogger.Error(prefix, ex);
            string message;
            if (ex is SqlException sql && (sql.Number == 208 || sql.Number == 207))
                message = prefix + "\n\nRequired database objects or columns are missing. Apply migrations 20260810_001 and 20260811_001 to the PharmaLIMS database, then reopen this window.";
            else if (ex is SqlException timeout && (timeout.Number == -2 || timeout.Number == 1222))
                message = prefix + "\n\nSQL Server could not obtain the required row locks in time. Close other running PharmaLIMS/debug sessions, verify no migration query is left open in SSMS, then retry once.";
            else
                message = prefix + "\n\n" + Infrastructure.UserFacingError.SafeMessage(ex);
            ShowError(message);
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "External Trend Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
