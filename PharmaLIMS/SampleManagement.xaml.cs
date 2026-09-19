#nullable disable
using Microsoft.Data.SqlClient;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using System.Data;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PharmaLIMS
{
    [SupportedOSPlatform("windows7.0")]
    public partial class SampleManagement : Window
    {
        private DataTable samplesTable;
        private static bool pdfFontsConfigured = false;

        public SampleManagement()
        {
            ConfigurePdfFonts();
            InitializeComponent();
            Loaded += SampleManagement_Loaded;
        }

        private async void SampleManagement_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                InitializeFilters();
                await LoadSamplingPointsFilterAsync();
                await LoadSamplesAsync();
            }
            catch (Exception ex)
            {
                ShowError("Error while opening Sample Management:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private void ConfigurePdfFonts()
        {
            if (pdfFontsConfigured)
                return;

            try
            {
                GlobalFontSettings.FontResolver = new SampleManagementWindowsFontResolver();
                pdfFontsConfigured = true;
            }
            catch
            {
                // Keep window working even if PDF font resolver is already configured elsewhere.
            }
        }

        private void InitializeFilters()
        {
            cboSampleType.Items.Clear();
            cboSampleType.Items.Add(new ComboBoxItem { Content = "All", Tag = "" });
            cboSampleType.Items.Add(new ComboBoxItem { Content = "Purified Water", Tag = "Purified Water" });
            cboSampleType.Items.Add(new ComboBoxItem { Content = "Potable Water", Tag = "Potable Water" });
            cboSampleType.Items.Add(new ComboBoxItem { Content = "PW", Tag = "PW" });
            cboSampleType.Items.Add(new ComboBoxItem { Content = "PTW", Tag = "PTW" });
            cboSampleType.Items.Add(new ComboBoxItem { Content = "Environmental Monitoring", Tag = "Environmental Monitoring" });
            cboSampleType.Items.Add(new ComboBoxItem { Content = "Raw Material", Tag = "Raw Material" });
            cboSampleType.Items.Add(new ComboBoxItem { Content = "Production / In-Process", Tag = "Production / In-Process" });
            cboSampleType.Items.Add(new ComboBoxItem { Content = "Finished Product", Tag = "Finished Product" });
            cboSampleType.Items.Add(new ComboBoxItem { Content = "Stability", Tag = "Stability" });
            cboSampleType.SelectedIndex = 0;

            cboStatus.Items.Clear();
            string[] statuses =
            {
                "All",
                "Registered",
                "Incubation",
                "In Progress",
                "Results Entered",
                "Under Investigation",
                "Submitted for Review",
                "Reviewed",
                "Approved",
                "COA Issued",
                "Cancelled",
                "Rejected"
            };

            foreach (string status in statuses)
                cboStatus.Items.Add(new ComboBoxItem { Content = status, Tag = status == "All" ? "" : status });

            cboStatus.SelectedIndex = 0;

            dpDateFrom.SelectedDate = DateTime.Today.AddMonths(-1);
            dpDateTo.SelectedDate = DateTime.Today;

        }

        private async Task LoadSamplingPointsFilterAsync()
        {
            cboSamplingPoint.Items.Clear();
            cboSamplingPoint.Items.Add(new ComboBoxItem { Content = "All", Tag = "" });

            try
            {
                string query = @"
                    SELECT PointCode
                    FROM
                    (
                        SELECT DISTINCT PointCode
                        FROM
                        (
                            SELECT PointCode FROM WaterSamplingPoints WHERE ISNULL(PointCode, '') <> ''
                            UNION ALL
                            SELECT PointCode FROM SamplingPoints WHERE ISNULL(PointCode, '') <> ''
                            UNION ALL
                            SELECT AreaCode AS PointCode FROM EM_Areas WHERE ISNULL(AreaCode, '') <> '' AND ISNULL(IsActive, 1) = 1
                        ) source_points
                    ) distinct_points
                    ORDER BY
                        CASE WHEN PointCode LIKE 'D%' THEN 1 ELSE 0 END,
                        TRY_CONVERT(INT, REPLACE(PointCode, 'D', '')),
                        PointCode";

                DataTable dt = await Task.Run(() => DatabaseHelper.ExecuteQuery(query, commandTimeoutSeconds: 10));

                foreach (DataRow row in dt.Rows)
                {
                    string pointCode = row["PointCode"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(pointCode))
                        cboSamplingPoint.Items.Add(new ComboBoxItem { Content = pointCode, Tag = pointCode });
                }
            }
            catch
            {
                // Filter remains usable with All option.
            }

            cboSamplingPoint.SelectedIndex = 0;
        }

        private async Task LoadSamplesAsync()
        {
            try
            {
                SetStatus("Loading samples...", true);

                if (btnSearch != null)
                    btnSearch.IsEnabled = false;

                List<SqlParameter> parameters = new List<SqlParameter>();
                List<string> outerWhere = new List<string>();

                string sampleNo = txtSampleNumber.Text?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(sampleNo))
                {
                    outerWhere.Add("SampleNumber LIKE @sampleNo");
                    parameters.Add(new SqlParameter("@sampleNo", "%" + sampleNo + "%"));
                }

                string sampleType = GetComboTag(cboSampleType);
                if (!string.IsNullOrWhiteSpace(sampleType))
                {
                    outerWhere.Add("SampleType LIKE @sampleType");
                    parameters.Add(new SqlParameter("@sampleType", "%" + sampleType + "%"));
                }

                string pointCode = GetComboTag(cboSamplingPoint);
                if (!string.IsNullOrWhiteSpace(pointCode))
                {
                    outerWhere.Add("PointCode = @pointCode");
                    parameters.Add(new SqlParameter("@pointCode", pointCode));
                }

                if (dpDateFrom.SelectedDate.HasValue)
                {
                    outerWhere.Add("SamplingDateTime >= @dateFrom");
                    parameters.Add(new SqlParameter("@dateFrom", dpDateFrom.SelectedDate.Value.Date));
                }

                if (dpDateTo.SelectedDate.HasValue)
                {
                    outerWhere.Add("SamplingDateTime < @dateTo");
                    parameters.Add(new SqlParameter("@dateTo", dpDateTo.SelectedDate.Value.Date.AddDays(1)));
                }

                string query = @"
                    ;WITH WaterRows AS
                    (
                        SELECT
                            'WATER' AS RecordKind,
                            s.SampleID,
                            ISNULL(s.SampleNumber, '') AS SampleNumber,
                            ISNULL(s.SampleType, '') AS SampleType,
                            COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode, sp.PointCode, '') AS PointCode,
                            COALESCE(NULLIF(s.PointLocationSnapshot,N''),wp.Location, sp.Location, '') AS Location,
                            s.SamplingDateTime,
                            FORMAT(s.SamplingDateTime, 'yyyy-MM-dd HH:mm') AS SamplingDateTimeText,
                            ISNULL(s.SampledBy, '') AS SampledBy,
                            COUNT(st.SampleTestID) AS TestsCount,
                            SUM(CASE WHEN st.SampleTestID IS NOT NULL AND st.ResultValue IS NOT NULL AND ISNULL(LTRIM(RTRIM(CONVERT(nvarchar(100), st.ResultValue))), '') <> '' THEN 1 ELSE 0 END) AS CompletedResults,
                            SUM(CASE WHEN st.SampleTestID IS NOT NULL AND (st.ResultValue IS NULL OR ISNULL(LTRIM(RTRIM(CONVERT(nvarchar(100), st.ResultValue))), '') = '') THEN 1 ELSE 0 END) AS PendingResults,
                            SUM(CASE WHEN UPPER(ISNULL(st.ResultStatus, '')) = 'ALERT' THEN 1 ELSE 0 END) AS AlertResults,
                            SUM(CASE WHEN UPPER(ISNULL(st.ResultStatus, '')) IN ('FAIL', 'OOS', 'ACTION') THEN 1 ELSE 0 END) AS FailResults,
                            COALESCE(NULLIF(LTRIM(RTRIM(s.Status)),N''),
                                CASE
                                    WHEN COUNT(st.SampleTestID) = 0 THEN 'Registered'
                                    WHEN SUM(CASE WHEN st.SampleTestID IS NOT NULL AND (st.ResultValue IS NULL OR ISNULL(LTRIM(RTRIM(CONVERT(nvarchar(100), st.ResultValue))), '') = '') THEN 1 ELSE 0 END) > 0
                                         AND SUM(CASE WHEN st.SampleTestID IS NOT NULL AND st.ResultValue IS NOT NULL AND ISNULL(LTRIM(RTRIM(CONVERT(nvarchar(100), st.ResultValue))), '') <> '' THEN 1 ELSE 0 END) > 0 THEN 'In Progress'
                                    WHEN SUM(CASE WHEN st.SampleTestID IS NOT NULL AND (st.ResultValue IS NULL OR ISNULL(LTRIM(RTRIM(CONVERT(nvarchar(100), st.ResultValue))), '') = '') THEN 1 ELSE 0 END) > 0 THEN 'Incubation'
                                    ELSE 'Results Entered'
                                END) AS CurrentStatus,
                            CASE
                                WHEN SUM(CASE WHEN UPPER(ISNULL(st.ResultStatus, '')) IN ('FAIL', 'OOS', 'ACTION') THEN 1 ELSE 0 END) > 0 THEN 'Required'
                                ELSE 'Not Required'
                            END AS InvestigationStatus,
                            CASE WHEN EXISTS
                            (
                                SELECT 1 FROM dbo.Certificates c
                                WHERE c.SampleID=s.SampleID
                                  AND ISNULL(c.IsCancelled,0)=0
                                  AND ISNULL(NULLIF(c.CertificateStatus,N''),ISNULL(c.Status,N'Active')) IN (N'Active',N'Issued')
                            ) THEN 'Active' ELSE 'Not Issued' END AS COAStatus,
                            ISNULL(s.SampledBy, '') AS CreatedBy,
                            s.SamplingDateTime AS CreatedDate,
                            FORMAT(s.SamplingDateTime, 'yyyy-MM-dd HH:mm') AS CreatedDateText
                        FROM Samples s
                        LEFT JOIN WaterSamplingPoints wp ON s.PointID = wp.Id
                        LEFT JOIN SamplingPoints sp ON s.PointID = sp.PointID
                        LEFT JOIN SampleTests st ON s.SampleID = st.SampleID
                        WHERE ISNULL(s.SampleNumber, '') NOT LIKE '%TEST%'
                        GROUP BY
                            s.SampleID,
                            s.SampleNumber,
                            s.SampleType,
                            wp.PointCode,
                            sp.PointCode,
                            wp.Location,
                            sp.Location,
                            s.PointCodeSnapshot,
                            s.PointLocationSnapshot,
                            s.SamplingDateTime,
                            s.SampledBy,
                            s.Status
                    ),
                    EMRows AS
                    (
                        SELECT
                            'EM' AS RecordKind,
                            e.Id AS SampleID,
                            ISNULL(e.EventNo, '') AS SampleNumber,
                            'Environmental Monitoring' AS SampleType,
                            ISNULL(a.AreaCode, '') AS PointCode,
                            LTRIM(RTRIM(
                                ISNULL(a.AreaName, '') +
                                CASE
                                    WHEN ISNULL(a.AreaGroup, '') = '' THEN ''
                                    ELSE ' - ' + ISNULL(a.AreaGroup, '')
                                END
                            )) AS Location,
                            CAST(e.EventDate AS datetime) AS SamplingDateTime,
                            FORMAT(e.EventDate, 'yyyy-MM-dd') +
                                CASE
                                    WHEN ISNULL(NULLIF(LTRIM(RTRIM(e.SamplingTimeFrom)), ''), '') <> ''
                                      OR ISNULL(NULLIF(LTRIM(RTRIM(e.SamplingTimeTo)), ''), '') <> ''
                                    THEN ' ' + ISNULL(NULLIF(LTRIM(RTRIM(e.SamplingTimeFrom)), ''), '--:--') + ' - ' + ISNULL(NULLIF(LTRIM(RTRIM(e.SamplingTimeTo)), ''), '--:--')
                                    ELSE ''
                                END AS SamplingDateTimeText,
                            '' AS SampledBy,
                            COUNT(p.Id) AS TestsCount,
                            SUM(CASE WHEN p.Id IS NOT NULL AND p.TotalCount IS NOT NULL THEN 1 ELSE 0 END) AS CompletedResults,
                            SUM(CASE WHEN p.Id IS NOT NULL AND p.TotalCount IS NULL THEN 1 ELSE 0 END) AS PendingResults,
                            SUM(CASE WHEN UPPER(ISNULL(p.Status, '')) = 'ALERT' THEN 1 ELSE 0 END) AS AlertResults,
                            SUM(CASE WHEN UPPER(ISNULL(p.Status, '')) IN ('FAIL', 'OOS', 'ACTION') THEN 1 ELSE 0 END) AS FailResults,
                            CASE
                                WHEN UPPER(ISNULL(LTRIM(RTRIM(e.WorkflowStatus)), '')) IN ('APPROVED', 'CLOSED', 'CANCELLED')
                                    THEN LTRIM(RTRIM(e.WorkflowStatus))
                                WHEN SUM(CASE WHEN UPPER(ISNULL(p.Status, '')) IN ('FAIL', 'OOS', 'ACTION') THEN 1 ELSE 0 END) > 0
                                    THEN 'OOS / Investigation'
                                WHEN SUM(CASE WHEN UPPER(ISNULL(p.Status, '')) = 'ALERT' THEN 1 ELSE 0 END) > 0
                                    THEN 'Alert / QA Review'
                                WHEN ISNULL(NULLIF(LTRIM(RTRIM(e.WorkflowStatus)), ''), '') <> '' THEN LTRIM(RTRIM(e.WorkflowStatus))
                                WHEN ISNULL(NULLIF(LTRIM(RTRIM(e.FinalResult)), ''), '') <> '' THEN LTRIM(RTRIM(e.FinalResult))
                                WHEN COUNT(p.Id) = 0 THEN 'Registered'
                                WHEN SUM(CASE WHEN p.Id IS NOT NULL AND p.TotalCount IS NULL THEN 1 ELSE 0 END) > 0
                                     AND SUM(CASE WHEN p.Id IS NOT NULL AND p.TotalCount IS NOT NULL THEN 1 ELSE 0 END) > 0 THEN 'In Progress'
                                WHEN SUM(CASE WHEN p.Id IS NOT NULL AND p.TotalCount IS NULL THEN 1 ELSE 0 END) > 0 THEN 'Pending'
                                ELSE 'Results Entered'
                            END AS CurrentStatus,
                            CASE
                                WHEN SUM(CASE WHEN UPPER(ISNULL(p.Status, '')) IN ('FAIL', 'OOS', 'ACTION') THEN 1 ELSE 0 END) > 0 THEN 'Required'
                                ELSE 'Not Required'
                            END AS InvestigationStatus,
                            'N/A' AS COAStatus,
                            '' AS CreatedBy,
                            ISNULL(e.CreatedAt, CAST(e.EventDate AS datetime)) AS CreatedDate,
                            FORMAT(ISNULL(e.CreatedAt, CAST(e.EventDate AS datetime)), 'yyyy-MM-dd HH:mm') AS CreatedDateText
                        FROM dbo.EM_Events e
                        INNER JOIN dbo.EM_Areas a ON e.AreaId = a.Id
                        LEFT JOIN dbo.EM_EventPlates p ON p.EventId = e.Id
                        GROUP BY
                            e.Id,
                            e.EventNo,
                            e.EventDate,
                            e.SamplingTimeFrom,
                            e.SamplingTimeTo,
                            e.WorkflowStatus,
                            e.FinalResult,
                            e.CreatedAt,
                            a.AreaCode,
                            a.AreaName,
                            a.AreaGroup
                    ),
                    PRMRows AS
                    (
                        SELECT
                            'PRM' AS RecordKind,
                            ps.SampleID,
                            ISNULL(ps.SampleNumber, '') AS SampleNumber,
                            ISNULL(ps.SampleCategory, '') AS SampleType,
                            CASE
                                WHEN ps.SampleCategory = N'Raw Material' THEN ISNULL(ps.MaterialCode, '')
                                ELSE ISNULL(ps.ProductCode, '')
                            END AS PointCode,
                            CASE
                                WHEN ps.SampleCategory = N'Raw Material' THEN ISNULL(ps.MaterialName, '')
                                ELSE ISNULL(ps.ProductName, '')
                            END AS Location,
                            ISNULL(CAST(ps.SampleDateTime AS datetime), CAST(ps.CreatedDate AS datetime)) AS SamplingDateTime,
                            FORMAT(ISNULL(CAST(ps.SampleDateTime AS datetime), CAST(ps.CreatedDate AS datetime)), 'yyyy-MM-dd HH:mm') AS SamplingDateTimeText,
                            ISNULL(ps.SampledBy, '') AS SampledBy,
                            COUNT(pt.SampleTestID) AS TestsCount,
                            SUM(CASE WHEN pt.SampleTestID IS NOT NULL AND ISNULL(LTRIM(RTRIM(pt.ResultValue)), '') <> '' THEN 1 ELSE 0 END) AS CompletedResults,
                            SUM(CASE WHEN pt.SampleTestID IS NOT NULL AND ISNULL(LTRIM(RTRIM(pt.ResultValue)), '') = '' THEN 1 ELSE 0 END) AS PendingResults,
                            SUM(CASE WHEN UPPER(ISNULL(pt.Interpretation, '')) IN ('ALERT', 'CHECK REQUIRED') THEN 1 ELSE 0 END) AS AlertResults,
                            SUM(CASE WHEN UPPER(ISNULL(pt.Interpretation, '')) IN ('FAIL', 'OOS', 'ACTION', 'REJECTED', 'DOES NOT CONFORM', 'NON-CONFORM') THEN 1 ELSE 0 END) AS FailResults,
                            CASE
                                WHEN ISNULL(NULLIF(LTRIM(RTRIM(ps.SampleStatus)), ''), '') <> '' THEN LTRIM(RTRIM(ps.SampleStatus))
                                WHEN COUNT(pt.SampleTestID) = 0 THEN 'Registered'
                                WHEN SUM(CASE WHEN pt.SampleTestID IS NOT NULL AND ISNULL(LTRIM(RTRIM(pt.ResultValue)), '') = '' THEN 1 ELSE 0 END) > 0
                                     AND SUM(CASE WHEN pt.SampleTestID IS NOT NULL AND ISNULL(LTRIM(RTRIM(pt.ResultValue)), '') <> '' THEN 1 ELSE 0 END) > 0 THEN 'In Progress'
                                WHEN SUM(CASE WHEN pt.SampleTestID IS NOT NULL AND ISNULL(LTRIM(RTRIM(pt.ResultValue)), '') = '' THEN 1 ELSE 0 END) > 0 THEN 'Incubation'
                                ELSE 'Results Entered'
                            END AS CurrentStatus,
                            CASE
                                WHEN SUM(CASE WHEN UPPER(ISNULL(pt.Interpretation, '')) IN ('FAIL', 'OOS', 'ACTION', 'REJECTED', 'DOES NOT CONFORM', 'NON-CONFORM') THEN 1 ELSE 0 END) > 0 THEN 'Required'
                                WHEN SUM(CASE WHEN UPPER(ISNULL(pt.Interpretation, '')) = 'CHECK REQUIRED' THEN 1 ELSE 0 END) > 0 THEN 'Required'
                                ELSE 'Not Required'
                            END AS InvestigationStatus,
                            CASE
                                WHEN EXISTS
                                (
                                    SELECT 1
                                    FROM dbo.PRM_Certificates pc
                                    WHERE pc.SampleID = ps.SampleID
                                      AND ISNULL(pc.CertificateStatus, N'Active') = N'Active'
                                      AND ISNULL(pc.IsCancelled, 0) = 0
                                ) THEN 'Active'
                                ELSE ISNULL(NULLIF(LTRIM(RTRIM(ps.ReportStatus)), ''), 'Not Issued')
                            END AS COAStatus,
                            ISNULL(ps.CreatedBy, '') AS CreatedBy,
                            CAST(ps.CreatedDate AS datetime) AS CreatedDate,
                            FORMAT(ps.CreatedDate, 'yyyy-MM-dd HH:mm') AS CreatedDateText
                        FROM dbo.PRM_Samples ps
                        LEFT JOIN dbo.PRM_SampleTests pt ON pt.SampleID = ps.SampleID
                        GROUP BY
                            ps.SampleID, ps.SampleNumber, ps.SampleCategory,
                            ps.MaterialCode, ps.MaterialName, ps.ProductCode, ps.ProductName,
                            ps.SampleDateTime, ps.SampledBy, ps.SampleStatus, ps.ReportStatus,
                            ps.CreatedBy, ps.CreatedDate
                    ),
                    CombinedRows AS
                    (
                        SELECT * FROM WaterRows
                        UNION ALL
                        SELECT * FROM EMRows
                        UNION ALL
                        SELECT * FROM PRMRows
                    )
                    SELECT *
                    FROM CombinedRows
                    WHERE 1 = 1";

                if (outerWhere.Count > 0)
                    query += " AND " + string.Join(" AND ", outerWhere);

                string status = GetComboTag(cboStatus);
                if (!string.IsNullOrWhiteSpace(status))
                {
                    query += " AND CurrentStatus = @status";
                    parameters.Add(new SqlParameter("@status", status));
                }

                query += " ORDER BY SamplingDateTime DESC, CreatedDate DESC, SampleID DESC";

                SqlParameter[] parameterArray = parameters.ToArray();
                DataTable loadedTable = await Task.Run(() => DatabaseHelper.ExecuteQuery(query, parameterArray, commandTimeoutSeconds: 10));

                samplesTable = loadedTable;
                dgSamples.ItemsSource = samplesTable.DefaultView;

                UpdateCounters();
                SetStatus("Water, EM and PRM samples loaded successfully.", false);
            }
            catch (Exception ex)
            {
                SetStatus("Error loading samples.", false);
                ShowError("Error loading samples:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
            finally
            {
                if (btnSearch != null)
                    btnSearch.IsEnabled = true;
            }
        }

        private string GetComboTag(ComboBox comboBox)
        {
            if (comboBox?.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString() ?? "";

            return "";
        }

        private void UpdateSampleStatuses()
        {
            if (samplesTable == null || samplesTable.Rows.Count == 0)
                return;

            foreach (DataRow row in samplesTable.Rows)
            {
                int sampleId = SafeInt(row["SampleID"]);
                if (sampleId <= 0)
                    continue;

                if (IsEMRow(row) || IsPRMRow(row))
                    continue;

                try
                {
                    object situation = TryEvaluateSampleSituation(sampleId);
                    if (situation == null)
                        continue;

                    string currentStage = GetObjectPropertyText(situation, "CurrentStage");
                    if (!string.IsNullOrWhiteSpace(currentStage) && samplesTable.Columns.Contains("CurrentStatus"))
                        row["CurrentStatus"] = currentStage;

                    string investigationStatus = GetObjectPropertyText(situation, "InvestigationStatusText");
                    if (string.IsNullOrWhiteSpace(investigationStatus))
                        investigationStatus = BuildInvestigationStatusFromSituation(situation, row);

                    if (!string.IsNullOrWhiteSpace(investigationStatus) && samplesTable.Columns.Contains("InvestigationStatus"))
                        row["InvestigationStatus"] = investigationStatus;

                    string coaStatus = GetObjectPropertyText(situation, "COAStatusText");
                    if (string.IsNullOrWhiteSpace(coaStatus))
                        coaStatus = BuildCOAStatusFromSituation(situation, row);

                    if (!string.IsNullOrWhiteSpace(coaStatus) && samplesTable.Columns.Contains("COAStatus"))
                        row["COAStatus"] = coaStatus;
                }
                catch
                {
                    // Keep SQL-derived values if the evaluation layer is not available yet.
                }
            }

            if (dgSamples != null)
                dgSamples.Items.Refresh();
        }

        private object TryEvaluateSampleSituation(int sampleId)
        {
            try
            {
                Type detailsType = Assembly.GetExecutingAssembly().GetType("PharmaLIMS.SampleDetails");
                if (detailsType == null)
                    return null;

                MethodInfo method = detailsType.GetMethod(
                    "EvaluateSampleSituation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(int) },
                    null);

                if (method == null)
                    return null;

                return method.Invoke(null, new object[] { sampleId });
            }
            catch
            {
                return null;
            }
        }

        private string GetObjectPropertyText(object obj, string propertyName)
        {
            if (obj == null || string.IsNullOrWhiteSpace(propertyName))
                return "";

            try
            {
                PropertyInfo property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property == null)
                    return "";

                object value = property.GetValue(obj);
                return value == null || value == DBNull.Value ? "" : value.ToString();
            }
            catch
            {
                return "";
            }
        }

        private bool GetObjectPropertyBool(object obj, string propertyName)
        {
            string text = GetObjectPropertyText(obj, propertyName);
            return text.Equals("True", StringComparison.OrdinalIgnoreCase) || text.Equals("1", StringComparison.OrdinalIgnoreCase) || text.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        private string BuildInvestigationStatusFromSituation(object situation, DataRow row)
        {
            if (GetObjectPropertyBool(situation, "HasOpenInvestigation"))
                return "Open";

            if (GetObjectPropertyBool(situation, "HasClosedInvestigation"))
                return "Closed";

            if (GetObjectPropertyBool(situation, "InvestigationRequired"))
                return "Required";

            return GetCellText(row, "InvestigationStatus");
        }

        private string BuildCOAStatusFromSituation(object situation, DataRow row)
        {
            if (GetObjectPropertyBool(situation, "HasActiveCOA"))
                return "Active";

            if (GetObjectPropertyBool(situation, "HasCancelledCOA"))
                return "Cancelled/Reissued";

            return GetCellText(row, "COAStatus");
        }

        private void UpdateCounters()
        {
            int total = samplesTable == null ? 0 : samplesTable.Rows.Count;
            int pending = 0;
            int alert = 0;
            int fail = 0;
            int coaActive = 0;

            if (samplesTable != null)
            {
                foreach (DataRow row in samplesTable.Rows)
                {
                    pending += SafeInt(row["PendingResults"]);
                    alert += SafeInt(row["AlertResults"]);
                    fail += SafeInt(row["FailResults"]);

                    string coa = row["COAStatus"]?.ToString() ?? "";
                    if (coa.Equals("Active", StringComparison.OrdinalIgnoreCase))
                        coaActive++;
                }
            }

            lblCounts.Text = $"Samples: {total} | Pending Results: {pending} | Alert: {alert} | Fail/OOS: {fail} | COA Active: {coaActive}";
            lblHeaderStatus.Text = total.ToString(CultureInfo.InvariantCulture) + " sample(s)";
        }

        private int SafeInt(object value)
        {
            if (value == null || value == DBNull.Value)
                return 0;

            if (int.TryParse(value.ToString(), out int result))
                return result;

            return 0;
        }

        private DataRowView GetSelectedSample()
        {
            return dgSamples.SelectedItem as DataRowView;
        }

        private int GetSelectedSampleId()
        {
            DataRowView row = GetSelectedSample();
            if (row == null)
                return 0;

            if (int.TryParse(row["SampleID"]?.ToString(), out int sampleId))
                return sampleId;

            return 0;
        }

        private string GetSelectedSampleNumber()
        {
            DataRowView row = GetSelectedSample();
            return row == null ? "" : (row["SampleNumber"]?.ToString() ?? "");
        }

        private string GetSelectedRecordKind()
        {
            DataRowView row = GetSelectedSample();
            return row == null ? "" : GetCellText(row.Row, "RecordKind");
        }

        private bool IsSelectedEMRecord()
        {
            return GetSelectedRecordKind().Equals("EM", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSelectedPRMRecord()
        {
            return GetSelectedRecordKind().Equals("PRM", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsEMRow(DataRow row)
        {
            return GetCellText(row, "RecordKind").Equals("EM", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPRMRow(DataRow row)
        {
            return GetCellText(row, "RecordKind").Equals("PRM", StringComparison.OrdinalIgnoreCase);
        }

        private void OpenPRMResults(int sampleId)
        {
            var window = new ProductionRawMaterialResults(sampleId) { Owner = this };
            window.Closed += async (_, _) => await LoadSamplesAsync();
            window.Show();
        }

        private void OpenPRMInvestigation(int sampleId)
        {
            // The PRM Results window owns the controlled creation/opening of PRM quality events.
            OpenPRMResults(sampleId);
        }

        private bool RequireSampleSelection(out int sampleId, out string sampleNumber)
        {
            sampleId = GetSelectedSampleId();
            sampleNumber = GetSelectedSampleNumber();

            if (sampleId <= 0)
            {
                ShowInfo("Please select a sample first.");
                return false;
            }

            return true;
        }

        private void OpenWindowByTypeName(string typeName, int sampleId, string sampleNumber, bool trySampleIdConstructor)
        {
            try
            {
                Type windowType = Assembly.GetExecutingAssembly().GetType(typeName);

                if (windowType == null)
                {
                    ShowInfo("The target window was not found:\n" + typeName);
                    return;
                }

                object instance = null;

                if (trySampleIdConstructor && sampleId > 0)
                {
                    ConstructorInfo intConstructor = windowType.GetConstructor(new[] { typeof(int) });
                    if (intConstructor != null)
                        instance = intConstructor.Invoke(new object[] { sampleId });
                }

                if (instance == null && !string.IsNullOrWhiteSpace(sampleNumber))
                {
                    ConstructorInfo stringConstructor = windowType.GetConstructor(new[] { typeof(string) });
                    if (stringConstructor != null)
                        instance = stringConstructor.Invoke(new object[] { sampleNumber });
                }

                if (instance == null && App.ServiceProvider != null)
                {
                    instance = App.ServiceProvider.GetService(windowType);
                }

                if (instance == null)
                {
                    ConstructorInfo emptyConstructor = windowType.GetConstructor(Type.EmptyTypes);
                    if (emptyConstructor != null)
                        instance = emptyConstructor.Invoke(null);
                }

                if (instance == null)
                {
                    ShowInfo("The target window could not be created:\n" + typeName);
                    return;
                }

                TryPassSelectedSample(instance, sampleId, sampleNumber);

                if (instance is Window window)
                {
                    window.Owner = this;
                    window.Closed += async (_, _) => await LoadSamplesAsync();
                    window.Show();
                    return;
                }

                ShowInfo("The target type is not a WPF Window:\n" + typeName);
            }
            catch (Exception ex)
            {
                ShowError("Could not open target window:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private void TryPassSelectedSample(object instance, int sampleId, string sampleNumber)
        {
            try
            {
                if (instance == null)
                    return;

                Type type = instance.GetType();

                PropertyInfo sampleIdProperty = type.GetProperty("SampleID") ??
                                                type.GetProperty("SampleId") ??
                                                type.GetProperty("SelectedSampleID") ??
                                                type.GetProperty("SelectedSampleId") ??
                                                type.GetProperty("PreselectedSampleID") ??
                                                type.GetProperty("PreselectedSampleId");

                if (sampleIdProperty != null && sampleIdProperty.CanWrite && sampleId > 0)
                {
                    if (sampleIdProperty.PropertyType == typeof(int))
                        sampleIdProperty.SetValue(instance, sampleId);
                    else if (sampleIdProperty.PropertyType == typeof(int?))
                        sampleIdProperty.SetValue(instance, sampleId);
                }

                PropertyInfo sampleNumberProperty = type.GetProperty("SampleNumber") ??
                                                    type.GetProperty("SelectedSampleNumber") ??
                                                    type.GetProperty("PreselectedSampleNumber");

                if (sampleNumberProperty != null && sampleNumberProperty.CanWrite && !string.IsNullOrWhiteSpace(sampleNumber))
                {
                    if (sampleNumberProperty.PropertyType == typeof(string))
                        sampleNumberProperty.SetValue(instance, sampleNumber);
                }

                MethodInfo loadByIdMethod = type.GetMethod("LoadSample", new[] { typeof(int) }) ??
                                            type.GetMethod("LoadSampleById", new[] { typeof(int) }) ??
                                            type.GetMethod("OpenSample", new[] { typeof(int) }) ??
                                            type.GetMethod("SetSample", new[] { typeof(int) });

                if (loadByIdMethod != null && sampleId > 0)
                {
                    loadByIdMethod.Invoke(instance, new object[] { sampleId });
                    return;
                }

                MethodInfo loadByNumberMethod = type.GetMethod("LoadSample", new[] { typeof(string) }) ??
                                                type.GetMethod("LoadSampleByNumber", new[] { typeof(string) }) ??
                                                type.GetMethod("OpenSample", new[] { typeof(string) }) ??
                                                type.GetMethod("SetSample", new[] { typeof(string) });

                if (loadByNumberMethod != null && !string.IsNullOrWhiteSpace(sampleNumber))
                    loadByNumberMethod.Invoke(instance, new object[] { sampleNumber });
            }
            catch
            {
                // Opening the target window is more important than preselecting the sample.
            }
        }

        private void OpenEMResultsEntry(int eventId, string eventNo)
        {
            try
            {
                if (eventId <= 0 && string.IsNullOrWhiteSpace(eventNo))
                {
                    ShowInfo("Please select an EM event first.");
                    return;
                }

                Type windowType = Assembly.GetExecutingAssembly().GetType("PharmaLIMS.EMResultsEntry");
                if (windowType == null)
                {
                    ShowInfo("The EM Results Entry window was not found.");
                    return;
                }

                object instance = null;

                try
                {
                    if (App.ServiceProvider != null)
                        instance = App.ServiceProvider.GetService(windowType);
                }
                catch
                {
                    instance = null;
                }

                if (instance == null)
                {
                    try
                    {
                        Type authType = Assembly.GetExecutingAssembly().GetType("PharmaLIMS.IAuthService");
                        object authService = authType != null && App.ServiceProvider != null
                            ? App.ServiceProvider.GetService(authType)
                            : null;

                        if (authType != null && App.ServiceProvider != null && authService != null)
                        {
                            ConstructorInfo ctor = windowType.GetConstructor(new[] { typeof(IServiceProvider), authType });
                            if (ctor != null)
                                instance = ctor.Invoke(new object[] { App.ServiceProvider, authService });
                        }
                    }
                    catch
                    {
                        instance = null;
                    }
                }

                if (instance == null)
                {
                    ShowInfo("Could not create EM Results Entry window. Open EM Results Entry from the main menu and search for:\n" + eventNo);
                    return;
                }

                try
                {
                    FieldInfo eventNoField = windowType.GetField("txtEventNo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (eventNoField?.GetValue(instance) is TextBox txt)
                        txt.Text = eventNo;

                    MethodInfo loadMethod = windowType.GetMethod("LoadEMEvent", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                    if (loadMethod != null && !string.IsNullOrWhiteSpace(eventNo))
                        loadMethod.Invoke(instance, new object[] { eventNo });
                }
                catch
                {
                    // If preloading fails, the user can still search inside the EM Results Entry window.
                }

                if (instance is Window window)
                {
                    window.Owner = this;
                    window.Closed += async (_, _) => await LoadSamplesAsync();
                    window.Show();
                    return;
                }

                ShowInfo("The EM Results Entry target is not a WPF Window.");
            }
            catch (Exception ex)
            {
                ShowError("Could not open EM Results Entry:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            await LoadSamplesAsync();
        }

        private async void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            txtSampleNumber.Text = "";
            cboSampleType.SelectedIndex = 0;
            cboSamplingPoint.SelectedIndex = 0;
            cboStatus.SelectedIndex = 0;
            dpDateFrom.SelectedDate = DateTime.Today.AddMonths(-1);
            dpDateTo.SelectedDate = DateTime.Today;
            await LoadSamplesAsync();
        }

        private async void TxtSampleNumber_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                await LoadSamplesAsync();
        }

        private void DgSamples_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            BtnViewDetails_Click(sender, e);
        }

        private void BtnNewSample_Click(object sender, RoutedEventArgs e)
        {
            OpenWindowByTypeName("PharmaLIMS.NewSampleDialog", 0, "", false);
        }

        private void BtnViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSampleSelection(out int sampleId, out string sampleNumber))
                return;

            if (IsSelectedEMRecord())
            {
                OpenEMResultsEntry(sampleId, sampleNumber);
                return;
            }

            if (IsSelectedPRMRecord())
            {
                OpenPRMResults(sampleId);
                return;
            }

            OpenWindowByTypeName("PharmaLIMS.SampleDetails", sampleId, sampleNumber, true);
        }

        private void BtnEnterResults_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSampleSelection(out int sampleId, out string sampleNumber))
                return;

            if (IsSelectedEMRecord())
            {
                OpenEMResultsEntry(sampleId, sampleNumber);
                return;
            }

            if (IsSelectedPRMRecord())
            {
                OpenPRMResults(sampleId);
                return;
            }

            OpenWindowByTypeName("PharmaLIMS.ResultsEntry", sampleId, sampleNumber, true);
        }

        private void BtnOpenInvestigation_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSampleSelection(out int sampleId, out string sampleNumber))
                return;

            if (IsSelectedEMRecord())
            {
                OpenEMResultsEntry(sampleId, sampleNumber);
                return;
            }

            if (IsSelectedPRMRecord())
            {
                OpenPRMInvestigation(sampleId);
                return;
            }

            OpenWindowByTypeName("PharmaLIMS.QualityEventInvestigation", sampleId, sampleNumber, true);
        }

        private void BtnViewIssueCoa_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSampleSelection(out int sampleId, out string sampleNumber))
                return;

            if (IsSelectedEMRecord())
            {
                ShowInfo("Environmental Monitoring records do not issue COA. Use EM Results Entry to review or print the EMRR report.");
                return;
            }

            if (IsSelectedPRMRecord())
            {
                OpenPRMResults(sampleId);
                return;
            }

            OpenWindowByTypeName("PharmaLIMS.ReportCertificate", sampleId, sampleNumber, true);
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadSamplesAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!DatabaseHelper.CanAccessReports(Login.CurrentUser))
                    throw new UnauthorizedAccessException("You are not authorized to export report data.");

                if (samplesTable == null || samplesTable.Rows.Count == 0)
                {
                    ShowInfo("No samples to export.");
                    return;
                }

                string fileName = "SampleManagement_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
                string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                using (StreamWriter writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    string[] columns = GetReportColumns();
                    writer.WriteLine(string.Join(",", columns.Select(c => EscapeCsv(c))));

                    foreach (DataRow row in samplesTable.Rows)
                    {
                        List<string> values = new List<string>();
                        foreach (string col in columns)
                            values.Add(EscapeCsv(row.Table.Columns.Contains(col) ? row[col] : null));

                        writer.WriteLine(string.Join(",", values));
                    }
                }

                string exportHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
                DatabaseHelper.AddAuditTrailAdvanced(
                    "Samples", 0, "Uncontrolled CSV Export", string.Empty,
                    fileName + "; SHA256=" + exportHash + "; Rows=" + samplesTable.Rows.Count.ToString(CultureInfo.InvariantCulture),
                    "User-requested working copy export; CSV is not a controlled GMP record.",
                    Login.CurrentUser, "Export", null, fileName, "Sample Management");

                ShowInfo("UNCONTROLLED COPY - CSV exported successfully:\n" + filePath + "\nSHA-256: " + exportHash);
            }
            catch (Exception ex)
            {
                ShowError("Error exporting CSV:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private string EscapeCsv(object value) => Infrastructure.CsvSecurity.Escape(value);

        private string[] GetReportColumns()
        {
            return new[]
            {
                "SampleNumber",
                "SampleType",
                "PointCode",
                "Location",
                "SamplingDateTimeText",
                "SampledBy",
                "CurrentStatus",
                "TestsCount",
                "CompletedResults",
                "PendingResults",
                "AlertResults",
                "FailResults",
                "InvestigationStatus",
                "COAStatus",
                "CreatedBy",
                "CreatedDateText"
            };
        }

        private string GetCellText(DataRow row, string columnName)
        {
            if (row == null || string.IsNullOrWhiteSpace(columnName) || !row.Table.Columns.Contains(columnName))
                return "";

            object value = row[columnName];
            return value == null || value == DBNull.Value ? "" : value.ToString();
        }

        private void BtnPrintSummary_Click(object sender, RoutedEventArgs e)
        {
            ExportSamplesPdf(false);
        }

        private void BtnPrintDetails_Click(object sender, RoutedEventArgs e)
        {
            ExportSamplesPdf(true);
        }

        private void ExportSamplesPdf(bool detailed)
        {
            try
            {
                if (samplesTable == null || samplesTable.Rows.Count == 0)
                {
                    ShowInfo("No samples to print.");
                    return;
                }

                string reportType = detailed ? "Detailed" : "Summary";
                string fileName = "SampleManagement_" + reportType + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".pdf";
                string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                using (PdfDocument document = new PdfDocument())
                {
                    document.Info.Title = "Sample Management " + reportType + " Report";
                    document.Info.Author = GetCurrentUserName();
                    document.Info.CreationDate = DateTime.Now;

                    if (detailed)
                        DrawDetailedPdf(document);
                    else
                        DrawSummaryPdf(document);

                    document.Save(filePath);
                }

                ShowInfo(reportType + " PDF exported successfully:\n" + filePath);
            }
            catch (Exception ex)
            {
                ShowError("Error printing report:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private void DrawSummaryPdf(PdfDocument document)
        {
            string[] columns =
            {
                "SampleNumber", "SampleType", "PointCode", "SamplingDateTimeText", "CurrentStatus",
                "TestsCount", "PendingResults", "AlertResults", "FailResults", "InvestigationStatus", "COAStatus"
            };

            double[] widths = { 90, 90, 65, 105, 90, 42, 52, 42, 48, 85, 75 };
            int rowIndex = 0;
            int pageNo = 0;
            int rowsPerPage = 26;

            while (rowIndex < samplesTable.Rows.Count || pageNo == 0)
            {
                pageNo++;
                PdfPage page = document.AddPage();
                page.Width = XUnit.FromPoint(842);
                page.Height = XUnit.FromPoint(595);

                using XGraphics gfx = XGraphics.FromPdfPage(page);
                XFont titleFont = new XFont("Arial", 13, XFontStyleEx.Bold);
                XFont boldFont = new XFont("Arial", 7, XFontStyleEx.Bold);
                XFont normalFont = new XFont("Arial", 7, XFontStyleEx.Regular);
                XFont smallFont = new XFont("Arial", 6, XFontStyleEx.Regular);

                DrawPdfHeader(gfx, page, "Sample Management Summary Report", pageNo);

                double y = 112;
                DrawSummaryBox(gfx, page, y, normalFont, boldFont);
                y += 60;

                double x = 30;
                double rowHeight = 14;
                DrawTableHeader(gfx, columns, widths, x, y, rowHeight, boldFont);
                y += rowHeight;

                int drawn = 0;
                while (rowIndex < samplesTable.Rows.Count && drawn < rowsPerPage)
                {
                    DrawTableRow(gfx, samplesTable.Rows[rowIndex], columns, widths, x, y, rowHeight, normalFont);
                    y += rowHeight;
                    rowIndex++;
                    drawn++;
                }

                DrawPdfFooter(gfx, page, smallFont);

                if (rowIndex >= samplesTable.Rows.Count)
                    break;
            }
        }

        private void DrawDetailedPdf(PdfDocument document)
        {
            int pageNo = 0;
            int sampleIndex = 0;

            while (sampleIndex < samplesTable.Rows.Count || pageNo == 0)
            {
                pageNo++;
                PdfPage page = document.AddPage();
                page.Width = XUnit.FromPoint(842);
                page.Height = XUnit.FromPoint(595);

                using XGraphics gfx = XGraphics.FromPdfPage(page);
                XFont titleFont = new XFont("Arial", 13, XFontStyleEx.Bold);
                XFont boldFont = new XFont("Arial", 8, XFontStyleEx.Bold);
                XFont normalFont = new XFont("Arial", 7, XFontStyleEx.Regular);
                XFont smallFont = new XFont("Arial", 6, XFontStyleEx.Regular);

                DrawPdfHeader(gfx, page, "Sample Management Detailed Report", pageNo);

                double y = 112;
                int samplesOnPage = 0;

                while (sampleIndex < samplesTable.Rows.Count && y < 505 && samplesOnPage < 4)
                {
                    DataRow row = samplesTable.Rows[sampleIndex];
                    double blockHeight = 96;
                    gfx.DrawRoundedRectangle(XPens.LightGray, XBrushes.White, 30, y, page.Width.Point - 60, blockHeight, 5, 5);

                    string sampleTitle = GetCellText(row, "SampleNumber") + " | " + GetCellText(row, "SampleType") + " | " + GetCellText(row, "CurrentStatus");
                    gfx.DrawString(sampleTitle, boldFont, XBrushes.Black, new XRect(40, y + 7, page.Width.Point - 80, 12), XStringFormats.TopLeft);

                    string line1 = "Point: " + GetCellText(row, "PointCode") + " | Location: " + GetCellText(row, "Location") + " | Sampling: " + GetCellText(row, "SamplingDateTimeText") + " | Sampled By: " + GetCellText(row, "SampledBy");
                    string line2 = "Tests: " + GetCellText(row, "TestsCount") + " | Completed: " + GetCellText(row, "CompletedResults") + " | Pending: " + GetCellText(row, "PendingResults") + " | Alert: " + GetCellText(row, "AlertResults") + " | Fail/OOS: " + GetCellText(row, "FailResults");
                    string line3 = "Investigation: " + GetCellText(row, "InvestigationStatus") + " | COA: " + GetCellText(row, "COAStatus") + " | Created By: " + GetCellText(row, "CreatedBy") + " | Created Date: " + GetCellText(row, "CreatedDateText");
                    string line4 = "Next Action: " + BuildNextAction(row);

                    gfx.DrawString(line1, normalFont, XBrushes.Black, new XRect(40, y + 24, page.Width.Point - 80, 11), XStringFormats.TopLeft);
                    gfx.DrawString(line2, normalFont, XBrushes.Black, new XRect(40, y + 39, page.Width.Point - 80, 11), XStringFormats.TopLeft);
                    gfx.DrawString(line3, normalFont, XBrushes.Black, new XRect(40, y + 54, page.Width.Point - 80, 11), XStringFormats.TopLeft);
                    gfx.DrawString(line4, normalFont, XBrushes.DarkSlateGray, new XRect(40, y + 72, page.Width.Point - 80, 11), XStringFormats.TopLeft);

                    y += blockHeight + 10;
                    sampleIndex++;
                    samplesOnPage++;
                }

                DrawPdfFooter(gfx, page, smallFont);

                if (sampleIndex >= samplesTable.Rows.Count)
                    break;
            }
        }

        private void DrawPdfHeader(XGraphics gfx, PdfPage page, string title, int pageNo)
        {
            XFont companyFont = new XFont("Arial", 14, XFontStyleEx.Bold);
            XFont titleFont = new XFont("Arial", 12, XFontStyleEx.Bold);
            XFont normalFont = new XFont("Arial", 7, XFontStyleEx.Regular);
            XFont boldFont = new XFont("Arial", 7, XFontStyleEx.Bold);

            double pageWidth = page.Width.Point;
            gfx.DrawRectangle(XPens.DarkSlateGray, 22, 16, pageWidth - 44, 76);
            gfx.DrawRectangle(XBrushes.DarkSlateGray, 22, 16, pageWidth - 44, 30);

            gfx.DrawString("MEDICA PHARMACEUTICAL INDUSTRY", companyFont, XBrushes.White, new XRect(32, 22, pageWidth - 64, 16), XStringFormats.TopCenter);
            gfx.DrawString("Microbiology Department", normalFont, XBrushes.White, new XRect(32, 36, pageWidth - 64, 10), XStringFormats.TopCenter);

            gfx.DrawString(title, titleFont, XBrushes.Black, new XRect(32, 54, pageWidth - 64, 15), XStringFormats.TopCenter);
            gfx.DrawString("Generated By: " + GetCurrentUserName() + " | Generated On: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " | Page: " + pageNo.ToString(CultureInfo.InvariantCulture), normalFont, XBrushes.Black, new XRect(32, 72, pageWidth - 64, 10), XStringFormats.TopCenter);

            string filters = "Filters - Sample No: " + EmptyAsAll(txtSampleNumber.Text) + " | Type: " + ComboText(cboSampleType) + " | Point: " + ComboText(cboSamplingPoint) + " | Status: " + ComboText(cboStatus) + " | From: " + DateText(dpDateFrom) + " | To: " + DateText(dpDateTo);
            gfx.DrawString(filters, boldFont, XBrushes.Black, new XRect(30, 96, pageWidth - 60, 10), XStringFormats.TopLeft);
        }

        private void DrawSummaryBox(XGraphics gfx, PdfPage page, double y, XFont normalFont, XFont boldFont)
        {
            gfx.DrawRoundedRectangle(XPens.LightGray, XBrushes.White, 30, y, page.Width.Point - 60, 44, 5, 5);
            gfx.DrawString("Summary", boldFont, XBrushes.Black, new XRect(40, y + 6, 100, 12), XStringFormats.TopLeft);
            gfx.DrawString(lblCounts.Text, normalFont, XBrushes.Black, new XRect(40, y + 23, page.Width.Point - 80, 12), XStringFormats.TopLeft);
        }

        private void DrawTableHeader(XGraphics gfx, string[] columns, double[] widths, double x, double y, double rowHeight, XFont font)
        {
            double currentX = x;
            for (int i = 0; i < columns.Length; i++)
            {
                gfx.DrawRectangle(XPens.Gray, XBrushes.LightGray, currentX, y, widths[i], rowHeight);
                gfx.DrawString(HeaderText(columns[i]), font, XBrushes.Black, new XRect(currentX + 2, y + 3, widths[i] - 4, rowHeight), XStringFormats.TopLeft);
                currentX += widths[i];
            }
        }

        private void DrawTableRow(XGraphics gfx, DataRow row, string[] columns, double[] widths, double x, double y, double rowHeight, XFont font)
        {
            double currentX = x;
            for (int i = 0; i < columns.Length; i++)
            {
                string value = TrimCell(GetCellText(row, columns[i]), MaxChars(columns[i]));
                XBrush background = columns[i] == "CurrentStatus" ? StatusBrush(GetCellText(row, columns[i])) : XBrushes.White;
                gfx.DrawRectangle(XPens.LightGray, background, currentX, y, widths[i], rowHeight);
                gfx.DrawString(value, font, XBrushes.Black, new XRect(currentX + 2, y + 3, widths[i] - 4, rowHeight), XStringFormats.TopLeft);
                currentX += widths[i];
            }
        }

        private void DrawPdfFooter(XGraphics gfx, PdfPage page, XFont font)
        {
            gfx.DrawString("Generated by PharmaLIMS | Sample Management", font, XBrushes.Gray, new XRect(30, page.Height.Point - 20, page.Width.Point - 60, 10), XStringFormats.TopCenter);
        }

        private XBrush StatusBrush(string status)
        {
            status = (status ?? "").Trim().ToUpperInvariant();

            if (status.Contains("APPROVED") || status.Contains("COA"))
                return new XSolidBrush(XColor.FromArgb(255, 231, 246, 236));

            if (status.Contains("INVESTIGATION") || status.Contains("ALERT"))
                return new XSolidBrush(XColor.FromArgb(255, 255, 243, 205));

            if (status.Contains("REJECT") || status.Contains("CANCEL"))
                return new XSolidBrush(XColor.FromArgb(255, 253, 226, 225));

            return XBrushes.White;
        }

        private string HeaderText(string column)
        {
            return column switch
            {
                "SampleNumber" => "Sample No",
                "SampleType" => "Type",
                "PointCode" => "Point",
                "SamplingDateTimeText" => "Sampling Date/Time",
                "CurrentStatus" => "Status",
                "TestsCount" => "Tests",
                "PendingResults" => "Pending",
                "AlertResults" => "Alert",
                "FailResults" => "Fail/OOS",
                "InvestigationStatus" => "Investigation",
                "COAStatus" => "COA",
                _ => column
            };
        }

        private int MaxChars(string column)
        {
            return column switch
            {
                "SampleNumber" => 16,
                "SampleType" => 18,
                "Location" => 28,
                "SamplingDateTimeText" => 18,
                "InvestigationStatus" => 16,
                _ => 14
            };
        }

        private string TrimCell(string value, int max)
        {
            value ??= "";
            value = value.Trim();

            if (value.Length <= max)
                return value;

            return value.Substring(0, Math.Max(0, max - 3)) + "...";
        }

        private string BuildNextAction(DataRow row)
        {
            if (row == null)
                return "Review sample workflow status.";

            if (IsEMRow(row))
            {
                int pendingEm = SafeInt(row["PendingResults"]);
                int failEm = SafeInt(row["FailResults"]);
                string investigationEm = GetCellText(row, "InvestigationStatus");
                string statusEm = GetCellText(row, "CurrentStatus");

                if (pendingEm > 0)
                    return "Complete pending EM plate results.";

                if (failEm > 0 && investigationEm.Equals("Required", StringComparison.OrdinalIgnoreCase))
                    return "Open EM quality event / investigation.";

                if (statusEm.Equals("Approved", StringComparison.OrdinalIgnoreCase) || statusEm.Equals("Approved with Alert", StringComparison.OrdinalIgnoreCase))
                    return "Print or review EMRR report.";

                return "Review EM workflow status in EM Results Entry.";
            }

            if (GetCellText(row, "COAStatus").Equals("Active", StringComparison.OrdinalIgnoreCase))
                return "COA already issued. Direct editing is blocked.";

            int tests = SafeInt(row["TestsCount"]);
            if (tests == 0)
                return "Assign tests.";

            int pending = SafeInt(row["PendingResults"]);
            if (pending > 0)
                return "Complete pending results.";

            int fail = SafeInt(row["FailResults"]);
            string investigation = GetCellText(row, "InvestigationStatus");

            if (fail > 0 && investigation.Equals("Required", StringComparison.OrdinalIgnoreCase))
                return "Open investigation for FAIL/OOS result.";

            if (investigation.Equals("Open", StringComparison.OrdinalIgnoreCase))
                return "Close open investigation before approval.";

            string status = GetCellText(row, "CurrentStatus");
            string coa = GetCellText(row, "COAStatus");

            if (status.Equals("Approved", StringComparison.OrdinalIgnoreCase) && coa.Equals("Not Issued", StringComparison.OrdinalIgnoreCase))
                return "Issue COA.";

            if (status.Equals("Reviewed", StringComparison.OrdinalIgnoreCase))
                return "QA approval required.";

            if (status.Equals("Results Entered", StringComparison.OrdinalIgnoreCase) || status.Equals("In Progress", StringComparison.OrdinalIgnoreCase))
                return "Submit for review.";

            return "Review sample workflow status.";
        }

        private string EmptyAsAll(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "All" : value.Trim();
        }

        private string ComboText(ComboBox combo)
        {
            if (combo?.SelectedItem is ComboBoxItem item)
                return item.Content?.ToString() ?? "All";

            return "All";
        }

        private string DateText(DatePicker picker)
        {
            if (picker != null && picker.SelectedDate.HasValue)
                return picker.SelectedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            return "All";
        }

        private static string GetCurrentUserName()
        {
            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                return Login.CurrentUser.Trim();

            if (!string.IsNullOrWhiteSpace(Login.CurrentUserFullName))
                return Login.CurrentUserFullName.Trim();

            return string.Empty;
        }

        private void SetStatus(string message, bool busy)
        {
            lblStatus.Text = message;
            lblHeaderStatus.Text = busy ? "Working..." : lblHeaderStatus.Text;
        }

        private void ShowInfo(string message)
        {
            MessageBox.Show(message, "Sample Management", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowError(string message)
        {
            MessageBox.Show(message, "Sample Management Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [SupportedOSPlatform("windows7.0")]
    public class SampleManagementWindowsFontResolver : IFontResolver
    {
        public byte[] GetFont(string faceName)
        {
            string fontPath = faceName switch
            {
                "Arial#Bold" => @"C:\Windows\Fonts\arialbd.ttf",
                "Arial#Italic" => @"C:\Windows\Fonts\ariali.ttf",
                "Arial#BoldItalic" => @"C:\Windows\Fonts\arialbi.ttf",
                _ => @"C:\Windows\Fonts\arial.ttf"
            };

            if (!File.Exists(fontPath))
                fontPath = @"C:\Windows\Fonts\segoeui.ttf";

            return File.ReadAllBytes(fontPath);
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (isBold && isItalic)
                return new FontResolverInfo("Arial#BoldItalic");
            if (isBold)
                return new FontResolverInfo("Arial#Bold");
            if (isItalic)
                return new FontResolverInfo("Arial#Italic");
            return new FontResolverInfo("Arial#Regular");
        }
    }
}
