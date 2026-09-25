using PharmaLIMS.Services;
using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Interfaces;
using PharmaLIMS.Repositories;

namespace PharmaLIMS
{
    // ==================== MAIN WINDOW ====================

    public partial class CultureMediaPreparation : Window
    {
        private readonly string _currentUser;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _toastTimer;
        private readonly ICultureMediaRepository _repository;

        private int _selectedLotId;
        private int _selectedPreparationId;
        private int _selectedPreparationPrintId;
        private int _selectedReleaseReportId;
        private bool _isLoading;
        private DataTable _currentOrganisms = new DataTable();
        private string _pendingSignatureEntityType = string.Empty;
        private int _pendingSignatureEntityId;
        private string _pendingSignatureRecordNumber = string.Empty;
        private string _pendingSignatureAction = string.Empty;
        private string _pendingSignatureSignedBy = string.Empty;
        private string _pendingSignatureMeaning = string.Empty;
        private string _pendingSignatureReason = string.Empty;

        private static bool IsCultureMediaAdministrator()
        {
            string role = Login.CurrentUserRole ?? string.Empty;
            return AppConfig.DevelopmentAdminFullPermissions &&
                   (role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
        }

        private static bool CanEnterCultureMedia()
            => IsCultureMediaAdministrator() ||
               DatabaseHelper.CanEditResults(Login.CurrentUser) ||
               DatabaseHelper.CanRegisterSamples(Login.CurrentUser);

        private static bool CanReviewCultureMedia()
        {
            return IsCultureMediaAdministrator() ||
                   DatabaseHelper.CanReviewResults(Login.CurrentUser);
        }

        private static bool CanPrintCultureMedia()
            => IsCultureMediaAdministrator() || DatabaseHelper.CanAccessReports(Login.CurrentUser);

        private static void RequireCultureMediaEntryPermission()
        {
            if (!CanEnterCultureMedia())
                throw new InvalidOperationException("Culture media receipt, preparation, and qualification entry require data-entry permission.");
        }

        private static void RequireCultureMediaReviewPermission()
        {
            if (!CanReviewCultureMedia())
                throw new InvalidOperationException("Culture media review requires review permission.");
        }

        private static void RequireCultureMediaPrintPermission()
        {
            if (!CanPrintCultureMedia())
                throw new InvalidOperationException("Culture media report and label printing require report access permission.");
        }

        private static bool EnsureCultureMediaEntryAccess()
        {
            if (CanEnterCultureMedia()) return true;
            MessageBox.Show("Culture media entry permission is required.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private static bool EnsureCultureMediaPrintAccess()
        {
            if (CanPrintCultureMedia()) return true;
            MessageBox.Show("Culture media report access permission is required.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private static bool CanReleaseCultureMedia()
        {
            return IsCultureMediaAdministrator() ||
                   DatabaseHelper.CanQaApproveResults(Login.CurrentUser);
        }

        private static void RequireCultureMediaReleasePermission()
        {
            if (!CanReleaseCultureMedia())
                throw new InvalidOperationException("Only QA/Admin users with approval permission can release or reject culture media.");
        }

        public CultureMediaPreparation()
        {
            InitializeComponent();

            _currentUser = GetCurrentUserName();
            _repository = new CultureMediaRepository(AppConfig.ConnectionString);

            // Status timer
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += (s, e) =>
            {
                _statusTimer.Stop();
                TxtHeaderStatus.Text = "Ready";
            };

            // Toast timer
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                pnlToast.Visibility = Visibility.Collapsed;
            };

            // Search placeholder
            txtGlobalSearch.GotFocus += (s, e) => txtSearchPlaceholder.Visibility = Visibility.Collapsed;
            txtGlobalSearch.LostFocus += (s, e) =>
                txtSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(txtGlobalSearch.Text) ? Visibility.Visible : Visibility.Collapsed;

            // Hide side panel initially
            pnlSidePanel.Visibility = Visibility.Collapsed;

            InitializeCurrentOrganismTable();
            DgCurrentOrganisms.ItemsSource = _currentOrganisms.DefaultView;
            InitializeDefaults();
            WireWorkflowChecklistEvents();
            Loaded += CultureMediaPreparation_Loaded;
        }

        private async void CultureMediaPreparation_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= CultureMediaPreparation_Loaded;
            await LoadAllDataAsync();
        }

        private void WireWorkflowChecklistEvents()
        {
            void RefreshRelease(object? sender, EventArgs e) => UpdateReleaseNextAction();

            TxtReleaseReviewedBy.TextChanged += RefreshRelease;
            TxtReleaseArNo.TextChanged += RefreshRelease;
            TxtReleaseBottleNo.TextChanged += RefreshRelease;
            TxtReleaseMediaPh.TextChanged += RefreshRelease;
            CmbReleaseConclusion.SelectionChanged += (_, _) => UpdateReleaseNextAction();
            CmbOverallResult.SelectionChanged += (_, _) => UpdateReleaseNextAction();
            DpReleaseDate.SelectedDateChanged += (_, _) => UpdateReleaseNextAction();
        }

        private void EnsureCultureMediaSopSchema()
        {
            object? ready = _repository.ReadScalar(CultureMediaScalar.CultureMediaSopSchemaReady);

            if (ready == null || ready == DBNull.Value || Convert.ToInt32(ready, CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException(
                    "The controlled Culture Media schema is incomplete. Apply the approved migration manifest including " +
                    "Database/Migrations/20260906_001_GMP_Timing_Governance_Hardening.sql before opening this module.");
            }
        }

        private void InitializeCurrentOrganismTable()
        {
            _currentOrganisms = new DataTable();
            _currentOrganisms.Columns.Add("TestName", typeof(string));
            _currentOrganisms.Columns.Add("OrganismName", typeof(string));
            _currentOrganisms.Columns.Add("ATCCNumber", typeof(string));
            _currentOrganisms.Columns.Add("InoculumLevel", typeof(string));
            _currentOrganisms.Columns.Add("ExpectedResult", typeof(string));
            _currentOrganisms.Columns.Add("ActualResult", typeof(string));
            _currentOrganisms.Columns.Add("ControlCount", typeof(string));
            _currentOrganisms.Columns.Add("TestCount", typeof(string));
            _currentOrganisms.Columns.Add("RecoveryPercent", typeof(string));
            _currentOrganisms.Columns.Add("IncubationConditions", typeof(string));
            _currentOrganisms.Columns.Add("TestResult", typeof(string));
            _currentOrganisms.Columns.Add("Remarks", typeof(string));
        }

        private void InitializeDefaults()
        {
            // Set default values for hidden controls
            TxtPreparedBy.Text = string.Empty;
            TxtReleasePerformedBy.Text = _currentUser;
            SetComboText(CmbReceiptStatus, "Quarantine");
            SetComboText(CmbOverallResult, "Pending");
            SetComboText(CmbTestResult, "Pass");

            // Set default dates
            if (DpReceiptDate != null)
                DpReceiptDate.SelectedDate = DateTime.Today;
            if (DpPreparationDate != null)
                DpPreparationDate.SelectedDate = DateTime.Today;
            if (DpPreparationExpiry != null)
                DpPreparationExpiry.SelectedDate = DateTime.Today.AddDays(14);
            if (DpReleaseDate != null)
                DpReleaseDate.SelectedDate = null;

            UpdateReleaseTestTypeFields();
        }

        private bool IsDevelopmentAdminOverride()
        {
            string role = Login.CurrentUserRole ?? string.Empty;
            return IsDevelopmentAdminOverrideForRole(role);
        }

        private static bool IsDevelopmentAdminOverrideForRole(string role)
        {
            return AppConfig.DevelopmentAdminFullPermissions &&
                   (role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
        }

        private string GetCurrentUserName()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                    return Login.CurrentUser.Trim();
            }
            catch (Exception ex)
            {
                Infrastructure.ApplicationLogger.Warning("Unable to resolve the authenticated culture-media account.", ex);
            }

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required to use the Culture Media module.");
        }

        // ==================== LOADING ====================

        private async Task LoadAllDataAsync()
        {
            if (_isLoading) return;
            try
            {
                _isLoading = true;
                Mouse.OverrideCursor = Cursors.Wait;
                SetStatus("Loading data...");

                await Task.Run(EnsureCultureMediaSopSchema);
                await Task.WhenAll(
                    LoadReceiptsAsync(),
                    LoadReleaseReportsAsync(),
                    LoadPreparationsAsync(),
                    LoadMediaForFiltersAsync(),
                    LoadStoredMediaForPreparationAsync(),
                    LoadPreparationsForReleaseAsync(),
                    UpdateDashboardAsync());

                SetStatus("Data loaded");
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Culture media workspace loading failed.", ex);
                ShowError("Error loading data", ex);
            }
            finally
            {
                _isLoading = false;
                Mouse.OverrideCursor = null;
            }
        }

        private async Task LoadReceiptsAsync()
        {

            DataTable data = await Task.Run(() => _repository.Load(CultureMediaQuery.LoadReceipts));
            DgReceipts.ItemsSource = data.DefaultView;
            UpdateRecordCount(data.Rows.Count);
        }

        private async Task LoadReleaseReportsAsync()
        {

            DataTable data = await Task.Run(() => _repository.Load(CultureMediaQuery.LoadReleaseReports));
            DgReleaseReports.ItemsSource = data.DefaultView;
        }

        private async Task LoadPreparationsAsync()
        {

            DataTable data = await Task.Run(() => _repository.Load(CultureMediaQuery.LoadPreparations));
            DgPreparations.ItemsSource = data.DefaultView;
        }

        private async Task LoadMediaForFiltersAsync()
        {

            DataTable data = await Task.Run(() => _repository.Load(CultureMediaQuery.LoadMediaForFilters));
            cboFilterMedia.ItemsSource = data.DefaultView;
            cboFilterMedia.DisplayMemberPath = "MediaCode";
            cboFilterMedia.SelectedValuePath = "MediaID";
        }

        private async Task LoadStoredMediaForPreparationAsync()
        {
            object? previousValue = CmbPreparationMediaLot?.SelectedValue;

            DataTable data = await Task.Run(() => _repository.Load(CultureMediaQuery.LoadStoredMediaForPreparation));

            if (CmbPreparationMediaLot != null)
            {
                CmbPreparationMediaLot.ItemsSource = data.DefaultView;
                CmbPreparationMediaLot.DisplayMemberPath = "DisplayName";
                CmbPreparationMediaLot.SelectedValuePath = "MediaLotID";

                if (_selectedLotId > 0)
                    CmbPreparationMediaLot.SelectedValue = _selectedLotId;
                else if (previousValue != null)
                    CmbPreparationMediaLot.SelectedValue = previousValue;
            }
        }

        private async Task LoadPreparationsForReleaseAsync()
        {
            object? previousValue = CmbReleaseMediaLot?.SelectedValue;

            DataTable data = await Task.Run(() => _repository.Load(CultureMediaQuery.LoadMediaLotsForRelease));
            if (CmbReleaseMediaLot != null)
            {
                CmbReleaseMediaLot.ItemsSource = data.DefaultView;
                CmbReleaseMediaLot.DisplayMemberPath = "DisplayName";
                CmbReleaseMediaLot.SelectedValuePath = "MediaLotID";

                if (_selectedLotId > 0)
                    CmbReleaseMediaLot.SelectedValue = _selectedLotId;
                else if (previousValue != null)
                    CmbReleaseMediaLot.SelectedValue = previousValue;
            }
        }

        private void LoadGPTHistory(int lotId)
        {

            var data = _repository.Load(CultureMediaQuery.LoadGptHistory, new SqlParameter("@MediaLotID", lotId));

            if (data.Rows.Count > 0)
            {
                dgDetailGPT.ItemsSource = data.DefaultView;
                dgDetailGPT.Visibility = Visibility.Visible;
                txtNoGPT.Visibility = Visibility.Collapsed;
            }
            else
            {
                dgDetailGPT.ItemsSource = null;
                dgDetailGPT.Visibility = Visibility.Collapsed;
                txtNoGPT.Visibility = Visibility.Visible;
            }
        }

        private async Task UpdateDashboardAsync()
        {
            try
            {

                DataTable data = await Task.Run(() => _repository.Load(CultureMediaQuery.LoadDashboardSummary));
                if (data.Rows.Count > 0)
                {
                    var row = data.Rows[0];
                    lblTotalBatches.Text = RowInt(row, "Total").ToString();
                    lblReleased.Text = RowInt(row, "Released").ToString();
                    lblQuarantine.Text = RowInt(row, "Quarantine").ToString();
                    lblRejected.Text = RowInt(row, "Rejected").ToString();
                    lblExpiring.Text = RowInt(row, "Expiring").ToString();
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to refresh the culture-media dashboard summary.", ex);
            }
        }

        // ==================== HELPERS ====================

        private static string RowString(DataRow row, string column)
            => row?.Table?.Columns.Contains(column) == true && row[column] != DBNull.Value
                ? row[column].ToString() ?? string.Empty
                : string.Empty;

        private static int RowInt(DataRow row, string column)
            => int.TryParse(RowString(row, column), out int value) ? value : 0;

        private static DateTime? RowDate(DataRow row, string column)
            => DateTime.TryParse(RowString(row, column), out DateTime date) ? date : (DateTime?)null;

        private static int SelectedInt(ComboBox combo)
        {
            if (combo == null)
                return 0;

            if (combo.SelectedValue == null)
                return 0;

            return int.TryParse(combo.SelectedValue.ToString(), out int value) ? value : 0;
        }

        private static DataRow? RowFromGrid(DataGrid grid)
            => (grid.SelectedItem as DataRowView)?.Row;

        private static string ComboText(ComboBox combo)
            => combo == null ? string.Empty : ((combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? combo.Text ?? string.Empty);

        private static string EscapeRowFilterValue(string value)
            => (value ?? string.Empty).Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");

        private static void SetComboText(ComboBox combo, string value)
        {
            foreach (object item in combo.Items)
            {
                if (item is ComboBoxItem comboItem &&
                    string.Equals(comboItem.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = comboItem;
                    return;
                }
            }
            combo.Text = value;
        }

        private static string NormalizeLotStatusForSave(string status)
        {
            status = (status ?? string.Empty).Trim();
            if (status.Equals("Accepted", StringComparison.OrdinalIgnoreCase))
                return "Released";
            if (string.IsNullOrWhiteSpace(status))
                return "Quarantine";
            return status;
        }

        private static string NormalizeStatus(string status)
        {
            status = (status ?? string.Empty).Trim();

            if (status.Equals("Accepted", StringComparison.OrdinalIgnoreCase))
                return "Released";

            if (status.Equals("UnderRelease", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Under Release", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Pending Release", StringComparison.OrdinalIgnoreCase))
                return "Under Release";

            if (status.Equals("GPT Passed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                return "Released";

            if (status.Equals("GPT Failed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                return "Rejected";

            return status;
        }

        private static object DbValue(string value)
            => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

        private static object DbDate(DateTime? value)
            => value.HasValue ? value.Value.Date : DBNull.Value;

        private static object DbDecimal(string value)
        {
            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result))
                return result;
            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out result))
                return result;
            return DBNull.Value;
        }

        private static bool TryParseGramQuantity(string value, out decimal quantityG)
        {
            bool parsed = decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out quantityG) ||
                          decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out quantityG);
            return parsed && quantityG > 0 && decimal.Round(quantityG, 3) == quantityG;
        }

        private static decimal ParseGramQuantity(string value)
        {
            if (!TryParseGramQuantity(value, out decimal quantityG))
                throw new InvalidOperationException("A positive gram quantity with no more than three decimal places is required.");
            return quantityG;
        }

        private static string FormatDate(DateTime? date)
            => date?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

        // ==================== UI HELPERS ====================

        private void SetStatus(string message)
        {
            TxtHeaderStatus.Text = message;
            lblStatus.Text = message;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private void ShowToast(string message, string icon = "✅")
        {
            txtToastIcon.Text = icon;
            txtToastMessage.Text = message;
            pnlToast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void ShowError(string message, Exception? ex = null)
        {
            SetStatus("Error");
            MessageBox.Show($"{message}: {ex?.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void UpdateRecordCount(int count)
            => lblRecordCount.Text = $"{count} records";

        private void UpdatePanelStatus(string? status)
        {
            var color = status?.ToLowerInvariant() switch
            {
                "accepted" or "released" => "#16A34A",
                "quarantine" or "pending" => "#F59E0B",
                "rejected" => "#DC2626",
                _ => "#94A3B8"
            };

            pnlStatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            txtDetailStatus.Text = status ?? "-";
        }

        private void FillPanelDetails(DataRow row)
        {
            txtDetailMedia.Text = $"{RowString(row, "MediaCode")} - {RowString(row, "MediaName")}";
            txtDetailLotNo.Text = RowString(row, "LotNumber");
            txtDetailSupplier.Text = RowString(row, "Supplier");
            txtDetailReceived.Text = RowString(row, "ReceivedDateText");
            txtDetailExpiry.Text = RowString(row, "ExpiryDateText");
            txtDetailQuantity.Text = RowString(row, "QuantityReceived");
            txtDetailCOA.Text = RowString(row, "COANumber");
            txtDetailStorage.Text = RowString(row, "StorageCondition");
            txtDetailRemarks.Text = RowString(row, "Remarks");
            UpdatePanelStatus(RowString(row, "ReceiptStatus"));
        }

        private void UpdatePreparationMediaInfo(DataRow row)
        {
            // This would fill the preparation form fields if visible
        }

        private int GetDefaultExpiryDays(DataRow row)
        {
            if (int.TryParse(RowString(row, "DefaultExpiryDays"), out int days) && days > 0)
                return days;
            return 14;
        }

        // ==================== EVENTS ====================

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadAllDataAsync();
            ShowToast("Data refreshed", "🔄");
        }

        private void BtnClosePanel_Click(object sender, RoutedEventArgs e)
        {
            pnlSidePanel.Visibility = Visibility.Collapsed;
            DgReceipts.SelectedItem = null;
        }

        private void BtnNewReceipt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Reset form for new receipt
                _selectedLotId = 0;

                // Clear receipt fields
                TxtReceiptMediaCode.Clear();
                TxtReceiptMediaName.Clear();
                TxtReceiptMediaType.Clear();
                TxtReceiptStorageCondition.Clear();
                TxtReceiptSupplier.Clear();
                TxtReceiptSupplierLot.Clear();
                TxtReceiptCOA.Clear();
                TxtReceiptQuantity.Clear();
                TxtReceiptSerialNo.Clear();
                TxtReceiptMpmNo.Clear();
                TxtReceiptPackNo.Clear();
                TxtReceiptTotalPacks.Clear();
                TxtReceiptPackSize.Clear();
                TxtReceiptInitialBalance.Clear();

                // Set defaults
                if (DpReceiptDate != null)
                    DpReceiptDate.SelectedDate = DateTime.Today;
                if (DpReceiptExpiry != null)
                    DpReceiptExpiry.SelectedDate = null;
                if (DpReceiptOpeningDate != null)
                    DpReceiptOpeningDate.SelectedDate = null;
                if (DpReceiptLotReleaseDate != null)
                    DpReceiptLotReleaseDate.SelectedDate = null;

                SetComboText(CmbReceiptStatus, "Quarantine");
                DgReceipts.SelectedItem = null;
                pnlSidePanel.Visibility = Visibility.Collapsed;

                ShowToast("New receipt form ready", "📦");
                SetStatus("New media receipt ready");
            }
            catch (Exception ex)
            {
                ShowError("Error starting new receipt", ex);
            }
        }

        private void BtnSaveReceipt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaEntryPermission();
                if (!ValidateReceipt())
                    return;

                int mediaId = FindMediaMasterId(TxtReceiptMediaCode.Text);

                string lotNumber = TxtReceiptSupplierLot.Text.Trim();
                if (string.IsNullOrWhiteSpace(lotNumber))
                    lotNumber = _repository.GenerateNumber("MediaLot", "ML");

                int existingLotId = mediaId > 0 ? _repository.FindExistingMediaLotId(mediaId, lotNumber) : 0;
                if (_selectedLotId == 0 && existingLotId > 0)
                {
                    _selectedLotId = existingLotId;
                    if (MessageBox.Show(
                        "This media lot already exists. Do you want to update the existing record instead of creating a duplicate?",
                        "Duplicate Media Lot",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        SetStatus("Save cancelled - duplicate lot");
                        return;
                    }
                }

                if (_selectedLotId > 0 && existingLotId > 0 && existingLotId != _selectedLotId)
                {
                    MessageBox.Show(
                        "Another media record already has the same Media Code and Manufacturer Lot No. Duplicate lots are not allowed.",
                        "Duplicate Media Lot",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (_selectedLotId > 0 && !CanEditMediaReceipt(_selectedLotId))
                {
                    MessageBox.Show(
                        "Only media lots in Quarantine can be edited. Released or rejected lots are locked to protect traceability.",
                        "Culture Media Workflow",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                bool isNew = _selectedLotId == 0;
                string action = isNew ? "Receive Culture Media Lot" : "Amend Culture Media Receipt";
                string recordNumber = string.IsNullOrWhiteSpace(lotNumber) ? "Culture Media Lot" : lotNumber;
                if (!ConfirmCultureMediaSignature(action, recordNumber, _selectedLotId, out string signedBy, out string signatureReason))
                    return;

                decimal requestedInitialStockG = ParseGramQuantity(TxtReceiptQuantity.Text);
                int savedLotId = _selectedLotId;

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    mediaId = EnsureMediaMasterInTransaction(conn, tx);
                    EnsureNoDuplicateMediaLotInTransaction(conn, tx, mediaId, lotNumber, isNew ? 0 : savedLotId);

                    if (isNew)
                    {
                        const string insertSql = @"
DECLARE @InsertedLot TABLE(MediaLotID INT NOT NULL);

INSERT INTO dbo.CultureMediaLots
(MediaID, LotNumber, Supplier, ManufacturerLot, ReceivedDate, ExpiryDate, QuantityReceived, InitialStockG, CurrentStockG, StockStatus, COANumber, ReceivedBy, ReceiptStatus, Remarks)
OUTPUT INSERTED.MediaLotID INTO @InsertedLot(MediaLotID)
VALUES
(@MediaID, @LotNumber, @Supplier, @ManufacturerLot, @ReceivedDate, @ExpiryDate, @QuantityReceived, @InitialStockG, @InitialStockG, 'Quarantine', @COANumber, @ReceivedBy, 'Quarantine', @Remarks);

SELECT TOP(1) MediaLotID
FROM @InsertedLot;";

                        object? id = ExecuteScalarInTransaction(conn, tx, insertSql, BuildReceiptParameters(mediaId, lotNumber));
                        savedLotId = Convert.ToInt32(id ?? throw new InvalidOperationException("The media receipt ID was not returned by the database."), CultureInfo.InvariantCulture);
                        _pendingSignatureEntityId = savedLotId;
                        _pendingSignatureRecordNumber = recordNumber;

                        SaveReceiptSopFieldsInTransaction(conn, tx, savedLotId);
                        InsertCultureMediaStockTransactionInTransaction(
                            conn, tx, savedLotId, null, "Receipt", requestedInitialStockG,
                            0m, requestedInitialStockG, lotNumber,
                            "Opening balance from approved media receipt", signedBy);
                    }
                    else
                    {
                        decimal oldInitialStockG;
                        decimal oldCurrentStockG;
                        int issueTransactionCount;
                        using (var lockCommand = new SqlCommand(@"
SELECT
    ISNULL(InitialStockG, 0),
    ISNULL(CurrentStockG, 0),
    (SELECT COUNT(1)
     FROM dbo.CultureMediaStockTransactions st
     WHERE st.MediaLotID = l.MediaLotID
       AND st.TransactionType NOT IN ('Receipt', 'ReceiptCorrection', 'HistoricalOpeningBalance'))
FROM dbo.CultureMediaLots l WITH (UPDLOCK, HOLDLOCK)
WHERE l.MediaLotID = @MediaLotID
  AND l.ReceiptStatus IN ('Quarantine', 'Pending');", conn, tx))
                        {
                            lockCommand.Parameters.Add("@MediaLotID", SqlDbType.Int).Value = savedLotId;
                            using SqlDataReader reader = lockCommand.ExecuteReader();
                            if (!reader.Read())
                                throw new InvalidOperationException("Only a media lot in Quarantine can be amended.");
                            oldInitialStockG = reader.GetDecimal(0);
                            oldCurrentStockG = reader.GetDecimal(1);
                            issueTransactionCount = reader.GetInt32(2);
                        }

                        decimal stockDifferenceG = requestedInitialStockG - oldInitialStockG;
                        if (stockDifferenceG != 0m && issueTransactionCount > 0)
                        {
                            throw new InvalidOperationException(
                                "Quantity Received cannot be changed after the lot has been issued to a preparation. Use the controlled stock-reconciliation process instead.");
                        }

                        decimal newCurrentStockG = oldCurrentStockG + stockDifferenceG;
                        if (newCurrentStockG < 0m)
                            throw new InvalidOperationException("The corrected stock balance cannot be negative.");

                        const string updateSql = @"
UPDATE dbo.CultureMediaLots
SET MediaID = @MediaID,
    LotNumber = @LotNumber,
    Supplier = @Supplier,
    ManufacturerLot = @ManufacturerLot,
    ReceivedDate = @ReceivedDate,
    ExpiryDate = @ExpiryDate,
    QuantityReceived = @QuantityReceived,
    InitialStockG = @InitialStockG,
    CurrentStockG = @CurrentStockG,
    StockStatus = CASE WHEN @CurrentStockG <= 0 THEN 'Depleted' ELSE 'Quarantine' END,
    COANumber = @COANumber,
    ReceivedBy = @ReceivedBy,
    ReceiptStatus = 'Quarantine',
    Remarks = @Remarks
WHERE MediaLotID = @MediaLotID
  AND ReceiptStatus IN ('Quarantine', 'Pending');";

                        SqlParameter[] parameters = BuildReceiptParameters(mediaId, lotNumber, savedLotId)
                            .Concat(new[]
                            {
                                new SqlParameter("@CurrentStockG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = newCurrentStockG }
                            })
                            .ToArray();
                        int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(updateSql, parameters, conn, tx);
                        if (affected != 1)
                            throw new DBConcurrencyException("The media receipt changed before it could be saved.");

                        SaveReceiptSopFieldsInTransaction(conn, tx, savedLotId);
                        if (stockDifferenceG != 0m)
                        {
                            InsertCultureMediaStockTransactionInTransaction(
                                conn, tx, savedLotId, null, "ReceiptCorrection", stockDifferenceG,
                                oldCurrentStockG, newCurrentStockG, lotNumber,
                                signatureReason, signedBy);
                        }
                    }

                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(
                        conn, tx, "CultureMediaLots", savedLotId,
                        isNew ? "Media Receipt Created" : "Media Receipt Amended",
                        isNew ? string.Empty : "Quarantine", "Quarantine",
                        signatureReason, signedBy, lotNumber);
                });

                ClearPendingCultureMediaSignature();
                _selectedLotId = savedLotId;

                _ = LoadAllDataAsync();
                ShowToast(isNew ? "New media receipt saved successfully" : "Media receipt amended successfully", "✅");
                SetStatus(isNew ? "New media receipt saved and opening stock recorded" : "Media receipt amended with controlled stock traceability");
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
            {
                ClearPendingCultureMediaSignature();
                MessageBox.Show(
                    "This media lot already exists. Select the existing row and click Edit Selected, then update it instead of creating a duplicate.",
                    "Duplicate Media Lot",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                SetStatus("Duplicate media lot");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error saving media receipt", ex);
            }
        }

        private void BtnReconcileStock_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaReleasePermission();
                if (_selectedLotId <= 0)
                {
                    MessageBox.Show("Select a media receipt first.", "Stock Reconciliation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DataTable lot = _repository.Load(CultureMediaQuery.LoadMediaLotForStockReconciliation,
                    new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = _selectedLotId });
                if (lot.Rows.Count != 1)
                    throw new InvalidOperationException("The selected media lot was not found.");

                DataRow lotRow = lot.Rows[0];
                decimal? systemBalance = RowNullableDecimal(lotRow, "CurrentStockG");
                if (!TryShowStockReconciliationDialog(systemBalance, out decimal physicalCountG, out string reconciliationReason))
                    return;

                string lotNumber = RowString(lotRow, "LotNumber");
                if (!ConfirmCultureMediaSignature("Reconcile Culture Media Stock", lotNumber, _selectedLotId, out string reconciledBy, out string signatureReason))
                    return;

                int lotId = _selectedLotId;
                decimal? capturedSystemBalance = null;
                decimal differenceG = 0m;
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    DatabaseHelper.EnsureQaApprovalAuthorizationInTransaction(
                        conn, tx, reconciledBy, "reconcile culture media stock");

                    string receiptStatus;
                    using (var command = new SqlCommand(@"
SELECT CurrentStockG, ReceiptStatus
FROM dbo.CultureMediaLots WITH (UPDLOCK, HOLDLOCK)
WHERE MediaLotID = @MediaLotID;", conn, tx))
                    {
                        command.Parameters.Add("@MediaLotID", SqlDbType.Int).Value = lotId;
                        using SqlDataReader reader = command.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("The media lot was not found during reconciliation.");
                        capturedSystemBalance = reader.IsDBNull(0) ? null : reader.GetDecimal(0);
                        receiptStatus = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    }

                    decimal balanceBefore = capturedSystemBalance ?? 0m;
                    differenceG = physicalCountG - balanceBefore;
                    int updated = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.CultureMediaLots
SET CurrentStockG = @PhysicalCountG,
    StockStatus = CASE
        WHEN UPPER(LTRIM(RTRIM(ISNULL(ReceiptStatus, N'')))) = N'REJECTED' THEN N'Rejected'
        WHEN @PhysicalCountG <= 0 THEN N'Depleted'
        WHEN UPPER(LTRIM(RTRIM(ISNULL(ReceiptStatus, N'')))) IN (N'RELEASED', N'ACCEPTED') THEN N'Available'
        ELSE N'Quarantine'
    END
WHERE MediaLotID = @MediaLotID;",
                        new[]
                        {
                            new SqlParameter("@PhysicalCountG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = physicalCountG },
                            new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = lotId }
                        }, conn, tx);
                    if (updated != 1)
                        throw new DBConcurrencyException("The stock balance changed before reconciliation could be completed.");

                    DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT INTO dbo.CultureMediaStockReconciliations
(MediaLotID, SystemBalanceG, PhysicalCountG, DifferenceG, Reason, ReconciledBy, ReconciledAt)
VALUES
(@MediaLotID, @SystemBalanceG, @PhysicalCountG, @DifferenceG, @Reason, @ReconciledBy, SYSUTCDATETIME());",
                        new[]
                        {
                            new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = lotId },
                            new SqlParameter("@SystemBalanceG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = capturedSystemBalance.HasValue ? capturedSystemBalance.Value : DBNull.Value },
                            new SqlParameter("@PhysicalCountG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = physicalCountG },
                            new SqlParameter("@DifferenceG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = differenceG },
                            new SqlParameter("@Reason", SqlDbType.NVarChar, 500) { Value = reconciliationReason },
                            new SqlParameter("@ReconciledBy", SqlDbType.NVarChar, 100) { Value = reconciledBy }
                        }, conn, tx);

                    if (differenceG != 0m)
                    {
                        InsertCultureMediaStockTransactionInTransaction(
                            conn, tx, lotId, null, "PhysicalReconciliation", differenceG,
                            balanceBefore, physicalCountG, lotNumber, reconciliationReason, reconciledBy);
                    }
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(
                        conn, tx, "CultureMediaLots", lotId, "Physical Stock Reconciled",
                        capturedSystemBalance?.ToString("0.###", CultureInfo.InvariantCulture) ?? "Unknown",
                        physicalCountG.ToString("0.###", CultureInfo.InvariantCulture),
                        reconciliationReason + " | " + signatureReason, reconciledBy, lotNumber);
                });

                ClearPendingCultureMediaSignature();
                _ = LoadAllDataAsync();
                ShowToast($"Stock reconciled. Difference: {differenceG:0.###} g", "⚖️");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error reconciling culture media stock", ex);
            }
        }

        private bool TryShowStockReconciliationDialog(decimal? systemBalanceG, out decimal physicalCountG, out string reason)
        {
            decimal selectedPhysicalCountG = 0m;
            string selectedReason = string.Empty;
            var countBox = new TextBox { Height = 34, Margin = new Thickness(0, 4, 0, 10), Text = systemBalanceG?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty };
            var reasonBox = new TextBox { Height = 70, Margin = new Thickness(0, 4, 0, 10), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            var okButton = new Button { Content = "Continue to Signature", Width = 170, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "Cancel", Width = 90, Height = 34, IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "System Balance (g)", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = systemBalanceG?.ToString("0.###", CultureInfo.InvariantCulture) ?? "Unknown - reconciliation required", Margin = new Thickness(0, 4, 0, 10) });
            panel.Children.Add(new TextBlock { Text = "Physical Count (g) *", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(countBox);
            panel.Children.Add(new TextBlock { Text = "Reason / Count Reference *", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(reasonBox);
            panel.Children.Add(buttons);
            var dialog = new Window
            {
                Title = "Culture Media Stock Reconciliation",
                Owner = this,
                Width = 520,
                Height = 350,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = panel
            };
            okButton.Click += (_, _) =>
            {
                if (!decimal.TryParse(countBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) || parsed < 0m)
                {
                    MessageBox.Show(dialog, "Enter a non-negative physical count in grams.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(reasonBox.Text))
                {
                    MessageBox.Show(dialog, "A reconciliation reason or physical-count reference is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                selectedPhysicalCountG = parsed;
                selectedReason = reasonBox.Text.Trim();
                dialog.DialogResult = true;
            };
            bool accepted = dialog.ShowDialog() == true;
            physicalCountG = accepted ? selectedPhysicalCountG : 0m;
            reason = accepted ? selectedReason : string.Empty;
            return accepted;
        }

        private bool ValidateReceipt()
        {
            if (string.IsNullOrWhiteSpace(TxtReceiptMediaCode.Text))
            {
                MessageBox.Show("Media Code is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(TxtReceiptMediaName.Text))
            {
                MessageBox.Show("Media Name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(TxtReceiptSupplier.Text))
            {
                MessageBox.Show("Supplier / Manufacturer is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(TxtReceiptSupplierLot.Text))
            {
                MessageBox.Show("Manufacturer Lot No. is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(TxtReceiptCOA.Text))
            {
                MessageBox.Show("Supplier Certificate of Analysis No. is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(TxtReceiptSerialNo.Text) ||
                string.IsNullOrWhiteSpace(TxtReceiptMpmNo.Text) ||
                string.IsNullOrWhiteSpace(TxtReceiptPackNo.Text) ||
                string.IsNullOrWhiteSpace(TxtReceiptTotalPacks.Text) ||
                string.IsNullOrWhiteSpace(TxtReceiptPackSize.Text))
            {
                MessageBox.Show(
                    "SOP receipt traceability is incomplete. Serial No., MPM No., Pack No., Total Packs, and Pack Size are required.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
            if (!TryParseGramQuantity(TxtReceiptQuantity.Text, out decimal receivedQuantityG))
            {
                MessageBox.Show("Quantity Received must be a positive number in grams.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            TxtReceiptQuantity.Text = receivedQuantityG.ToString("0.###", CultureInfo.InvariantCulture);
            TxtReceiptInitialBalance.Text = TxtReceiptQuantity.Text;

            if (!DpReceiptDate.SelectedDate.HasValue)
            {
                MessageBox.Show("Received Date is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!DpReceiptExpiry.SelectedDate.HasValue)
            {
                MessageBox.Show("Manufacturer Expiry Date is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (DpReceiptExpiry.SelectedDate.Value <= DpReceiptDate.SelectedDate.Value)
            {
                MessageBox.Show("Expiry Date must be after Received Date.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private int FindMediaMasterId(string mediaCode)
        {
            if (string.IsNullOrWhiteSpace(mediaCode))
                return 0;

            object? value = _repository.ReadScalar(CultureMediaScalar.FindMediaMasterId,
                new SqlParameter("@MediaCode", SqlDbType.NVarChar, 50) { Value = mediaCode.Trim() });
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private int EnsureMediaMasterInTransaction(SqlConnection conn, SqlTransaction tx)
        {
            const string sql = @"
MERGE dbo.CultureMedia WITH (HOLDLOCK) AS target
USING
(
    SELECT
        @MediaCode AS MediaCode,
        @MediaName AS MediaName,
        @MediaType AS MediaType,
        @Manufacturer AS Manufacturer,
        @StorageCondition AS StorageCondition,
        @UserName AS UserName
) AS source
ON UPPER(LTRIM(RTRIM(target.MediaCode))) = UPPER(LTRIM(RTRIM(source.MediaCode)))
WHEN MATCHED THEN
    UPDATE SET
        MediaName = source.MediaName,
        MediaType = source.MediaType,
        Manufacturer = source.Manufacturer,
        StorageCondition = source.StorageCondition,
        IsActive = 1,
        UpdatedBy = source.UserName,
        UpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT
    (MediaCode, MediaName, MediaType, Manufacturer, StorageCondition, DefaultExpiryDays, PreparationInstruction, IsActive, CreatedBy)
    VALUES
    (source.MediaCode, source.MediaName, source.MediaType, source.Manufacturer, source.StorageCondition, 14, NULL, 1, source.UserName)
OUTPUT inserted.MediaID;";

            object? value = ExecuteScalarInTransaction(
                conn,
                tx,
                sql,
                new SqlParameter("@MediaCode", SqlDbType.NVarChar, 50) { Value = TxtReceiptMediaCode.Text.Trim() },
                new SqlParameter("@MediaName", SqlDbType.NVarChar, 200) { Value = TxtReceiptMediaName.Text.Trim() },
                new SqlParameter("@MediaType", SqlDbType.NVarChar, 80) { Value = DbValue(TxtReceiptMediaType.Text) },
                new SqlParameter("@Manufacturer", SqlDbType.NVarChar, 150) { Value = DbValue(TxtReceiptSupplier.Text) },
                new SqlParameter("@StorageCondition", SqlDbType.NVarChar, 150) { Value = DbValue(TxtReceiptStorageCondition.Text) },
                new SqlParameter("@UserName", SqlDbType.NVarChar, 100) { Value = _currentUser });

            return Convert.ToInt32(value ?? throw new InvalidOperationException("The culture-media master ID was not returned."), CultureInfo.InvariantCulture);
        }

        private static void EnsureNoDuplicateMediaLotInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int mediaId,
            string lotNumber,
            int excludedMediaLotId)
        {
            using var command = new SqlCommand(@"
SELECT TOP (1) MediaLotID
FROM dbo.CultureMediaLots WITH (UPDLOCK, HOLDLOCK)
WHERE MediaID = @MediaID
  AND UPPER(LTRIM(RTRIM(LotNumber))) = UPPER(LTRIM(RTRIM(@LotNumber)))
  AND MediaLotID <> @ExcludedMediaLotID;", conn, tx);
            command.Parameters.Add("@MediaID", SqlDbType.Int).Value = mediaId;
            command.Parameters.Add("@LotNumber", SqlDbType.NVarChar, 100).Value = lotNumber;
            command.Parameters.Add("@ExcludedMediaLotID", SqlDbType.Int).Value = excludedMediaLotId;
            object? duplicate = command.ExecuteScalar();
            if (duplicate != null && duplicate != DBNull.Value)
                throw new InvalidOperationException("Another media receipt already uses the same Media Code and Manufacturer Lot No.");
        }

        private SqlParameter[] BuildReceiptParameters(int mediaId, string lotNumber, int lotId = 0)
        {
            return new[]
            {
                new SqlParameter("@MediaID", SqlDbType.Int) { Value = mediaId },
                new SqlParameter("@LotNumber", SqlDbType.NVarChar, 100) { Value = lotNumber },
                new SqlParameter("@Supplier", SqlDbType.NVarChar, 150) { Value = DbValue(TxtReceiptSupplier.Text) },
                new SqlParameter("@ManufacturerLot", SqlDbType.NVarChar, 100) { Value = lotNumber },
                new SqlParameter("@ReceivedDate", SqlDbType.Date) { Value = DbDate(DpReceiptDate.SelectedDate) },
                new SqlParameter("@ExpiryDate", SqlDbType.Date) { Value = DbDate(DpReceiptExpiry.SelectedDate) },
                new SqlParameter("@QuantityReceived", SqlDbType.NVarChar, 80) { Value = DbValue(TxtReceiptQuantity.Text) },
                new SqlParameter("@InitialStockG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = ParseGramQuantity(TxtReceiptQuantity.Text) },
                new SqlParameter("@COANumber", SqlDbType.NVarChar, 100) { Value = DbValue(TxtReceiptCOA.Text) },
                new SqlParameter("@ReceivedBy", SqlDbType.NVarChar, 100) { Value = _currentUser },
                new SqlParameter("@ReceiptStatus", SqlDbType.NVarChar, 30) { Value = "Quarantine" },
                new SqlParameter("@Remarks", SqlDbType.NVarChar) { Value = DBNull.Value },
                new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = lotId }
            };
        }

        private bool CanEditMediaReceipt(int mediaLotId)
        {
            if (mediaLotId <= 0)
                return false;

            object? statusValue = _repository.ReadScalar(CultureMediaScalar.MediaLotReceiptStatus,
                new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId });

            string status = NormalizeStatus(statusValue?.ToString() ?? string.Empty);
            return status.Equals("Quarantine", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Pending", StringComparison.OrdinalIgnoreCase);
        }

        private void BtnEditLot_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedLotId <= 0)
            {
                ShowToast("Please select a lot first", "⚠️");
                return;
            }

            // Load lot data into receipt form
            var row = RowFromGrid(DgReceipts);
            if (row != null)
            {
                TxtReceiptMediaCode.Text = RowString(row, "MediaCode");
                TxtReceiptMediaName.Text = RowString(row, "MediaName");
                TxtReceiptMediaType.Text = RowString(row, "MediaType");
                TxtReceiptStorageCondition.Text = RowString(row, "StorageCondition");
                TxtReceiptSupplier.Text = RowString(row, "Supplier");
                TxtReceiptSupplierLot.Text = RowString(row, "LotNumber");
                TxtReceiptCOA.Text = RowString(row, "COANumber");
                TxtReceiptQuantity.Text = RowString(row, "QuantityReceived");
                TxtReceiptInitialBalance.Text = RowString(row, "CurrentStockG");
                DpReceiptDate.SelectedDate = RowDate(row, "ReceivedDate");
                DpReceiptExpiry.SelectedDate = RowDate(row, "ExpiryDate");
                SetComboText(CmbReceiptStatus, RowString(row, "ReceiptStatus"));
                LoadReceiptSopFields(_selectedLotId);

                ShowToast($"Editing lot {_selectedLotId}", "✏️");
            }
        }

        private void BtnTestLot_Click(object sender, RoutedEventArgs e)
        {
            if (CmbReleaseMediaLot != null && CmbReleaseMediaLot.SelectedValue != null &&
                int.TryParse(CmbReleaseMediaLot.SelectedValue.ToString(), out int releaseLotId) && releaseLotId > 0)
            {
                _selectedLotId = releaseLotId;
            }

            if (_selectedLotId <= 0)
            {
                ShowToast("Select Media Lot to Release first", "⚠️");
                return;
            }

            DataRow? row = GetReleaseLotRow(_selectedLotId) ?? RowFromGrid(DgReceipts);
            if (row != null)
            {
                TxtRelPrepNo.Text = "Lot Receipt: " + RowString(row, "LotNumber");
                TxtRelMediaName.Text = RowString(row, "MediaCode") + " - " + RowString(row, "MediaName");
                TxtRelLotNo.Text = RowString(row, "LotNumber");
                TxtRelPH.Text = RowString(row, "COANumber");
                TxtRelStatus.Text = RowString(row, "ReceiptStatus");
                TxtReleaseReportNo.Text = "Generated on Start";
                _selectedReleaseReportId = 0;
                _currentOrganisms.Clear();
                UpdateReleaseNextAction();

                ShowToast($"Loaded lot {RowString(row, "LotNumber")} for release", "🧪");
            }
        }

        private void CmbReleaseMediaLot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            if (CmbReleaseMediaLot == null || CmbReleaseMediaLot.SelectedValue == null)
                return;

            if (!int.TryParse(CmbReleaseMediaLot.SelectedValue.ToString(), out int mediaLotId) || mediaLotId <= 0)
                return;

            _selectedLotId = mediaLotId;
            BtnTestLot_Click(sender, e);
        }

        private DataRow? GetReleaseLotRow(int mediaLotId)
        {
            if (CmbReleaseMediaLot?.ItemsSource is DataView view)
            {
                foreach (DataRowView rowView in view)
                {
                    DataRow row = rowView.Row;
                    if (RowInt(row, "MediaLotID") == mediaLotId)
                        return row;
                }
            }

            return null;
        }

        private void BtnReleaseLot_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Manual media lot release is blocked. Save a PASS Growth Promotion / Lot Qualification report to release this lot. This protects the workflow: Receipt -> Quarantine -> GPT/Review -> Released.",
                "Culture Media GMP Workflow",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BtnRejectLot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaReleasePermission();

                if (_selectedLotId <= 0)
                {
                ShowToast("Please select a lot first", "⚠️");
                return;
            }

            if (MessageBox.Show("Reject this media lot?", "Confirm Reject",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                if (!ConfirmCultureMediaSignature("Reject Media Lot", "Media Lot " + _selectedLotId, _selectedLotId, out string signedBy, out string reason))
                    return;

                const string sql = @"
UPDATE dbo.CultureMediaLots
SET ReceiptStatus = 'Rejected',
    StockStatus = 'Rejected',
    Remarks = CONCAT(ISNULL(Remarks, ''), CHAR(13) + CHAR(10), 'Rejected manually on ', FORMAT(GETDATE(), 'yyyy-MM-dd HH:mm'), ' by ', @User)
WHERE MediaLotID = @MediaLotID
  AND ReceiptStatus IN ('Quarantine', 'Pending');";

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    DatabaseHelper.EnsureQaApprovalAuthorizationInTransaction(
                        conn, tx, signedBy, "reject culture media lot");

                    int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(sql,
                        new[]
                        {
                            new SqlParameter("@MediaLotID", _selectedLotId),
                            new SqlParameter("@User", signedBy)
                        }, conn, tx);
                    if (affected != 1)
                        throw new InvalidOperationException("The selected media lot was not found or changed.");
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(conn, tx, "CultureMediaLots", _selectedLotId,
                        "Media Lot Rejected", string.Empty, "Rejected", reason, signedBy);
                });
                ClearPendingCultureMediaSignature();

                _ = LoadAllDataAsync();
                ShowToast("Media lot rejected", "❌");
            }
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error rejecting media lot", ex);
            }
        }

        private void BtnConfirmQualificationTiming_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaReleasePermission();
                if (!DatabaseHelper.CanQaApproveResults(Login.CurrentUser ?? string.Empty) && !IsCultureMediaAdministrator())
                    throw new InvalidOperationException("QA role and approval permission are required to confirm Culture Media timing controls.");

                DataTable requirements = _repository.Load(CultureMediaQuery.LoadQualificationTimingRequirements);

                if (requirements.Rows.Count == 0)
                    throw new InvalidOperationException("No active QA-approved Culture Media qualification requirements are available to confirm.");

                foreach (DataRow row in requirements.Rows)
                {
                    decimal? minimum = RowNullableDecimal(row, "MinimumIncubationHours");
                    if (!minimum.HasValue || minimum.Value < 0m)
                    {
                        throw new InvalidOperationException(
                            "Every active approved Culture Media requirement must define a non-negative Minimum Incubation Hours value before QA timing confirmation.");
                    }
                }

                string summary = string.Join(Environment.NewLine, requirements.AsEnumerable().Take(12).Select(row =>
                    RowString(row, "MediaTypePattern") + " | " + RowString(row, "TestName") + " = " +
                    RowNullableDecimal(row, "MinimumIncubationHours")?.ToString("0.##", CultureInfo.InvariantCulture) + " h"));
                if (requirements.Rows.Count > 12)
                    summary += Environment.NewLine + "... " + (requirements.Rows.Count - 12).ToString(CultureInfo.InvariantCulture) + " additional requirement(s).";

                MessageBoxResult proceed = MessageBox.Show(
                    "Confirm the following currently Approved Culture Media timing controls?\n\n" + summary +
                    "\n\nThis action records an electronic QA confirmation. Any later change to Minimum Incubation Hours invalidates the confirmation until QA confirms again.",
                    "QA Confirm Qualification Timing", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (proceed != MessageBoxResult.Yes)
                    return;

                var signature = new ElectronicSignature(
                    "Culture Media Qualification Timing Controls",
                    _currentUser,
                    "Confirm Culture Media Qualification Timing Controls",
                    true)
                {
                    Owner = this
                };
                if (signature.ShowDialog() != true || !signature.IsConfirmed)
                    return;

                string signedBy = string.IsNullOrWhiteSpace(signature.SignedBy) ? _currentUser : signature.SignedBy;
                string reason = string.IsNullOrWhiteSpace(signature.Reason)
                    ? "QA confirmation of controlled Culture Media qualification timing values"
                    : signature.Reason;
                string meaning = string.IsNullOrWhiteSpace(signature.Meaning) ? "I confirm this action" : signature.Meaning;

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    DatabaseHelper.EnsureQaApprovalAuthorizationInTransaction(
                        conn, tx, signedBy, "confirm Culture Media qualification timing controls");

                    int invalidCount;
                    int activeCount;
                    using (SqlCommand validation = new SqlCommand(@"
SELECT
    COUNT(1) AS ActiveCount,
    SUM(CASE WHEN MinimumIncubationHours IS NULL OR MinimumIncubationHours < 0 THEN 1 ELSE 0 END) AS InvalidCount
FROM dbo.CultureMediaQualificationRequirements WITH(UPDLOCK,HOLDLOCK)
WHERE IsActive=1
  AND ApprovalStatus=N'Approved'
  AND ApprovedBy IS NOT NULL
  AND ApprovedAt IS NOT NULL;", conn, tx))
                    {
                        validation.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        using SqlDataReader reader = validation.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("Culture Media timing requirements could not be locked for QA confirmation.");
                        activeCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                        invalidCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    }

                    if (activeCount <= 0)
                        throw new InvalidOperationException("No active Approved Culture Media qualification requirements remain. Reload and try again.");
                    if (invalidCount > 0)
                        throw new InvalidOperationException("One or more active Culture Media requirements have an invalid Minimum Incubation Hours value.");

                    int updated = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.CultureMediaQualificationRequirements
SET TimingConfirmedMinimumIncubationHours=MinimumIncubationHours,
    TimingConfirmedBy=@SignedBy,
    TimingConfirmedAt=SYSDATETIME()
WHERE IsActive=1
  AND ApprovalStatus=N'Approved'
  AND ApprovedBy IS NOT NULL
  AND ApprovedAt IS NOT NULL
  AND MinimumIncubationHours IS NOT NULL
  AND MinimumIncubationHours >= 0;",
                        new[] { new SqlParameter("@SignedBy", SqlDbType.NVarChar, 100) { Value = signedBy } }, conn, tx);
                    if (updated != activeCount)
                        throw new DBConcurrencyException("Culture Media timing requirements changed during QA confirmation. Reload and try again.");

                    DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT INTO dbo.CultureMediaSignatures
(EntityType, EntityID, RecordNumber, ActionType, ActionReason, SignedBy, MeaningOfSignature)
SELECT N'CultureMediaRequirement', RequirementID,
       N'Requirement ' + CONVERT(NVARCHAR(20),RequirementID),
       N'Confirm Qualification Timing', @Reason, @SignedBy, @Meaning
FROM dbo.CultureMediaQualificationRequirements
WHERE IsActive=1
  AND ApprovalStatus=N'Approved'
  AND TimingConfirmedBy=@SignedBy
  AND TimingConfirmedAt IS NOT NULL
  AND TimingConfirmedMinimumIncubationHours=MinimumIncubationHours;",
                        new[]
                        {
                            new SqlParameter("@Reason", SqlDbType.NVarChar, -1) { Value = reason },
                            new SqlParameter("@SignedBy", SqlDbType.NVarChar, 100) { Value = signedBy },
                            new SqlParameter("@Meaning", SqlDbType.NVarChar, 255) { Value = meaning }
                        }, conn, tx);

                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn, tx, "CultureMediaQualificationRequirements", 0,
                        "QA Timing Controls Confirmed", "Timing confirmation pending",
                        activeCount.ToString(CultureInfo.InvariantCulture) + " active requirement(s) confirmed",
                        reason, signedBy, "MinimumIncubationHours", null,
                        "Active Culture Media Requirement Set", "Culture Media");
                });

                ShowToast("Culture Media timing controls QA-confirmed", "✅");
                SetStatus("Qualification timing controls confirmed");
            }
            catch (Exception ex)
            {
                ShowError("Error confirming Culture Media timing controls", ex);
            }
        }

        private void BtnStartQualification_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaEntryPermission();
                if (!DatabaseHelper.CanEditResults(Login.CurrentUser ?? string.Empty) && !IsCultureMediaAdministrator())
                    throw new InvalidOperationException("Starting a media-lot qualification requires result-entry permission.");
                if (_selectedLotId <= 0)
                    throw new InvalidOperationException("Select and load a stored media lot first.");

                DateTime databaseNow = DatabaseHelper.GetAuthoritativeDatabaseTime();
                if (DpReleaseDate != null)
                    DpReleaseDate.SelectedDate = databaseNow.Date;
                if (!ValidateMediaLotForQualification(_selectedLotId, databaseNow.Date))
                    return;

                if (HasActiveQualificationForLot(_selectedLotId))
                {
                    MessageBox.Show(
                        "An active qualification already exists for this media lot. Select that qualification from the report grid and continue its controlled workflow.",
                        "Duplicate Qualification Blocked",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                decimal minimumIncubationHours = 0m;
                string reportNo = _repository.GenerateNumber("MediaQualificationReport", "MQR");
                if (!ConfirmCultureMediaSignature("Start Media Lot Qualification / Incubation", reportNo, 0, out string startedBy, out string startReason))
                    return;

                int mediaLotId = _selectedLotId;
                int qualificationId = 0;
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn, tx, startedBy, "CanEnterResults", "start culture media qualification");
                    EnsureNoActiveQualificationForLotInTransaction(conn, tx, mediaLotId);

                    object? id = ExecuteScalarInTransaction(conn, tx, @"
INSERT INTO dbo.MediaQualifications
(
    QualificationNo, MediaLotID, MediaPreparationID, QualificationType, QualificationDate,
    PerformedBy, ReviewedBy, ReleasedBy, ReviewDate, ReleaseDate,
    QualificationStatus, OverallResult, Remarks,
    QualificationStartedAt, MinimumIncubationHoursSnapshot, IncubationCompletedAt
)
VALUES
(
    @QualificationNo, @MediaLotID, NULL, N'Media Lot Promotion Test / Release', CAST(SYSDATETIME() AS date),
    @PerformedBy, NULL, NULL, NULL, NULL,
    N'In Progress', N'Pending', NULL,
    SYSDATETIME(), @MinimumIncubationHours, NULL
);
SELECT CAST(SCOPE_IDENTITY() AS int);",
                        new SqlParameter("@QualificationNo", SqlDbType.NVarChar, 50) { Value = reportNo },
                        new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId },
                        CreateDecimalParameter("@MinimumIncubationHours", minimumIncubationHours));

                    qualificationId = Convert.ToInt32(id ?? throw new InvalidOperationException("The qualification report ID was not returned by the database."), CultureInfo.InvariantCulture);
                    minimumIncubationHours = CreateQualificationRequirementSnapshotsInTransaction(
                        conn, tx, qualificationId, mediaLotId);

                    int timingUpdated = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.MediaQualifications
SET MinimumIncubationHoursSnapshot=@MinimumIncubationHours
WHERE MediaQualificationID=@MediaQualificationID
  AND QualificationStatus=N'In Progress';",
                        new[]
                        {
                            CreateDecimalParameter("@MinimumIncubationHours", minimumIncubationHours),
                            new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId }
                        }, conn, tx);
                    if (timingUpdated != 1)
                        throw new DBConcurrencyException("The Culture Media qualification timing snapshot could not be frozen.");

                    _pendingSignatureEntityId = qualificationId;
                    _pendingSignatureRecordNumber = reportNo;
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(
                        conn, tx, "MediaQualifications", qualificationId,
                        "Media Lot Qualification Started", string.Empty, "In Progress",
                        startReason, startedBy, reportNo);
                });

                ClearPendingCultureMediaSignature();
                _selectedReleaseReportId = qualificationId;
                TxtReleaseReportNo.Text = reportNo;
                TxtReleasePerformedBy.Text = startedBy;
                TxtReleaseReviewedBy.Clear();
                SetComboText(CmbOverallResult, "Pending");
                TxtReleaseNextAction.Text =
                    "Qualification / incubation started. Complete results only after the controlled minimum elapsed time of " +
                    minimumIncubationHours.ToString("0.##", CultureInfo.InvariantCulture) + " hour(s).";
                _ = LoadAllDataAsync();
                ShowToast("Qualification incubation started", "✅");
                SetStatus("Qualification in progress");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error starting media lot qualification", ex);
            }
        }

        private void BtnSaveRelease_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaEntryPermission();
                if (!DatabaseHelper.CanEditResults(Login.CurrentUser ?? string.Empty) && !IsCultureMediaAdministrator())
                    throw new InvalidOperationException("Completing a media-lot qualification requires result-entry permission.");
                if (_selectedReleaseReportId <= 0)
                {
                    MessageBox.Show(
                        "Start a controlled qualification first, or select an In Progress qualification from the report grid.",
                        "Qualification Start Required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                DataTable workflow = LoadQualificationWorkflowRecord(_selectedReleaseReportId);
                if (workflow.Rows.Count != 1)
                    throw new InvalidOperationException("The selected qualification report was not found.");
                DataRow workflowRow = workflow.Rows[0];
                if (!RowString(workflowRow, "QualificationStatus").Equals("In Progress", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "Only a qualification in In Progress status can be completed and submitted for review.",
                        "Qualification Workflow",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                if (RowInt(workflowRow, "MediaLotID") != _selectedLotId)
                    throw new InvalidOperationException("The selected qualification does not belong to the loaded media lot.");

                if (!ValidateReleaseReport())
                    return;

                string reportNo = RowString(workflowRow, "QualificationNo");
                string originalPerformer = RowString(workflowRow, "PerformedBy");
                string overall = CalculateQualificationResult(_currentOrganisms);
                SetComboText(CmbOverallResult, overall);

                if (!ConfirmCultureMediaSignature("Complete Media Lot Qualification", reportNo, _selectedReleaseReportId, out string completedBy, out string completionReason))
                    return;

                if (!IsDevelopmentAdminOverride() &&
                    !string.Equals(originalPerformer.Trim(), completedBy.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    ClearPendingCultureMediaSignature();
                    MessageBox.Show(
                        "The user who started this qualification must complete and sign the result entry. Transfer requires a controlled documented workflow, not reassignment of the performer field.",
                        "Qualification Attribution",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                int qualificationId = _selectedReleaseReportId;
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn, tx, completedBy, "CanEnterResults", "complete culture media qualification");

                    EnsureQualificationIncubationElapsedInTransaction(
                        conn, tx, qualificationId, _selectedLotId, completedBy, completionReason, reportNo);

                    int updated = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.MediaQualifications
SET QualificationDate = CAST(SYSDATETIME() AS date),
    QualificationStatus = N'Pending Review',
    OverallResult = @OverallResult,
    Remarks = @Remarks,
    IncubationCompletedAt = SYSDATETIME()
WHERE MediaQualificationID = @MediaQualificationID
  AND MediaLotID = @MediaLotID
  AND QualificationStatus = N'In Progress';",
                        new[]
                        {
                            new SqlParameter("@OverallResult", SqlDbType.NVarChar, 30) { Value = overall },
                            new SqlParameter("@Remarks", SqlDbType.NVarChar, -1) { Value = DbValue(TxtReleaseRemarks.Text) },
                            new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId },
                            new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = _selectedLotId }
                        }, conn, tx);
                    if (updated != 1)
                        throw new DBConcurrencyException("The qualification status changed before result completion. Reload the record and try again.");

                    DatabaseHelper.ExecuteNonQueryWithTransaction(
                        "DELETE FROM dbo.MediaQualificationTests WHERE MediaQualificationID=@MediaQualificationID;",
                        new[] { new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId } }, conn, tx);
                    SaveReleaseSopFieldsInTransaction(conn, tx, qualificationId);
                    foreach (DataRow row in _currentOrganisms.Rows)
                        InsertReleaseOrganismInTransaction(conn, tx, qualificationId, row);

                    EnsureQualificationTestTimingEvidenceInTransaction(conn, tx, qualificationId, "Qualification Completion");
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(
                        conn, tx, "MediaQualifications", qualificationId,
                        "Media Lot Qualification Completed", "In Progress", "Pending Review",
                        completionReason, completedBy, reportNo);
                });

                ClearPendingCultureMediaSignature();
                _ = LoadAllDataAsync();
                UpdateReleaseNextAction();
                ShowToast("Qualification completed and sent for review", "✅");
                SetStatus("Qualification pending independent review");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error completing media lot qualification", ex);
            }
        }

        private void BtnReviewQualification_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaReviewPermission();
                if (_selectedReleaseReportId <= 0)
                {
                    MessageBox.Show("Select a saved qualification report first.", "Review", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DataTable report = LoadQualificationWorkflowRecord(_selectedReleaseReportId);
                if (report.Rows.Count != 1)
                    throw new InvalidOperationException("The selected qualification report was not found.");

                DataRow reportRow = report.Rows[0];
                string performedBy = RowString(reportRow, "PerformedBy");
                string status = RowString(reportRow, "QualificationStatus");
                if (!status.Equals("Pending Review", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Only a qualification in Pending Review status can be reviewed.", "Review Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DataTable tests = LoadQualificationTests(_selectedReleaseReportId);
                string calculatedResult = CalculateQualificationResult(tests);
                string storedConclusion = LoadSopField("MediaQualification", _selectedReleaseReportId, "1035-L-0005/A8", "Conclusion").Trim();
                if (!storedConclusion.Equals("Satisfactory", StringComparison.OrdinalIgnoreCase))
                    calculatedResult = "Fail";

                if (calculatedResult.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("The qualification cannot be reviewed while one or more tests are incomplete.", "Review Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (calculatedResult.Equals("Pass", StringComparison.OrdinalIgnoreCase) &&
                    !HasCompletePassingReleaseTests(tests, _selectedReleaseReportId))
                    throw new InvalidOperationException("The qualification does not meet its frozen approved test and recovery requirements.");

                if (!ConfirmCultureMediaSignature("Review Media Lot Qualification", RowString(reportRow, "QualificationNo"), _selectedReleaseReportId, out string reviewedBy, out string reason))
                    return;

                if (!IsDevelopmentAdminOverride() &&
                    string.Equals(performedBy.Trim(), reviewedBy.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    ClearPendingCultureMediaSignature();
                    MessageBox.Show("The performer cannot review the same qualification.", "Segregation of Duties", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string qualificationStatus = calculatedResult.Equals("Pass", StringComparison.OrdinalIgnoreCase) ? "Qualified" : "Failed";
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn,
                        tx,
                        reviewedBy,
                        "CanReviewResults",
                        "review culture media qualification");

                    string lockedPerformedBy;
                    string lockedStatus;
                    using (SqlCommand workflowCommand = new SqlCommand(@"
SELECT ISNULL(PerformedBy,N''), ISNULL(QualificationStatus,N'')
FROM dbo.MediaQualifications WITH (UPDLOCK, HOLDLOCK)
WHERE MediaQualificationID = @MediaQualificationID;", conn, tx))
                    {
                        workflowCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        workflowCommand.Parameters.Add("@MediaQualificationID", SqlDbType.Int).Value = _selectedReleaseReportId;
                        using SqlDataReader reader = workflowCommand.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("The selected qualification report no longer exists.");
                        lockedPerformedBy = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                        lockedStatus = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                    }

                    if (!lockedStatus.Equals("Pending Review", StringComparison.OrdinalIgnoreCase))
                        throw new DBConcurrencyException("The qualification is no longer Pending Review.");

                    EnsureQualificationTestTimingEvidenceInTransaction(conn, tx, _selectedReleaseReportId, "Independent Review");

                    if (!IsDevelopmentAdminOverrideForRole(signerRole) &&
                        string.Equals(lockedPerformedBy, reviewedBy.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The qualification performer cannot perform the independent review.");
                    }

                    int reviewedRows = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.MediaQualifications
SET ReviewedBy = @ReviewedBy,
    ReviewDate = SYSDATETIME(),
    QualificationStatus = @QualificationStatus,
    OverallResult = @OverallResult
WHERE MediaQualificationID = @MediaQualificationID
  AND QualificationStatus = 'Pending Review';",
                        new[]
                        {
                            new SqlParameter("@ReviewedBy", SqlDbType.NVarChar, 100) { Value = reviewedBy },
                            new SqlParameter("@QualificationStatus", SqlDbType.NVarChar, 30) { Value = qualificationStatus },
                            new SqlParameter("@OverallResult", SqlDbType.NVarChar, 30) { Value = calculatedResult },
                            new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = _selectedReleaseReportId }
                        }, conn, tx);
                    if (reviewedRows != 1)
                        throw new InvalidOperationException("The qualification status changed before review. Reload the record and try again.");
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(conn, tx, "MediaQualifications", _selectedReleaseReportId,
                        "Qualification Reviewed", "Pending Review", qualificationStatus, reason, reviewedBy,
                        RowString(reportRow, "QualificationNo"));
                });

                ClearPendingCultureMediaSignature();
                _ = LoadAllDataAsync();
                ShowToast("Qualification review completed", "✅");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error reviewing media lot qualification", ex);
            }
        }

        private void BtnFinalizeLotRelease_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaReleasePermission();
                if (_selectedReleaseReportId <= 0)
                {
                    MessageBox.Show("Select a reviewed qualification report first.", "Final Release", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                RequireCultureMediaReleasePermission();
                DataTable report = LoadQualificationWorkflowRecord(_selectedReleaseReportId);
                if (report.Rows.Count != 1)
                    throw new InvalidOperationException("The selected qualification report was not found.");

                DataRow reportRow = report.Rows[0];
                string storedConclusion = LoadSopField("MediaQualification", _selectedReleaseReportId, "1035-L-0005/A8", "Conclusion").Trim();
                if (!RowString(reportRow, "QualificationStatus").Equals("Qualified", StringComparison.OrdinalIgnoreCase) ||
                    !RowString(reportRow, "OverallResult").Equals("Pass", StringComparison.OrdinalIgnoreCase) ||
                    !storedConclusion.Equals("Satisfactory", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(RowString(reportRow, "ReviewedBy")))
                {
                    MessageBox.Show("Final release requires a completed independent review with a Qualified result.", "Final Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DataTable releaseTests = LoadQualificationTests(_selectedReleaseReportId);
                if (!CalculateQualificationResult(releaseTests).Equals("Pass", StringComparison.OrdinalIgnoreCase) ||
                    !HasCompletePassingReleaseTests(releaseTests, _selectedReleaseReportId))
                    throw new InvalidOperationException("Final release requires complete passing tests against the qualification's frozen requirements.");

                if (!ConfirmCultureMediaSignature("Final Release of Media Lot", RowString(reportRow, "QualificationNo"), _selectedReleaseReportId, out string releasedBy, out string reason))
                    return;

                string performedBy = RowString(reportRow, "PerformedBy");
                string reviewedBy = RowString(reportRow, "ReviewedBy");
                if (!IsDevelopmentAdminOverride() &&
                    (string.Equals(performedBy.Trim(), releasedBy.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(reviewedBy.Trim(), releasedBy.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    ClearPendingCultureMediaSignature();
                    MessageBox.Show("The final releaser must be different from both the performer and the reviewer.", "Segregation of Duties", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int mediaLotId = RowInt(reportRow, "MediaLotID");
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureQaApprovalAuthorizationInTransaction(
                        conn,
                        tx,
                        releasedBy,
                        "release culture media lot");

                    string lockedPerformedBy;
                    string lockedReviewedBy;
                    string lockedStatus;
                    string lockedOverallResult;
                    int lockedMediaLotId;
                    using (SqlCommand workflowCommand = new SqlCommand(@"
SELECT ISNULL(PerformedBy,N''),
       ISNULL(ReviewedBy,N''),
       ISNULL(QualificationStatus,N''),
       ISNULL(OverallResult,N''),
       ISNULL(MediaLotID,0)
FROM dbo.MediaQualifications WITH (UPDLOCK, HOLDLOCK)
WHERE MediaQualificationID = @MediaQualificationID;", conn, tx))
                    {
                        workflowCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        workflowCommand.Parameters.Add("@MediaQualificationID", SqlDbType.Int).Value = _selectedReleaseReportId;
                        using SqlDataReader reader = workflowCommand.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("The selected qualification report no longer exists.");
                        lockedPerformedBy = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                        lockedReviewedBy = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                        lockedStatus = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                        lockedOverallResult = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
                        lockedMediaLotId = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
                    }

                    if (!lockedStatus.Equals("Qualified", StringComparison.OrdinalIgnoreCase) ||
                        !lockedOverallResult.Equals("Pass", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(lockedReviewedBy) ||
                        lockedMediaLotId <= 0 ||
                        lockedMediaLotId != mediaLotId)
                    {
                        throw new DBConcurrencyException(
                            "The qualification review/release state changed before final release. Reload the record and try again.");
                    }

                    string lockedLotStatus;
                    DateTime? lockedLotExpiryDate;
                    DateTime lockedDatabaseNow;
                    using (SqlCommand lotGateCommand = new SqlCommand(@"
SELECT ISNULL(ReceiptStatus,N''), ExpiryDate, SYSDATETIME()
FROM dbo.CultureMediaLots WITH (UPDLOCK, HOLDLOCK)
WHERE MediaLotID = @MediaLotID;", conn, tx))
                    {
                        lotGateCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        lotGateCommand.Parameters.Add("@MediaLotID", SqlDbType.Int).Value = mediaLotId;
                        using SqlDataReader reader = lotGateCommand.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("The selected media lot no longer exists.");
                        lockedLotStatus = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                        lockedLotExpiryDate = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
                        lockedDatabaseNow = reader.GetDateTime(2);
                    }

                    if (!lockedLotStatus.Equals("Quarantine", StringComparison.OrdinalIgnoreCase) &&
                        !lockedLotStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DBConcurrencyException(
                            "The media lot status changed before final release. Reload the record and try again.");
                    }

                    if (!lockedLotExpiryDate.HasValue || lockedLotExpiryDate.Value.Date < lockedDatabaseNow.Date)
                    {
                        throw new InvalidOperationException(
                            "Final release blocked: the media lot has no valid manufacturer expiry date or is expired according to SQL Server time.");
                    }

                    EnsureQualificationTestTimingEvidenceInTransaction(conn, tx, _selectedReleaseReportId, "Final Release");

                    if (!IsDevelopmentAdminOverrideForRole(signerRole) &&
                        (string.Equals(lockedPerformedBy, releasedBy.Trim(), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(lockedReviewedBy, releasedBy.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(
                            "The final releaser must be independent of both the qualification performer and reviewer.");
                    }

                    int qualificationRows = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.MediaQualifications
SET ReleasedBy = @ReleasedBy,
    ReleaseDate = SYSDATETIME(),
    QualificationStatus = 'Released'
WHERE MediaQualificationID = @MediaQualificationID
  AND QualificationStatus = 'Qualified'
  AND OverallResult = 'Pass'
  AND EXISTS
  (
      SELECT 1
      FROM dbo.CultureMediaSopFields f
      WHERE f.EntityType = N'MediaQualification'
        AND f.EntityID = dbo.MediaQualifications.MediaQualificationID
        AND f.AnnexureCode = N'1035-L-0005/A8'
        AND f.FieldName = N'Conclusion'
        AND UPPER(LTRIM(RTRIM(ISNULL(f.FieldValue, N'')))) = N'SATISFACTORY'
  );",
                        new[]
                        {
                            new SqlParameter("@ReleasedBy", SqlDbType.NVarChar, 100) { Value = releasedBy },
                            new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = _selectedReleaseReportId }
                        }, conn, tx);
                    if (qualificationRows != 1)
                        throw new InvalidOperationException("The qualification status changed before final release. Reload the record and try again.");

                    int lotRows = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.CultureMediaLots
SET ReceiptStatus = 'Released',
    StockStatus = CASE
        WHEN CurrentStockG IS NULL THEN 'Requires Reconciliation'
        WHEN CurrentStockG > 0 THEN 'Available'
        ELSE 'Depleted'
    END
WHERE MediaLotID = @MediaLotID
  AND ReceiptStatus IN ('Quarantine', 'Pending')
  AND ExpiryDate IS NOT NULL
  AND ExpiryDate >= CAST(SYSDATETIME() AS date);",
                        new[] { new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId } }, conn, tx);
                    if (lotRows != 1)
                        throw new InvalidOperationException("The media lot is no longer eligible for final release. Reload the record and try again.");

                    object? databaseReleaseDateValue = ExecuteScalarInTransaction(conn, tx, @"
SELECT ReleaseDate
FROM dbo.MediaQualifications WITH(UPDLOCK,HOLDLOCK)
WHERE MediaQualificationID=@MediaQualificationID;",
                        new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = _selectedReleaseReportId });
                    if (databaseReleaseDateValue == null || databaseReleaseDateValue == DBNull.Value)
                        throw new InvalidOperationException("The authoritative database Release Date was not stored.");
                    DateTime databaseReleaseDate = Convert.ToDateTime(databaseReleaseDateValue, CultureInfo.InvariantCulture);
                    SaveSopFieldInTransaction(conn, tx, "MediaLot", mediaLotId, "1035-L-0005/A2", "DateOfReleaseOfLot", databaseReleaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(conn, tx, "MediaQualifications", _selectedReleaseReportId,
                        "Media Lot Finally Released", "Qualified", "Released", reason, releasedBy,
                        RowString(reportRow, "QualificationNo"));
                });
                ClearPendingCultureMediaSignature();
                _ = LoadAllDataAsync();
                ShowToast("Media lot released", "✅");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error completing final media lot release", ex);
            }
        }

        private bool HasActiveQualificationForLot(int mediaLotId)
        {
            object? value = _repository.ReadScalar(CultureMediaScalar.HasActiveQualificationForLot,
                new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId });
            return value != null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
        }

        private static void EnsureNoActiveQualificationForLotInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int mediaLotId)
        {
            using var command = new SqlCommand(@"
SELECT TOP (1) MediaQualificationID
FROM dbo.MediaQualifications WITH (UPDLOCK, HOLDLOCK)
WHERE MediaLotID = @MediaLotID
  AND QualificationType = 'Media Lot Promotion Test / Release'
  AND QualificationStatus IN ('In Progress', 'Pending Review', 'Qualified', 'Released');", conn, tx);
            command.Parameters.Add("@MediaLotID", SqlDbType.Int).Value = mediaLotId;
            object? existing = command.ExecuteScalar();
            if (existing != null && existing != DBNull.Value)
                throw new InvalidOperationException("An active qualification already exists for this media lot.");
        }

        private DataTable LoadQualificationWorkflowRecord(int qualificationId)
        {
            return _repository.Load(CultureMediaQuery.LoadQualificationWorkflowRecord,
                new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId });
        }

        private DataTable LoadQualificationTests(int qualificationId)
        {
            return _repository.Load(CultureMediaQuery.LoadQualificationTests,
                new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId });
        }

        private bool ValidateReleaseReport()
        {
            if (_selectedLotId <= 0)
            {
                MessageBox.Show("Select stored media lot first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            DateTime databaseNow = DatabaseHelper.GetAuthoritativeDatabaseTime();
            if (DpReleaseDate != null)
                DpReleaseDate.SelectedDate = databaseNow.Date;

            if (!ValidateMediaLotForQualification(_selectedLotId, databaseNow.Date))
                return false;

            if (_currentOrganisms.Rows.Count == 0)
            {
                MessageBox.Show("Add the required SOP checks and GPT organisms before saving the media lot release report.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(TxtReleasePerformedBy.Text))
            {
                MessageBox.Show("Performed By is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!ValidateGrowthPromotionCoverage())
                return false;

            string calculatedResult = CalculateQualificationResult(_currentOrganisms);
            SetComboText(CmbOverallResult, calculatedResult);

            string releaseConclusion = ComboText(CmbReleaseConclusion).Trim();
            if (string.IsNullOrWhiteSpace(releaseConclusion) ||
                releaseConclusion.Equals("Pending", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("A controlled A8 conclusion is required before the qualification can be saved.", "SOP Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (calculatedResult.Equals("Pass", StringComparison.OrdinalIgnoreCase) &&
                !releaseConclusion.Equals("Satisfactory", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("A passing qualification requires the A8 conclusion to be Satisfactory. Investigation Required or Unsatisfactory conclusions cannot release the media lot.", "SOP Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (calculatedResult.Equals("Pass", StringComparison.OrdinalIgnoreCase) &&
                !HasCompletePassingReleaseTests())
            {
                MessageBox.Show(
                    "Overall Result can be Pass only after manually recording passing SOP checks: pH Check, Growth Promotion with a real organism and ATCC/inoculum, Indicative Property, Inhibitory Property, and Preincubation Check.",
                    "SOP Release Gate",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private bool ValidateMediaLotForQualification(int mediaLotId, DateTime qualificationDate)
        {
            DataTable table = _repository.Load(CultureMediaQuery.LoadMediaLotQualificationValidation,
                new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId });

            if (table.Rows.Count != 1)
            {
                MessageBox.Show("The selected media lot no longer exists.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            DataRow row = table.Rows[0];
            string status = NormalizeStatus(RowString(row, "ReceiptStatus"));
            if (!status.Equals("Quarantine", StringComparison.OrdinalIgnoreCase) &&
                !status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Only a media lot in Quarantine can be qualified. Released and rejected lots are locked.",
                    "Culture Media Workflow",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            DateTime? receivedDate = RowDate(row, "ReceivedDate");
            DateTime? expiryDate = RowDate(row, "ExpiryDate");
            if (receivedDate.HasValue && qualificationDate.Date < receivedDate.Value.Date)
            {
                MessageBox.Show("Qualification Date cannot be before Received Date.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (expiryDate.HasValue && qualificationDate.Date > expiryDate.Value.Date)
            {
                MessageBox.Show("An expired media lot cannot be qualified or released.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void AddReleaseCheckRow(string testName, string organismOrCheck, string atcc, string inoculum, string expected, string actual, string result)
        {
            _currentOrganisms.Rows.Add(
                testName,
                organismOrCheck,
                atcc,
                inoculum,
                expected,
                actual,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                result,
                string.Empty);
        }

        private bool HasCompletePassingReleaseTests()
            => HasCompletePassingReleaseTests(_currentOrganisms, _selectedReleaseReportId);

        private bool HasCompletePassingReleaseTests(DataTable tests, int qualificationId)
        {
            if (qualificationId <= 0)
                return false;
            DataTable requirements = LoadQualificationRequirementSnapshots(qualificationId);
            foreach (DataRow requirement in requirements.Rows)
            {
                string requiredTest = RowString(requirement, "TestName");
                decimal? minimumRecovery = RowNullableDecimal(requirement, "MinimumRecoveryPercent");
                decimal? maximumRecovery = RowNullableDecimal(requirement, "MaximumRecoveryPercent");
                bool found = false;
                foreach (DataRow row in tests.Rows)
                {
                    string testName = RowString(row, "TestName");
                    string result = RowString(row, "TestResult");
                    if (testName.Equals(requiredTest, StringComparison.OrdinalIgnoreCase) &&
                        (result.Equals("Pass", StringComparison.OrdinalIgnoreCase) || result.Equals("Passed", StringComparison.OrdinalIgnoreCase)) &&
                        IsReleaseTestRowComplete(row, requiredTest, minimumRecovery, maximumRecovery))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return false;
            }

            return requirements.Rows.Count > 0;
        }

        private DataTable LoadQualificationRequirementSnapshots(int qualificationId)
        {
            if (qualificationId <= 0)
                throw new InvalidOperationException("Start or select a controlled qualification before entering results.");

            DataTable requirements = _repository.Load(CultureMediaQuery.LoadQualificationRequirementSnapshots,
                new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId });
            if (requirements.Rows.Count == 0)
                throw new InvalidOperationException("The selected qualification has no frozen requirement snapshot. Results cannot be evaluated.");
            return requirements;
        }

        private DataTable LoadApplicableQualificationRequirements(int mediaLotId)
        {
            DataTable requirements = _repository.Load(CultureMediaQuery.LoadApplicableQualificationRequirements,
                new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId });

            if (requirements.Rows.Count > 0)
                return requirements;

            if (AppConfig.IsProduction)
            {
                throw new InvalidOperationException(
                    "No active QA-approved Culture Media qualification requirements apply to the selected media lot. " +
                    "Production qualification is fail-closed until controlled requirements are reviewed, approved, activated, assigned Minimum Incubation Hours, and electronically QA-confirmed for timing.");
            }

            var fallback = new DataTable();
            fallback.Columns.Add("TestName", typeof(string));
            fallback.Columns.Add("MinimumRecoveryPercent", typeof(decimal));
            fallback.Columns.Add("MaximumRecoveryPercent", typeof(decimal));
            fallback.Columns.Add("MinimumIncubationHours", typeof(decimal));
            fallback.Columns.Add("TimingConfirmedMinimumIncubationHours", typeof(decimal));
            fallback.Columns.Add("TimingConfirmedBy", typeof(string));
            fallback.Columns.Add("TimingConfirmedAt", typeof(DateTime));
            fallback.Rows.Add("pH Check", DBNull.Value, DBNull.Value, 0m, 0m, "Development", DateTime.UtcNow);
            fallback.Rows.Add("Growth Promotion", 50m, 200m, 120m, 120m, "Development", DateTime.UtcNow);
            fallback.Rows.Add("Indicative Property", DBNull.Value, DBNull.Value, 120m, 120m, "Development", DateTime.UtcNow);
            fallback.Rows.Add("Inhibitory Property", DBNull.Value, DBNull.Value, 120m, 120m, "Development", DateTime.UtcNow);
            fallback.Rows.Add("Preincubation Check", DBNull.Value, DBNull.Value, 24m, 24m, "Development", DateTime.UtcNow);
            return fallback;
        }

        private decimal CreateQualificationRequirementSnapshotsInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int qualificationId,
            int mediaLotId)
        {
            int selectedCount;
            int invalidCount;
            decimal maximumHours;

            using (SqlCommand validation = new SqlCommand(@"
;WITH Applicable AS
(
    SELECT
        r.RequirementID, r.TestName, r.MinimumRecoveryPercent, r.MaximumRecoveryPercent,
        r.MinimumIncubationHours, r.ApprovedBy, r.ApprovedAt,
        r.TimingConfirmedMinimumIncubationHours, r.TimingConfirmedBy, r.TimingConfirmedAt,
        ROW_NUMBER() OVER
        (
            PARTITION BY r.TestName
            ORDER BY CASE WHEN r.MediaTypePattern=N'%' THEN 1 ELSE 0 END,
                     LEN(r.MediaTypePattern) DESC, r.EffectiveDate DESC, r.RequirementID DESC
        ) AS rn
    FROM dbo.CultureMediaQualificationRequirements r WITH(UPDLOCK,HOLDLOCK)
    INNER JOIN dbo.CultureMediaLots l WITH(UPDLOCK,HOLDLOCK) ON l.MediaLotID=@MediaLotID
    INNER JOIN dbo.CultureMedia m WITH(UPDLOCK,HOLDLOCK) ON m.MediaID=l.MediaID
    WHERE r.IsActive=1
      AND r.IsRequired=1
      AND r.ApprovalStatus=N'Approved'
      AND r.ApprovedBy IS NOT NULL
      AND r.ApprovedAt IS NOT NULL
      AND r.EffectiveDate <= CAST(SYSDATETIME() AS date)
      AND ISNULL(m.MediaType,N'') LIKE r.MediaTypePattern
)
SELECT
    COUNT(1),
    SUM(CASE WHEN MinimumIncubationHours IS NULL OR MinimumIncubationHours < 0
                  OR TimingConfirmedMinimumIncubationHours IS NULL
                  OR TimingConfirmedMinimumIncubationHours <> MinimumIncubationHours
                  OR TimingConfirmedBy IS NULL OR TimingConfirmedAt IS NULL
             THEN 1 ELSE 0 END),
    ISNULL(MAX(MinimumIncubationHours),0)
FROM Applicable
WHERE rn=1;", conn, tx))
            {
                validation.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                validation.Parameters.Add("@MediaLotID", SqlDbType.Int).Value = mediaLotId;
                using SqlDataReader reader = validation.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("Culture Media qualification requirements could not be resolved.");
                selectedCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                invalidCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                maximumHours = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
            }

            if (selectedCount <= 0)
                throw new InvalidOperationException("No active QA-approved Culture Media qualification requirements apply to the selected media lot.");
            if (invalidCount > 0)
            {
                throw new InvalidOperationException(
                    "Culture Media qualification timing is not QA-confirmed for the complete active requirement set. Use QA Confirm Timing before Start Qualification.");
            }

            int inserted = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
;WITH Applicable AS
(
    SELECT
        r.RequirementID, r.TestName, r.MinimumRecoveryPercent, r.MaximumRecoveryPercent,
        r.MinimumIncubationHours, r.ApprovedBy, r.ApprovedAt, r.TimingConfirmedBy, r.TimingConfirmedAt,
        ROW_NUMBER() OVER
        (
            PARTITION BY r.TestName
            ORDER BY CASE WHEN r.MediaTypePattern=N'%' THEN 1 ELSE 0 END,
                     LEN(r.MediaTypePattern) DESC, r.EffectiveDate DESC, r.RequirementID DESC
        ) AS rn
    FROM dbo.CultureMediaQualificationRequirements r WITH(UPDLOCK,HOLDLOCK)
    INNER JOIN dbo.CultureMediaLots l WITH(UPDLOCK,HOLDLOCK) ON l.MediaLotID=@MediaLotID
    INNER JOIN dbo.CultureMedia m WITH(UPDLOCK,HOLDLOCK) ON m.MediaID=l.MediaID
    WHERE r.IsActive=1
      AND r.IsRequired=1
      AND r.ApprovalStatus=N'Approved'
      AND r.ApprovedBy IS NOT NULL
      AND r.ApprovedAt IS NOT NULL
      AND r.TimingConfirmedBy IS NOT NULL
      AND r.TimingConfirmedAt IS NOT NULL
      AND r.TimingConfirmedMinimumIncubationHours=r.MinimumIncubationHours
      AND r.EffectiveDate <= CAST(SYSDATETIME() AS date)
      AND ISNULL(m.MediaType,N'') LIKE r.MediaTypePattern
)
INSERT dbo.MediaQualificationRequirementSnapshots
(
    MediaQualificationID, RequirementID, TestName, MinimumRecoveryPercent, MaximumRecoveryPercent,
    MinimumIncubationHoursSnapshot, RequirementApprovedBy, RequirementApprovedAt,
    TimingConfirmedBy, TimingConfirmedAt
)
SELECT @MediaQualificationID, RequirementID, TestName, MinimumRecoveryPercent, MaximumRecoveryPercent,
       MinimumIncubationHours, ApprovedBy, ApprovedAt, TimingConfirmedBy, TimingConfirmedAt
FROM Applicable
WHERE rn=1;",
                new[]
                {
                    new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId },
                    new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId }
                }, conn, tx);

            if (inserted != selectedCount)
                throw new DBConcurrencyException("Culture Media qualification requirements changed while the controlled timing snapshot was being created.");

            return maximumHours;
        }

        private decimal GetControlledQualificationMinimumIncubationHours(int mediaLotId)
        {
            DataTable requirements = LoadApplicableQualificationRequirements(mediaLotId);
            if (requirements.Rows.Count == 0)
                throw new InvalidOperationException("No applicable Culture Media qualification requirements were found.");

            decimal maximum = 0m;
            foreach (DataRow row in requirements.Rows)
            {
                decimal? hours = RowNullableDecimal(row, "MinimumIncubationHours");
                if (!hours.HasValue || hours.Value < 0m)
                {
                    throw new InvalidOperationException(
                        "Every active approved Culture Media qualification requirement must define a non-negative Minimum Incubation Hours value before qualification can start.");
                }
                if (hours.Value > maximum)
                    maximum = hours.Value;
            }

            return maximum;
        }

        private void EnsureQualificationIncubationElapsedInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int qualificationId,
            int mediaLotId,
            string signedBy,
            string reason,
            string reportNo)
        {
            DateTime startedAt;
            DateTime databaseNow;
            decimal minimumHours;
            string lockedStatus;
            int lockedLotId;
            int requirementSnapshotCount;
            decimal requirementSnapshotMaximum;

            using (SqlCommand command = new SqlCommand(@"
SELECT
    ISNULL(MediaLotID,0),
    ISNULL(QualificationStatus,N''),
    QualificationStartedAt,
    MinimumIncubationHoursSnapshot,
    SYSDATETIME(),
    (SELECT COUNT(1) FROM dbo.MediaQualificationRequirementSnapshots snap WITH(UPDLOCK,HOLDLOCK)
     WHERE snap.MediaQualificationID=dbo.MediaQualifications.MediaQualificationID),
    (SELECT MAX(snap.MinimumIncubationHoursSnapshot) FROM dbo.MediaQualificationRequirementSnapshots snap WITH(UPDLOCK,HOLDLOCK)
     WHERE snap.MediaQualificationID=dbo.MediaQualifications.MediaQualificationID)
FROM dbo.MediaQualifications WITH (UPDLOCK, HOLDLOCK)
WHERE MediaQualificationID=@MediaQualificationID;", conn, tx))
            {
                command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                command.Parameters.Add("@MediaQualificationID", SqlDbType.Int).Value = qualificationId;
                using SqlDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The selected qualification report no longer exists.");
                lockedLotId = reader.GetInt32(0);
                lockedStatus = reader.GetString(1).Trim();
                if (reader.IsDBNull(2) || reader.IsDBNull(3))
                    throw new InvalidOperationException("The qualification does not contain the controlled start-time and minimum-incubation snapshot required by this release.");
                startedAt = reader.GetDateTime(2);
                minimumHours = reader.GetDecimal(3);
                databaseNow = reader.GetDateTime(4);
                requirementSnapshotCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                requirementSnapshotMaximum = reader.IsDBNull(6) ? -1m : reader.GetDecimal(6);
            }

            if (lockedLotId != mediaLotId)
                throw new InvalidOperationException("The selected qualification no longer belongs to the loaded media lot.");
            if (!lockedStatus.Equals("In Progress", StringComparison.OrdinalIgnoreCase))
                throw new DBConcurrencyException("The qualification status changed before completion. Reload and try again.");
            if (minimumHours < 0m)
                throw new InvalidOperationException("The frozen Minimum Incubation Hours value is invalid.");
            if (requirementSnapshotCount <= 0 || requirementSnapshotMaximum < 0m || requirementSnapshotMaximum != minimumHours)
            {
                throw new InvalidOperationException(
                    "The Culture Media qualification does not contain a complete immutable requirement timing snapshot. Completion is fail-closed.");
            }

            DateTime eligibleAt = startedAt.AddHours(Convert.ToDouble(minimumHours, CultureInfo.InvariantCulture));
            if (databaseNow >= eligibleAt)
                return;

            string timingEvidence =
                "QualificationStartedAt=" + startedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "; MinimumIncubationHoursSnapshot=" + minimumHours.ToString("0.##", CultureInfo.InvariantCulture) +
                "; EarliestCompletionTime=" + eligibleAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "; DatabaseNow=" + databaseNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            if (!AppConfig.AllowEarlyMicrobiologyResults)
            {
                throw new InvalidOperationException(
                    "Qualification completion is blocked until the controlled incubation period is complete. Earliest permitted database time: " +
                    eligibleAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");
            }

            DatabaseHelper.AddAuditTrailAdvanced(
                conn, tx, "MediaQualifications", qualificationId,
                "Development Culture Media Timing Override", timingEvidence,
                "Early qualification completion permitted in Development only", reason,
                signedBy, "MinimumIncubationHoursSnapshot", null, reportNo, "Culture Media");
        }
    }
}
