#nullable disable
using Microsoft.Data.SqlClient;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Wpf;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System.Data;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PharmaLIMS
{
    /// <summary>
    /// Reports and Trends Analysis Window
    /// Provides visualization and analysis of pharmaceutical quality data
    /// </summary>
    [SupportedOSPlatform("windows7.0")]
    public partial class ReportsTrends : Window
    {
        #region Private Fields

        private DataTable currentDataTable = null;
        private bool isWindowReady = false;
        private static bool pdfFontsConfigured = false;
        private int? externalTrendBatchId;
        private string externalTrendParameter = string.Empty;
        private readonly int? initialExternalBatchId;
        private readonly string initialExternalParameter = string.Empty;

        #endregion

        #region Constructor & Initialization

        private void OpenInternalEMTrend_Click(object sender, RoutedEventArgs e)
        {
            if (!UserHasPermission("ViewReports"))
            {
                ShowInfo("You do not have permission to view the internal Environmental Monitoring trend.");
                return;
            }

            new EMTrendReport { Owner = this }.ShowDialog();
        }

        private void OpenEMTrend_Click(object sender, RoutedEventArgs e)
        {
            // External EM review is a separate controlled workflow.  It never reads
            // Water or native LIMS monitoring tables.
            new ExternalTrendThreeCycleReviewWindow { Owner = this }.ShowDialog();
        }

        public ReportsTrends()
        {
            ConfigurePdfFonts();
            InitializeComponent();
            this.Loaded += ReportsTrends_Loaded;
        }

        public ReportsTrends(int externalBatchId, string parameterName) : this()
        {
            initialExternalBatchId = externalBatchId > 0 ? externalBatchId : null;
            initialExternalParameter = parameterName?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// Configures PDF font resolver for cross-platform compatibility
        /// </summary>
        private void ConfigurePdfFonts()
        {
            if (pdfFontsConfigured)
                return;

            try
            {
                GlobalFontSettings.FontResolver = new WindowsFontResolver();
                pdfFontsConfigured = true;
            }
            catch (Exception ex)
            {
                LogError("Failed to configure PDF fonts", ex);
            }
        }

        /// <summary>
        /// Window loaded event handler - initializes UI components
        /// </summary>
        private void ReportsTrends_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (isWindowReady)
                    return;

                isWindowReady = true;

                // Keep the window responsive while SQL Server may still be completing
                // post-login recovery, but always populate the Internal Trend source list
                // after the first render. Waiting only for a category SelectionChanged event
                // leaves PW/PTW/EM points hidden because the default category is already All.
                if (lblEntityFilter != null)
                    lblEntityFilter.Text = "Sampling Point / Area:";
                SetDefaultDates();

                if (cboCategory != null && cboCategory.SelectedIndex < 0)
                    cboCategory.SelectedIndex = 0;

                if (cboViewMode != null && cboViewMode.SelectedIndex < 0)
                    cboViewMode.SelectedIndex = 0;

                if (cboChartType != null && cboChartType.SelectedIndex < 0)
                    cboChartType.SelectedIndex = 0;

                if (cboTest != null)
                {
                    cboTest.ItemsSource = null;
                    cboTest.SelectedIndex = -1;
                    cboTest.IsEnabled = false;
                }

                CboViewMode_SelectionChanged(cboViewMode, null);

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!IsLoaded)
                        return;

                    LoadSamplingPoints(GetSelectedCategory());
                    if (initialExternalBatchId.HasValue && !string.IsNullOrWhiteSpace(initialExternalParameter))
                        LoadExternalTrendBatch(initialExternalBatchId.Value, initialExternalParameter);
                }));

                lblStatus.Text = initialExternalBatchId.HasValue ? "Opening approved external trend..." : "Ready";
            }
            catch (Exception ex)
            {
                LogError("Error during window load", ex);
                MessageBox.Show("Error while opening Reports and Trends:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Error Handling

        private static void LogError(string context, Exception ex)
        {
            ApplicationLogger.Error(context, ex);
        }

        #endregion

        #region Data Loading Methods

        /// <summary>
        /// Loads sampling points from both Water and Environmental sources
        /// </summary>
        private void LoadSamplingPoints(string category = "All")
        {
            try
            {
                if (cboPoint == null)
                    return;

                cboPoint.Items.Clear();
                bool isPrm = IsPrmCategory(category);

                if (lblEntityFilter != null)
                    lblEntityFilter.Text = isPrm ? "Material / Product:" : "Sampling Point / Area:";

                cboPoint.Items.Add(new ComboBoxItem
                {
                    Content = isPrm ? "All Materials / Products" : "All Sampling Points / Areas",
                    Tag = "-1"
                });

                string query;
                SqlParameter[] parameters = Array.Empty<SqlParameter>();

                if (isPrm)
                {
                    query = @"
                        SELECT DISTINCT
                            COALESCE(NULLIF(LTRIM(RTRIM(MaterialName)), ''),
                                     NULLIF(LTRIM(RTRIM(ProductName)), ''),
                                     NULLIF(LTRIM(RTRIM(MaterialCode)), ''),
                                     NULLIF(LTRIM(RTRIM(ProductCode)), ''),
                                     SampleNumber) AS EntityName
                        FROM dbo.PRM_Samples
                        WHERE " + BuildPrmCategorySql("SampleCategory") + @"
                        ORDER BY EntityName";
                    parameters = new[] { new SqlParameter("@category", category) };
                }
                else
                {
                    query = @"
                        SELECT DISTINCT PointID, PointCode
                        FROM
                        (
                            SELECT Id AS PointID, PointCode
                            FROM WaterSamplingPoints
                            WHERE ISNULL(Status, '') = 'Active' AND ISNULL(PointCode, '') <> ''
                            UNION ALL
                            SELECT PointID, PointCode
                            FROM SamplingPoints
                            WHERE ISNULL(IsActive, 1) = 1 AND ISNULL(PointCode, '') <> ''
                        ) q
                        ORDER BY PointCode";
                }

                DataTable dt = DatabaseHelper.ExecuteQuery(query, parameters);
                foreach (DataRow row in dt.Rows)
                {
                    if (isPrm)
                    {
                        string name = row["EntityName"]?.ToString()?.Trim() ?? "";
                        if (name.Length == 0) continue;
                        cboPoint.Items.Add(new ComboBoxItem { Content = name, Tag = name });
                    }
                    else
                    {
                        cboPoint.Items.Add(new ComboBoxItem
                        {
                            Content = row["PointCode"].ToString(),
                            Tag = row["PointID"].ToString()
                        });
                    }
                }

                cboPoint.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                LogError("Error loading source filter", ex);
                SetStatus("Source list is temporarily unavailable. You can still open 3-Cycle EM Review; retry the legacy filters after SQL Server is available.", false);
            }
        }

        /// <summary>
        /// Sets default date range to last 6 months
        /// </summary>
        private void SetDefaultDates()
        {
            try
            {
                DateTime authoritativeNow = GetAuthoritativeTrendTime();
                if (dpDateFrom != null)
                    dpDateFrom.SelectedDate = authoritativeNow.AddMonths(-6);

                if (dpDateTo != null)
                    dpDateTo.SelectedDate = authoritativeNow;
            }
            catch (Exception ex)
            {
                LogError("Error setting default dates", ex);
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Handles category selection change - loads corresponding tests
        /// </summary>
        private void CboCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isWindowReady)
                return;

            if (cboCategory == null || cboTest == null)
                return;

            if (cboCategory.SelectedItem == null)
                return;

            ComboBoxItem selectedCategory = cboCategory.SelectedItem as ComboBoxItem;

            if (selectedCategory == null)
                return;

            string category = selectedCategory.Content?.ToString() ?? "All";

            LoadSamplingPoints(category);

            if (category == "All")
            {
                cboTest.ItemsSource = null;
                cboTest.IsEnabled = false;
                cboTest.SelectedIndex = -1;
                return;
            }

            try
            {
                bool isPrmCategory = IsPrmCategory(category);
                string query;
                SqlParameter[] pars;

                if (isPrmCategory)
                {
                    query = @"
                        SELECT
                            ROW_NUMBER() OVER (ORDER BY LTRIM(RTRIM(pt.TestName))) AS TestID,
                            LTRIM(RTRIM(pt.TestName)) AS TestName
                        FROM dbo.PRM_SampleTests pt
                        INNER JOIN dbo.PRM_Samples ps ON ps.SampleID = pt.SampleID
                        WHERE " + BuildPrmCategorySql("ps.SampleCategory") + @"
                          AND ISNULL(LTRIM(RTRIM(pt.TestName)), '') <> ''
                        GROUP BY LTRIM(RTRIM(pt.TestName))
                        ORDER BY LTRIM(RTRIM(pt.TestName))";

                    pars = new[] { new SqlParameter("@category", category) };
                }
                else
                {
                    query = @"
                        SELECT TestID, TestName
                        FROM Tests
                        WHERE TestCategory = @category
                        ORDER BY TestName";

                    pars = new[] { new SqlParameter("@category", category) };
                }

                DataTable dt = DatabaseHelper.ExecuteQuery(query, pars);

                // Make the report-wide behavior explicit instead of using an empty
                // selection as an undocumented synonym for all tests. Trend by Test
                // still requires one concrete test and will reject this sentinel.
                DataRow allTestsRow = dt.NewRow();
                allTestsRow["TestID"] = 0;
                allTestsRow["TestName"] = "All Tests";
                dt.Rows.InsertAt(allTestsRow, 0);

                cboTest.ItemsSource = dt.DefaultView;
                cboTest.DisplayMemberPath = "TestName";
                cboTest.SelectedValuePath = "TestID";
                cboTest.IsEnabled = true;
                cboTest.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                LogError("Error loading tests for category", ex);
                MessageBox.Show("Error loading tests:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles view mode change - updates UI controls accordingly
        /// </summary>
        private void CboViewMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isWindowReady)
                return;

            if (cboViewMode == null || chkShowLimits == null || cboChartType == null)
                return;

            if (cboViewMode.SelectedItem == null)
                return;

            ComboBoxItem selectedMode = cboViewMode.SelectedItem as ComboBoxItem;

            if (selectedMode == null)
                return;

            string mode = selectedMode.Content?.ToString() ?? "";

            if (mode == "Trend by Test")
            {
                chkShowLimits.IsEnabled = true;
                cboChartType.IsEnabled = false;

                foreach (object obj in cboChartType.Items)
                {
                    ComboBoxItem item = obj as ComboBoxItem;

                    if (item != null &&
                        item.Content != null &&
                        item.Content.ToString() == "Line Chart")
                    {
                        cboChartType.SelectedItem = item;
                        break;
                    }
                }
            }
            else
            {
                chkShowLimits.IsEnabled = false;
                chkShowLimits.IsChecked = false;
                cboChartType.IsEnabled = mode == "Status Summary";
                if (mode == "Category Summary")
                {
                    foreach (object obj in cboChartType.Items)
                    {
                        if (obj is ComboBoxItem item && string.Equals(item.Content?.ToString(), "Column Chart", StringComparison.Ordinal))
                        {
                            cboChartType.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
        }

        private void LimitsVisibilityChanged(object sender, RoutedEventArgs e)
        {
            if (!isWindowReady || cboViewMode?.SelectedItem is not ComboBoxItem mode ||
                !string.Equals(mode.Content?.ToString(), "Trend by Test", StringComparison.Ordinal) ||
                TrendChart?.Model == null)
                return;

            BuildCurrentTrendModels();
        }

        /// <summary>
        /// Main Load Data button handler
        /// </summary>
        private void BtnLoadData_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ClearExternalTrendContext();
                InvalidateTrendData();
                SetStatus("Loading started...", true);

                if (!ValidateInputs())
                    return;

                ComboBoxItem selectedMode = cboViewMode.SelectedItem as ComboBoxItem;

                if (selectedMode == null)
                {
                    ShowInfo("Please select View Mode.");
                    return;
                }

                string viewMode = selectedMode.Content?.ToString() ?? "";

                bool chartLoaded = false;

                switch (viewMode)
                {
                    case "Trend by Test":
                        chartLoaded = LoadTrendChart();
                        if (!chartLoaded)
                        {
                            SetStatus("Trend chart was not loaded.", false);
                            return;
                        }
                        break;

                    case "Status Summary":
                        LoadStatusSummary();
                        break;

                    case "Category Summary":
                        LoadCategorySummary();
                        break;

                    default:
                        ShowInfo($"Unknown View Mode: {viewMode}");
                        return;
                }

                SetStatus("Data loaded successfully. " + currentDataTable?.Rows.Count + " source record(s).", false);
            }
            catch (Exception ex)
            {
                LogError("Unexpected error while loading data", ex);
                ShowError("Unexpected error while loading data:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        /// <summary>
        /// Clear Filters button handler
        /// </summary>
        private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (cboPoint != null)
                    cboPoint.SelectedIndex = 0;

                if (cboCategory != null)
                    cboCategory.SelectedIndex = 0;

                if (cboTest != null)
                {
                    cboTest.ItemsSource = null;
                    cboTest.SelectedIndex = -1;
                    cboTest.IsEnabled = false;
                }

                SetDefaultDates();

                if (TrendChart != null)
                    TrendChart.Model = null;

                if (dgTrends != null)
                    dgTrends.ItemsSource = null;

                InvalidateTrendData();
                ClearExternalTrendContext();

                ResetStatusCounters();

                SetStatus("Filters cleared", false);
            }
            catch (Exception ex)
            {
                LogError("Error clearing filters", ex);
                ShowError("Error clearing filters:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private void ClearExternalTrendContext()
        {
            externalTrendBatchId = null;
            externalTrendParameter = string.Empty;
        }

        private bool LoadExternalTrendBatch(int batchId, string parameterName)
        {
            try
            {
                var service = new ExternalTrendImportService();
                DataTable data = service.LoadApprovedTrend(batchId, parameterName);
                if (data.Rows.Count == 0)
                {
                    ShowInfo("No approved numeric rows were found for the selected import and parameter.");
                    return false;
                }

                ApplyReportData(data);
                externalTrendBatchId = batchId;
                externalTrendParameter = parameterName;
                return BuildCurrentTrendModels();
            }
            catch (Exception ex)
            {
                InvalidateTrendData(); ClearExternalTrendContext();
                LogError("Error loading approved external trend", ex);
                ShowError("Approved external trend data could not be loaded:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
                return false;
            }
        }

        #endregion

        #region Validation Methods

        /// <summary>
        /// Validates user inputs before loading data
        /// </summary>
        private bool ValidateInputs()
        {
            if (!UserHasPermission("ViewReports"))
            {
                ShowInfo("You do not have permission to view reports.");
                return false;
            }

            if (cboViewMode == null || cboViewMode.SelectedItem == null)
            {
                ShowInfo("Please select View Mode.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if current user has the required permission
        /// </summary>
        private bool UserHasPermission(string permission)
        {
            if (string.IsNullOrWhiteSpace(Login.CurrentUser))
                return false;

            switch (permission)
            {
                case "ViewReports":
                case "ExportReports":
                case "ExportPDF":
                    // Authorization is driven by the current database permission, not a cached role name.
                    // DatabaseHelper fails closed and logs permission-query failures.
                    return DatabaseHelper.CanAccessReports(Login.CurrentUser);

                default:
                    return false;
            }
        }

        #endregion

        #region UI Helper Methods

        private void SetStatus(string message, bool isBusy)
        {
            if (lblStatus != null)
            {
                Dispatcher.Invoke(() =>
                {
                    lblStatus.Text = message;

                    if (isBusy)
                        lblStatus.Foreground = System.Windows.Media.Brushes.Orange;
                    else
                        lblStatus.Foreground = System.Windows.Media.Brushes.Black;
                });
            }
        }

        private void ResetStatusCounters()
        {
            lblPassCount.Text = "PASS: 0";
            lblAlertCount.Text = "ALERT: 0";
            lblFailCount.Text = "FAIL: 0";
            lblTotalSamples.Text = "Samples: 0";
            lblTotalResults.Text = "Results: 0 | PENDING: 0";
            lblTestTypes.Text = "Test Types: 0";
        }

        private void ShowInfo(string message)
        {
            MessageBox.Show(message, "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowError(string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        #endregion

        #region CSV Export Helper

        private string EscapeCsv(string value) => Infrastructure.CsvSecurity.Escape(value);

        #endregion

        #region Data Retrieval Helper Methods

        private static string GetCurrentUserName()
        {
            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                return Login.CurrentUser.Trim();

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required to generate a controlled report.");
        }

        private static string GetCurrentUserRole()
        {
            return string.IsNullOrWhiteSpace(Login.CurrentUserRole)
                ? string.Empty
                : Login.CurrentUserRole.Trim();
        }

        private string GetSelectedCategory()
        {
            if (cboCategory != null && cboCategory.SelectedItem is ComboBoxItem selectedCategory)
                return selectedCategory.Content?.ToString() ?? "All";

            return "All";
        }

        private static bool IsPrmCategory(string category)
        {
            return category == "Primary Packaging" || category == "Raw Material" ||
                   category == "Production / In-Process" ||
                   category == "Finished Product" ||
                   category == "Stability";
        }

        private static string BuildPrmCategorySql(string columnName)
        {
            return $@"(
                LTRIM(RTRIM({columnName})) = @category
                OR (@category = N'Production / In-Process' AND LTRIM(RTRIM({columnName})) IN (N'Production', N'In Process', N'In-Process', N'Production / In Process'))
                OR (@category = N'Finished Product' AND LTRIM(RTRIM({columnName})) IN (N'Finished', N'Finished Products'))
                OR (@category = N'Raw Material' AND LTRIM(RTRIM({columnName})) IN (N'Raw Materials', N'RM'))
                OR (@category = N'Stability' AND LTRIM(RTRIM({columnName})) IN (N'Stability Sample', N'Stability Samples'))
            )";
        }

        private int? GetSelectedPointId()
        {
            try
            {
                if (cboPoint != null &&
                    cboPoint.SelectedItem is ComboBoxItem selectedPoint &&
                    selectedPoint.Tag != null &&
                    selectedPoint.Tag.ToString() != "-1")
                {
                    return Convert.ToInt32(selectedPoint.Tag);
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private string GetSelectedPointText()
        {
            if (cboPoint != null && cboPoint.SelectedItem is ComboBoxItem selectedPoint)
                return selectedPoint.Content?.ToString() ?? "All Sampling Points";

            return "All Sampling Points";
        }

        private int? GetSelectedTestId()
        {
            try
            {
                if (cboTest != null && cboTest.IsEnabled && cboTest.SelectedValue != null)
                {
                    int selectedTestId = Convert.ToInt32(cboTest.SelectedValue, CultureInfo.InvariantCulture);
                    return selectedTestId > 0 ? selectedTestId : null;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private string GetSelectedTestText()
        {
            try
            {
                if (cboTest != null && cboTest.IsEnabled && cboTest.SelectedItem is DataRowView row)
                {
                    string selectedTest = row["TestName"]?.ToString()?.Trim() ?? string.Empty;
                    return selectedTest.Equals("All Tests", StringComparison.OrdinalIgnoreCase)
                        ? "All"
                        : selectedTest;
                }

                if (cboTest != null && cboTest.IsEnabled && !string.IsNullOrWhiteSpace(cboTest.Text))
                {
                    string selectedTest = cboTest.Text.Trim();
                    return selectedTest.Equals("All Tests", StringComparison.OrdinalIgnoreCase)
                        ? "All"
                        : selectedTest;
                }
            }
            catch
            {
                return "All";
            }

            return "All";
        }

        private static bool TryGetDouble(object value, out double result)
        {
            result = 0;

            if (value == null || value == DBNull.Value)
                return false;

            string text = value.ToString().Trim();

            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (!TrendReportData.ExactNumber(value, out result))
                return false;

            return true;
        }

        private static bool TryGetTrendNumericValue(object value, out double result, out bool isCensored)
        {
            result = 0d;
            isCensored = false;
            if (value == null || value == DBNull.Value)
                return false;

            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = text.Replace("≤", "<", StringComparison.Ordinal).Replace("≥", ">", StringComparison.Ordinal).TrimStart();
            isCensored = normalized.StartsWith("<", StringComparison.Ordinal) || normalized.StartsWith(">", StringComparison.Ordinal);
            if (isCensored)
                return false;

            return TryGetDouble(value, out result);
        }

        private static bool TryExtractSpecificationUpperLimit(object value, out double limit)
        {
            limit = 0d;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) || text.Contains(','))
                return false;

            // Only an explicit upper-limit expression may become a chart limit.
            // Qualitative requirements such as Absent/Complies remain non-numeric.
            if (!Regex.IsMatch(text, @"\b(NMT|NOT\s+MORE\s+THAN|MAX(?:IMUM)?)\b|<=|≤", RegexOptions.IgnoreCase))
                return false;

            Match match = Regex.Match(text, @"[-+]?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant);
            if (!match.Success || !ControlledNumericValue.TryParse(match.Value, out decimal exact) || exact < 0m)
                return false;
            limit = (double)exact;
            return true;
        }

        private static string NormalizeWaterProfile(string sampleType, string pointCode)
        {
            string type = (sampleType ?? string.Empty).Trim().ToLowerInvariant();
            string point = (pointCode ?? string.Empty).Trim().ToUpperInvariant();

            if (type.Contains("potable") || type.Contains("drinking") || type.Contains("ptw") || point.StartsWith("PTWS", StringComparison.OrdinalIgnoreCase))
                return "PTW";

            if (type.Contains("purified") || type == "pw" || type.Contains("pws") || point.StartsWith("PWS", StringComparison.OrdinalIgnoreCase))
                return "PW";

            return string.Empty;
        }

        private static bool IsConductivityReportTest(string testName)
        {
            return (testName ?? string.Empty).Trim().Contains("conductivity", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPhReportTest(string testName)
        {
            string value = (testName ?? string.Empty).Trim().ToLowerInvariant();
            return value == "ph" || value == "p.h" || value.Contains("ph value") || value.Contains("ph test") || value.StartsWith("ph ") || value.EndsWith(" ph");
        }

        private static bool IsHardnessReportTest(string testName)
        {
            string value = (testName ?? string.Empty).Trim().ToLowerInvariant();
            return value.Contains("hardness") || (value.Contains("calcium") && value.Contains("magnesium"));
        }

        private static int ExtractWaterPointNumber(string pointCode)
        {
            Match match = Regex.Match(pointCode ?? string.Empty, @"(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                ? number
                : 0;
        }

        private static (double? Alert, double? Action)? GetWaterProfileRuleLimits(string waterProfile, string pointCode, string testName)
        {
            // Legacy point/type rules are retained only as documented reference for older validation evidence.
            // v204 trend generation does NOT call this method to reinterpret historical results.
            if (IsConductivityReportTest(testName))
            {
                if (waterProfile == "PW") return (null, 2.00d);
                if (waterProfile == "PTW") return (null, 500.00d);
            }
            if (IsPhReportTest(testName))
            {
                if (waterProfile == "PW") return (5.00d, 7.00d);
                if (waterProfile == "PTW") return (6.50d, 8.50d);
            }
            return null;
        }

        private static bool NullableLimitEquals(double? left, double? right)
        {
            if (!left.HasValue || !right.HasValue)
                return left.HasValue == right.HasValue;
            return Math.Abs(left.Value - right.Value) < 0.000001d;
        }

        private static (double? Alert, double? Action) GetEffectiveWaterLimits(
            string waterProfile,
            string pointCode,
            string testName,
            object snapshotAlert,
            object snapshotAction,
            object masterAlert,
            object masterAction,
            bool hasSpecificationSnapshot)
        {
            double? snapAlert = TryGetNullableDouble(snapshotAlert);
            double? snapAction = TryGetNullableDouble(snapshotAction);

            // A stored specification snapshot is immutable historical evidence.
            // Frozen specification evidence is authoritative for trend generation.
            // We intentionally do not fall back to today's Tests master or hard-coded point rules,
            // because doing so could rewrite the historical interpretation of a released result.
            if (hasSpecificationSnapshot)
                return (snapAlert, snapAction);
            if (snapAlert.HasValue || snapAction.HasValue)
                return (snapAlert, snapAction);

            return (null, null);
        }

        private static double? TryGetNullableDouble(object value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            string text = value.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return ControlledNumericValue.TryParse(text, out decimal parsed)
                ? (double)parsed
                : null;
        }

        private static string EvaluateWaterTrendStatus(string storedStatus, string testName, decimal resultValue, decimal? alertLimit, decimal? actionLimit)
            => TrendReportData.WaterStatusExact(storedStatus, testName, resultValue, alertLimit, actionLimit);

        private static void NormalizeInternalWaterRows(DataTable table)
        {
            if (table == null)
                return;


            if (!table.Columns.Contains("WaterProfile"))
                table.Columns.Add("WaterProfile", typeof(string));
            if (!table.Columns.Contains("AlertLimit"))
                table.Columns.Add("AlertLimit", typeof(double));
            if (!table.Columns.Contains("ActionLimit"))
                table.Columns.Add("ActionLimit", typeof(double));
            if (!table.Columns.Contains("Status"))
                table.Columns.Add("Status", typeof(string));

            foreach (DataRow row in table.Rows)
            {
                string sampleType = Convert.ToString(row["_SampleType"], CultureInfo.InvariantCulture) ?? string.Empty;
                string pointCode = Convert.ToString(row["PointCode"], CultureInfo.InvariantCulture) ?? string.Empty;
                string testName = Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? string.Empty;
                string profile = NormalizeWaterProfile(sampleType, pointCode);
                row["WaterProfile"] = profile;

                bool hasSpecificationSnapshot = row.Table.Columns.Contains("_HasSpecSnapshot") &&
                    row["_HasSpecSnapshot"] != DBNull.Value && Convert.ToInt32(row["_HasSpecSnapshot"], CultureInfo.InvariantCulture) == 1;

                (double? alert, double? action) = GetEffectiveWaterLimits(
                    profile,
                    pointCode,
                    testName,
                    row["_SnapshotAlert"],
                    row["_SnapshotAction"],
                    row["_MasterAlert"],
                    row["_MasterAction"],
                    hasSpecificationSnapshot);


                row["AlertLimit"] = alert.HasValue ? alert.Value : DBNull.Value;
                row["ActionLimit"] = action.HasValue ? action.Value : DBNull.Value;

                if (row["ResultValue"] == DBNull.Value || string.IsNullOrWhiteSpace(Convert.ToString(row["ResultValue"], CultureInfo.InvariantCulture)))
                {
                    row["Status"] = "Pending";
                }
                else
                {
                    string storedStatus = Convert.ToString(row["_StoredStatus"], CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!ControlledNumericValue.TryParse(Convert.ToString(row["ResultValue"], CultureInfo.InvariantCulture), out decimal resultValue))
                        row["Status"] = "NOT ASSESSED";
                    else
                    {
                        decimal? exactAlert = ControlledNumericValue.TryParse(Convert.ToString(row["_SnapshotAlert"], CultureInfo.InvariantCulture), out decimal a) ? a : null;
                        decimal? exactAction = ControlledNumericValue.TryParse(Convert.ToString(row["_SnapshotAction"], CultureInfo.InvariantCulture), out decimal b) ? b : null;
                        row["Status"] = EvaluateWaterTrendStatus(storedStatus, testName, resultValue, exactAlert, exactAction);
                    }
                }
            }

            string[] helperColumns = { "_SampleType", "_SnapshotAlert", "_SnapshotAction", "_MasterAlert", "_MasterAction", "_StoredStatus", "_HasSpecSnapshot" };
            foreach (string column in helperColumns)
                if (table.Columns.Contains(column))
                    table.Columns.Remove(column);

        }

        private static DateTime GetAuthoritativeTrendTime()
        {
            try
            {
                return DatabaseHelper.GetAuthoritativeDatabaseTime();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Authoritative database time is unavailable. The controlled trend report/export was not generated.",
                    ex);
            }
        }

        private string GenerateReportNumber()
        {
            DateTime now = GetAuthoritativeTrendTime();
            return $"RTL-{now:yyyyMMdd-HHmmss}";
        }

        private string GetReportDateText(DatePicker picker)
        {
            if (picker != null && picker.SelectedDate.HasValue)
                return picker.SelectedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            return "All";
        }

        #endregion

        #region Trend Chart Loading

        /// <summary>
        /// Loads trend chart with data from the database
        /// </summary>
        private bool LoadTrendChart()
        {
            string category = GetSelectedCategory();
            if (category == "All") { ShowInfo("Select a specific category and test for a numeric trend."); return false; }
            if (IsPrmCategory(category)) return LoadPrmTrendChart(category);
            if (!GetSelectedTestId().HasValue) { ShowInfo("Select a specific test."); return false; }
            if (!LoadReportData()) return false;
            return BuildCurrentTrendModels();
        }

        private bool LoadPrmTrendChart(string category)
        {
            if (GetSelectedTestText() == "All") { ShowInfo("Select a specific PRM test."); return false; }
            if (!LoadReportData()) return false;
            // Qualified/censored values remain visible; they are censored values, never exact measurements.
            SetStatus("PRM qualified/censored result(s) excluded from exact quantitative plotting.", false);
            return BuildCurrentTrendModels();
        }

        #endregion

        #region Summary Charts

        /// <summary>
        /// Loads Status Summary chart
        /// </summary>
        private void LoadStatusSummary()
        {
            try
            {
                DataTable dt = LoadSummaryData(null);

                int passCount = 0, alertCount = 0, failCount = 0, pendingCount = 0, notAssessedCount = 0;

                foreach (DataRow row in dt.Rows)
                {
                    string status = row["Status"].ToString();

                    if (status == "PASS")
                        passCount++;
                    else if (status == "ALERT")
                        alertCount++;
                    else if (status == "FAIL")
                        failCount++;
                    else if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                        pendingCount++;
                    else
                        notAssessedCount++;
                }

                var plotModel = BuildStatusSummaryChart(passCount, alertCount, failCount, pendingCount, notAssessedCount);

                TrendChart.Model = null;
                TrendChart.Model = plotModel;

                SetStatus($"Status Summary: PASS={passCount}, ALERT={alertCount}, FAIL={failCount}, PENDING={pendingCount}, NOT ASSESSED={notAssessedCount}", false);
            }
            catch (Exception ex)
            {
                InvalidateTrendData();
                LogError("Error loading status summary", ex);
                ShowError("Error loading status summary:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        /// <summary>
        /// Loads Category Summary chart
        /// </summary>
        private void LoadCategorySummary()
        {
            try
            {
                DataTable dt = LoadSummaryData("HistoricalTestCategory");

                var categoryStatus = new Dictionary<string, (int pass, int alert, int fail, int pending, int notAssessed)>();

                foreach (DataRow row in dt.Rows)
                {
                    string testCategory = row["TestCategory"].ToString();
                    string status = row["Status"].ToString();

                    if (!categoryStatus.ContainsKey(testCategory))
                        categoryStatus[testCategory] = (0, 0, 0, 0, 0);

                    var current = categoryStatus[testCategory];

                    if (status == "PASS")
                        current.pass++;
                    else if (status == "ALERT")
                        current.alert++;
                    else if (status == "FAIL")
                        current.fail++;
                    else if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                        current.pending++;
                    else
                        current.notAssessed++;

                    categoryStatus[testCategory] = current;
                }

                var plotModel = BuildCategorySummaryChart(categoryStatus);

                TrendChart.Model = null;
                TrendChart.Model = plotModel;

                SetStatus($"Category Summary loaded: {categoryStatus.Count} categories", false);
            }
            catch (Exception ex)
            {
                InvalidateTrendData();
                LogError("Error loading category summary", ex);
                ShowError("Error loading category summary:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        /// <summary>
        /// Loads summary data from the database
        /// </summary>
        private DataTable LoadSummaryData(string groupByColumn)
        {
            if (!LoadReportData()) throw new InvalidOperationException("Report data could not be loaded; summary cancelled.");
            DataTable summaryData = currentDataTable.Copy();
            // NormalizeInternalWaterRows(summaryData) is performed once by LoadReportData before this copy.
            return summaryData;
        }

        /// <summary>
        /// Builds the Status Summary chart model
        /// </summary>
        private PlotModel BuildStatusSummaryChart(int passCount, int alertCount, int failCount, int pendingCount, int notAssessedCount)
        {
            var plotModel = new PlotModel
            {
                Title = "Status Summary",
                TitleFontSize = 14,
                TitleFontWeight = OxyPlot.FontWeights.Bold,
                Background = OxyColors.White
            };

            string chartType = "Column Chart";

            if (cboChartType != null && cboChartType.SelectedItem is ComboBoxItem item)
                chartType = item.Content?.ToString() ?? "Column Chart";

            if (chartType == "Pie Chart")
            {
                var pieSeries = new PieSeries
                {
                    InsideLabelPosition = 0.5,
                    StrokeThickness = 1,
                    Stroke = OxyColors.White
                };

                if (passCount > 0)
                    pieSeries.Slices.Add(new PieSlice("PASS", passCount) { Fill = OxyColors.Green });

                if (alertCount > 0)
                    pieSeries.Slices.Add(new PieSlice("ALERT", alertCount) { Fill = OxyColors.Orange });

                if (failCount > 0)
                    pieSeries.Slices.Add(new PieSlice("FAIL", failCount) { Fill = OxyColors.Red });
                if (pendingCount > 0)
                    pieSeries.Slices.Add(new PieSlice("PENDING", pendingCount) { Fill = OxyColors.SteelBlue });
                if (notAssessedCount > 0)
                    pieSeries.Slices.Add(new PieSlice("NOT ASSESSED", notAssessedCount) { Fill = OxyColors.Gray });

                if (pieSeries.Slices.Count == 0)
                    pieSeries.Slices.Add(new PieSlice("No Data", 1) { Fill = OxyColors.LightGray });

                plotModel.Series.Add(pieSeries);
            }
            else
            {
                var categoryAxis = new CategoryAxis
                {
                    Position = AxisPosition.Left,
                    Title = "Status",
                    MajorGridlineStyle = LineStyle.Solid,
                    MajorGridlineColor = OxyColors.LightGray
                };

                categoryAxis.Labels.Add("PASS");
                categoryAxis.Labels.Add("ALERT");
                categoryAxis.Labels.Add("FAIL");
                categoryAxis.Labels.Add("PENDING");
                categoryAxis.Labels.Add("NOT ASSESSED");

                plotModel.Axes.Add(categoryAxis);

                var valueAxis = new LinearAxis
                {
                    Position = AxisPosition.Bottom,
                    Title = "Count",
                    Minimum = 0,
                    MajorGridlineStyle = LineStyle.Solid,
                    MajorGridlineColor = OxyColors.LightGray
                };

                plotModel.Axes.Add(valueAxis);

                var barSeries = new BarSeries
                {
                    Title = "Status Summary"
                };

                barSeries.Items.Add(new BarItem { Value = passCount, Color = OxyColors.Green });
                barSeries.Items.Add(new BarItem { Value = alertCount, Color = OxyColors.Orange });
                barSeries.Items.Add(new BarItem { Value = failCount, Color = OxyColors.Red });
                barSeries.Items.Add(new BarItem { Value = pendingCount, Color = OxyColors.SteelBlue });
                barSeries.Items.Add(new BarItem { Value = notAssessedCount, Color = OxyColors.Gray });

                plotModel.Series.Add(barSeries);
            }

            return plotModel;
        }

        /// <summary>
        /// Builds the Category Summary chart model
        /// </summary>
        private PlotModel BuildCategorySummaryChart(Dictionary<string, (int pass, int alert, int fail, int pending, int notAssessed)> categoryStatus)
        {
            var plotModel = new PlotModel
            {
                Title = "Category Summary",
                TitleFontSize = 14,
                TitleFontWeight = OxyPlot.FontWeights.Bold,
                Background = OxyColors.White
            };

            var categoryAxis = new CategoryAxis
            {
                Position = AxisPosition.Left,
                Title = "Test Category",
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.LightGray
            };

            var passSeries = new BarSeries
            {
                Title = "PASS",
                FillColor = OxyColors.Green
            };

            var alertSeries = new BarSeries
            {
                Title = "ALERT",
                FillColor = OxyColors.Orange
            };

            var failSeries = new BarSeries
            {
                Title = "FAIL",
                FillColor = OxyColors.Red
            };

            var pendingSeries = new BarSeries
            {
                Title = "PENDING",
                FillColor = OxyColors.SteelBlue
            };

            var notAssessedSeries = new BarSeries
            {
                Title = "NOT ASSESSED",
                FillColor = OxyColors.Gray
            };

            foreach (var cat in categoryStatus)
            {
                categoryAxis.Labels.Add(cat.Key);
                passSeries.Items.Add(new BarItem { Value = cat.Value.pass });
                alertSeries.Items.Add(new BarItem { Value = cat.Value.alert });
                failSeries.Items.Add(new BarItem { Value = cat.Value.fail });
                pendingSeries.Items.Add(new BarItem { Value = cat.Value.pending });
                notAssessedSeries.Items.Add(new BarItem { Value = cat.Value.notAssessed });
            }

            plotModel.Axes.Add(categoryAxis);

            var valueAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Count",
                Minimum = 0,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.LightGray
            };
            plotModel.Axes.Add(valueAxis);

            plotModel.Series.Add(passSeries);
            plotModel.Series.Add(alertSeries);
            plotModel.Series.Add(failSeries);
            plotModel.Series.Add(pendingSeries);
            plotModel.Series.Add(notAssessedSeries);

            plotModel.Legends.Add(new OxyPlot.Legends.Legend
            {
                LegendTitle = "Status",
                LegendPosition = OxyPlot.Legends.LegendPosition.RightTop,
                LegendPlacement = OxyPlot.Legends.LegendPlacement.Outside
            });

            return plotModel;
        }

        #endregion

        #region Report Data Loading

        /// <summary>
        /// Loads the main report data grid
        /// </summary>
        private bool LoadReportData()
        {
            try
            {
                string category = GetSelectedCategory();

                if (IsPrmCategory(category))
                {
                    LoadPrmReportData(category);
                    return true;
                }

                int? testId = GetSelectedTestId();

                string query = @"
                    SELECT
                        s.SampleNumber,
                        COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode, p.PointCode, '') AS PointCode,
                        COALESCE(NULLIF(s.PointLocationSnapshot,N''),wp.Location, p.Location, '') AS Location,
                        FORMAT(s.SamplingDateTime, 'yyyy-MM-dd HH:mm') AS SamplingDate,
                        s.SamplingDateTime AS SamplingDateTime,
                        s.SampleType AS _SampleType,
                        s.Status AS SampleStatus,
                        ISNULL(NULLIF(st.TestNameSnapshot,N''),t.TestName) AS TestName,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.TestCategorySnapshot ELSE t.TestCategory END AS TestCategory,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.UnitSnapshot ELSE t.Unit END AS Unit,
                        st.ResultValue,
                        st.AlertLimitSnapshot AS _SnapshotAlert,
                        st.ActionLimitSnapshot AS _SnapshotAction,
                        t.AlertLimit AS _MasterAlert,
                        t.ActionLimit AS _MasterAction,
                        st.ResultStatus AS _StoredStatus,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL
                                   OR st.AlertLimitSnapshot IS NOT NULL
                                   OR st.ActionLimitSnapshot IS NOT NULL
                             THEN 1 ELSE 0 END AS _HasSpecSnapshot,
                        s.SampledBy
                    FROM Samples s
                    LEFT JOIN WaterSamplingPoints wp ON s.PointID = wp.Id
                    LEFT JOIN SamplingPoints p ON s.PointID = p.PointID
                    LEFT JOIN SampleTests st ON s.SampleID = st.SampleID
                    LEFT JOIN Tests t ON st.TestID = t.TestID
                    WHERE ISNULL(s.SampleType, '') NOT LIKE '%Environment%'
                      AND ISNULL(s.SampleType, '') NOT LIKE '%Environmental%'
                      AND ISNULL(s.SampleNumber, '') NOT LIKE '%TEST%'";

                var parameters = new List<SqlParameter>();
                int? pointId = GetSelectedPointId();

                if (pointId.HasValue)
                {
                    query += " AND s.PointID = @pointId";
                    parameters.Add(new SqlParameter("@pointId", pointId.Value));
                }

                if (category != "All")
                {
                    query += " AND CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.TestCategorySnapshot,N'') ELSE ISNULL(t.TestCategory,N'') END = @category";
                    parameters.Add(new SqlParameter("@category", category));
                }

                if (testId.HasValue)
                {
                    query += " AND st.TestID = @testId";
                    parameters.Add(new SqlParameter("@testId", testId.Value));
                }

                if (dpDateFrom != null && dpDateFrom.SelectedDate.HasValue)
                {
                    query += " AND s.SamplingDateTime >= @dateFrom";
                    parameters.Add(new SqlParameter("@dateFrom", dpDateFrom.SelectedDate.Value));
                }

                if (dpDateTo != null && dpDateTo.SelectedDate.HasValue)
                {
                    query += " AND s.SamplingDateTime < @dateTo";
                    parameters.Add(new SqlParameter("@dateTo", dpDateTo.SelectedDate.Value.AddDays(1)));
                }

                query += " ORDER BY s.SamplingDateTime DESC, s.SampleID DESC, " +
                         "CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.TestCategorySnapshot,N'') ELSE ISNULL(t.TestCategory,N'') END, " +
                         "COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'')";
                DataTable data = DatabaseHelper.ExecuteQuery(query, parameters.ToArray());
                NormalizeInternalWaterRows(data);
                ApplyReportData(data);
                return true;
            }
            catch (Exception ex)
            {
                InvalidateTrendData();
                LogError("Error loading report", ex);
                ShowError("Error loading report:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
                return false;
            }
        }

        private void LoadPrmReportData(string category)
        {
            string selectedTest = GetSelectedTestText();
            string selectedEntity = GetSelectedPointText();

            string query = @"
                SELECT
                    ps.SampleNumber,
                    CASE WHEN ps.SampleCategory IN (N'Raw Material', N'Primary Packaging') THEN ISNULL(ps.MaterialCode, N'') ELSE ISNULL(ps.ProductCode, N'') END AS PointCode,
                    CASE WHEN ps.SampleCategory IN (N'Raw Material', N'Primary Packaging') THEN ISNULL(ps.MaterialName, N'') ELSE ISNULL(ps.ProductName, N'') END AS Location,
                    FORMAT(COALESCE(ps.SampleDateTime, ps.CreatedDate), 'yyyy-MM-dd HH:mm') AS SamplingDate,
                    COALESCE(ps.SampleDateTime, ps.CreatedDate) AS SamplingDateTime,
                    ps.SampleCategory AS SampleType,
                    ps.SampleStatus,
                    ISNULL(pt.TestName, N'') AS TestName,
                    ps.SampleCategory AS TestCategory,
                    ISNULL(pt.Unit, N'') AS Unit,
                    pt.ResultValue,
                    CAST(NULL AS decimal(18,3)) AS AlertLimit,
                    pt.SpecificationLimit AS ActionLimit,
                    CASE
                        WHEN NULLIF(LTRIM(RTRIM(ISNULL(pt.ResultValue, N''))), N'') IS NULL THEN 'Pending'
                        WHEN UPPER(ISNULL(pt.Interpretation, N'')) IN (N'DOES NOT CONFORM', N'NON-CONFORM', N'FAIL', N'OOS', N'ACTION', N'REJECTED') THEN 'FAIL'
                        WHEN UPPER(ISNULL(pt.Interpretation, N'')) IN (N'CHECK REQUIRED', N'ALERT') THEN 'ALERT'
                        WHEN UPPER(ISNULL(pt.Interpretation, N'')) IN (N'CONFORMS', N'CONFORM', N'PASS') THEN 'PASS'
                        ELSE 'NOT ASSESSED'
                    END AS Status,
                    ps.SampledBy
                FROM dbo.PRM_Samples ps
                LEFT JOIN dbo.PRM_SampleTests pt ON pt.SampleID = ps.SampleID
                WHERE " + BuildPrmCategorySql("ps.SampleCategory");

            var parameters = new List<SqlParameter>
            {
                new SqlParameter("@category", category)
            };

            if (!string.IsNullOrWhiteSpace(selectedEntity) &&
                selectedEntity != "All Materials / Products" &&
                selectedEntity != "All Sampling Points / Areas")
            {
                query += @" AND COALESCE(NULLIF(LTRIM(RTRIM(ps.MaterialName)), ''),
                                               NULLIF(LTRIM(RTRIM(ps.ProductName)), ''),
                                               NULLIF(LTRIM(RTRIM(ps.MaterialCode)), ''),
                                               NULLIF(LTRIM(RTRIM(ps.ProductCode)), ''),
                                               ps.SampleNumber) = @entity";
                parameters.Add(new SqlParameter("@entity", selectedEntity));
            }

            if (!string.IsNullOrWhiteSpace(selectedTest) && selectedTest != "All")
            {
                query += " AND LTRIM(RTRIM(pt.TestName)) = LTRIM(RTRIM(@testName))";
                parameters.Add(new SqlParameter("@testName", selectedTest));
            }

            if (dpDateFrom != null && dpDateFrom.SelectedDate.HasValue)
            {
                query += " AND COALESCE(ps.SampleDateTime, ps.CreatedDate) >= @dateFrom";
                parameters.Add(new SqlParameter("@dateFrom", dpDateFrom.SelectedDate.Value));
            }

            if (dpDateTo != null && dpDateTo.SelectedDate.HasValue)
            {
                query += " AND COALESCE(ps.SampleDateTime, ps.CreatedDate) < @dateTo";
                parameters.Add(new SqlParameter("@dateTo", dpDateTo.SelectedDate.Value.AddDays(1)));
            }

            query += " ORDER BY COALESCE(ps.SampleDateTime, ps.CreatedDate) DESC, ps.SampleID DESC, pt.SortOrder, pt.SampleTestID";
            ApplyReportData(DatabaseHelper.ExecuteQuery(query, parameters.ToArray()));
        }

        private void ApplyReportData(DataTable data)
        {
            currentDataTable = data ?? new DataTable();

            if (dgTrends != null)
                dgTrends.ItemsSource = currentDataTable.DefaultView;

            UpdateStatusCounters();
            SetStatus($"Loaded {currentDataTable.Rows.Count} record(s), including {GetPendingCount()} pending record(s).", false);

            if (currentDataTable.Rows.Count == 0)
                ShowInfo("No data found for selected filters.");
        }

        /// <summary>
        /// Updates the status counters on the UI
        /// </summary>
        private void UpdateStatusCounters()
        {
            int passCount = 0;
            int alertCount = 0;
            int failCount = 0;
            int pendingCount = 0, notAssessedCount = 0;

            var distinctSamples = new HashSet<string>();
            var distinctTestTypes = new HashSet<string>();

            foreach (DataRow row in currentDataTable.Rows)
            {
                string status = row["Status"] == DBNull.Value ? "Pending" : row["Status"].ToString();

                if (status == "PASS")
                    passCount++;
                else if (status == "ALERT")
                    alertCount++;
                else if (status == "FAIL")
                    failCount++;
                else if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                    pendingCount++;
                else
                    notAssessedCount++;

                string sampleNumber = row["SampleNumber"] == DBNull.Value ? "" : row["SampleNumber"].ToString();
                string testName = row["TestName"] == DBNull.Value ? "" : row["TestName"].ToString();

                if (!string.IsNullOrWhiteSpace(sampleNumber))
                    distinctSamples.Add(sampleNumber);

                if (!string.IsNullOrWhiteSpace(testName))
                    distinctTestTypes.Add(testName);
            }

            lblPassCount.Text = $"PASS: {passCount}";
            lblAlertCount.Text = $"ALERT: {alertCount}";
            lblFailCount.Text = $"FAIL: {failCount}";
            lblTotalSamples.Text = $"Samples: {distinctSamples.Count}";
            lblTotalResults.Text = $"Results: {currentDataTable.Rows.Count} | PENDING: {pendingCount} | NOT ASSESSED: {notAssessedCount}";
            lblTestTypes.Text = $"Test Types: {distinctTestTypes.Count}";
        }

        private int GetPendingCount()
        {
            int count = 0;
            foreach (DataRow row in currentDataTable.Rows)
            {
                string status = row["Status"] == DBNull.Value ? "Pending" : row["Status"].ToString();
                if (status == "Pending")
                    count++;
            }
            return count;
        }

        #endregion

        #region Export Methods

        /// <summary>
        /// Export Chart as PNG
        /// </summary>
        private void BtnExportChart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!UserHasPermission("ExportReports"))
                {
                    ShowInfo("You do not have permission to export charts.");
                    return;
                }

                if (TrendChart == null || TrendChart.Model == null)
                {
                    ShowInfo("No chart to export. Please load data first.");
                    return;
                }

                DateTime exportTime = GetAuthoritativeTrendTime();
                string fileName = $"PharmaLIMS_Chart_{exportTime:yyyyMMdd_HHmmss}.png";
                string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                using (var stream = File.Create(filePath))
                {
                    var exporter = new PngExporter
                    {
                        Width = 1600,
                        Height = 650
                    };
                    exporter.Export(TrendChart.Model, stream);
                }

                ShowInfo($"Chart exported successfully!\n\nFile saved to:\n{filePath}");
                SetStatus($"Chart exported to {fileName}", false);
            }
            catch (Exception ex)
            {
                LogError("Error exporting chart", ex);
                ShowError("Error exporting chart:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        /// <summary>
        /// Export data as CSV
        /// </summary>
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!UserHasPermission("ExportReports"))
                {
                    ShowInfo("You do not have permission to export data.");
                    return;
                }

                if (currentDataTable == null || currentDataTable.Rows.Count == 0)
                {
                    ShowInfo("No data to export. Please load data first.");
                    return;
                }

                DateTime exportTime = GetAuthoritativeTrendTime();
                string fileName = $"PharmaLIMS_Report_{exportTime:yyyyMMdd_HHmmss}.csv";
                string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                using (StreamWriter sw = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    // Write headers
                    for (int i = 0; i < currentDataTable.Columns.Count; i++)
                    {
                        sw.Write(EscapeCsv(currentDataTable.Columns[i].ColumnName));
                        if (i < currentDataTable.Columns.Count - 1)
                            sw.Write(",");
                    }
                    sw.WriteLine();

                    // Write data
                    foreach (DataRow row in currentDataTable.Rows)
                    {
                        for (int i = 0; i < currentDataTable.Columns.Count; i++)
                        {
                            sw.Write(EscapeCsv(row[i]?.ToString() ?? ""));
                            if (i < currentDataTable.Columns.Count - 1)
                                sw.Write(",");
                        }
                        sw.WriteLine();
                    }
                }

                string exportHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
                DatabaseHelper.AddAuditTrailAdvanced(
                    "AuditTrail", 0, "Uncontrolled Trend CSV Export", string.Empty,
                    fileName + "; SHA256=" + exportHash + "; Rows=" + currentDataTable.Rows.Count.ToString(CultureInfo.InvariantCulture),
                    "User-requested working copy export; CSV is not a controlled GMP record.",
                    Login.CurrentUser, "Export", null, fileName, "Reports / Trends");

                ShowInfo($"UNCONTROLLED COPY exported successfully!\n\nFile saved to:\n{filePath}\nSHA-256: {exportHash}");
                SetStatus($"Exported to {fileName}", false);
            }
            catch (Exception ex)
            {
                LogError("Error exporting data", ex);
                ShowError("Error exporting:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        /// <summary>
        /// Rebuilds the chart and report table immediately before PDF export.
        /// This prevents exporting an old in-memory TrendChart.Model after code or filter changes.
        /// </summary>
        private bool EnsureCurrentTrendDataForPdf()
        {
            try
            {
                if (externalTrendBatchId.HasValue && !string.IsNullOrWhiteSpace(externalTrendParameter))
                {
                    SetStatus("Refreshing approved external trend before PDF export...", true);
                    return LoadExternalTrendBatch(externalTrendBatchId.Value, externalTrendParameter) &&
                           currentDataTable != null && currentDataTable.Rows.Count > 0 &&
                           TrendChart != null && TrendChart.Model != null;
                }

                if (!ValidateInputs())
                    return false;

                if (cboViewMode == null || cboViewMode.SelectedItem == null)
                {
                    ShowInfo("Please select View Mode.");
                    return false;
                }

                ComboBoxItem selectedMode = cboViewMode.SelectedItem as ComboBoxItem;
                string viewMode = selectedMode == null ? "" : (selectedMode.Content?.ToString() ?? "");

                if (viewMode != "Trend by Test")
                {
                    ShowInfo("PDF trend report export requires View Mode: Trend by Test.");
                    return false;
                }

                SetStatus("Refreshing trend chart and report data before PDF export...", true);

                bool chartLoaded = LoadTrendChart();
                if (!chartLoaded)
                {
                    SetStatus("PDF export cancelled: trend chart could not be refreshed.", false);
                    return false;
                }

                if (currentDataTable == null || currentDataTable.Rows.Count == 0)
                {
                    ShowInfo("No report data available for PDF export after refresh.");
                    return false;
                }

                if (TrendChart == null || TrendChart.Model == null)
                {
                    ShowInfo("No chart available for PDF export after refresh.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogError("Error refreshing trend data before PDF export", ex);
                ShowError("Error refreshing trend data before PDF export:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
                return false;
            }
        }

        /// <summary>
        /// Export as PDF with full formatting
        /// </summary>
        private async void BtnExportPDF_Click(object sender, RoutedEventArgs e)
        {
            bool windowDisabledForExport = false;
            try
            {
                if (!UserHasPermission("ExportPDF")) { ShowInfo("PDF export permission is required."); return; }
                if (chkShowLimits != null) chkShowLimits.IsChecked = true;
                if (!EnsureCurrentTrendDataForPdf()) return;

                DateTime generatedOn = GetAuthoritativeTrendTime();
                string reportNumber = GenerateReportNumber();
                string filePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"PharmaLIMS_Report_{reportNumber}.pdf");

                bool hasWaterProfile = currentDataTable.Columns.Contains("WaterProfile");
                bool hasSampleType = currentDataTable.Columns.Contains("SampleType");
                string identityColumn = hasWaterProfile ? "WaterProfile" : hasSampleType ? "SampleType" : "Location";
                string[] columns = { "SampleNumber", "PointCode", identityColumn, "SamplingDate", "TestName", "MethodName", "ResultValue", "Unit", "AlertLimit", "ActionLimit", "Status" };

                // Snapshot all UI-owned report inputs before leaving the dispatcher thread.
                // The window is temporarily disabled so the PlotModel collection cannot be
                // changed by another user action while OxyPlot/PDF rendering runs in background.
                PlotModel[] reportModels = _reportTrendModels.ToArray();
                DataTable reportData = currentDataTable.Copy();
                string reportScope = CurrentReportScope();
                string generatedBy = GetCurrentUserName();
                string logoPath = FindReportLogoPath();
                string[] statistics = BuildStatisticsLines().ToArray();
                string interpretation = BuildInterpretation();

                SetStatus("Rendering trend charts and PDF in background...", true);
                IsEnabled = false;
                windowDisabledForExport = true;

                TrendReportChart[] charts = await RunOnStaThreadAsync(() =>
                    reportModels
                        .Select(model => new TrendReportChart(
                            model.Title ?? "Trend",
                            ExportTrendModel(model)))
                        .ToArray());

                await Task.Run(() =>
                    TrendPdfReportWriter.Write(
                        filePath,
                        "REPORTS AND TRENDS ANALYSIS",
                        reportNumber,
                        reportScope,
                        generatedBy,
                        generatedOn,
                        logoPath,
                        statistics,
                        charts,
                        new[] { new TrendReportTable("Full individual results", reportData, columns) },
                        interpretation));

                DatabaseHelper.AddAuditTrailAdvanced(
                    "AuditTrail",
                    0,
                    "Trend PDF Review Draft Export",
                    "",
                    Path.GetFileName(filePath),
                    "Source rows=" + reportData.Rows.Count + "; Charts=" + charts.Length,
                    Login.CurrentUser);

                ShowInfo("Medica trend review draft exported with all curves and results:\n\n" + filePath);
                SetStatus("PDF exported successfully.", false);
            }
            catch (Exception ex)
            {
                LogError("Error exporting PDF", ex);
                ShowError("PDF export failed:\n\n" + UserFacingError.SafeMessage(ex));
            }
            finally
            {
                if (windowDisabledForExport)
                    IsEnabled = true;
            }
        }

        private static Task<T> RunOnStaThreadAsync<T>(Func<T> work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Thread thread = new Thread(() =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "PharmaLIMS Trend Export"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        #endregion
    }
}
