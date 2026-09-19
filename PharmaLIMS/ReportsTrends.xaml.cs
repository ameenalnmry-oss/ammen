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

            // Rebuild using the same selected filters so the checkbox has a real, immediate effect.
            if (externalTrendBatchId.HasValue && !string.IsNullOrWhiteSpace(externalTrendParameter))
                LoadExternalTrendBatch(externalTrendBatchId.Value, externalTrendParameter);
            else
                LoadTrendChart();
        }

        /// <summary>
        /// Main Load Data button handler
        /// </summary>
        private void BtnLoadData_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ClearExternalTrendContext();
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

                LoadReportData();
                SetStatus("Data loaded successfully.", false);
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

                currentDataTable = null;
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

                var points = new List<DataPoint>();
                var pointsByEntity = new Dictionary<string, List<DataPoint>>(StringComparer.OrdinalIgnoreCase);
                var qualifiedPointsByEntity = new Dictionary<string, List<ScatterPoint>>(StringComparer.OrdinalIgnoreCase);
                var alertPointsByClassification = new Dictionary<string, List<DataPoint>>(StringComparer.OrdinalIgnoreCase);
                var actionPointsByClassification = new Dictionary<string, List<DataPoint>>(StringComparer.OrdinalIgnoreCase);
                DateTime? minimumDate = null;
                DateTime? maximumDate = null;

                foreach (DataRow row in data.Rows)
                {
                    DateTime date;
                    object dateValue = row["SamplingDateTime"];
                    if (dateValue is DateTimeOffset offset)
                        date = offset.DateTime;
                    else if (dateValue is DateTime nativeDate)
                        date = nativeDate;
                    else if (!DateTime.TryParse(Convert.ToString(dateValue, CultureInfo.InvariantCulture), out date))
                        continue;

                    if (!TryGetDouble(row["ResultValue"], out double result))
                        continue;

                    var point = new DataPoint(DateTimeAxis.ToDouble(date), result);
                    // Keep every numeric boundary in the axis-domain calculation, but never
                    // connect a qualified/censored observation (<, <=, >, >=) as if it were
                    // an exact numeric measurement.
                    points.Add(point);
                    string entityCode = Convert.ToString(row["PointCode"], CultureInfo.InvariantCulture) ?? "External";
                    string classification = Convert.ToString(row["AreaClassification"], CultureInfo.InvariantCulture) ?? "Unspecified";
                    string seriesKey = $"{classification} | {entityCode}";
                    string qualifier = row.Table.Columns.Contains("ResultQualifier")
                        ? (Convert.ToString(row["ResultQualifier"], CultureInfo.InvariantCulture) ?? string.Empty).Trim()
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(qualifier))
                    {
                        if (!pointsByEntity.TryGetValue(seriesKey, out List<DataPoint> entityPoints))
                        {
                            entityPoints = new List<DataPoint>();
                            pointsByEntity[seriesKey] = entityPoints;
                        }
                        entityPoints.Add(point);
                    }
                    else
                    {
                        if (!qualifiedPointsByEntity.TryGetValue(seriesKey, out List<ScatterPoint> qualifiedPoints))
                        {
                            qualifiedPoints = new List<ScatterPoint>();
                            qualifiedPointsByEntity[seriesKey] = qualifiedPoints;
                        }
                        qualifiedPoints.Add(new ScatterPoint(point.X, point.Y, 5, double.NaN, qualifier));
                    }
                    minimumDate = !minimumDate.HasValue || date < minimumDate.Value ? date : minimumDate;
                    maximumDate = !maximumDate.HasValue || date > maximumDate.Value ? date : maximumDate;

                    if (TryGetDouble(row["AlertLimit"], out double alert))
                    {
                        if (!alertPointsByClassification.TryGetValue(classification, out List<DataPoint> limitPoints))
                        {
                            limitPoints = new List<DataPoint>();
                            alertPointsByClassification[classification] = limitPoints;
                        }
                        limitPoints.Add(new DataPoint(DateTimeAxis.ToDouble(date), alert));
                    }
                    if (TryGetDouble(row["ActionLimit"], out double action))
                    {
                        if (!actionPointsByClassification.TryGetValue(classification, out List<DataPoint> limitPoints))
                        {
                            limitPoints = new List<DataPoint>();
                            actionPointsByClassification[classification] = limitPoints;
                        }
                        limitPoints.Add(new DataPoint(DateTimeAxis.ToDouble(date), action));
                    }
                }

                if (points.Count == 0 || !minimumDate.HasValue || !maximumDate.HasValue)
                {
                    ShowInfo("The approved import contains no numeric rows that can be plotted.");
                    return false;
                }

                string unit = Convert.ToString(data.Rows[0]["Unit"], CultureInfo.InvariantCulture) ?? string.Empty;
                string importNumber = Convert.ToString(data.Rows[0]["ImportNumber"], CultureInfo.InvariantCulture) ?? batchId.ToString(CultureInfo.InvariantCulture);
                string module = Convert.ToString(data.Rows[0]["ModuleName"], CultureInfo.InvariantCulture) ?? "External";
                string title = $"{module} External Data - {parameterName} [{importNumber}]";

                currentDataTable = data;
                dgTrends.ItemsSource = data.DefaultView;
                TrendChart.Model = null;
                PlotModel model = BuildTrendChartModel(parameterName, unit, points, null, null, minimumDate.Value, maximumDate.Value);
                model.Title = "Trend Chart - " + title;

                if (model.Series.Count > 0)
                    model.Series.RemoveAt(0);

                OxyColor[] palette =
                {
                    OxyColors.Blue, OxyColors.SeaGreen, OxyColors.DarkOrange, OxyColors.Purple,
                    OxyColors.Brown, OxyColors.Teal, OxyColors.DeepPink, OxyColors.SlateGray
                };
                int colorIndex = 0;
                foreach (KeyValuePair<string, List<DataPoint>> entity in pointsByEntity.OrderBy(item => item.Key))
                {
                    var series = new LineSeries
                    {
                        Title = entity.Key,
                        Color = palette[colorIndex++ % palette.Length],
                        StrokeThickness = 2,
                        MarkerType = MarkerType.Circle,
                        MarkerSize = 5
                    };
                    foreach (DataPoint entityPoint in entity.Value.OrderBy(item => item.X))
                        series.Points.Add(entityPoint);
                    model.Series.Add(series);
                }

                foreach (KeyValuePair<string, List<ScatterPoint>> entity in qualifiedPointsByEntity.OrderBy(item => item.Key))
                {
                    var qualifiedSeries = new ScatterSeries
                    {
                        Title = entity.Key + " qualified/censored boundary",
                        MarkerType = MarkerType.Diamond,
                        MarkerSize = 5,
                        MarkerFill = OxyColors.Purple,
                        MarkerStroke = OxyColors.Purple,
                        TrackerFormatString = "{0}\nDate: {2:yyyy-MM-dd HH:mm}\nReported boundary: {4:0.###}\nQualifier: {Tag}"
                    };
                    foreach (ScatterPoint qualifiedPoint in entity.Value.OrderBy(item => item.X))
                        qualifiedSeries.Points.Add(qualifiedPoint);
                    model.Series.Add(qualifiedSeries);
                }

                bool showLimits = chkShowLimits?.IsChecked == true;
                if (showLimits)
                {
                    foreach (KeyValuePair<string, List<DataPoint>> item in alertPointsByClassification.OrderBy(item => item.Key))
                    {
                        var alertSeries = new LineSeries
                        {
                            Title = $"{item.Key} Alert Limit (historical)",
                            Color = OxyColors.DarkOrange,
                            LineStyle = LineStyle.Dash,
                            StrokeThickness = 1.7,
                            MarkerType = MarkerType.None
                        };
                        foreach (DataPoint limitPoint in item.Value.OrderBy(point => point.X))
                            alertSeries.Points.Add(limitPoint);
                        model.Series.Add(alertSeries);
                    }
                    foreach (KeyValuePair<string, List<DataPoint>> item in actionPointsByClassification.OrderBy(item => item.Key))
                    {
                        var actionSeries = new LineSeries
                        {
                            Title = $"{item.Key} Action Limit (historical)",
                            Color = OxyColors.Red,
                            LineStyle = LineStyle.Dot,
                            StrokeThickness = 1.7,
                            MarkerType = MarkerType.None
                        };
                        foreach (DataPoint limitPoint in item.Value.OrderBy(point => point.X))
                            actionSeries.Points.Add(limitPoint);
                        model.Series.Add(actionSeries);
                    }
                }

                TrendChart.Model = model;
                externalTrendBatchId = batchId;
                externalTrendParameter = parameterName;

                UpdateStatusCounters();
                int qualifiedCount = qualifiedPointsByEntity.Values.Sum(list => list.Count);
                int exactCount = pointsByEntity.Values.Sum(list => list.Count);
                SetStatus($"Approved external trend loaded: {importNumber} | {exactCount:N0} exact numeric result(s)" +
                    (qualifiedCount > 0 ? $"; {qualifiedCount:N0} qualified/censored boundary result(s) shown separately and excluded from exact statistics." : "."), false);
                return true;
            }
            catch (Exception ex)
            {
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
            return category == "Raw Material" ||
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

            text = text.Replace(",", ".");

            if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
            {
                Match match = Regex.Match(text, @"[-+]?\d+(?:\.\d+)?");
                if (!match.Success || !double.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
                    return false;
            }

            if (double.IsNaN(result) || double.IsInfinity(result))
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
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Only an explicit upper-limit expression may become a chart limit.
            // Qualitative requirements such as Absent/Complies remain non-numeric.
            if (!Regex.IsMatch(text, @"\b(NMT|NOT\s+MORE\s+THAN|MAX(?:IMUM)?)\b|<=|≤", RegexOptions.IgnoreCase))
                return false;

            Match match = Regex.Match(text, @"[-+]?\d+(?:[\.,]\d+)?", RegexOptions.CultureInvariant);
            return match.Success &&
                   double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out limit) &&
                   !double.IsNaN(limit) && !double.IsInfinity(limit);
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

            text = text.Replace(",", ".");
            return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed) &&
                   !double.IsNaN(parsed) && !double.IsInfinity(parsed)
                ? parsed
                : null;
        }

        private static string EvaluateWaterTrendStatus(string storedStatus, string testName, double resultValue, double? alertLimit, double? actionLimit)
        {
            if (IsPhReportTest(testName))
            {
                if (alertLimit.HasValue && resultValue < alertLimit.Value)
                    return "FAIL";
                if (actionLimit.HasValue && resultValue > actionLimit.Value)
                    return "FAIL";
                if (alertLimit.HasValue || actionLimit.HasValue)
                    return "PASS";
            }
            else
            {
                if (actionLimit.HasValue && resultValue > actionLimit.Value)
                    return "FAIL";
                if (alertLimit.HasValue && resultValue > alertLimit.Value)
                    return "ALERT";
                if (alertLimit.HasValue || actionLimit.HasValue)
                    return "PASS";
            }

            // If no numeric trend limit exists, retain the controlled stored result
            // classification rather than inventing a PASS conclusion.
            string normalized = (storedStatus ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized is "PASS" or "ALERT")
                return normalized;
            if (normalized is "FAIL" or "OOS" or "ACTION")
                return "FAIL";
            return "Pending";
        }

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

                if (row["ResultValue"] == DBNull.Value || !TryGetNullableDouble(row["ResultValue"]).HasValue)
                {
                    row["Status"] = "Pending";
                }
                else
                {
                    double resultValue = TryGetNullableDouble(row["ResultValue"])!.Value;
                    string storedStatus = Convert.ToString(row["_StoredStatus"], CultureInfo.InvariantCulture) ?? string.Empty;
                    row["Status"] = EvaluateWaterTrendStatus(storedStatus, testName, resultValue, alert, action);
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
            try
            {
                string category = GetSelectedCategory();

                if (category == "All")
                {
                    ShowInfo("Please select a specific Test Category first, then select Test Name.");
                    return false;
                }

                if (IsPrmCategory(category))
                    return LoadPrmTrendChart(category);

                int? testId = GetSelectedTestId();
                if (!testId.HasValue)
                {
                    ShowInfo("Please select a specific Test Name for Trend Chart.");
                    return false;
                }

                string query = @"
                    SELECT
                        COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'') AS TestName,
                        s.SamplingDateTime,
                        st.ResultValue,
                        s.SampleType AS _SampleType,
                        COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode,p.PointCode,N'') AS PointCode,
                        st.AlertLimitSnapshot AS _SnapshotAlert,
                        st.ActionLimitSnapshot AS _SnapshotAction,
                        t.AlertLimit AS _MasterAlert,
                        t.ActionLimit AS _MasterAction,
                        st.ResultStatus AS _StoredStatus,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL
                                   OR st.AlertLimitSnapshot IS NOT NULL
                                   OR st.ActionLimitSnapshot IS NOT NULL
                             THEN 1 ELSE 0 END AS _HasSpecSnapshot,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.UnitSnapshot ELSE t.Unit END AS Unit
                    FROM SampleTests st
                    INNER JOIN Samples s ON st.SampleID = s.SampleID
                    LEFT JOIN Tests t ON st.TestID = t.TestID
                    LEFT JOIN WaterSamplingPoints wp ON s.PointID = wp.Id
                    LEFT JOIN SamplingPoints p ON s.PointID = p.PointID
                    WHERE st.TestID = @testId
                      AND st.ResultValue IS NOT NULL
                      AND ISNULL(s.SampleType,N'') NOT LIKE '%Environment%'";

                var parameters = new List<SqlParameter>
                {
                    new SqlParameter("@testId", testId.Value)
                };

                int? pointId = GetSelectedPointId();
                if (pointId.HasValue)
                {
                    query += " AND s.PointID = @pointId";
                    parameters.Add(new SqlParameter("@pointId", pointId.Value));
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

                query += " ORDER BY s.SamplingDateTime ASC";
                DataTable dt = DatabaseHelper.ExecuteQuery(query, parameters.ToArray());
                if (dt.Rows.Count == 0)
                {
                    ShowInfo("No data found for the selected criteria.");
                    return false;
                }

                // Normalize limits/status with the same PW/PTW rules used by ResultsEntry.
                if (!dt.Columns.Contains("WaterProfile")) dt.Columns.Add("WaterProfile", typeof(string));
                if (!dt.Columns.Contains("AlertLimit")) dt.Columns.Add("AlertLimit", typeof(double));
                if (!dt.Columns.Contains("ActionLimit")) dt.Columns.Add("ActionLimit", typeof(double));
                if (!dt.Columns.Contains("Status")) dt.Columns.Add("Status", typeof(string));

                foreach (DataRow row in dt.Rows)
                {
                    string sampleType = Convert.ToString(row["_SampleType"], CultureInfo.InvariantCulture) ?? string.Empty;
                    string pointCode = Convert.ToString(row["PointCode"], CultureInfo.InvariantCulture) ?? string.Empty;
                    string testName = Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? string.Empty;
                    string profile = NormalizeWaterProfile(sampleType, pointCode);
                    row["WaterProfile"] = profile;
                    bool hasSpecificationSnapshot = row["_HasSpecSnapshot"] != DBNull.Value &&
                        Convert.ToInt32(row["_HasSpecSnapshot"], CultureInfo.InvariantCulture) == 1;
                    (double? alert, double? action) = GetEffectiveWaterLimits(profile, pointCode, testName,
                        row["_SnapshotAlert"], row["_SnapshotAction"], row["_MasterAlert"], row["_MasterAction"], hasSpecificationSnapshot);
                    row["AlertLimit"] = alert.HasValue ? alert.Value : DBNull.Value;
                    row["ActionLimit"] = action.HasValue ? action.Value : DBNull.Value;
                    double value = Convert.ToDouble(row["ResultValue"], CultureInfo.InvariantCulture);
                    row["Status"] = EvaluateWaterTrendStatus(Convert.ToString(row["_StoredStatus"], CultureInfo.InvariantCulture) ?? string.Empty,
                        testName, value, alert, action);
                }

                string selectedTestName = Convert.ToString(dt.Rows[0]["TestName"], CultureInfo.InvariantCulture) ?? "Trend";
                List<string> profiles = dt.Rows.Cast<DataRow>()
                    .Select(row => Convert.ToString(row["WaterProfile"], CultureInfo.InvariantCulture) ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                PlotModel plotModel;
                if (profiles.Count > 1)
                {
                    plotModel = BuildWaterProfileTrendChartModel(selectedTestName, dt, profiles);
                }
                else
                {
                    var points = new List<DataPoint>();
                    DateTime? minDate = null;
                    DateTime? maxDate = null;
                    double? alert = null;
                    double? action = null;
                    string unit = Convert.ToString(dt.Rows[0]["Unit"], CultureInfo.InvariantCulture) ?? string.Empty;

                    foreach (DataRow row in dt.Rows)
                    {
                        if (!DateTime.TryParse(Convert.ToString(row["SamplingDateTime"], CultureInfo.InvariantCulture), out DateTime date) ||
                            !TryGetDouble(row["ResultValue"], out double resultValue))
                            continue;
                        points.Add(new DataPoint(DateTimeAxis.ToDouble(date), resultValue));
                        minDate = !minDate.HasValue || date < minDate.Value ? date : minDate;
                        maxDate = !maxDate.HasValue || date > maxDate.Value ? date : maxDate;
                        alert ??= TryGetNullableDouble(row["AlertLimit"]);
                        action ??= TryGetNullableDouble(row["ActionLimit"]);
                    }

                    if (points.Count == 0 || !minDate.HasValue || !maxDate.HasValue)
                    {
                        ShowInfo("No numeric result values found for this test. Trend chart requires numeric results.");
                        return false;
                    }
                    string suffix = profiles.Count == 1 ? $" ({profiles[0]})" : string.Empty;
                    plotModel = BuildTrendChartModel(selectedTestName + suffix, unit, points, alert, action, minDate.Value, maxDate.Value);
                }

                TrendChart.Model = null;
                TrendChart.Model = plotModel;
                SetStatus(profiles.Count > 1
                    ? $"Chart loaded with separate PW/PTW scales: {dt.Rows.Count:N0} numeric result(s)."
                    : $"Chart loaded: {dt.Rows.Count:N0} numeric result(s) for {selectedTestName}.", false);
                return true;
            }
            catch (Exception ex)
            {
                LogError("Error loading trend chart", ex);
                ShowError("Error loading trend chart:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
                return false;
            }
        }

        /// <summary>
        /// Builds the PlotModel for the trend chart
        /// </summary>
        private bool LoadPrmTrendChart(string category)
        {
            string testName = GetSelectedTestText();
            if (string.IsNullOrWhiteSpace(testName) || testName == "All")
            {
                ShowInfo("Please select a specific PRM Test Name for Trend Chart.");
                return false;
            }

            bool hasSpecificationLimit = Convert.ToInt32(DatabaseHelper.ExecuteScalar(
                "SELECT CASE WHEN COL_LENGTH(N'dbo.PRM_SampleTests', N'SpecificationLimit') IS NULL THEN 0 ELSE 1 END")) == 1;

            string actionLimitExpression = hasSpecificationLimit
                ? "CONVERT(nvarchar(500), pt.SpecificationLimit)"
                : "CAST(NULL AS nvarchar(500))";

            string query = @"
                SELECT
                    ps.SampleNumber,
                    LTRIM(RTRIM(pt.TestName)) AS TestName,
                    CASE
                        WHEN UPPER(ISNULL(pt.Interpretation, '')) IN ('FAIL', 'OOS', 'ACTION', 'REJECTED') THEN 'FAIL'
                        WHEN UPPER(ISNULL(pt.Interpretation, '')) = 'ALERT' THEN 'ALERT'
                        WHEN ISNULL(LTRIM(RTRIM(pt.ResultValue)), '') = '' THEN 'Pending'
                        ELSE 'PASS'
                    END AS Status,
                    COALESCE(CAST(pt.EnteredDate AS datetime), CAST(ps.SampleDateTime AS datetime), CAST(ps.CreatedDate AS datetime)) AS SamplingDateTime,
                    pt.ResultValue,
                    CAST(NULL AS float) AS AlertLimit,
                    " + actionLimitExpression + @" AS ActionLimit,
                    ISNULL(pt.Unit, '') AS Unit
                FROM dbo.PRM_SampleTests pt
                INNER JOIN dbo.PRM_Samples ps ON ps.SampleID = pt.SampleID
                WHERE " + BuildPrmCategorySql("ps.SampleCategory") + @"
                  AND LTRIM(RTRIM(pt.TestName)) = LTRIM(RTRIM(@testName))
                  AND ISNULL(LTRIM(RTRIM(pt.ResultValue)), '') <> ''";

            var parameters = new List<SqlParameter>
            {
                new SqlParameter("@category", category),
                new SqlParameter("@testName", testName)
            };

            string selectedEntity = GetSelectedPointText();
            if (!string.IsNullOrWhiteSpace(selectedEntity) &&
                selectedEntity != "All Materials / Products" &&
                selectedEntity != "All Sampling Points / Areas" &&
                selectedEntity != "All Sampling Points")
            {
                query += @" AND COALESCE(NULLIF(LTRIM(RTRIM(ps.MaterialName)), ''),
                                         NULLIF(LTRIM(RTRIM(ps.ProductName)), ''),
                                         NULLIF(LTRIM(RTRIM(ps.MaterialCode)), ''),
                                         NULLIF(LTRIM(RTRIM(ps.ProductCode)), ''),
                                         ps.SampleNumber) = @entityName";
                parameters.Add(new SqlParameter("@entityName", selectedEntity));
            }

            if (dpDateFrom?.SelectedDate != null)
            {
                query += " AND COALESCE(pt.EnteredDate, ps.SampleDateTime, ps.CreatedDate) >= @dateFrom";
                parameters.Add(new SqlParameter("@dateFrom", dpDateFrom.SelectedDate.Value));
            }

            if (dpDateTo?.SelectedDate != null)
            {
                query += " AND COALESCE(pt.EnteredDate, ps.SampleDateTime, ps.CreatedDate) < @dateTo";
                parameters.Add(new SqlParameter("@dateTo", dpDateTo.SelectedDate.Value.AddDays(1)));
            }

            query += " ORDER BY COALESCE(pt.EnteredDate, ps.SampleDateTime, ps.CreatedDate) ASC";
            DataTable dt = DatabaseHelper.ExecuteQuery(query, parameters.ToArray());
            if (dt.Rows.Count == 0)
            {
                ShowInfo("No PRM result data found for the selected category, test and date range.");
                return false;
            }

            var points = new List<DataPoint>();
            double? actionLimit = null;
            DateTime? minDate = null;
            DateTime? maxDate = null;
            string unit = dt.Rows[0]["Unit"]?.ToString() ?? string.Empty;

            foreach (DataRow row in dt.Rows)
            {
                if (!DateTime.TryParse(row["SamplingDateTime"]?.ToString(), out DateTime date) ||
                    !TryGetTrendNumericValue(row["ResultValue"], out double value, out _))
                    continue;

                points.Add(new DataPoint(DateTimeAxis.ToDouble(date), value));
                minDate = !minDate.HasValue || date < minDate.Value ? date : minDate;
                maxDate = !maxDate.HasValue || date > maxDate.Value ? date : maxDate;

                if (!actionLimit.HasValue && TryExtractSpecificationUpperLimit(row["ActionLimit"], out double limit))
                    actionLimit = limit;
            }

            currentDataTable = dt;
            if (dgTrends != null)
                dgTrends.ItemsSource = dt.DefaultView;

            if (points.Count == 0 || !minDate.HasValue || !maxDate.HasValue)
            {
                ShowInfo("The PRM records were found and listed, but there are no exact numeric values that can be plotted. Qualified results such as <10 or >25 remain visible in the table but are excluded from quantitative statistics/trend lines because they are censored values.");
                UpdateStatusCounters();
                return false;
            }

            PlotModel model = BuildTrendChartModel(testName + " - " + category, unit, points, null, actionLimit, minDate.Value, maxDate.Value);
            TrendChart.Model = null;
            TrendChart.Model = model;

            UpdateStatusCounters();
            int censoredCount = dt.Rows.Cast<DataRow>().Count(row =>
            {
                _ = TryGetTrendNumericValue(row["ResultValue"], out _, out bool censored);
                return censored;
            });
            SetStatus("PRM trend loaded: " + points.Count + " exact numeric result(s)" +
                (censoredCount > 0 ? $"; {censoredCount} qualified/censored result(s) excluded from quantitative plotting." : "."), false);
            return true;
        }

        private PlotModel BuildTrendChartModel(
            string testName,
            string unit,
            List<DataPoint> points,
            double? alertLimit,
            double? actionLimit,
            DateTime minDataDate,
            DateTime maxDataDate)
        {
            // Calculate date range with padding
            DateTime minDate;
            DateTime maxDate;

            if ((maxDataDate - minDataDate).TotalDays < 2)
            {
                minDate = minDataDate.AddHours(-12);
                maxDate = maxDataDate.AddHours(12);
            }
            else
            {
                minDate = minDataDate.AddDays(-1);
                maxDate = maxDataDate.AddDays(1);
            }

            double minX = DateTimeAxis.ToDouble(minDate);
            double maxX = DateTimeAxis.ToDouble(maxDate);

            var plotModel = new PlotModel
            {
                Title = $"Trend Chart - {testName}",
                TitleFontSize = 14,
                TitleFontWeight = OxyPlot.FontWeights.Bold,
                Background = OxyColors.White,
                PlotAreaBorderColor = OxyColors.DarkGray,
                PlotMargins = new OxyThickness(70, 20, 120, 55)
            };

            // X-Axis (DateTime)
            var dateAxis = new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Sampling Date",
                StringFormat = "MM-dd HH:mm",
                Angle = 45,
                IntervalType = DateTimeIntervalType.Hours,
                MinorIntervalType = DateTimeIntervalType.Hours,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.LightGray,
                Minimum = minX,
                Maximum = maxX,
                IsZoomEnabled = true,
                IsPanEnabled = true
            };
            plotModel.Axes.Add(dateAxis);

            // Y-Axis with dynamic range
            double minY = points.Min(p => p.Y);
            double maxY = points.Max(p => p.Y);

            if (alertLimit.HasValue)
            {
                minY = Math.Min(minY, alertLimit.Value);
                maxY = Math.Max(maxY, alertLimit.Value);
            }

            if (actionLimit.HasValue)
            {
                minY = Math.Min(minY, actionLimit.Value);
                maxY = Math.Max(maxY, actionLimit.Value);
            }

            double padding = (maxY - minY) * 0.15;
            if (padding <= 0)
                padding = 1;

            var valueAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = string.IsNullOrWhiteSpace(unit) ? testName : $"{testName} ({unit})",
                Minimum = Math.Max(0, minY - padding),
                Maximum = maxY + padding,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.LightGray
            };
            plotModel.Axes.Add(valueAxis);

            // Main data series
            var series = new LineSeries
            {
                Title = testName,
                Color = OxyColors.Blue,
                StrokeThickness = 2,
                MarkerType = MarkerType.Circle,
                MarkerSize = 6,
                MarkerFill = OxyColors.Blue
            };

            foreach (DataPoint point in points.OrderBy(p => p.X))
                series.Points.Add(point);

            plotModel.Series.Add(series);

            bool showLimits = chkShowLimits?.IsChecked == true;
            if (showLimits && alertLimit.HasValue &&
                !double.IsNaN(alertLimit.Value) &&
                !double.IsInfinity(alertLimit.Value))
            {
                var alertSeries = new LineSeries
                {
                    Title = "Alert Limit",
                    Color = OxyColors.Orange,
                    StrokeThickness = 2,
                    LineStyle = LineStyle.Dash,
                    MarkerType = MarkerType.None
                };
                alertSeries.Points.Add(new DataPoint(minX, alertLimit.Value));
                alertSeries.Points.Add(new DataPoint(maxX, alertLimit.Value));
                plotModel.Series.Add(alertSeries);
            }

            if (showLimits && actionLimit.HasValue &&
                !double.IsNaN(actionLimit.Value) &&
                !double.IsInfinity(actionLimit.Value))
            {
                var actionSeries = new LineSeries
                {
                    Title = "Action Limit",
                    Color = OxyColors.Red,
                    StrokeThickness = 2,
                    LineStyle = LineStyle.Dash,
                    MarkerType = MarkerType.None
                };
                actionSeries.Points.Add(new DataPoint(minX, actionLimit.Value));
                actionSeries.Points.Add(new DataPoint(maxX, actionLimit.Value));
                plotModel.Series.Add(actionSeries);
            }

            // Legend
            plotModel.Legends.Add(new OxyPlot.Legends.Legend
            {
                LegendTitle = "Legend",
                LegendPosition = OxyPlot.Legends.LegendPosition.RightTop,
                LegendPlacement = OxyPlot.Legends.LegendPlacement.Outside
            });

            return plotModel;
        }

        private PlotModel BuildWaterProfileTrendChartModel(string testName, DataTable data, IReadOnlyList<string> profiles)
        {
            var model = new PlotModel
            {
                Title = $"Trend Chart - {testName} | PW/PTW separated scales",
                TitleFontSize = 14,
                TitleFontWeight = OxyPlot.FontWeights.Bold,
                Background = OxyColors.White,
                PlotAreaBorderColor = OxyColors.DarkGray,
                PlotMargins = new OxyThickness(70, 20, 135, 55)
            };

            List<(DateTime Date, double Value, string Profile, string Unit, double? Alert, double? Action)> values = new();
            foreach (DataRow row in data.Rows)
            {
                if (!DateTime.TryParse(Convert.ToString(row["SamplingDateTime"], CultureInfo.InvariantCulture), out DateTime date) ||
                    !TryGetDouble(row["ResultValue"], out double value))
                    continue;

                values.Add((
                    date,
                    value,
                    Convert.ToString(row["WaterProfile"], CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(row["Unit"], CultureInfo.InvariantCulture) ?? string.Empty,
                    TryGetNullableDouble(row["AlertLimit"]),
                    TryGetNullableDouble(row["ActionLimit"])));
            }

            if (values.Count == 0)
                return model;

            DateTime minDate = values.Min(item => item.Date);
            DateTime maxDate = values.Max(item => item.Date);
            DateTime paddedMin = (maxDate - minDate).TotalDays < 2 ? minDate.AddHours(-12) : minDate.AddDays(-1);
            DateTime paddedMax = (maxDate - minDate).TotalDays < 2 ? maxDate.AddHours(12) : maxDate.AddDays(1);
            double minX = DateTimeAxis.ToDouble(paddedMin);
            double maxX = DateTimeAxis.ToDouble(paddedMax);

            model.Axes.Add(new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Sampling Date",
                StringFormat = "MM-dd HH:mm",
                Angle = 45,
                IntervalType = DateTimeIntervalType.Hours,
                MinorIntervalType = DateTimeIntervalType.Hours,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.LightGray,
                Minimum = minX,
                Maximum = maxX,
                IsZoomEnabled = true,
                IsPanEnabled = true
            });

            string[] orderedProfiles = profiles
                .Where(profile => profile.Equals("PW", StringComparison.OrdinalIgnoreCase) || profile.Equals("PTW", StringComparison.OrdinalIgnoreCase))
                .OrderBy(profile => profile.Equals("PW", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToArray();

            for (int index = 0; index < orderedProfiles.Length; index++)
            {
                string profile = orderedProfiles[index];
                var profileValues = values.Where(item => item.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase)).ToList();
                if (profileValues.Count == 0)
                    continue;

                double minY = profileValues.Min(item => item.Value);
                double maxY = profileValues.Max(item => item.Value);
                double? alert = profileValues.Select(item => item.Alert).FirstOrDefault(value => value.HasValue);
                double? action = profileValues.Select(item => item.Action).FirstOrDefault(value => value.HasValue);
                if (alert.HasValue) { minY = Math.Min(minY, alert.Value); maxY = Math.Max(maxY, alert.Value); }
                if (action.HasValue) { minY = Math.Min(minY, action.Value); maxY = Math.Max(maxY, action.Value); }
                double padding = (maxY - minY) * 0.15;
                if (padding <= 0) padding = Math.Max(1, maxY * 0.10);

                double startPosition = profile.Equals("PTW", StringComparison.OrdinalIgnoreCase) ? 0.00 : 0.55;
                double endPosition = profile.Equals("PTW", StringComparison.OrdinalIgnoreCase) ? 0.45 : 1.00;
                string unit = profileValues.Select(item => item.Unit).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                string axisKey = profile;

                model.Axes.Add(new LinearAxis
                {
                    Key = axisKey,
                    Position = AxisPosition.Left,
                    StartPosition = startPosition,
                    EndPosition = endPosition,
                    Title = string.IsNullOrWhiteSpace(unit) ? profile : $"{profile} ({unit})",
                    Minimum = Math.Max(0, minY - padding),
                    Maximum = maxY + padding,
                    MajorGridlineStyle = LineStyle.Solid,
                    MajorGridlineColor = OxyColors.LightGray
                });

                var series = new LineSeries
                {
                    Title = $"{profile} results",
                    YAxisKey = axisKey,
                    Color = profile.Equals("PW", StringComparison.OrdinalIgnoreCase) ? OxyColors.Blue : OxyColors.DarkGreen,
                    StrokeThickness = 2,
                    MarkerType = MarkerType.Circle,
                    MarkerSize = 5,
                    MarkerFill = profile.Equals("PW", StringComparison.OrdinalIgnoreCase) ? OxyColors.Blue : OxyColors.DarkGreen
                };
                foreach (var item in profileValues.OrderBy(item => item.Date))
                    series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(item.Date), item.Value));
                model.Series.Add(series);

                if (chkShowLimits?.IsChecked == true && alert.HasValue)
                {
                    var alertSeries = new LineSeries
                    {
                        Title = $"{profile} Alert",
                        YAxisKey = axisKey,
                        Color = OxyColors.Orange,
                        StrokeThickness = 1.5,
                        LineStyle = LineStyle.Dash,
                        MarkerType = MarkerType.None
                    };
                    alertSeries.Points.Add(new DataPoint(minX, alert.Value));
                    alertSeries.Points.Add(new DataPoint(maxX, alert.Value));
                    model.Series.Add(alertSeries);
                }

                if (chkShowLimits?.IsChecked == true && action.HasValue)
                {
                    var actionSeries = new LineSeries
                    {
                        Title = $"{profile} Action/Spec",
                        YAxisKey = axisKey,
                        Color = OxyColors.Red,
                        StrokeThickness = 1.5,
                        LineStyle = LineStyle.Dash,
                        MarkerType = MarkerType.None
                    };
                    actionSeries.Points.Add(new DataPoint(minX, action.Value));
                    actionSeries.Points.Add(new DataPoint(maxX, action.Value));
                    model.Series.Add(actionSeries);
                }
            }

            model.Legends.Add(new OxyPlot.Legends.Legend
            {
                LegendTitle = "Water Profile",
                LegendPosition = OxyPlot.Legends.LegendPosition.RightTop,
                LegendPlacement = OxyPlot.Legends.LegendPlacement.Outside
            });

            return model;
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
                LogError("Error loading category summary", ex);
                ShowError("Error loading category summary:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        /// <summary>
        /// Loads summary data from the database
        /// </summary>
        private DataTable LoadSummaryData(string groupByColumn)
        {
            // Historical compatibility expression retained for schema/evidence traceability only;
            // v204 summary evaluation below uses frozen snapshot columns and never executes this fallback.
            const string effectiveActionExpression = "CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END";
            _ = effectiveActionExpression;
            string category = GetSelectedCategory();

            if (IsPrmCategory(category))
            {
                string prmGroupExpression = string.IsNullOrEmpty(groupByColumn)
                    ? "''"
                    : "ps.SampleCategory";

                string prmQuery = @"
                    SELECT
                        " + prmGroupExpression + @" AS TestCategory,
                        pt.ResultValue,
                        pt.SpecificationLimit AS ActionLimit,
                        CAST(NULL AS decimal(18, 3)) AS AlertLimit,
                        CASE
                            WHEN UPPER(ISNULL(pt.Interpretation, N'')) IN (N'DOES NOT CONFORM', N'NON-CONFORM', N'FAIL', N'OOS', N'ACTION', N'REJECTED') THEN 'FAIL'
                            WHEN UPPER(ISNULL(pt.Interpretation, N'')) IN (N'CHECK REQUIRED', N'ALERT') THEN 'ALERT'
                            WHEN UPPER(ISNULL(pt.Interpretation, N'')) IN (N'CONFORMS', N'CONFORM', N'PASS') THEN 'PASS'
                            WHEN NULLIF(LTRIM(RTRIM(ISNULL(pt.ResultValue, N''))), N'') IS NULL THEN 'Pending'
                            ELSE 'NOT ASSESSED'
                        END AS Status
                    FROM dbo.PRM_SampleTests pt
                    INNER JOIN dbo.PRM_Samples ps ON ps.SampleID = pt.SampleID
                    WHERE " + BuildPrmCategorySql("ps.SampleCategory");

                var prmParameters = new List<SqlParameter>
                {
                    new SqlParameter("@category", category)
                };

                string selectedEntity = GetSelectedPointText();
                if (!string.IsNullOrWhiteSpace(selectedEntity) &&
                    selectedEntity != "All Materials / Products" &&
                    selectedEntity != "All Sampling Points / Areas")
                {
                    prmQuery += @" AND COALESCE(NULLIF(LTRIM(RTRIM(ps.MaterialName)), ''),
                                                      NULLIF(LTRIM(RTRIM(ps.ProductName)), ''),
                                                      NULLIF(LTRIM(RTRIM(ps.MaterialCode)), ''),
                                                      NULLIF(LTRIM(RTRIM(ps.ProductCode)), ''),
                                                      ps.SampleNumber) = @entity";
                    prmParameters.Add(new SqlParameter("@entity", selectedEntity));
                }

                string selectedPrmTest = GetSelectedTestText();
                if (!string.IsNullOrWhiteSpace(selectedPrmTest) && !selectedPrmTest.Equals("All", StringComparison.OrdinalIgnoreCase))
                {
                    prmQuery += " AND LTRIM(RTRIM(pt.TestName)) = @selectedTest";
                    prmParameters.Add(new SqlParameter("@selectedTest", selectedPrmTest));
                }

                if (dpDateFrom != null && dpDateFrom.SelectedDate.HasValue)
                {
                    prmQuery += " AND COALESCE(ps.SampleDateTime, ps.CreatedDate) >= @dateFrom";
                    prmParameters.Add(new SqlParameter("@dateFrom", dpDateFrom.SelectedDate.Value));
                }

                if (dpDateTo != null && dpDateTo.SelectedDate.HasValue)
                {
                    prmQuery += " AND COALESCE(ps.SampleDateTime, ps.CreatedDate) < @dateTo";
                    prmParameters.Add(new SqlParameter("@dateTo", dpDateTo.SelectedDate.Value.AddDays(1)));
                }

                return DatabaseHelper.ExecuteQuery(prmQuery, prmParameters.ToArray());
            }

            string effectiveCategoryExpression = "CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN COALESCE(NULLIF(st.TestCategorySnapshot,N''),N'Uncategorized') ELSE COALESCE(t.TestCategory,N'Uncategorized') END";

            string query = @"
                SELECT
                    " + (string.IsNullOrEmpty(groupByColumn) ? "''" : effectiveCategoryExpression) + @" AS TestCategory,
                    st.ResultValue,
                    s.SampleType AS _SampleType,
                    COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode,p.PointCode,N'') AS PointCode,
                    COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'') AS TestName,
                    st.AlertLimitSnapshot AS _SnapshotAlert,
                    st.ActionLimitSnapshot AS _SnapshotAction,
                    t.AlertLimit AS _MasterAlert,
                    t.ActionLimit AS _MasterAction,
                    st.ResultStatus AS _StoredStatus,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL
                               OR st.AlertLimitSnapshot IS NOT NULL
                               OR st.ActionLimitSnapshot IS NOT NULL
                         THEN 1 ELSE 0 END AS _HasSpecSnapshot
                FROM SampleTests st
                INNER JOIN Samples s ON st.SampleID = s.SampleID
                LEFT JOIN Tests t ON st.TestID = t.TestID
                LEFT JOIN WaterSamplingPoints wp ON s.PointID = wp.Id
                LEFT JOIN SamplingPoints p ON s.PointID = p.PointID
                WHERE st.ResultValue IS NOT NULL
                  AND ISNULL(s.SampleType,N'') NOT LIKE '%Environment%'
                  AND ISNULL(s.SampleType,N'') NOT LIKE '%Environmental%'";

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
            int? selectedSummaryTestId = GetSelectedTestId();
            if (selectedSummaryTestId.HasValue)
            {
                query += " AND st.TestID = @summaryTestId";
                parameters.Add(new SqlParameter("@summaryTestId", selectedSummaryTestId.Value));
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

            DataTable summaryData = DatabaseHelper.ExecuteQuery(query, parameters.ToArray());
            NormalizeInternalWaterRows(summaryData);
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
        private void LoadReportData()
        {
            try
            {
                string category = GetSelectedCategory();

                if (IsPrmCategory(category))
                {
                    LoadPrmReportData(category);
                    return;
                }

                int? testId = GetSelectedTestId();

                string query = @"
                    SELECT
                        s.SampleNumber,
                        COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode, p.PointCode, '') AS PointCode,
                        COALESCE(NULLIF(s.PointLocationSnapshot,N''),wp.Location, p.Location, '') AS Location,
                        FORMAT(s.SamplingDateTime, 'yyyy-MM-dd HH:mm') AS SamplingDate,
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
            }
            catch (Exception ex)
            {
                LogError("Error loading report", ex);
                ShowError("Error loading report:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private void LoadPrmReportData(string category)
        {
            string selectedTest = GetSelectedTestText();
            string selectedEntity = GetSelectedPointText();

            string query = @"
                SELECT
                    ps.SampleNumber,
                    CASE WHEN ps.SampleCategory = N'Raw Material' THEN ISNULL(ps.MaterialCode, N'') ELSE ISNULL(ps.ProductCode, N'') END AS PointCode,
                    CASE WHEN ps.SampleCategory = N'Raw Material' THEN ISNULL(ps.MaterialName, N'') ELSE ISNULL(ps.ProductName, N'') END AS Location,
                    FORMAT(COALESCE(ps.SampleDateTime, ps.CreatedDate), 'yyyy-MM-dd HH:mm') AS SamplingDate,
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
                        ELSE 'ALERT'
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
            int pendingCount = 0;

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
                else
                    pendingCount++;

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
            lblTotalResults.Text = $"Results: {currentDataTable.Rows.Count} | PENDING: {pendingCount}";
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

                LoadReportData();

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
        private void BtnExportPDF_Click(object sender, RoutedEventArgs e)
        {
            string tempImagePath = null;

            try
            {
                if (!UserHasPermission("ExportPDF"))
                {
                    ShowInfo("You do not have permission to export PDF reports.");
                    return;
                }

                // Controlled PDF reports always include the applicable Alert/Action limit evidence,
                // even if the operator hid limit lines temporarily on the interactive screen.
                if (chkShowLimits != null)
                    chkShowLimits.IsChecked = true;
                if (!EnsureCurrentTrendDataForPdf())
                    return;

                SetStatus("Generating PDF report...", true);

                string reportNumber = GenerateReportNumber();
                DateTime generatedOn = GetAuthoritativeTrendTime();
                string generatedBy = GetCurrentUserName();
                string reportTitle = TrendChart.Model.Title ?? "Trend Analysis Report";

                // Export chart as temporary image
                tempImagePath = Path.Combine(Path.GetTempPath(), $"PharmaLIMS_Chart_{Guid.NewGuid():N}.png");
                using (var stream = File.Create(tempImagePath))
                {
                    var exporter = new PngExporter { Width = 1600, Height = 650 };
                    exporter.Export(TrendChart.Model, stream);
                }

                string fileName = $"PharmaLIMS_Report_{reportNumber}.pdf";
                string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                int waterProfileCount = currentDataTable != null && currentDataTable.Columns.Contains("WaterProfile")
                    ? currentDataTable.Rows.Cast<DataRow>()
                        .Select(row => Convert.ToString(row["WaterProfile"], CultureInfo.InvariantCulture) ?? string.Empty)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                    : 0;
                bool hasWaterProfile = waterProfileCount > 0;
                bool hasSampleType = currentDataTable != null && currentDataTable.Columns.Contains("SampleType");
                string identityColumn = hasWaterProfile ? "WaterProfile" : hasSampleType ? "SampleType" : "Location";
                string[] cols = { "SampleNumber", "PointCode", identityColumn, "SamplingDate", "TestName", "ResultValue", "Unit", "AlertLimit", "ActionLimit", "Status" };
                double[] widths = { 68, 52, 64, 78, 108, 52, 40, 50, 55, 43 };

                int rowCount = currentDataTable == null ? 0 : currentDataTable.Rows.Count;
                int firstPageRows = waterProfileCount > 1 ? 1 : 3;
                int continuationRowsPerPage = 21;
                int finalPageRows = 13;

                // Calculate page distribution
                int remainingAfterFirst = Math.Max(0, rowCount - firstPageRows);
                List<int> continuationRowPlan = new List<int>();

                while (remainingAfterFirst > finalPageRows)
                {
                    int rowsToDrawNow = Math.Min(continuationRowsPerPage, remainingAfterFirst - finalPageRows);
                    if (rowsToDrawNow <= 0)
                        break;
                    continuationRowPlan.Add(rowsToDrawNow);
                    remainingAfterFirst -= rowsToDrawNow;
                }

                if (remainingAfterFirst > 0)
                    continuationRowPlan.Add(remainingAfterFirst);

                int totalPages = Math.Max(1, 1 + continuationRowPlan.Count);

                using (PdfDocument document = new PdfDocument())
                {
                    document.Info.Title = "PharmaLIMS Trend Report";
                    document.Info.Author = generatedBy;
                    document.Info.Subject = "Reports and Trends Analysis";
                    document.Info.CreationDate = generatedOn;

                    // First page: Chart + Summary + Table start
                    PdfPage firstPage = document.AddPage();
                    firstPage.Width = XUnit.FromPoint(842);
                    firstPage.Height = XUnit.FromPoint(595);

                    using (XGraphics gfx = XGraphics.FromPdfPage(firstPage))
                    {
                        XFont normalFont = new XFont("Arial", 8, XFontStyleEx.Regular);
                        XFont boldFont = new XFont("Arial", 8, XFontStyleEx.Bold);
                        XFont footerFont = new XFont("Arial", 7, XFontStyleEx.Regular);

                        double pageWidth = firstPage.Width.Point;
                        double pageHeight = firstPage.Height.Point;

                        DrawPdfPageBorder(gfx, pageWidth, pageHeight);
                        DrawReportPdfHeader(gfx, firstPage, reportNumber, generatedBy, generatedOn, reportTitle, 1, totalPages);

                        double yPos = DrawPdfChart(gfx, firstPage, tempImagePath);
                        yPos = DrawPdfSummary(gfx, firstPage, yPos, normalFont, boldFont);
                        yPos = DrawPdfTableStart(gfx, firstPage, yPos, cols, widths, firstPageRows, normalFont, boldFont, footerFont);

                        if (totalPages == 1)
                        {
                            DrawReportControlNote(gfx, pageWidth, pageHeight - 124, footerFont);
                            DrawReportGenerationRecord(gfx, pageWidth, pageHeight, generatedBy, generatedOn, normalFont, boldFont, footerFont);
                        }
                        else
                        {
                            gfx.DrawString("The report generation record is placed on the final page of this controlled report.",
                                footerFont, XBrushes.Gray, new XRect(40, pageHeight - 44, pageWidth - 80, 12), XStringFormats.TopCenter);
                        }

                        DrawGeneratedFooter(gfx, pageWidth, pageHeight, generatedOn, footerFont);
                    }

                    // Continuation pages
                    int rowIndex = firstPageRows;
                    for (int continuationPageIndex = 0; continuationPageIndex < continuationRowPlan.Count; continuationPageIndex++)
                    {
                        int pageNo = continuationPageIndex + 2;
                        int rowsPlannedForThisPage = continuationRowPlan[continuationPageIndex];
                        bool isLastPage = pageNo == totalPages;

                        PdfPage page = document.AddPage();
                        page.Width = XUnit.FromPoint(842);
                        page.Height = XUnit.FromPoint(595);

                        using (XGraphics gfx = XGraphics.FromPdfPage(page))
                        {
                            XFont normalFont = new XFont("Arial", 8, XFontStyleEx.Regular);
                            XFont boldFont = new XFont("Arial", 8, XFontStyleEx.Bold);
                            XFont footerFont = new XFont("Arial", 7, XFontStyleEx.Regular);

                            double pageWidth = page.Width.Point;
                            double pageHeight = page.Height.Point;
                            double tableX = 40;
                            double rowHeight = 13;

                            DrawPdfPageBorder(gfx, pageWidth, pageHeight);
                            DrawReportPdfHeader(gfx, page, reportNumber, generatedBy, generatedOn, "Full Data Table - Continued", pageNo, totalPages);

                            double yPos = 166;
                            gfx.DrawString("Full Data Table - Continued", boldFont, XBrushes.Black, new XRect(40, yPos, 250, 15), XStringFormats.TopLeft);
                            yPos += 17;

                            if (currentDataTable != null && rowsPlannedForThisPage > 0)
                            {
                                DrawReportTableHeader(gfx, cols, widths, tableX, yPos, rowHeight, boldFont);
                                yPos += rowHeight;

                                int drawnRows = 0;
                                while (rowIndex < currentDataTable.Rows.Count && drawnRows < rowsPlannedForThisPage)
                                {
                                    DrawReportTableRow(gfx, currentDataTable.Rows[rowIndex], cols, widths, tableX, yPos, rowHeight, normalFont);
                                    yPos += rowHeight;
                                    rowIndex++;
                                    drawnRows++;
                                }
                            }

                            if (isLastPage)
                            {
                                DrawReportControlNote(gfx, pageWidth, pageHeight - 124, footerFont);
                                DrawReportGenerationRecord(gfx, pageWidth, pageHeight, generatedBy, generatedOn, normalFont, boldFont, footerFont);
                            }
                            else
                            {
                                gfx.DrawString("Continued on next page...", footerFont, XBrushes.Gray,
                                    new XRect(40, pageHeight - 44, pageWidth - 80, 12), XStringFormats.TopCenter);
                            }

                            DrawGeneratedFooter(gfx, pageWidth, pageHeight, generatedOn, footerFont);
                        }
                    }

                    document.Save(filePath);
                }

                SetStatus($"PDF exported to {fileName}", false);
                ShowInfo($"PDF report exported successfully!\n\nFile saved to:\n{filePath}");
            }
            catch (Exception ex)
            {
                LogError("Error exporting PDF", ex);
                ShowError("Error exporting PDF:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempImagePath) && File.Exists(tempImagePath))
                        File.Delete(tempImagePath);
                }
                catch { /* Ignore cleanup errors */ }
            }
        }

        #endregion
    }
}
